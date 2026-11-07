#!/usr/bin/env python3
"""deal.py STORE PORT: the hub's work pool (pool/README.md). Every worker thread of every host (worker.py)
asks it for its next chunk, and it deals the chunks of every open job. One thread: requests run one at a time and need no
lock among themselves; a file lock per job (JOB/deal/lock) keeps it apart from dist.py taking in a chunk. All state is
files under STORE, so a restart loses nothing.

  pool/token                the pool's token, in every request (dist/pool.json gives it to the workers)
  pool/steps/NAME.json      a step, written by box.py: {"priority", "deadline_epoch" (null: none), "launched",
                            "stopped" (null, or why), "jobs": [JOB, ...]}
  pool/held/SHARD           "JOB INDEX N EPOCH": the chunk the thread SHARD (HOST-i) plays
  pool/seen-HOST            "EPOCH THREADS PLAYING BUSY NCPU": HOST's worker's last look; pool/cpu-HOST.txt, a line a look
  pool/swept-HOST           the look after which HOST went silent and its chunks were taken back
  JOB/deal/job.json         rev, options, chunk, inputs: written by box.py at launch, as is queue.txt ("INDEX N" a chunk)
  JOB/deal/pos              how many lines of queue.txt were dealt
  JOB/deal/front.txt        chunks taken back, dealt before the queue's next, lowest index first
  JOB/deal/stopped          why, once the job is stopped (box.py stop)
  JOB/deal/claims.log       "EPOCH SHARD INDEX N" a chunk dealt, "EPOCH SHARD INDEX N back WHY" a chunk taken back
  JOB/deal/back/SHARD-INDEX a chunk taken back from SHARD: dist.py refuses it from SHARD
  JOB/deal/fails/HOST       HOST's chunks of JOB that failed in a row; at 2, refused/HOST says why JOB is no longer HOST's

The order: open steps (not stopped, deadline not passed) by priority, higher first, then launch; inside a step the job
with the lowest share of its queue dealt (ties: the step's order), skipping a stopped job and one refused to the host.

  GET /next?token&shard=HOST-i&last=JOB/INDEX&failed=0|1&more=1|0[&only=KIND]
      LAST is the chunk the thread finished (empty at first), FAILED whether it ended with fewer runs than dealt by itself
      (not cut, not past the deadline). Answers "JOB INDEX N", or 204 when nothing can be dealt now. When the hub holds
      another chunk for SHARD than LAST, the answer was lost: the same chunk again. MORE=0: the thread ends; nothing is
      dealt. ONLY: deal only jobs of that kind (selfplay, eval, forks).
  GET /back?token&shard&job&index&why   the thread was cut (box.py down, fewer threads): its chunk goes back to the front
  GET /pool?token&host&threads&playing&busy&ncpu&start=0|1
      each worker once a minute: JSON with the hub's clock and every step and job not yet fetched. START=1: the worker has
      just started and plays nothing, so what HOST's threads held goes back.

A chunk goes back to the front of its job's queue when its thread is cut, when its host's worker starts again, and when
the host has not looked for SILENCE s (then every chunk dealt to it with no done- on the hub: in play or not yet sent).
"""
import fcntl
import hmac
import json
import os
import re
import sys
import time
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib.parse import parse_qs, urlparse

JOB = re.compile(r"[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*")
NAME = re.compile(r"[A-Za-z0-9._-]+")
SHARD = re.compile(r"([A-Za-z0-9._]+)-([0-9]+)")
SILENCE = float(os.environ.get("DEAL_SILENCE", 900))  # a test sets it shorter
SWEEP = min(60.0, SILENCE / 5)
STORE = "."
queues = {}  # JOB: the lines of queue.txt, which never change
specs = {}  # JOB: job.json
last_sweep = 0.0


def read(path):
    try:
        with open(path) as f:
            return f.read().strip()
    except FileNotFoundError:
        return None


def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path + ".new", "w") as f:
        f.write(text)
    os.replace(path + ".new", path)


def remove(path):
    try:
        os.remove(path)
    except FileNotFoundError:
        pass


def pool(*parts):
    return os.path.join(STORE, "pool", *parts)


def deal_dir(job, *parts):
    return os.path.join(STORE, job, "deal", *parts)


@contextmanager
def locked(job):
    """The job's lock, shared with dist.py: a chunk is taken back or taken in, never both."""
    with open(deal_dir(job, "lock"), "a") as f:
        fcntl.flock(f, fcntl.LOCK_EX)
        try:
            yield
        finally:
            fcntl.flock(f, fcntl.LOCK_UN)


