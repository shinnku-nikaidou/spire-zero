"""test_pool.py: the work pool end to end on this machine (pool/README.md): the real deal.py, dist.py and worker.py over
a throwaway store, with fakesim.py for the simulator (localpool.py sets them up). No game, no .NET, no other machine.

    python3 -m unittest discover -s pool/tests -v

POOL_TEST_KEEP=1 keeps each test's temp folder (the store, the logs, the workers' folders) and prints where it is.
"""
import shutil
import subprocess
import time
import unittest

from localpool import INPUTS, REV, TOOLS, Hub, alive, chunks


def setUpModule():
    missing = [tool for tool in TOOLS if not shutil.which(tool)]
    if missing:
        raise unittest.SkipTest(f"the pool's tests need {', '.join(missing)}")


class PoolTest(unittest.TestCase):
    def pool(self, **options):
        """A hub for this test; when the test ends it is taken down, and the dealer must not have raised."""
        hub = Hub(**options)

        def close():
            errors = hub.dealer_errors()
            hub.close()
            self.assertEqual(errors, [], "deal.py raised while serving")

        self.addCleanup(close)
        return hub

    def assert_each_run_once(self, hub, job, indices):
        self.assertEqual(hub.runs(job), sorted(indices), "the run indices in the job's done- files")


class BasicPath(PoolTest):
    def test_every_chunk_is_dealt_once_played_and_stored(self):
        hub = self.pool()
        job = "silent/sl/selfplay/basic/a"
        queue = chunks(0, 32, 2)
        hub.launch(job, "basic", queue, ["--tag", "basic", "--sleep", "0.1"], chunk=2)
        a, b = hub.worker("hostA", 2), hub.worker("hostB", 2)
        hub.wait("every chunk on the hub", lambda: len(hub.done(job)) == 16)
        hub.wait("no chunk held, the outboxes empty", lambda: not hub.held() and not a.outbox() and not b.outbox())

        # each line of the queue was dealt once, to threads of both hosts, and none was taken back
        claims = hub.claims(job)
        self.assertEqual([f"{c.index} {c.n}" for c in sorted(claims, key=lambda c: c.index)], queue)
        self.assertFalse(any(c.back for c in claims))
        self.assertEqual({c.shard.rsplit("-", 1)[0] for c in claims}, {"hostA", "hostB"})

        # each chunk is on the hub whole, under the thread it was dealt to, with all its files
        done = hub.done(job)
        self.assertEqual(set(done), {(c.shard, c.index) for c in claims})
        for (shard, index), runs in done.items():
            self.assertEqual(runs, [index, index + 1])
            stem = f"{shard}-{index}"
            for name in ("rec-{}.jsonl.zst", "log-{}.jsonl.zst", "out-{}.txt.zst", "counts-{}.txt", "tally-{}.txt"):
                self.assertTrue((hub.store / job / name.format(stem)).is_file(), name.format(stem))
            self.assertIn(f"2 rec-{stem}.jsonl\n", (hub.store / job / f"counts-{stem}.txt").read_text())

        # every run once, played by the job's build with its options, in its inputs
        records = hub.records(job)
        self.assertEqual(sorted(r["index"] for r in records), list(range(32)))
        self.assertEqual({(r["rev"], r["tag"], r["inputs"]) for r in records}, {(REV, "basic", INPUTS)})

        # the job's counts: the queue used up, 32 runs, the 11 with an index that divides by 3 won, nothing missing
        self.assert_each_run_once(hub, job, range(32))
        self.assertEqual(hub.numbers(job), {"queue": 16, "pos": 16, "front": 0, "back": 0, "runs": 32, "won": 11,
                                            "ends": {"whole": 16}, "missing": []})


