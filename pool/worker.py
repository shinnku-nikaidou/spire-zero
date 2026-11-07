#!/usr/bin/env python3
"""worker.py --name NAME [--threads N] [--until-empty] [--only KIND] [--dir DIR]: one machine's worker of the hub's work
pool (pool/README.md). Standard library only, python3 >= 3.10; needs tar and zstd.

It runs threads NAME-0, NAME-1, ... (NAME: the host's alias, box.py up gives it; a notebook's nbHEX). A thread asks the
hub's dealer (deal.py) for its next chunk, plays it with the job's sim and inputs, which come from the hub's dist/
on first need (dist.py), and leaves it in the outbox; one sender thread PUTs the outbox to the hub in order and
tries again until the hub takes it. Files in DIR (default ~/sts2/pool; the sim goes to DIR/..): config.json (the dist
URL, its x-api-key, the hub's certificate: box.py up writes it), threads, worker.pid, outbox/, work/SHARD/. It logs to
standard output (box.py up sends that to worker.log).

The threads file holds a number, or lines "HH:MM-HH:MM N" in UTC+8 and a last line with a number; the first line that
holds now wins. It is read every 10 s: missing threads start at once, and a thread whose number is no longer below N is
cut: its run killed, its chunk given back to the hub (which deals it again), the thread ended. --threads N: a fixed
number instead (a notebook).

A run is killed (TERM, KILL 30 s later) when the worker is cut (above, or SIGTERM: box.py down; then the outbox is sent,
5 min at most, and it exits), or when its step's deadline passes or its job or step is stopped: then its chunk goes up
with the runs that finished, maybe none. The hub's clock is the truth: every look (/pool, once a minute) gives it, and
the offset with the shortest round trip of the last 10 is used for deadlines and end lines.

--until-empty: ends once no thread got work for 10 min and the outbox is empty. --only KIND: only that kind of job.
"""

import argparse
import json
import os
import re
import shutil
import signal
import ssl
import subprocess
import sys
import tarfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

UTC8 = timezone(timedelta(hours=8))
SUMMARY = "game Log.Error lines"  # how a run's last line of counts starts in its output
FIELDS = re.compile(r'"(outcome)":"([a-z]+)"|"(act)":([0-9]+)|"(floor)":([0-9]+)')

args = None
DIR = S = Path()
config = {}
context = None
stop = threading.Event()  # SIGTERM: cut everything, send the outbox, exit
wanted = {"n": 0}  # the threads wanted now
state = {"look": None, "deal": None, "clocks": [], "last_work": time.time(), "played": 0, "sent": 0, "playing": {}}
state_lock = threading.Lock()
installing = threading.Lock()


def log(text):
    print(f"{datetime.now(UTC8):%Y-%m-%d %H:%M:%S} {args.name}: {text}", flush=True)


def true_now():
    """The hub's clock: the offset of the look with the shortest round trip of the last 10."""
    clocks = state["clocks"]
    return time.time() + (min(clocks)[1] if clocks else 0.0)


def pause(seconds):
    """Waits, or less when the worker stops; True when it stops."""
    return stop.wait(seconds)


# --- the hub

def dist(path, data=None, method="GET", timeout=120):
    request = urllib.request.Request(config["dist"] + path, data=data, method=method, headers={"x-api-key": config["key"]})
    return urllib.request.urlopen(request, timeout=timeout, context=context)


def deal(path, **query):
    """A request to the dealer: the answer's text, or None for 204."""
    if state["deal"] is None:
        with dist("/pool.json") as answer:
            state["deal"] = json.load(answer)
    query["token"] = state["deal"]["token"]
    url = f"{state['deal']['deal']}{path}?{urllib.parse.urlencode(query)}"
    try:
        with urllib.request.urlopen(url, timeout=20) as answer:
            return None if answer.status == 204 else answer.read().decode()
    except urllib.error.HTTPError as e:
        if e.code == 403:
            state["deal"] = None  # a new token: read pool.json again
        raise