def steps():
    """Every step not yet fetched, in dealing order: priority, higher first, then launch."""
    found = []
    folder = pool("steps")
    for name in sorted(os.listdir(folder)) if os.path.isdir(folder) else []:
        if name.endswith(".json"):
            try:
                step = json.loads(read(os.path.join(folder, name)))
            except (TypeError, ValueError):
                continue
            step["name"] = name[:-5]
            found.append(step)
    return sorted(found, key=lambda s: (-s.get("priority", 0), s.get("launched", 0), s["name"]))


def is_open(step, now):
    return not step.get("stopped") and (step.get("deadline_epoch") is None or now < step["deadline_epoch"])


def queue(job):
    if job not in queues:
        text = read(deal_dir(job, "queue.txt"))
        if text is None:
            return []
        queues[job] = [line.strip() for line in text.split("\n") if line.strip()]
    return queues[job]


def spec(job):
    if job not in specs:
        text = read(deal_dir(job, "job.json"))
        if text is None:
            return None
        specs[job] = json.loads(text)
    return specs[job]


def front(job):
    text = read(deal_dir(job, "front.txt"))
    return [line for line in (text or "").split("\n") if line.strip()]


def dealt(job):
    """Chunks dealt and not taken back."""
    return int(read(deal_dir(job, "pos")) or 0) - len(front(job))


def has_left(job):
    return bool(front(job)) or int(read(deal_dir(job, "pos")) or 0) < len(queue(job))


def log(job, line):
    with open(deal_dir(job, "claims.log"), "a") as f:
        f.write(f"{time.time():.6f} {line}\n")


def give(job, shard):
    """JOB's next chunk to SHARD as "INDEX N": the front first, then the queue."""
    with locked(job):
        ahead = front(job)
        if ahead:
            chunk = ahead[0]
            write(deal_dir(job, "front.txt"), "".join(line + "\n" for line in ahead[1:]))
        else:
            pos = int(read(deal_dir(job, "pos")) or 0)
            chunk = queue(job)[pos]
            write(deal_dir(job, "pos"), f"{pos + 1}\n")
        index = chunk.split()[0]
        # dealt to SHARD again after being taken back from it: SHARD's copy counts again
        remove(deal_dir(job, "back", f"{shard}-{index}"))
        write(pool("held", shard), f"{job} {chunk} {time.time():.0f}\n")
        log(job, f"{shard} {chunk}")
    return chunk


def take_back(job, shard, index, n, why):
    """A chunk dealt to SHARD goes back to the front of JOB's queue, unless its done- is on the hub already."""
    with locked(job):
        if os.path.exists(os.path.join(STORE, job, f"done-{shard}-{index}.txt")):
            return False
        write(deal_dir(job, "back", f"{shard}-{index}"), why + "\n")
        ahead = sorted({*front(job), f"{index} {n}"}, key=lambda line: int(line.split()[0]))
        write(deal_dir(job, "front.txt"), "".join(line + "\n" for line in ahead))
        log(job, f"{shard} {index} {n} back {why}")
    return True


def take_back_held(shard, why):
    held = read(pool("held", shard))
    if held:
        job, index, n = held.split()[:3]
        if os.path.isdir(deal_dir(job)):
            take_back(job, shard, index, n, why)
        remove(pool("held", shard))


def host_of(shard):
    m = SHARD.fullmatch(shard)
    return m[1] if m else None


def held_by(host):
    folder = pool("held")
    return [s for s in os.listdir(folder) if host_of(s) == host] if os.path.isdir(folder) else []


def owed_by(host):
    """Every chunk dealt to HOST's threads, not taken back since, with no done- on the hub: (job, shard, index, n)."""
    owed = []
    for step in steps():
        for job in step.get("jobs", []):
            state = {}
            for line in (read(deal_dir(job, "claims.log")) or "").split("\n"):
                words = line.split()
                if len(words) >= 4 and host_of(words[1]) == host:
                    state[(words[1], words[2])] = (words[3], len(words) > 4 and words[4] == "back")
            for (shard, index), (n, back) in state.items():
                if not back and not os.path.exists(os.path.join(STORE, job, f"done-{shard}-{index}.txt")):
                    owed.append((job, shard, index, n))
    return owed


def sweep(now):
    """Hosts not heard from for SILENCE s: every chunk they owe goes back, once per silence."""
    folder = pool()
    for name in os.listdir(folder) if os.path.isdir(folder) else []:
        if not name.startswith("seen-"):
            continue
        host = name[5:]
        seen = read(os.path.join(folder, name))
        if not seen or now - float(seen.split()[0]) < SILENCE or read(pool(f"swept-{host}")) == seen:
            continue
        for job, shard, index, n in owed_by(host):
            take_back(job, shard, index, n, f"{host} silent since {seen.split()[0]}")
        for shard in held_by(host):
            remove(pool("held", shard))
        write(pool(f"swept-{host}"), seen + "\n")


