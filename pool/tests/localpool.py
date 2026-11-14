"""localpool.py: what test_pool.py builds each test on, a whole work pool on this machine. The hub's store is a temp
folder served by the real deal.py and dist.py on free ports, reached at 127.0.0.1; the workers are real worker.py
processes, each with its own folder for a machine's ~/sts2, whose simulator is fakesim.py. What box.py would put on the
hub (a job's queue.txt and job.json, the step files, the bundles in dist/) is written here directly. Nothing is written
outside the temp folder, and nothing is asked of another machine.
"""
import hashlib
import json
import os
import re
import secrets
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter, namedtuple
from pathlib import Path

TESTS = Path(__file__).resolve().parent
POOL = TESTS.parent
DEAL, DIST, WORKER, FAKESIM = POOL / "deal.py", POOL / "dist.py", POOL / "worker.py", TESTS / "fakesim.py"
REV, INPUTS = "abc1234", "abc1234-v0-1"  # a job's build and inputs unless a test names others
TOOLS = ("openssl", "zstd", "tar")  # besides python3: the certificate, and what worker.py itself needs

# a line of claims.log: a chunk dealt to SHARD, or (back) taken back from it, and why
Claim = namedtuple("Claim", "time shard index n back why")


def chunks(start, n, size=1):
    """A queue's lines as box.py cuts a range: "INDEX COUNT" a chunk."""
    return [f"{i} {min(size, start + n - i)}" for i in range(start, start + n, size)]


def clean_env(**more):
    """This process's environment without proxies and without the scripts' own knobs."""
    drop = ("http_proxy", "https_proxy", "all_proxy", "no_proxy", "deal_silence", "worker_pace")
    env = {k: v for k, v in os.environ.items() if k.lower() not in drop and not k.startswith("FAKESIM_")}
    return env | {k: str(v) for k, v in more.items()}


def write_whole(path, text):
    """A file the dealer may read at any moment: there whole or not at all."""
    path.parent.mkdir(parents=True, exist_ok=True)
    new = path.with_name(path.name + ".new")
    new.write_text(text)
    os.replace(new, path)


def alive(pid):
    try:
        return Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[0] != "Z"
    except OSError:
        return False


class Worker:
    """One machine's worker.py; hosts/NAME/sts2 in the hub's temp folder stands for the machine's ~/sts2. PACE scales its
    waits (WORKER_PACE): at 0.02 it looks at the pool every 1.2 s, checks its threads file and its runs every 0.2 s,
    asks again 1.2 s after an empty answer, and its sender looks at an empty outbox every 0.1 s."""

    def __init__(self, hub, name, threads, pace, env, retry=None):
        self.name, self.pace = name, pace
        self.dir = hub.tmp / "hosts" / name / "sts2" / "pool"
        self.dir.mkdir(parents=True)
        if retry is not None:
            write_whole(self.dir / "retry", f"{retry}\n")
        config = {"dist": f"https://127.0.0.1:{hub.dist_port}", "key": hub.key, "cert": (hub.tmp / "cert.pem").read_text()}
        (self.dir / "config.json").write_text(json.dumps(config))
        self.log_path = self.dir / "worker.log"
        self.set_threads(threads)
        self.start(env)

    def start(self, env=None):
        """Starts it as box.py up does: its threads from the threads file, its output to worker.log."""
        with self.log_path.open("ab") as log:
            self.process = subprocess.Popen(
                [sys.executable, str(WORKER), "--name", self.name, "--dir", str(self.dir)],
                cwd=self.dir, stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT,
                env=clean_env(WORKER_PACE=self.pace, **(env or {})),
            )

    def set_threads(self, n):
        write_whole(self.dir / "threads", f"{n}\n")

    def term(self):
        """box.py down: SIGTERM, and its exit code once it has ended."""
        self.process.terminate()
        return self.process.wait(30)

    def kill(self):
        """The machine dies: the worker is gone at once, its runs are left behind."""
        self.process.kill()
        self.process.wait()

    def runs(self):
        """The process groups of the runs in play."""
        found = []
        for path in self.dir.glob("work/*/pgid"):
            try:
                found.append(int(path.read_text()))
            except (OSError, ValueError):
                pass
        return found

    def outbox(self):
        return sorted(p.name for p in (self.dir / "outbox").glob("*.tar*"))

    def log(self):
        return self.log_path.read_text() if self.log_path.exists() else ""

    def close(self):
        if self.process.poll() is None:
            self.kill()