def cpu_ticks():
    words = Path("/proc/stat").read_text().split("\n", 1)[0].split()[1:9]
    v = [int(x) for x in words]
    return v[0] + v[1] + v[2] + v[5] + v[6] + v[7], sum(v)


def look(start=False, ticks=[None]):
    """The pool now: steps and jobs, and the hub's clock."""
    busy = ""
    now = cpu_ticks()
    if ticks[0] is not None and now[1] > ticks[0][1]:
        busy = f"{(now[0] - ticks[0][0]) / (now[1] - ticks[0][1]) * os.cpu_count():.1f}"
    ticks[0] = now
    sent = time.time()
    text = deal("/pool", host=args.name, threads=wanted["n"], playing=len(state["playing"]), busy=busy,
                ncpu=os.cpu_count(), start=int(start))
    back = time.time()
    answer = json.loads(text)
    with state_lock:
        state["clocks"] = (state["clocks"] + [(round(back - sent, 3), answer["now"] - (sent + back) / 2)])[-10:]
        state["look"] = answer
    return answer


def looker():
    while not pause(60):
        try:
            look()
        except (OSError, ValueError) as e:
            log(f"look failed: {e!r}")


# --- the sim and the inputs

def fetch(name, into):
    """A zstd tar of the hub's dist/ unpacked into a new folder INTO. The hub lets two large downloads run at once: wait
    for a turn."""
    part = into.with_name(f".{into.name}.part")
    shutil.rmtree(part, ignore_errors=True)
    part.mkdir(parents=True)
    packed = part.with_suffix(".tar.zst")
    while True:
        try:
            with dist("/" + name, timeout=1800) as answer, packed.open("wb") as f:
                shutil.copyfileobj(answer, f)
            break
        except urllib.error.HTTPError as e:
            if e.code != 503:
                raise
        except OSError as e:
            log(f"{name}: {e!r}, again in 30 s")
        if pause(30):
            raise RuntimeError("stopping")
    unzstd = subprocess.Popen(["zstd", "-dqc", str(packed)], stdout=subprocess.PIPE)
    subprocess.run(["tar", "-x", "-C", str(part)], stdin=unzstd.stdout, check=True)
    unzstd.stdout.close()
    if unzstd.wait():
        raise RuntimeError(f"{name}: zstd failed")
    packed.unlink()
    shutil.rmtree(into, ignore_errors=True)
    os.replace(part, into)


def merge(source, target):
    """Moves the files of SOURCE that TARGET lacks (the .NET runtime, the game: versioned, never changed)."""
    target.mkdir(parents=True, exist_ok=True)
    for path in sorted(source.rglob("*")):
        goal = target / path.relative_to(source)
        if path.is_dir():
            goal.mkdir(parents=True, exist_ok=True)
        elif not goal.exists():
            os.replace(path, goal)


def install(spec):
    """The sim at the job's REV and its inputs, under S as on every host (pool/README.md's layout)."""
    rev, key = spec["rev"], spec["inputs"]
    sim, inputs = S / f"sim-{rev}.sh", S / "inputs" / key
    with installing:
        if not sim.exists():
            log(f"downloading the sim {rev}")
            part = S / f"bundle-{rev}"
            fetch(f"sts2-{rev}.tar.zst", part)
            for folder in ("dotnet", "gamebin"):
                merge(part / folder, S / folder)
            shutil.rmtree(S / "builds" / rev, ignore_errors=True)
            (S / "builds").mkdir(exist_ok=True)
            os.replace(part / "builds" / rev, S / "builds" / rev)
            (S / "home").mkdir(exist_ok=True)
            # the launcher last: a copy cut off halfway never looks finished
            os.replace(part / sim.name, sim)
            shutil.rmtree(part)
        if not inputs.exists():
            log(f"downloading the inputs {key}")
            (S / "inputs").mkdir(exist_ok=True)
            fetch(f"inputs-{key}.tar.zst", inputs)
    return sim, inputs