class Order(PoolTest):
    def test_steps_by_priority_then_launch_and_jobs_by_share(self):
        hub = self.pool()
        low, late, big, small = (f"silent/sl/{kind}/order/{name}" for kind, name in
                                 (("selfplay", "low"), ("eval", "late"), ("eval", "big"), ("eval", "small")))
        # the steps' names, and the order they are written in, differ from the order they must be dealt in
        hub.launch(low, "a-low", chunks(0, 3), priority=0, launched=100)
        hub.launch(late, "b-late", chunks(0, 2), priority=1, launched=300)
        hub.launch(big, "c-early", chunks(0, 4), priority=1, launched=200)
        hub.launch(small, "c-early", chunks(0, 2))
        hub.worker("hostA", 1)
        hub.wait("every chunk on the hub", lambda: sum(len(hub.done(job)) for job in (low, late, big, small)) == 11)
        self.assertEqual(hub.order(low, late, big, small), [
            # priority 1, launched first; inside it the lowest share dealt, a tie to the step's first job
            (big, 0), (small, 0), (big, 1), (big, 2), (small, 1), (big, 3),
            # priority 1, launched later
            (late, 0), (late, 1),
            # priority 0
            (low, 0), (low, 1), (low, 2),
        ])

    def test_a_higher_step_launched_later_gets_every_thread_as_its_runs_end(self):
        hub = self.pool()
        data, gate = "silent/sl/selfplay/during/a", "silent/sl/eval/during/gate"
        hub.launch(data, "data", chunks(0, 40), ["--sleep", "0.2"], priority=0)
        hub.worker("hostA", 2)
        hub.wait("the data step in play", lambda: len(hub.dealt(data)) >= 4)
        hub.launch(gate, "gate", chunks(0, 4), priority=1)
        hub.wait("the gate on the hub", lambda: len(hub.done(gate)) == 4)
        hub.wait("a data chunk dealt after the gate", lambda: hub.order(data, gate)[-1][0] == data)
        jobs = [job for job, _ in hub.order(data, gate)]
        first = jobs.index(gate)
        # data before it, then the gate's four chunks with no data chunk between them, then data again
        self.assertGreaterEqual(first, 4)
        self.assertEqual(jobs[first:first + 4], [gate] * 4)
        # nothing was killed or taken back for it: every data chunk dealt before the gate ended whole
        hub.wait("the data chunks dealt before the gate on the hub", lambda: len(hub.done(data)) >= first)
        self.assertFalse(any(c.back for c in hub.claims(data)))
        self.assertEqual(set(hub.ends(data).values()) | set(hub.ends(gate).values()), {"whole"})