class Hub:
    """The hub in a temp folder, and the pool's workers. SILENCE: deal.py's DEAL_SILENCE, s."""

    def __init__(self, silence=None):
        self.tmp = Path(tempfile.mkdtemp(prefix="pool-test-"))
        self.store = self.tmp / "store"
        self.workers, self.services = [], []
        try:
            self.serve(silence)
        except BaseException:
            self.close()
            raise

    def serve(self, silence):
        (self.store / "pool").mkdir(parents=True)
        (self.store / "dist").mkdir()
        # as dist-admin.py setup leaves the hub: the key's hash, a self-signed certificate, the pool's token, pool.json
        self.token, self.key = secrets.token_hex(16), secrets.token_urlsafe(32)
        (self.store / "pool" / "token").write_text(self.token + "\n")
        (self.tmp / "key.sha256").write_text(hashlib.sha256(self.key.encode()).hexdigest() + "\n")
        subprocess.run(
            ["openssl", "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes", "-days", "2",
             "-subj", "/CN=sts2-dist", "-keyout", str(self.tmp / "key.pem"), "-out", str(self.tmp / "cert.pem")],
            check=True, stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        with socket.socket() as a, socket.socket() as b:
            a.bind(("127.0.0.1", 0))
            b.bind(("127.0.0.1", 0))
            self.deal_port, self.dist_port = a.getsockname()[1], b.getsockname()[1]
        self.deal_url = f"http://127.0.0.1:{self.deal_port}"
        (self.store / "dist" / "pool.json").write_text(json.dumps({"deal": self.deal_url, "token": self.token}) + "\n")
        self.deal_log, self.dist_log = self.tmp / "deal.log", self.tmp / "dist.log"
        env = clean_env(**({} if silence is None else {"DEAL_SILENCE": silence}))
        self.service(self.deal_log, self.deal_port, env, DEAL, self.store, self.deal_port)
        self.service(self.dist_log, self.dist_port, env, DIST, self.store, self.dist_port,
                     self.tmp / "cert.pem", self.tmp / "key.pem", self.tmp / "key.sha256")
        self.http = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def service(self, log, port, env, *line):
        with log.open("wb") as sink:
            process = subprocess.Popen([sys.executable, *map(str, line)], cwd=self.tmp, stdin=subprocess.DEVNULL,
                                       stdout=sink, stderr=subprocess.STDOUT, env=env)
        self.services.append(process)
        end = time.monotonic() + 20
        while True:
            try:
                socket.create_connection(("127.0.0.1", port), timeout=1).close()
                return
            except OSError:
                if process.poll() is not None or time.monotonic() > end:
                    raise RuntimeError(f"{line[0].name} did not start:\n{log.read_text()}")
                time.sleep(0.01)

    def close(self):
        for worker in self.workers:
            worker.close()
        # the runs the workers left: every fake sim whose files are in this hub's temp folder
        for path in Path("/proc").glob("[0-9]*/cmdline"):
            try:
                line = path.read_bytes()
                if b"fakesim.py" in line and os.fsencode(self.tmp) in line:
                    os.kill(int(path.parent.name), signal.SIGKILL)
            except OSError:
                pass
        for process in self.services:
            process.kill()
            process.wait()
        if os.environ.get("POOL_TEST_KEEP"):
            print(f"kept {self.tmp}", file=sys.stderr)
        else:
            shutil.rmtree(self.tmp, ignore_errors=True)

    # --- what box.py and dist-admin.py put on the hub

    def put_bundle(self, rev):
        """dist/sts2-REV.tar.zst with dist-admin.py bundle's layout: the launcher sim-REV.sh, builds/REV, dotnet/ and
        gamebin/; its build is fakesim.py, told its REV."""
        packed = self.store / "dist" / f"sts2-{rev}.tar.zst"
        if packed.exists():
            return
        stage = Path(tempfile.mkdtemp(dir=self.tmp, prefix="stage-"))
        release = stage / "builds" / rev / "release"
        release.mkdir(parents=True)
        shutil.copy(FAKESIM, release / "fakesim.py")
        for name in ("dotnet/dotnet", "gamebin/v0/release_info.json"):
            (stage / name).parent.mkdir(parents=True)
            (stage / name).write_text("not used by the fake sim\n")
        launcher = stage / f"sim-{rev}.sh"
        fake = f'"$(dirname "$0")/builds/{rev}/release/fakesim.py"'
        launcher.write_text(f'#!/bin/sh\nexec "{sys.executable}" {fake} {rev} "$@"\n')
        launcher.chmod(0o755)
        self.pack(stage, packed, ".")

    def put_inputs(self, key):
        """dist/inputs-KEY.tar.zst: a seed file, and a marker the fake sim copies into its records."""
        packed = self.store / "dist" / f"inputs-{key}.tar.zst"
        if packed.exists():
            return
        stage = Path(tempfile.mkdtemp(dir=self.tmp, prefix="stage-"))
        (stage / "eval" / "seeds").mkdir(parents=True)
        (stage / "eval" / "seeds" / "test.txt").write_text("".join(f"SEED{i}\n" for i in range(100)))
        (stage / "eval" / "marker.txt").write_text(key + "\n")
        self.pack(stage, packed, "eval")

    def pack(self, stage, packed, *members):
        tar = subprocess.run(["tar", "-C", str(stage), "--owner=0", "--group=0", "-cf", "-", *members],
                             check=True, stdout=subprocess.PIPE).stdout
        part = packed.with_name(packed.name + ".part")
        with part.open("wb") as f:
            subprocess.run(["zstd", "-q", "-1"], input=tar, stdout=f, check=True)
        os.replace(part, packed)
        shutil.rmtree(stage)

    def step_path(self, name):
        return self.store / "pool" / "steps" / f"{name}.json"

    def write_step(self, name, step):
        write_whole(self.step_path(name), json.dumps(step, indent=2) + "\n")

    def launch(self, job, step, queue, options=(), rev=REV, inputs=INPUTS, chunk=1, priority=0, deadline=None,
               launched=None):
        """What box.py run leaves on the hub: the build and the inputs in dist/, the job's queue.txt and job.json, then
        the job into its step, last. A new step gets PRIORITY, DEADLINE (epoch s, or none) and LAUNCHED (default now)."""
        self.put_bundle(rev)
        self.put_inputs(inputs)
        deal = self.store / job / "deal"
        deal.mkdir(parents=True)
        (deal / "queue.txt").write_text("".join(line + "\n" for line in queue))
        spec = {"job": job, "kind": job.split("/")[2], "rev": rev, "options": list(options), "chunk": chunk, "inputs": inputs}
        (deal / "job.json").write_text(json.dumps(spec, indent=1) + "\n")
        if self.step_path(step).exists():
            body = json.loads(self.step_path(step).read_text())
        else:
            body = {"priority": priority, "deadline_epoch": deadline, "stopped": None, "jobs": [],
                    "launched": time.time() if launched is None else launched}
        body["jobs"].append(job)
        self.write_step(step, body)

    def stop_job(self, job, reason):
        """box.py stop JOB."""
        write_whole(self.store / job / "deal" / "stopped", f"{reason}\n")

    def stop_step(self, name, reason):
        """box.py stop NAME."""
        body = json.loads(self.step_path(name).read_text())
        body["stopped"] = reason
        self.write_step(name, body)

    def delete_step(self, name):
        """A step taken off the hub with its jobs' folders: the step's file first, so the dealer never walks half a job."""
        jobs = json.loads(self.step_path(name).read_text())["jobs"]
        self.step_path(name).unlink()
        for job in jobs:
            shutil.rmtree(self.store / job)

    def worker(self, name, threads, pace=0.02, env=None, retry=None):
        worker = Worker(self, name, threads, pace, env, retry)
        self.workers.append(worker)
        return worker

    # --- the dealer, asked as a worker thread asks it

    def ask(self, path, **query):
        """(status, text) of a request to the dealer."""
        url = f"{self.deal_url}{path}?{urllib.parse.urlencode(query | {'token': self.token})}"
        try:
            with self.http.open(url, timeout=20) as answer:
                return answer.status, answer.read().decode().strip()
        except urllib.error.HTTPError as e:
            return e.code, ""

    def next(self, shard, last="", failed=0, more=1):
        """/next for SHARD: "JOB INDEX N", or None when nothing is dealt."""
        status, text = self.ask("/next", shard=shard, last=last, failed=failed, more=more)
        return text if status == 200 else None

    # --- the store, read as box.py status and fetch read it

    def claims(self, job):
        path = self.store / job / "deal" / "claims.log"
        found = []
        for line in path.read_text().splitlines() if path.exists() else []:
            w = line.split(maxsplit=5)
            if len(w) >= 4:
                back = len(w) > 4 and w[4] == "back"
                found.append(Claim(float(w[0]), w[1], int(w[2]), int(w[3]), back, w[5] if len(w) > 5 else ""))
        return found

    def dealt(self, job):
        """The chunks dealt, in order, a chunk once for each time it was dealt."""
        return [c for c in self.claims(job) if not c.back]

    def order(self, *jobs):
        """(job, index) of every chunk dealt of JOBS, in the order the dealer dealt them."""
        return [(job, c.index) for _, job, c in sorted((c.time, job, c) for job in jobs for c in self.dealt(job))]

    def done(self, job):
        """{(shard, index): the run indices in its done- file} for every chunk of JOB on the hub."""
        found = {}
        for path in (self.store / job).glob("done-*.txt"):
            shard, index = path.stem[5:].rsplit("-", 1)
            found[(shard, int(index))] = [int(x) for x in path.read_text().split()]
        return found

    def runs(self, job):
        """Every run index the hub counts for JOB, sorted; one counted twice is there twice."""
        return sorted(i for indices in self.done(job).values() for i in indices)

    def ends(self, job):
        """{(shard, index): why the chunk ended} from the tally- files: whole, deadline, stopped, failed."""
        found = {}
        for path in (self.store / job).glob("tally-*.txt"):
            shard, index = path.stem[6:].rsplit("-", 1)
            for line in path.read_text().splitlines():
                if line.startswith("end "):
                    found[(shard, int(index))] = line.split()[2].rstrip(":")
        return found

    def records(self, job):
        """The run records of JOB on the hub."""
        files = sorted(map(str, (self.store / job).glob("rec-*.jsonl.zst")))
        if not files:
            return []
        text = subprocess.run(["zstd", "-dc", *files], check=True, stdout=subprocess.PIPE, text=True).stdout
        return [json.loads(line) for line in text.splitlines()]

    def lines(self, job, name):
        """The lines of JOB/deal/NAME; none when it is not there."""
        path = self.store / job / "deal" / name
        return [line for line in (path.read_text() if path.exists() else "").splitlines() if line.strip()]

    def numbers(self, job):
        """A job's counts: the queue's and the dealer's as box.py status reads them, the runs from the tally- files, and
        (missing) the chunks dealt and not taken back that have no done- yet, which box.py fetch waits for."""
        tallies = [line.split() for path in (self.store / job).glob("tally-*.txt")
                   for line in path.read_text().splitlines()]
        state = {}
        for c in self.claims(job):
            state[(c.shard, c.index)] = c.back
        done = self.done(job)
        return {
            "queue": len(self.lines(job, "queue.txt")), "pos": int("".join(self.lines(job, "pos")) or 0),
            "front": len(self.lines(job, "front.txt")), "back": sum(c.back for c in self.claims(job)),
            "runs": sum(w[0] == "run" for w in tallies), "won": sum(w[0] == "run" and w[2] == "won" for w in tallies),
            "ends": dict(Counter(w[2].rstrip(":") for w in tallies if w[0] == "end")),
            "missing": sorted(key for key, back in state.items() if not back and key not in done),
        }

    def held(self):
        """{shard: (job, index)} the chunks the dealer holds for threads now."""
        found = {}
        folder = self.store / "pool" / "held"
        for path in folder.iterdir() if folder.is_dir() else []:
            try:
                words = path.read_text().split()
            except OSError:
                continue
            if len(words) >= 3 and not path.name.endswith(".new"):
                found[path.name] = (words[0], int(words[1]))
        return found

    def looks(self, host):
        """How many times HOST's worker has looked at the pool."""
        path = self.store / "pool" / f"cpu-{host}.txt"
        return len(path.read_text().splitlines()) if path.exists() else 0

    def dealer_errors(self):
        """What the dealer raised while serving, but for a client that went away."""
        blocks = [b for b in self.deal_log.read_text().split("-" * 40) if "Traceback" in b]
        gone = re.compile(r"^(BrokenPipeError|ConnectionResetError|TimeoutError)", re.MULTILINE)
        return [b for b in blocks if not gone.search(b)]

    # --- waiting

    def wait(self, what, check, timeout=30):
        """Waits until CHECK() is true and returns it; after TIMEOUT s fails with the pool's logs."""
        end = time.monotonic() + timeout
        while True:
            value = check()
            if value:
                return value
            if time.monotonic() > end:
                raise AssertionError(f"waited {timeout} s for: {what}\n{self.report()}")
            time.sleep(0.02)

    def report(self):
        """The dealer's, the file service's and the workers' output, and every claims.log."""
        parts = [f"--- deal.py\n{self.deal_log.read_text()}", f"--- dist.py\n{self.dist_log.read_text()[-3000:]}"]
        parts += [f"--- worker {w.name}\n{w.log()[-3000:]}" for w in self.workers]
        parts += [f"--- {p.relative_to(self.store)}\n{p.read_text()}" for p in sorted(self.store.glob("**/deal/claims.log"))]
        return "\n".join(parts)