# Colab's /proc/self/cgroup reads 0::/../../jupyter-children, and every .NET 9 segfaults at start on that "..". A cgroup
# namespace of the run's own shows it as /: the run stays in the same cgroup under the same limits, and .NET reads them.
OWN_CGROUP = "grep -w cgroup2 /proc/self/mountinfo | cut -d' ' -f5 | xargs -r -n1 umount; mount -t cgroup2 -o ro none /sys/fs/cgroup; exec \"$@\""


def sim_line(sim, *rest):
    line = [str(sim), *map(str, rest)]
    if "/.." in Path("/proc/self/cgroup").read_text():
        return ["unshare", "-Cm", "--propagation", "private", "bash", "-c", OWN_CGROUP, "sim", *line]
    return line


# --- a chunk

def why_kill(i, job):
    """Why the run of thread I, a chunk of JOB, must end now, or None."""
    if stop.is_set() or i >= wanted["n"]:
        return "cut"
    pool = state["look"]
    spec = pool["jobs"].get(job)
    if spec is None or spec["stopped"]:
        return "stopped"
    step = pool["steps"].get(spec["step"], {})
    if step.get("stopped"):
        return "stopped"
    if step.get("deadline_epoch") is not None and true_now() >= step["deadline_epoch"]:
        return "deadline"
    return None


def play(i, shard, job, index, n, spec):
    """One chunk: (runs finished, why it ended: whole, deadline, stopped, cut, or failed: ...), the work folder."""
    sim, inputs = install(spec)
    work = DIR / "work" / shard
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    rec, out = work / "rec.jsonl", work / "out.txt"
    with out.open("wb") as sink:
        run = subprocess.Popen(
            sim_line(sim, "run", *spec["options"], "--start", index, "--n", n, "--log", work / "log.jsonl", "--records", rec),
            cwd=inputs, stdin=subprocess.DEVNULL, stdout=sink, stderr=subprocess.STDOUT, start_new_session=True,
        )
        (work / "pgid").write_text(f"{run.pid}\n")
        why = None
        while run.poll() is None:
            why = why_kill(i, job)
            if why:
                os.killpg(run.pid, signal.SIGTERM)
                try:
                    run.wait(30)
                except subprocess.TimeoutExpired:
                    os.killpg(run.pid, signal.SIGKILL)
                    run.wait()
                break
            try:
                run.wait(10)
            except subprocess.TimeoutExpired:
                pass
    count = min(len(rec.read_text().splitlines()), n) if rec.exists() else 0
    if count >= n:
        why = "whole"
    elif why is None:
        why = f"failed: run exit {run.returncode} at index {index + count}"
    return count, why, work


def pack(shard, job, index, count, why, work):
    """The chunk into the outbox as one tar, for the sender: its runs, the output with its end line, done- last."""
    stem = f"{shard}-{index}"
    stage = work / "pack"
    stage.mkdir()
    out, ended = work / "out.txt", true_now()
    with out.open("a") as f:
        f.write(f"box: {shard} {index} ended {ended:.3f} {why}\n")
    names, counts, tally = [], [], []
    if count:
        lines = (work / "rec.jsonl").read_text().splitlines(keepends=True)[:count]
        (stage / f"rec-{stem}.jsonl").write_text("".join(lines))
        shutil.copy(work / "log.jsonl", stage / f"log-{stem}.jsonl")
        for k, line in enumerate(lines):
            got = {}
            for m in FIELDS.finditer(line):
                key, value = (m[1], m[2]) if m[1] else (m[3], m[4]) if m[3] else (m[5], m[6])
                got.setdefault(key, value)
            tally.append(f"run {index + k} {got.get('outcome', '?')} {got.get('act', '?')} {got.get('floor', 0)}")
    shutil.copy(out, stage / f"out-{stem}.txt")
    text = out.read_text(errors="replace")
    tally += [f"sum {line}" for line in text.splitlines() if line.startswith(SUMMARY)]
    tally.append(f"end {ended:.3f} {why}")
    for name in sorted(p.name for p in stage.iterdir()):
        lines = (stage / name).read_bytes().count(b"\n")
        counts.append(f"{lines} {name}")
        subprocess.run(["zstd", "-q", "-3", "--rm", str(stage / name)], check=True)
        names.append(name + ".zst")
    (stage / f"counts-{stem}.txt").write_text("".join(c + "\n" for c in counts))
    (stage / f"tally-{stem}.txt").write_text("".join(t + "\n" for t in tally))
    (stage / f"done-{stem}.txt").write_text("".join(f"{index + k}\n" for k in range(count)))
    names += [f"counts-{stem}.txt", f"tally-{stem}.txt", f"done-{stem}.txt"]
    box = DIR / "outbox"
    box.mkdir(exist_ok=True)
    name = f"{time.time_ns()}+{job.replace('/', '+')}+{shard}+{index}.tar"
    with tarfile.open(box / f"{name}.part", "w") as tar:
        for member in names:
            tar.add(stage / member, arcname=member)
    os.replace(box / f"{name}.part", box / name)
    shutil.rmtree(work)