class ChunksGoBack(PoolTest):
    def test_a_cut_threads_chunk_goes_back_to_the_front(self):
        hub = self.pool()
        job = "silent/sl/selfplay/cut/a"
        hub.launch(job, "cut", chunks(0, 3))
        release = hub.tmp / "release"
        a = hub.worker("hostA", 2, env={"FAKESIM_HOLD": release})
        hub.wait("both threads playing", lambda: len(hub.held()) == 2)
        index = hub.held()["hostA-1"][1]
        a.set_threads(1)  # box.py threads hostA 1: thread 1 is cut, thread 0 plays on
        hub.wait("thread 1's chunk given back", lambda: any(c.back for c in hub.claims(job)) and "hostA-1" not in hub.held())
        self.assertEqual(hub.held(), {"hostA-0": (job, 1 - index)})
        self.assertEqual(hub.lines(job, "front.txt"), [f"{index} 1"])
        release.touch()
        hub.wait("every run on the hub", lambda: len(hub.runs(job)) == 3)

        # taken back from thread 1 as cut, and its copy would be refused
        back = [c for c in hub.claims(job) if c.back]
        self.assertEqual([(c.shard, c.index, c.why) for c in back], [("hostA-1", index, "hostA cut")])
        self.assertTrue((hub.store / job / "deal" / "back" / f"hostA-1-{index}").exists())
        # the next chunk dealt after that is the one taken back, not the queue's next (chunk 2)
        after = [c for c in hub.dealt(job) if c.time > back[0].time]
        self.assertEqual((after[0].shard, after[0].index), ("hostA-0", index))
        # thread 0 played all three, each once
        self.assertEqual(sorted(hub.done(job)), [("hostA-0", 0), ("hostA-0", 1), ("hostA-0", 2)])
        self.assert_each_run_once(hub, job, range(3))
        self.assertEqual(hub.numbers(job)["missing"], [])

    def test_an_ended_workers_chunks_go_back_and_another_host_plays_them(self):
        hub = self.pool()
        job = "silent/sl/selfplay/down/a"
        hub.launch(job, "down", chunks(0, 5))
        a = hub.worker("hostA", 2, env={"FAKESIM_HOLD": hub.tmp / "never"})
        hub.wait("both threads playing", lambda: len(hub.held()) == 2)
        self.assertEqual(a.term(), 0)  # box.py down hostA

        # both chunks are at the front, taken back as cut; the worker ended clean, its runs with it
        self.assertEqual(hub.lines(job, "front.txt"), ["0 1", "1 1"])
        self.assertEqual(hub.held(), {})
        self.assertEqual(sorted((c.shard, c.why) for c in hub.claims(job) if c.back),
                         [("hostA-0", "hostA cut"), ("hostA-1", "hostA cut")])
        self.assertIn("done, 0 runs played, 0 sent, 0 chunks not sent", a.log())
        self.assertFalse((a.dir / "worker.pid").exists())
        self.assertEqual(a.runs(), [])

        # another host's thread takes them first, then the rest of the queue
        hub.worker("hostB", 1)
        hub.wait("every run on the hub", lambda: len(hub.runs(job)) == 5)
        self.assertEqual([(c.shard, c.index) for c in hub.dealt(job)][2:], [("hostB-0", i) for i in range(5)])
        self.assertEqual(sorted(hub.done(job)), [("hostB-0", i) for i in range(5)])
        self.assert_each_run_once(hub, job, range(5))

    def test_a_restarted_workers_chunks_go_back(self):
        hub = self.pool()
        job = "silent/sl/selfplay/restart/a"
        hub.launch(job, "restart", chunks(0, 4))
        a = hub.worker("hostA", 2, env={"FAKESIM_HOLD": hub.tmp / "never"})
        hub.wait("both threads playing", lambda: len(hub.held()) == 2 and len(a.runs()) == 2)
        left, held = a.runs(), hub.held()
        a.kill()  # no word to the hub; its runs stay behind
        self.assertTrue(all(alive(pid) for pid in left))
        a.start()

        hub.wait("every run on the hub", lambda: len(hub.runs(job)) == 4)
        # what its threads held went back at its first look, and was dealt again
        self.assertEqual(sorted((c.shard, c.index, c.why) for c in hub.claims(job) if c.back),
                         sorted((shard, index, "hostA's worker started again") for shard, (_, index) in held.items()))
        self.assert_each_run_once(hub, job, range(4))
        self.assertEqual(hub.numbers(job)["missing"], [])
        # the new worker killed the runs the old one left
        hub.wait("the left runs gone", lambda: not any(alive(pid) for pid in left))

    def test_a_silent_hosts_chunk_goes_back_and_its_late_copy_is_refused(self):
        hub = self.pool(silence=2)
        job = "silent/sl/selfplay/quiet/a"
        hub.launch(job, "quiet", chunks(0, 3))
        # hostA looks at the pool at its start and then not for 12 s: alive, playing, and silent to the hub
        release = hub.tmp / "release"
        a = hub.worker("hostA", 1, pace=0.2, env={"FAKESIM_HOLD": release})
        hub.wait("hostA playing chunk 0", lambda: hub.held().get("hostA-0") == (job, 0))
        # hostB looks every 0.6 s; each time it asks, the dealer checks who has gone silent
        b = hub.worker("hostB", 1, pace=0.01)
        hub.wait("chunk 0 on the hub from hostB", lambda: ("hostB-0", 0) in hub.done(job))
        back = [c for c in hub.claims(job) if c.back and c.shard == "hostA-0"]
        self.assertEqual([(c.index, c.why[:18]) for c in back], [(0, "hostA silent since")])
        self.assertTrue((hub.store / "pool" / "swept-hostA").exists())

        # hostA's run ends now and its chunk goes up late: dist.py answers 409, the worker drops it
        release.touch()
        hub.wait("hostA told 409", lambda: "the hub answered 409, taken back: dropped" in a.log())
        self.assertRegex(hub.dist_log.read_text(), rf'"PUT /chunk/{job}/hostA-0/0 HTTP/1.1" 409')
        self.assertFalse((a.dir / "outbox" / "refused").exists())
        self.assertEqual(list((hub.store / job).glob("*-hostA-0-0.*")), [])
        hub.wait("every run on the hub, the outboxes empty",
                 lambda: len(hub.runs(job)) >= 3 and not a.outbox() and not b.outbox())
        self.assertEqual(sorted(hub.done(job)), [("hostB-0", 0), ("hostB-0", 1), ("hostB-0", 2)])
        self.assert_each_run_once(hub, job, range(3))