def finish(shard, job, index, failed):
    """The thread finished JOB's chunk INDEX: count a failure, or clear the host's failures."""
    host = host_of(shard)
    fails = deal_dir(job, "fails", host)
    if failed:
        count = int(read(fails) or 0) + 1
        write(fails, f"{count}\n")
        if count >= 2:
            write(deal_dir(job, "refused", host), f"{time.time():.0f} {count} chunks in a row ended with fewer runs than dealt, the last at {index}\n")
    elif os.path.exists(fails):
        remove(fails)


def next_chunk(shard, last, failed, more, only):
    now = time.time()
    host = host_of(shard)
    held = read(pool("held", shard))
    if held:
        job, index, n = held.split()[:3]
        if f"{job}/{index}" != last:
            step = next((s for s in steps() if job in s.get("jobs", [])), None)
            if step and is_open(step, now) and not os.path.exists(deal_dir(job, "stopped")):
                return f"{job} {index} {n}"  # its answer was lost
            take_back_held(shard, "its step closed before it was played")
        else:
            remove(pool("held", shard))
            finish(shard, job, index, failed)
    if not more:
        return None
    for step in steps():
        if not is_open(step, now):
            continue
        best = None
        for order, job in enumerate(step.get("jobs", [])):
            if only and job.split("/")[2:3] != [only]:
                continue
            if not os.path.isdir(deal_dir(job)) or os.path.exists(deal_dir(job, "stopped")):
                continue
            if os.path.exists(deal_dir(job, "refused", host)) or not has_left(job):
                continue
            share = dealt(job) / max(len(queue(job)), 1)
            if best is None or share < best[0]:
                best = (share, order, job)
        if best:
            job = best[2]
            return f"{job} {give(job, shard)}"
    return None


def look(host, q):
    """A worker's look: noted, and the pool as it stands."""
    now = time.time()
    line = " ".join(str(q.get(k, "")) or "-" for k in ("threads", "playing", "busy", "ncpu"))
    write(pool(f"seen-{host}"), f"{now:.0f} {line}\n")
    with open(pool(f"cpu-{host}.txt"), "a") as f:
        f.write(f"{now:.0f} {line}\n")
    if q.get("start") == "1":
        for shard in held_by(host):
            take_back_held(shard, f"{host}'s worker started again")
    answer = {"now": time.time(), "steps": {}, "jobs": {}}
    for step in steps():
        answer["steps"][step["name"]] = {k: step.get(k) for k in ("priority", "deadline_epoch", "launched", "stopped")}
        for job in step.get("jobs", []):
            s = spec(job)
            if s is not None:
                answer["jobs"][job] = s | {"step": step["name"], "stopped": read(deal_dir(job, "stopped"))}
    return answer


class Handler(BaseHTTPRequestHandler):
    # a client that stalls must not hold up the others: requests run one at a time
    timeout = 10

    def answer(self, code, body=""):
        data = body.encode()
        self.send_response(code)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        global last_sweep
        url = urlparse(self.path)
        q = {k: v[0] for k, v in parse_qs(url.query, keep_blank_values=True).items()}
        token = read(pool("token"))
        if not token or not hmac.compare_digest(token, q.get("token", "")):
            return self.answer(403)
        now = time.time()
        if now - last_sweep >= SWEEP:
            last_sweep = now
            sweep(now)
        shard = q.get("shard", "")
        if url.path == "/pool":
            host = q.get("host", "")
            if not NAME.fullmatch(host) or "-" in host:
                return self.answer(404)
            return self.answer(200, json.dumps(look(host, q)))
        if not SHARD.fullmatch(shard):
            return self.answer(404)
        if url.path == "/next":
            last = q.get("last", "")
            if last and (not JOB.fullmatch(last) or ".." in last):
                return self.answer(404)
            chunk = next_chunk(shard, last, q.get("failed") == "1", q.get("more", "1") != "0", q.get("only", ""))
            return self.answer(204) if chunk is None else self.answer(200, chunk + "\n")
        if url.path == "/back":
            job, index = q.get("job", ""), q.get("index", "")
            held = (read(pool("held", shard)) or "").split()
            if held[:2] == [job, index]:
                take_back_held(shard, q.get("why", "cut") or "cut")
            return self.answer(200)
        return self.answer(404)

    def log_message(self, *args):
        pass


class Server(HTTPServer):
    request_queue_size = 256  # every thread of every host may ask at once


if __name__ == "__main__":
    STORE = sys.argv[1]
    Server(("", int(sys.argv[2])), Handler).serve_forever()