def thread(i):
    """Thread I: chunks until it is cut."""
    shard, last, failed = f"{args.name}-{i}", "", 0
    while True:
        if stop.is_set() or i >= wanted["n"]:
            if last:
                try:
                    deal("/next", shard=shard, last=last, failed=failed, more=0)
                except (OSError, ValueError):
                    pass
            return
        try:
            answer = deal("/next", shard=shard, last=last, failed=failed, more=1, only=args.only)
        except (OSError, ValueError) as e:
            log(f"{shard}: the dealer: {e!r}")
            pause(30)
            continue
        if answer is None:
            last = ""
            for _ in range(6):
                if pause(10) or i >= wanted["n"]:
                    break
            continue
        job, index, n = answer.split()
        index, n, last, failed = int(index), int(n), "", 0
        state["last_work"] = time.time()
        try:
            if job not in state["look"]["jobs"]:
                look()
            spec = state["look"]["jobs"][job]
            state["playing"][shard] = f"{job} {index}"
            count, why, work = play(i, shard, job, index, n, spec)
        except Exception as e:  # the job cannot be played here: give it back, wait, try again
            log(f"{shard}: {job} {index}: {e!r}")
            state["playing"].pop(shard, None)
            try:
                deal("/back", shard=shard, job=job, index=index, why=f"{args.name}: {e!r}"[:200])
            except (OSError, ValueError):
                pass
            pause(60)
            continue
        state["playing"].pop(shard, None)
        state["last_work"] = time.time()
        if why == "cut":
            try:
                deal("/back", shard=shard, job=job, index=index, why=f"{args.name} cut")
            except (OSError, ValueError):
                pass  # the hub takes it back when this host has been silent long enough
            log(f"{shard}: cut in {job} {index}, given back")
            shutil.rmtree(work, ignore_errors=True)
            continue
        pack(shard, job, index, count, why, work)
        with state_lock:
            state["played"] += count
        if why != "whole":
            log(f"{shard}: {job} {index}: {count} of {n} runs, {why}")
        last, failed = f"{job}/{index}", int(why.startswith("failed"))


def sender():
    """Sends the outbox in order, each chunk until the hub takes it."""
    box = DIR / "outbox"
    box.mkdir(exist_ok=True)
    while True:
        files = sorted(p for p in box.glob("*.tar"))
        if not files:
            time.sleep(5)
            continue
        path = files[0]
        _, *job, shard, index = path.stem.split("+")
        try:
            with dist(f"/chunk/{'/'.join(job)}/{shard}/{index}", data=path.read_bytes(), method="PUT", timeout=600):
                pass
            with tarfile.open(path) as tar:
                runs = tar.extractfile(f"done-{shard}-{index}.txt").read().count(b"\n")
            with state_lock:
                state["sent"] += runs
            path.unlink()
        except urllib.error.HTTPError as e:
            if e.code >= 500:
                time.sleep(30)
                continue
            # 409: the hub took the chunk back and deals it to another thread; anything else is kept for a look
            log(f"sending {path.name}: the hub answered {e.code}" + (", taken back: dropped" if e.code == 409 else ", kept in outbox/refused/"))
            if e.code == 409:
                path.unlink()
            else:
                (box / "refused").mkdir(exist_ok=True)
                os.replace(path, box / "refused" / path.name)
        except OSError as e:
            log(f"sending {path.name}: {e!r}, again in 30 s")
            time.sleep(30)