class StopsAndDeadlines(PoolTest):
    def test_a_stopped_job_deals_nothing_more(self):
        hub = self.pool()
        slow, fast = "silent/sl/selfplay/stop/slow", "silent/sl/selfplay/stop/fast"
        hub.launch(slow, "stop", chunks(0, 4), ["--sleep", "30"])
        hub.launch(fast, "stop", chunks(0, 16), ["--sleep", "0.1"])
        hub.worker("hostA", 2)
        hub.wait("both jobs in play", lambda: hub.dealt(slow) and hub.dealt(fast))
        hub.stop_job(slow, "test")
        time.sleep(0.3)  # a request under way has ended
        dealt = len(hub.dealt(slow))

        # the other job goes on to its end; the stopped one stays where it was, though its share dealt is the lowest
        hub.wait("the other job whole on the hub", lambda: len(hub.runs(fast)) == 16)
        hub.wait("the stopped job's chunks on the hub", lambda: not hub.numbers(slow)["missing"])
        self.assertEqual(len(hub.dealt(slow)), dealt)
        self.assertLess(dealt, 4)
        # its runs in play were killed at the worker's next look and sent with no run; none goes back to the queue
        self.assertEqual(hub.numbers(slow), {"queue": 4, "pos": dealt, "front": 0, "back": 0, "runs": 0, "won": 0,
                                             "ends": {"stopped": dealt}, "missing": []})
        self.assert_each_run_once(hub, fast, range(16))

    def test_a_stopped_step_deals_nothing_more(self):
        hub = self.pool()
        gate, data = "silent/sl/eval/stopstep/gate", "silent/sl/selfplay/stopstep/a"
        hub.launch(gate, "gate", chunks(0, 30), ["--sleep", "0.1"], priority=1)
        hub.launch(data, "data", chunks(0, 6), priority=0)
        hub.worker("hostA", 2)
        hub.wait("the gate in play", lambda: len(hub.dealt(gate)) >= 3)
        self.assertEqual(hub.dealt(data), [])
        stopped = time.time()
        hub.stop_step("gate", "test")
        time.sleep(0.3)  # a request under way has ended
        dealt = len(hub.dealt(gate))

        # the threads go to the lower step, and the stopped one stays where it was
        hub.wait("the lower step whole on the hub", lambda: len(hub.runs(data)) == 6)
        hub.wait("the stopped step's chunks on the hub", lambda: not hub.numbers(gate)["missing"])
        self.assertTrue(all(c.time > stopped for c in hub.dealt(data)))
        self.assertEqual(len(hub.dealt(gate)), dealt)
        self.assertLess(dealt, 30)
        # and a thread that asks now gets nothing, with 30 - dealt chunks of the stopped step left
        self.assertIsNone(hub.next("probe-0"))

    def test_a_step_past_its_deadline_deals_nothing_more(self):
        hub = self.pool()
        fast, slow = "silent/sl/selfplay/deadline/fast", "silent/sl/selfplay/deadline/slow"
        deadline = int(time.time()) + 3
        hub.launch(fast, "deadline", chunks(0, 200), ["--sleep", "0.1"], deadline=deadline)
        hub.launch(slow, "deadline", chunks(0, 2), ["--sleep", "30"])
        hub.worker("hostA", 2)
        hub.wait("both jobs in play before the deadline", lambda: hub.dealt(slow) and hub.dealt(fast))
        self.assertLess(time.time(), deadline)
        hub.wait("the deadline", lambda: time.time() > deadline, timeout=10)
        # each thread's run in play was killed or ended, went up, and the thread asked again
        hub.wait("every chunk dealt on the hub",
                 lambda: not hub.held() and not hub.numbers(fast)["missing"] and not hub.numbers(slow)["missing"])

        # nothing was dealt after the deadline, with most of the queue left
        self.assertTrue(all(c.time < deadline + 0.2 for c in hub.dealt(fast) + hub.dealt(slow)))
        self.assertLess(hub.numbers(fast)["pos"], 200)
        self.assertIsNone(hub.next("probe-0"))
        # the run that could not end in time was killed at the deadline and sent with no run; its seed does not go back
        self.assertEqual(hub.numbers(slow), {"queue": 2, "pos": 1, "front": 0, "back": 0, "runs": 0, "won": 0,
                                             "ends": {"deadline": 1}, "missing": []})
        self.assertLessEqual(set(hub.ends(fast).values()), {"whole", "deadline"})
        self.assertEqual(len(hub.runs(fast)), len(set(hub.runs(fast))))


class LaunchedAgain(PoolTest):
    def test_a_job_deleted_and_launched_again_is_dealt_as_the_new_one(self):
        hub = self.pool()
        job = "silent/sl/selfplay/again/a"
        hub.launch(job, "again", chunks(0, 6), ["--tag", "old"], rev="abc1234", inputs="abc1234-v0-1")
        hub.worker("hostA", 2, pace=0.01)
        hub.wait("the old job whole on the hub", lambda: len(hub.runs(job)) == 6)
        self.assertEqual({(r["rev"], r["tag"], r["inputs"]) for r in hub.records(job)}, {("abc1234", "old", "abc1234-v0-1")})

        # deleted, and the worker has looked at the pool without it
        hub.delete_step("again")
        looks = hub.looks("hostA")
        hub.wait("two looks of hostA", lambda: hub.looks("hostA") >= looks + 2)
        # the same name again: another build, other inputs, other options, another queue
        hub.launch(job, "again", chunks(100, 4, 2), ["--tag", "new"], rev="def5678", inputs="def5678-v0-2", chunk=2)
        hub.wait("the new job whole on the hub", lambda: len(hub.runs(job)) == 4)

        # the new queue was dealt from its start, and nothing of the old one
        self.assertEqual(sorted((c.index, c.n) for c in hub.claims(job)), [(100, 2), (102, 2)])
        self.assert_each_run_once(hub, job, range(100, 104))
        # and played by the new build with the new options, in the new inputs
        records = hub.records(job)
        self.assertEqual(sorted(r["index"] for r in records), [100, 101, 102, 103])
        self.assertEqual({(r["rev"], r["tag"], r["inputs"]) for r in records}, {("def5678", "new", "def5678-v0-2")})
        self.assertEqual(hub.numbers(job), {"queue": 2, "pos": 2, "front": 0, "back": 0, "runs": 4, "won": 1,
                                            "ends": {"whole": 2}, "missing": []})

    # worker.py looks again when it is dealt a chunk: by the job.json of its last look, up to 60 s old, the old build played it
    def test_a_job_launched_again_before_the_workers_next_look_is_played_by_the_new_build(self):
        hub = self.pool()
        job = "silent/sl/selfplay/soon/a"
        hub.launch(job, "soon", chunks(0, 20), ["--tag", "old", "--sleep", "0.5"], rev="abc1234")
        hub.put_bundle("def5678")
        # the worker looks at its start and then not for 12 s
        hub.worker("hostA", 1, pace=0.2)
        hub.wait("the old job in play", lambda: hub.dealt(job))
        # deleted and launched again while the thread plays its chunk of the old one
        hub.delete_step("soon")
        hub.launch(job, "soon", chunks(1000, 3), ["--tag", "new"], rev="def5678")
        hub.wait("the new job's chunks on the hub", lambda: {index for _, index in hub.done(job)} >= {1000, 1001, 1002})
        new = [r for r in hub.records(job) if r["index"] >= 1000]
        self.assertEqual({(r["rev"], r["tag"]) for r in new}, {("def5678", "new")})