# --- the threads file

def threads_now(path):
    if args.threads is not None:
        return args.threads
    try:
        lines = [line.split("#", 1)[0].strip() for line in path.read_text().splitlines()]
    except OSError:
        return 0
    now = datetime.now(UTC8)
    minute = now.hour * 60 + now.minute
    for line in filter(None, lines):
        m = re.fullmatch(r"(\d\d):(\d\d)-(\d\d):(\d\d)\s+(\d+)", line)
        if m:
            a, b = int(m[1]) * 60 + int(m[2]), int(m[3]) * 60 + int(m[4])
            if (a <= minute < b) if a <= b else (minute >= a or minute < b):
                return int(m[5])
        elif line.isdigit():
            return int(line)
    return 0


def leftovers():
    """Runs a worker killed by SIGKILL left behind: killed, their folders gone."""
    for pgid in (DIR / "work").glob("*/pgid"):
        try:
            group = int(pgid.read_text())
            # only if the group's leader is still one of our runs: a pid may have been used again since
            if b"run" in Path(f"/proc/{group}/cmdline").read_bytes():
                os.killpg(group, signal.SIGKILL)
                log(f"killed the leftover run of {pgid.parent.name}")
        except (OSError, ValueError):
            pass
    shutil.rmtree(DIR / "work", ignore_errors=True)


def main():
    global args, DIR, S, config, context
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--name", required=True)
    p.add_argument("--threads", type=int)
    p.add_argument("--until-empty", action="store_true")
    p.add_argument("--only", default="")
    p.add_argument("--dir", default=str(Path.home() / "sts2" / "pool"))
    args = p.parse_args()
    if not re.fullmatch(r"[A-Za-z0-9._]+", args.name):
        sys.exit("worker: --name is letters, digits, . and _")
    DIR = Path(args.dir).resolve()
    S = DIR.parent
    config = json.loads((DIR / "config.json").read_text())
    context = ssl.create_default_context(cadata=config["cert"])
    context.check_hostname = False
    (DIR / "worker.pid").write_text(f"{os.getpid()}\n")
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda *_: stop.set())
    leftovers()
    wanted["n"] = threads_now(DIR / "threads")
    log(f"started, pid {os.getpid()}, {wanted['n']} threads")
    while True:
        try:
            look(start=True)
            break
        except (OSError, ValueError) as e:
            log(f"first look failed: {e!r}, again in 30 s")
            if pause(30):
                return
    threading.Thread(target=looker, daemon=True).start()
    threading.Thread(target=sender, daemon=True).start()
    threads, said = {}, time.time()
    while not stop.is_set():
        n = threads_now(DIR / "threads")
        if n != wanted["n"]:
            log(f"threads {wanted['n']} -> {n}")
            wanted["n"] = n
        for i in range(n):
            if i not in threads or not threads[i].is_alive():
                threads[i] = threading.Thread(target=thread, args=(i,), daemon=True)
                threads[i].start()
        if time.time() - said >= 600:
            said = time.time()
            log(f"{len(state['playing'])} of {n} threads playing; {state['played']} runs played, {state['sent']} sent, "
                f"{len(list((DIR / 'outbox').glob('*.tar')))} chunks waiting")
        if args.until_empty and time.time() - state["last_work"] > 600 and not state["playing"] and not list((DIR / "outbox").glob("*.tar")):
            log("no work for 10 min and the outbox empty")
            stop.set()
        pause(10)
    for t in threads.values():
        t.join()
    left = time.time() + 300
    while list((DIR / "outbox").glob("*.tar")) and time.time() < left:
        time.sleep(5)
    waiting = len(list((DIR / "outbox").glob("*.tar")))
    log(f"done, {state['played']} runs played, {state['sent']} sent, {waiting} chunks not sent")
    (DIR / "worker.pid").unlink(missing_ok=True)


if __name__ == "__main__":
    main()