class FailingHost(PoolTest):
    def test_a_host_whose_chunks_fail_twice_in_a_row_gets_no_more_of_the_job(self):
        hub = self.pool()
        bad, good = "silent/sl/selfplay/fail/bad", "silent/sl/selfplay/fail/good"
        hub.launch(bad, "fail", chunks(0, 6), ["--tag", "bad"])
        hub.launch(good, "fail", chunks(0, 6), ["--tag", "good"])
        # on hostA every chunk of the job tagged bad ends with no run
        a = hub.worker("hostA", 1, env={"FAKESIM_FAIL_TAG": "bad"})
        hub.wait("hostA has played what it gets",
                 lambda: len(hub.done(good)) == 6 and len(hub.done(bad)) == 2 and not hub.held())

        # two chunks of the job failed there, with a chunk of the other job between them; then it got only the other job
        self.assertEqual(hub.order(bad, good), [(bad, 0), (good, 0), (bad, 1), *((good, i) for i in range(1, 6))])
        self.assertEqual((hub.store / bad / "deal" / "fails" / "hostA").read_text(), "2\n")
        self.assertIn("2 chunks in a row ended with fewer runs than dealt, the last at 1",
                      (hub.store / bad / "deal" / "refused" / "hostA").read_text())
        self.assertFalse((hub.store / good / "deal" / "refused").exists())
        self.assertIn("0 of 1 runs, failed: run exit 1 at index 0", a.log())
        # a thread of hostA that asks gets nothing, though the job has four chunks left
        self.assertIsNone(hub.next("hostA-7"))

        # another host plays the rest; the failed chunks are on the hub with no run and are not dealt again
        hub.worker("hostB", 1)
        hub.wait("the rest on the hub", lambda: len(hub.done(bad)) == 6)
        self.assertEqual([(c.shard, c.index) for c in hub.claims(bad)],
                         [("hostA-0", 0), ("hostA-0", 1), *(("hostB-0", i) for i in range(2, 6))])
        self.assert_each_run_once(hub, bad, range(2, 6))
        self.assertEqual(hub.numbers(bad), {"queue": 6, "pos": 6, "front": 0, "back": 0, "runs": 4, "won": 1,
                                            "ends": {"failed": 2, "whole": 4}, "missing": []})
        self.assert_each_run_once(hub, good, range(6))


class PlayedAgain(PoolTest):
    """A host with a retry file plays a failed chunk again before it goes up (pushqkzh, whose memory flips bits)."""

    def out(self, hub, job, index):
        files = list((hub.store / job).glob(f"out-*-{index}.txt.zst"))
        return subprocess.run(["zstd", "-dc", *map(str, files)], check=True, stdout=subprocess.PIPE, text=True).stdout

    def tally(self, hub, job, index):
        return "".join(p.read_text() for p in (hub.store / job).glob(f"tally-*-{index}.txt"))

    def test_a_spoiled_attempt_is_played_again_and_only_the_good_run_goes_up(self):
        for mode, why in (("exit", "failed: run exit 1 at index {}"), ("record", "a failed run: fakesim: flaky, a spoiled run")):
            with self.subTest(mode=mode):
                hub = self.pool()
                job = f"silent/sl/selfplay/again/{mode}"
                hub.launch(job, "again", chunks(0, 4), ["--tag", "again"])
                flaky = hub.tmp / f"flaky-{mode}"
                flaky.mkdir()
                a = hub.worker("hostA", 1, env={"FAKESIM_FLAKY": str(flaky), "FAKESIM_FLAKY_MODE": mode}, retry=1)
                hub.wait("every chunk on the hub", lambda: len(hub.done(job)) == 4 and not hub.held() and not a.outbox())
                self.assert_each_run_once(hub, job, range(4))
                self.assertEqual(hub.numbers(job)["ends"], {"whole": 4})
                self.assertEqual(sorted(r["outcome"] for r in hub.records(job)), ["lost", "lost", "won", "won"])
                for index in range(4):
                    self.assertIn(f"attempt 1 failed, played again: {why.format(index)}", self.out(hub, job, index))
                    self.assertIn("sum played again: 1\n", self.tally(hub, job, index))
                    self.assertNotIn("attempt 2", self.out(hub, job, index))
                self.assertIn(f"{job} 0: {why.format(0)}; played again (1)", a.log())
                # the dealer heard of no failure
                self.assertFalse((hub.store / job / "deal" / "refused").exists())
                self.assertFalse((hub.store / job / "deal" / "fails").exists())

    def test_a_failure_that_comes_back_goes_up_as_before(self):
        hub = self.pool()
        bad, good = "silent/sl/selfplay/again2/bad", "silent/sl/selfplay/again2/good"
        hub.launch(bad, "again2", chunks(0, 6), ["--tag", "bad"])
        hub.launch(good, "again2", chunks(0, 6), ["--tag", "good"])
        a = hub.worker("hostA", 1, env={"FAKESIM_FAIL_TAG": "bad"}, retry=2)
        hub.wait("hostA has played what it gets",
                 lambda: len(hub.done(good)) == 6 and len(hub.done(bad)) == 2 and not hub.held() and not a.outbox())
        # each failed chunk was played three times, then went up failed: the host is refused after two, as without retries
        for index in (0, 1):
            out = self.out(hub, bad, index)
            self.assertIn("attempt 1 failed, played again: failed: run exit 1", out)
            self.assertIn("attempt 2 failed, played again: failed: run exit 1", out)
            self.assertIn(f"ended", out.splitlines()[-1])
            self.assertIn("failed: run exit 1", out.splitlines()[-1])
            self.assertIn("sum played again: 2\n", self.tally(hub, bad, index))
        self.assertEqual(hub.numbers(bad)["ends"], {"failed": 2})
        self.assertIn("2 chunks in a row ended with fewer runs than dealt",
                      (hub.store / bad / "deal" / "refused" / "hostA").read_text())
        # the good job's chunks were never played again
        for index in range(6):
            self.assertNotIn("played again", self.tally(hub, good, index))


class DealerAlone(PoolTest):
    """The test asks the dealer itself, as a worker thread does: cases a worker cannot be made to hit on demand."""

    def test_a_lost_answer_gets_the_same_chunk_again(self):
        hub = self.pool()
        job = "silent/sl/selfplay/lost/a"
        hub.launch(job, "lost", chunks(0, 3))
        self.assertEqual(hub.next("x-0"), f"{job} 0 1")
        # the thread never got that answer: it asks as before, and is told the chunk the hub holds for it
        self.assertEqual(hub.next("x-0"), f"{job} 0 1")
        self.assertEqual(hub.next("x-0", last=f"{job}/0"), f"{job} 1 1")
        self.assertEqual([(c.shard, c.index) for c in hub.claims(job)], [("x-0", 0), ("x-0", 1)])

    def test_only_failures_in_a_row_count(self):
        hub = self.pool()
        job = "silent/sl/selfplay/row/a"
        hub.launch(job, "row", chunks(0, 6))
        refused = hub.store / job / "deal" / "refused"
        self.assertEqual(hub.next("x-0"), f"{job} 0 1")
        self.assertEqual(hub.next("x-0", last=f"{job}/0", failed=1), f"{job} 1 1")
        # a whole chunk between two failed ones: the count starts again
        self.assertEqual(hub.next("x-0", last=f"{job}/1", failed=0), f"{job} 2 1")
        self.assertEqual(hub.next("x-0", last=f"{job}/2", failed=1), f"{job} 3 1")
        self.assertFalse(refused.exists())
        # the second failure in a row: no more of the job for any thread of host x, and still all of it for host y
        self.assertIsNone(hub.next("x-0", last=f"{job}/3", failed=1))
        self.assertEqual([p.name for p in refused.iterdir()], ["x"])
        self.assertIsNone(hub.next("x-1"))
        self.assertEqual(hub.next("y-0"), f"{job} 4 1")

    def test_an_ending_thread_whose_answer_was_lost_holds_no_chunk(self):
        hub = self.pool()
        job = "silent/sl/selfplay/ending/a"
        hub.launch(job, "ending", chunks(0, 3))
        self.assertEqual(hub.next("x-0"), f"{job} 0 1")
        self.assertEqual(hub.next("x-0", last=f"{job}/0"), f"{job} 1 1")  # this answer is lost
        # the thread is cut and says so, as worker.py does: last is still chunk 0, more=0
        self.assertEqual(hub.ask("/next", shard="x-0", last=f"{job}/0", failed=0, more=0), (204, ""))
        self.assertNotIn("x-0", hub.held())
        self.assertEqual(hub.next("y-0"), f"{job} 1 1")


if __name__ == "__main__":
    unittest.main()
