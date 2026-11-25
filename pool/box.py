#!/usr/bin/env python3
"""box.py: jobs on the hub's work pool (pool/README.md). Every host runs one worker (worker.py) with a number
of threads, its only knob; the hub (deal.py) deals the chunks of every open job to whichever thread is free.

  box.py up HOST THREADS... [--only KIND]   ships worker.py and its config to HOST and starts it at nice 19, with
    THREADS: a number, or windows 'HH:MM-HH:MM N' in UTC+8 then a number for the rest of the day (one argument a line).
    A worker running there is cut first (box.py down), so its chunks in play go back to the hub. --only KIND: the
    worker takes only that kind of job (selfplay, eval, forks).
  box.py threads HOST THREADS...   rewrites HOST's threads; the worker reads it within 10 s. Fewer threads than in play:
    the extra ones are cut at once, their chunks go back to the hub.
  box.py down HOST   ends HOST's worker now: runs in play killed and their chunks given back, the outbox sent, exit.
  box.py run JOB REV UNTIL PURPOSE RANGE -- RUN-OPTION...
    Opens data/JOB on the pool: RANGE is START:N, run indices into --seeds' file (or `all` of it) or train-seed indices
    without --seeds (a ledger block: BLOCK:100000), cut into chunks of CHUNK runs (default 1), the job's queue on the
    hub. The sim at REV (pushed; built here if needed) and the inputs (the seeds and champion files of git at REV with
    the net files the job reads from weights/all here, or INPUTS=DIR) go to the hub's dist/ once, and every worker takes them on first need. A job belongs to a step, DEADLINE=NAME (default: the job with / as
    -), which has one deadline and one priority. UNTIL: a self-play or forks job's deadline (+90m, +2h, @EPOCH, or
    anything date -d reads); `none` for an eval job, which ends when its queue is used up and every chunk is back.
    Runs in play at the deadline are killed and kept out. PRIORITY=N (default: selfplay 0, eval and forks 1): the hub
    deals the open steps by priority, higher first, then by launch; inside a step, to the job with the lowest share of
    its queue dealt. Environment also: OWNER (default main), AFTER=JOB[,JOB...] leaves out the indices those jobs
    finished (their tally-*.txt here); a failed run is not finished, so its index is played again.
  box.py plan FILE [REV]   a dataset file (eval/datasets/NAME.toml, as it is here or at REV): prints the jobs it opens, with
    their options and seeds, and its runs by environment, character, ascension, game and tier; refuses a file that does
    not hang together. Nothing goes to the hub.
  box.py launch FILE REV [--smoke]   opens the dataset file at REV as one self-play step NAME, every job as box.py run
    would, with the champion file's options at REV under the file's own (a NoSL game's: nosl_champion's); then puts the
    file, its first line rev = REV, in each character's data/CHAR/ENV/selfplay/NAME/dataset.toml of each environment it
    plays, here and on the hub. --smoke instead plays one run per character, row and ascension (the first tier's) from
    +99,000 of each character's seeds, as forks jobs of step NAME-smoke with a 1-hour deadline.
  box.py deadline NAME UNTIL   moves step NAME's deadline, also after it passed while its jobs are not fetched.
  box.py priority NAME N       changes step NAME's priority.
  box.py stop JOB|NAME REASON  stops a job, or every job of a step: runs in play killed within a minute, finished runs kept.
  box.py status [JOB...|NAME...]   from the hub: runs, wins, floors, chunks dealt and held, hosts; default: every step.
  box.py fetch JOB|NAME   waits until the job (or every job of the step) is over and every chunk dealt is back on the
    hub, copies the small files here (done-, tally-, deal/), closes each manifest with the box's numbers, and once the
    whole step is fetched takes it off the hub. May be run again.
  box.py until UNTIL   prints the deadline as epoch seconds.
"""

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import tomllib
from collections import Counter
from datetime import datetime, timedelta, timezone
from pathlib import Path

POOL = Path(__file__).resolve().parent
ROOT = POOL.parent
HUB = POOL / "hub.sh"
ON = POOL / "on.sh"
DIST_ADMIN = POOL / "dist-admin.py"
STEPS = ROOT / "data" / "pool" / "steps"
CONFIG = Path.home() / ".config" / "sts2"
UTC8 = timezone(timedelta(hours=8))
HUB_LOW_G = 10


class Refused(Exception):
    """A request that makes no sense or cannot go on; its message is all the caller needs."""


def say(text: str) -> None:
    print(f"box: {text}", file=sys.stderr)


def output(*cmd, **kw) -> str:
    return subprocess.run([str(c) for c in cmd], check=True, stdout=subprocess.PIPE, text=True, **kw).stdout.strip()


# --- time

def until_epoch(until: str) -> int:
    m = re.fullmatch(r"\+(\d+(?:\.\d+)?)([smh])", until)
    if m:
        return int(time.time() + float(m[1]) * {"s": 1, "m": 60, "h": 3600}[m[2]])
    try:
        return int(output("date", "-d", until, "+%s"))
    except subprocess.CalledProcessError:
        raise Refused(f"the deadline {until} is not a time") from None


def iso_now() -> str:
    return datetime.now().astimezone().isoformat(timespec="seconds")


def iso_utc(epoch: float) -> str:
    return datetime.fromtimestamp(epoch, timezone.utc).isoformat(timespec="seconds")


def when(epoch: float | None) -> str:
    if epoch is None:
        return "no deadline"
    utc, utc8 = datetime.fromtimestamp(epoch, timezone.utc), datetime.fromtimestamp(epoch, UTC8)
    return f"{utc:%Y-%m-%d %H:%M:%S} UTC ({utc8:%m-%d %H:%M} UTC+8)"


# --- the hub

def hub_ssh() -> str:
    # one connection for the many rsyncs of a launch loop
    control = f"-o ControlMaster=auto -o ControlPath={Path.home()}/.ssh/cm-sts2-hub-%C -o ControlPersist=120"
    return output(HUB, "ssh", "local") + " " + control


def hub_addr() -> str:
    return output(HUB, "addr", "local")


def rsync(*args, **kw) -> subprocess.CompletedProcess:
    return subprocess.run(["rsync", "-e", hub_ssh(), *map(str, args)], check=True, **kw)


def to_hub(source: Path, folder: str) -> None:
    """source into the hub's folder, under its own name. Always a folder as the destination: the hub's rrsync (3.5.0)
    hands rsync an existing file as /proc/self/fd/N, which it cannot replace."""
    rsync("-a", "--mkpath", source, f"{hub_addr()}:{folder}/")


def hub_files(folder: str) -> set[str]:
    listing = rsync("--list-only", f"{hub_addr()}:{folder}/", stdout=subprocess.PIPE, text=True).stdout
    return {line.split()[-1] for line in listing.splitlines() if line.strip()}


def on_hub(script: str, *args) -> dict:
    """A python script run on the hub (as root, in /root/sts2-store), whose last line of output is JSON."""
    wrapped = "cd /root/sts2-store && python3 - \"$@\" <<'__py__'\n" + script + "\n__py__\n"
    done = subprocess.run([str(ON), "hub", *map(str, args)], input=wrapped, text=True, stdout=subprocess.PIPE)
    if done.returncode:
        raise Refused(f"the hub's script failed (exit {done.returncode})")
    return json.loads(done.stdout.strip().splitlines()[-1])


def hub_disk() -> str:
    """The hub's free space; the user enlarges its disk on request, so it says when to ask."""
    look = "df -BG --output=avail,size /root/sts2-store | tail -1\n"
    words = subprocess.run([str(ON), "hub"], input=look, text=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL).stdout.split()
    if len(words) != 2:
        return "hub: free space unknown"
    free, size = (int(word.rstrip("G")) for word in words)
    return f"hub: {free} G free of {size} G" + (f"; under {HUB_LOW_G} G, TELL THE USER: they enlarge the disk" if free < HUB_LOW_G else "")


# --- jobs and steps

def job_dir(job: str) -> Path:
    return ROOT / "data" / job


def load(job: str) -> dict:
    path = job_dir(job) / "manifest.json"
    if not path.exists():
        raise Refused(f"data/{job} has no manifest")
    return json.loads(path.read_text())


def save_json(path: Path, value: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    new = path.with_name(path.name + ".new")
    new.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n")
    os.replace(new, path)


def save(job: str, manifest: dict) -> None:
    save_json(job_dir(job) / "manifest.json", manifest)


def step_path(name: str) -> Path:
    return STEPS / f"{name}.json"


def load_step(name: str) -> dict:
    path = step_path(name)
    if not path.exists():
        raise Refused(f"no step {name} here (data/pool/steps/)")
    return json.loads(path.read_text())


def push_step(name: str, step: dict) -> None:
    """The step here, then on the hub: the dealer reads it from there."""
    save_json(step_path(name), step)
    to_hub(step_path(name), "pool/steps")


def jobs_of(names: list[str]) -> list[str]:
    """Each NAME a step's jobs, or a job."""
    jobs = []
    for name in names:
        jobs += load_step(name)["jobs"] if step_path(name).exists() else [name]
    return jobs


def open_steps() -> list[str]:
    return sorted(p.stem for p in STEPS.glob("*.json") if not json.loads(p.read_text()).get("fetched"))


# --- workers

def worker_config() -> str:
    cert = CONFIG / "dist-cert.pem"
    if not cert.exists():
        text = subprocess.run([str(ON), "hub"], input="cat /root/sts2-dist/cert.pem\n", text=True, check=True, stdout=subprocess.PIPE).stdout
        cert.write_text(text[text.index("-----BEGIN"):])
    return json.dumps({"dist": output(HUB, "dist"), "key": (CONFIG / "dist-key").read_text().strip(), "cert": cert.read_text()})


WORKER_ALIVE = r"""
d=$HOME/sts2/pool
alive() { [ -f "$d/worker.pid" ] && grep -qa worker.py "/proc/$(cat "$d/worker.pid")/cmdline" 2> /dev/null; }
# the running worker cut: its runs killed, their chunks given back, its outbox sent (5 min at most)
down() {
  alive || return 0
  kill -TERM "$(cat "$d/worker.pid")"
  for _ in $(seq 420); do alive || return 0; sleep 1; done
  echo "the worker did not end in 7 min" >&2; return 1
}
"""


def up(argv: list[str]) -> None:
    only = ""
    if "--only" in argv:
        at = argv.index("--only")
        only, argv = (argv[at + 1:at + 2] or [""])[0], argv[:at] + argv[at + 2:]
        if only not in ("selfplay", "eval", "forks"):
            raise Refused(f"--only {only}: a kind is selfplay, eval or forks")
    if len(argv) < 2:
        raise SystemExit(__doc__)
    host, threads = argv[0], argv[1:]
    if not re.fullmatch(r"[A-Za-z0-9._]+", host):
        raise Refused(f"{host}: a host's name is letters, digits, . and _")
    worker = (POOL / "worker.py").read_text()
    script = "set -eu\n" + WORKER_ALIVE + f"""
command -v python3 > /dev/null && command -v zstd > /dev/null && command -v tar > /dev/null || {{ echo "{host}: needs python3, zstd, tar" >&2; exit 1; }}
mkdir -p "$HOME/sts2" "$d"; chmod 700 "$HOME/sts2" "$d"
cat > "$d/worker.py.new" <<'__worker__'
{worker}__worker__
(umask 077; cat > "$d/config.json" <<'__config__'
{worker_config()}
__config__
)
cat > "$d/threads" <<'__threads__'
# threads: a number, or HH:MM-HH:MM N windows in UTC+8 and a last line with a number (box.py threads)
{chr(10).join(threads)}
__threads__
down
mv "$d/worker.py.new" "$d/worker.py"
cd "$d"
setsid nohup nice -n 19 python3 worker.py --name {host}{" --only " + only if only else ""} >> worker.log 2>&1 < /dev/null &
sleep 5
alive && echo "{host}: worker $(cat worker.pid) running" || {{ echo "{host}: the worker did not start" >&2; tail -5 worker.log >&2; exit 1; }}
tail -2 worker.log
"""
    subprocess.run([str(ON), host], input=script, text=True, check=True, env=os.environ | {"TIMEOUT": "600"})


def set_threads(argv: list[str]) -> None:
    if len(argv) < 2:
        raise SystemExit(__doc__)
    host, threads = argv[0], argv[1:]
    script = f"""set -eu
cat > "$HOME/sts2/pool/threads" <<'__threads__'
# threads: a number, or HH:MM-HH:MM N windows in UTC+8 and a last line with a number (box.py threads)
{chr(10).join(threads)}
__threads__
echo "{host}: $(grep -v '^#' "$HOME/sts2/pool/threads" | tr '\\n' ';')"
"""
    subprocess.run([str(ON), host], input=script, text=True, check=True)


def down(argv: list[str]) -> None:
    if len(argv) != 1:
        raise SystemExit(__doc__)
    script = "set -eu\n" + WORKER_ALIVE + f"down\necho '{argv[0]}: no worker running'\ntail -2 \"$d/worker.log\" 2> /dev/null || true\n"
    subprocess.run([str(ON), argv[0]], input=script, text=True, check=True, env=os.environ | {"TIMEOUT": "600"})


# --- launching

def queue(start: int, n: int, chunk: int, skip: set[int]) -> list[str]:
    """The chunks in order: runs of up to CHUNK consecutive indices of the range, skip left out, as "INDEX COUNT"."""
    lines, first, count = [], 0, 0
    for i in range(start, start + n):
        if i in skip:
            if count:
                lines.append(f"{first} {count}")
            count = 0
            continue
        if not count:
            first = i
        count += 1
        if count == chunk:
            lines.append(f"{first} {count}")
            count = 0
    if count:
        lines.append(f"{first} {count}")
    return lines


def seeds_in(text: str) -> int:
    return sum(1 for line in text.splitlines() if line.split("#", 1)[0].strip())


def finished_by(jobs: str) -> set[int]:
    """The indices the AFTER jobs finished: the runs with an outcome. A failed run has none to keep, so it is played again."""
    skip = set()
    for job in filter(None, jobs.split(",")):
        files = list(job_dir(job).glob("tally-*.txt"))
        if not files:
            raise Refused(f"{job} has no tally-*.txt here")
        for path in files:
            runs = [line.split() for line in path.read_text().splitlines() if line.startswith("run ")]
            skip.update(int(w[1]) for w in runs if w[2] != "failed")
    return skip


def check_seeds(kind: str, options: list[str]) -> None:
    """Train seeds and test seeds never meet: an eval job plays a seed file of eval/seeds/, every other job the base 1."""
    seeds = options[options.index("--seeds") + 1] if "--seeds" in options else None
    base = options[options.index("--seed") + 1] if "--seed" in options else None
    if kind == "eval":
        if not seeds or not re.fullmatch(r"eval/seeds/[^/]+\.txt", seeds):
            raise Refused("an eval job plays a test seed file, --seeds eval/seeds/NAME.txt")
        if base:
            raise Refused("an eval job takes no --seed: its seeds are the file's")
    else:
        if seeds:
            raise Refused(f"a {kind} job may be trained on, so it plays train seeds, not --seeds {seeds}")
        if base not in (None, "1"):
            raise Refused(f"a {kind} job plays train seeds, base 1, not --seed {base}")


def weights_of(full: str, inputs: str | None, options: list[str]) -> list[str]:
    """The net files a job's run reads, as weights/all/NAME.json: those its options name (--value, --combat-net, --f-net)
    and those of the champion file it names (--champion, read at REV or in INPUTS), its character's own nets
    (`characters`, for the job's --character, run's default SILENT) over the file's options."""
    named = {}
    if "--champion" in options:
        path = options[options.index("--champion") + 1]
        text = (ROOT / inputs / path).read_text() if inputs else output("git", "-C", ROOT, "show", f"{full}:{path}")
        file = json.loads(text)
        named |= file["options"]
        character = options[options.index("--character") + 1] if "--character" in options else "SILENT"
        named |= (file.get("characters") or {}).get(character.upper(), {})
    nets = ("value", "combat-net", "f-net")
    named |= {key: options[options.index(f"--{key}") + 1] for key in nets if f"--{key}" in options}
    return sorted({named[key] for key in nets if key in named})


def put_inputs(full: str, inputs: str | None, have: set[str], options: list[str]) -> str:
    """The inputs on the hub's dist/ as inputs-KEY.tar.zst, unless there already. Without INPUTS: the seeds and champion
    files of git at REV with the net files the job reads from weights/all here (weights/fetch.sh brings them from the
    release), KEY the short REV and those nets' names: a net's name is used once, so it says which weights. With INPUTS:
    that folder's eval/ and weights/, KEY the first 12 hex of the tar's SHA-256."""
    if inputs is None:
        nets = weights_of(full, None, options)
        for net in nets:
            if not re.fullmatch(r"weights/all/[A-Za-z0-9._-]+\.json", net):
                raise Refused(f"{net}: a job's net files are in weights/all, where a name is used once")
            if not (ROOT / net).exists():
                raise Refused(f"no {net} here: weights/fetch.sh {Path(net).stem} brings it from the release")
        key = "-".join([output("git", "-C", ROOT, "rev-parse", "--short", full), *(Path(net).stem for net in nets)])
        if f"inputs-{key}.tar.zst" in have:
            return key
        wanted = ["eval/seeds", "eval/champions"]
        there = [p for p in wanted if not subprocess.run(["git", "-C", ROOT, "cat-file", "-e", f"{full}:{p}"], stderr=subprocess.DEVNULL).returncode]
        with tempfile.TemporaryDirectory(dir=ROOT / "tmp") as t:
            archive = subprocess.run(["git", "-C", ROOT, "archive", full, *there], check=True, stdout=subprocess.PIPE).stdout
            subprocess.run(["tar", "-C", t, "-xf", "-"], input=archive, check=True)
            for net in nets:
                (Path(t) / net).parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(ROOT / net, Path(t) / net)
            tar = subprocess.run(["tar", "-C", t, "--sort=name", "--mtime=@0", "--owner=0", "--group=0", "-cf", "-", *sorted(os.listdir(t))],
                                 check=True, stdout=subprocess.PIPE).stdout
    else:
        tar = subprocess.run(["tar", "-C", ROOT / inputs, "--sort=name", "--mtime=@0", "--owner=0", "--group=0", "-cf", "-", "eval", "weights"],
                             check=True, stdout=subprocess.PIPE).stdout
        key = hashlib.sha256(tar).hexdigest()[:12]
        if f"inputs-{key}.tar.zst" in have:
            return key
    with tempfile.TemporaryDirectory(dir=ROOT / "tmp") as t:
        packed = Path(t) / f"inputs-{key}.tar.zst"
        with packed.open("wb") as f:
            subprocess.run(["zstd", "-q", "-12"], input=tar, stdout=f, check=True)
        to_hub(packed, "dist")
    say(f"inputs {key} on the hub")
    return key


def run(argv: list[str]) -> None:
    if "--" not in argv or argv.index("--") != 5:
        raise SystemExit(__doc__)
    cut = argv.index("--")
    job, rev, until, purpose, spread = argv[:5]
    priority = int(os.environ["PRIORITY"]) if "PRIORITY" in os.environ else None
    open_job(job, rev, until, purpose, spread, argv[cut + 1 :], os.environ.get("DEADLINE"), priority,
             int(os.environ.get("CHUNK", 1)), os.environ.get("INPUTS"), os.environ.get("AFTER"))


def pushed(rev: str) -> str:
    """REV's full hash, once it is on origin/main."""
    full = output("git", "-C", ROOT, "rev-parse", "--verify", f"{rev}^{{commit}}")
    if subprocess.run(["git", "-C", ROOT, "merge-base", "--is-ancestor", full, "origin/main"]).returncode:
        subprocess.run(["git", "-C", ROOT, "fetch", "-q", "origin"], check=True)
        if subprocess.run(["git", "-C", ROOT, "merge-base", "--is-ancestor", full, "origin/main"]).returncode:
            raise Refused(f"{rev} is not on origin/main; push it first")
    return full


def open_job(job: str, rev: str, until: str, purpose: str, spread: str, options: list[str], step_name: str | None,
             priority: int | None, chunk: int = 1, inputs: str | None = None, after: str | None = None) -> None:
    """box.py run: data/JOB on the pool, its queue on the hub, the job last into its step (step_name, default the job
    with / as -), at priority (default by kind) unless the step has its own."""
    full = pushed(rev)
    rev = output("git", "-C", ROOT, "rev-parse", "--short", full)
    parts = job.split("/")
    if len(parts) < 4 or parts[1] not in ("sl", "nosl"):
        raise Refused("JOB is CHAR/ENV/KIND/NAME..., ENV sl or nosl")
    kind = parts[2]
    if kind not in ("selfplay", "eval", "forks"):
        raise Refused("KIND is selfplay, eval or forks")
    check_seeds(kind, options)
    here = job_dir(job)
    if (here / "manifest.json").exists():
        raise Refused(f"data/{job} has a manifest already; a job is launched once")
    if kind == "eval":
        if until != "none":
            raise Refused("an eval job has no deadline (UNTIL none): it ends when its queue is used up")
        deadline = None
    else:
        if until == "none":
            raise Refused(f"a {kind} job has a deadline")
        deadline = until_epoch(until)
        if deadline <= time.time() + 60:
            raise Refused(f"the deadline {until} is not a minute away")
    name = step_name or job.replace("/", "-")
    if not re.fullmatch(r"[A-Za-z0-9._-]+", name):
        raise Refused("DEADLINE is a name: letters, digits, . _ -")
    asked = priority is not None
    priority = priority if asked else 0 if kind == "selfplay" else 1
    step = None
    if step_path(name).exists():
        step = load_step(name)
        if step.get("fetched"):
            raise Refused(f"step {name} is fetched already")
        if step["deadline_epoch"] != deadline:
            raise Refused(f"step {name} has the deadline {when(step['deadline_epoch'])}: give it (box.py deadline moves it)")
        if asked and priority != step["priority"]:
            raise Refused(f"step {name} has priority {step['priority']} (box.py priority changes it)")
        priority = step["priority"]

    seeds = options[options.index("--seeds") + 1] if "--seeds" in options else None
    if spread == "all":
        if not seeds:
            raise Refused("RANGE all needs --seeds")
        text = (ROOT / inputs / seeds).read_text() if inputs else output("git", "-C", ROOT, "show", f"{full}:{seeds}")
        start, n = 0, seeds_in(text)
    else:
        start, n = (int(x) for x in spread.split(":"))
    lines = queue(start, n, chunk, finished_by(after or ""))

    # the sim and the inputs on the hub's dist/, once
    have = hub_files("dist")
    key = put_inputs(full, inputs, have, options)
    if f"sts2-{rev}.tar.zst" not in have:
        subprocess.run([sys.executable, str(DIST_ADMIN), "bundle", rev], check=True)

    deal = here / "deal"
    deal.mkdir(parents=True)
    (deal / "queue.txt").write_text("".join(line + "\n" for line in lines))
    spec = {"job": job, "kind": kind, "rev": rev, "options": options, "chunk": chunk, "inputs": key}
    (deal / "job.json").write_text(json.dumps(spec, indent=1) + "\n")
    to_hub(deal / "queue.txt", f"{job}/deal")
    to_hub(deal / "job.json", f"{job}/deal")
    save(job, {
        "job": job, "kind": kind, "character": parts[0].upper(), "env": parts[1],
        "owner": os.environ.get("OWNER", "main"), "purpose": purpose, "rev": full,
        "game": os.environ.get("GAME", "v0.114.0"), "options": options, "inputs": inputs or "git at rev, nets from weights/all",
        "box": {
            "step": name, "priority": priority,
            "deadline": iso_utc(deadline) if deadline else None, "deadline_epoch": deadline,
            "seeds": seeds, "range": {"start": start, "n": n}, "chunk": chunk, "inputs_key": key,
            "after": after.split(",") if after else None,
        },
        "launched": iso_now(), "status": "running",
    })
    # the job last into its step: the dealer never sees a job without its queue
    step = step or {"priority": priority, "deadline_epoch": deadline, "launched": time.time(), "stopped": None, "jobs": []}
    step["jobs"].append(job)
    push_step(name, step)
    say(f"data/{job}: {len(lines)} chunks, step {name} (priority {priority}, {when(deadline)}), {len(step['jobs'])} jobs in it")


# --- datasets: a self-play step written as one TOML file (eval/datasets/NAME.toml)

CHARACTERS = ["SILENT", "IRONCLAD", "DEFECT", "REGENT", "NECROBINDER", "FOOL"]  # the ledger's order: character k from seeds + 100000 k
PER_CHARACTER, SMOKE_AT = 100000, 99000  # a character's block of seeds; the last 1,000 are the smoke's
EFFORTS = ("low", "medium", "high", "xhigh", "max")  # Budget.Efforts, cheapest first (xhigh: max with an early stop)
ROOMS = {"monster": "effort", "elite": "elite-effort", "boss": "boss-effort"}  # a game's effort by room, as run's options
DATASET_KEYS = {"rev", "name", "deadline", "priority", "seeds", "characters", "champion", "nosl_champion", "workers", "explore", "games",
                "runs", "families"}  # fmt: skip
ENVS = {"sl": "SL", "nosl": "NoSL"}  # a game's env, as a job's folder and as run's --env


def git_text(rev: str | None, path: str) -> str:
    """A file of this checkout as it is, or as it is at REV."""
    if rev is None:
        return (ROOT / path).read_text()
    try:
        return output("git", "-C", ROOT, "show", f"{rev}:{path}", stderr=subprocess.DEVNULL)
    except subprocess.CalledProcessError:
        raise Refused(f"{path} is not in git at {rev}") from None


def tier(t: dict, where: str, families: dict[str, list[str]]) -> tuple[float, float, tuple[str, ...], float, tuple[str, ...], tuple[str, ...], tuple[str, ...]]:
    """An exploration tier, (epsilon, tau, favor, favor_by, refuse, keep, upgrade_first): epsilon and tau each a whole
    number of hundredths, as job names show them; favor, refuse, keep and upgrade_first card families (the file's
    [families]): l0 weighs each afterstate with a bonus of favor_by in log V for each favored card it adds, never lets a
    refused one in nor a kept one out, and an upgrade's prompt takes a card to upgrade first first (run's --favor,
    --favor-by, --refuse, --keep and --upgrade-first; plans/2026-11-20-1231-silent-shiv-poison.md,
    plans/2026-11-23-1545-sl-regent-g0.md), as the job's name ends -fA.B-bBBB-rC-kD-uE."""
    if not isinstance(t, dict) or set(t) - {"epsilon", "tau", "favor", "favor_by", "refuse", "keep", "upgrade_first"} or "epsilon" not in t:
        raise Refused(f"{where}: a tier is {{ epsilon = E }} or {{ epsilon = E, tau = T }}, and may have favor = [families] with favor_by = B, "
                      "refuse = [families], keep = [families] and upgrade_first = [families]")
    eps, tau, by = float(t["epsilon"]), float(t.get("tau", 0)), float(t.get("favor_by", 0))
    if not 0 <= eps <= 1 or tau < 0:
        raise Refused(f"{where}: epsilon is from 0 to 1, tau from 0")
    if any(abs(x * 100 - round(x * 100)) > 1e-9 for x in (eps, tau, by)):
        raise Refused(f"{where}: epsilon, tau and favor_by are whole hundredths (job names show them as eEEE, tTTT and bBBB)")
    favor, refuse, keep, first = (tuple(t.get(key, [])) for key in ("favor", "refuse", "keep", "upgrade_first"))
    if bool(favor) != (by > 0):
        raise Refused(f"{where}: favor goes with favor_by, above 0")
    if unknown := [f for f in favor + refuse + keep + first if f not in families]:
        raise Refused(f"{where}: no family {unknown[0]} in [families]")
    if set(favor) & set(refuse) or any(len(set(x)) != len(x) for x in (favor, refuse, keep, first)):
        raise Refused(f"{where}: a family is favored or refused once, not both")
    if set(keep) & set(refuse):
        raise Refused(f"{where}: a family is kept or refused, not both")
    return eps, tau, favor, by, refuse, keep, first


def families_of(spec: dict, path: str) -> dict[str, list[str]]:
    """The file's [families]: each a name (lower-case letters) and its card ids, in no other family."""
    families = spec.get("families", {})
    seen = Counter()
    for name, ids in families.items():
        if not re.fullmatch(r"[a-z]+", name) or not isinstance(ids, list) or not ids or any(not re.fullmatch(r"[A-Z0-9_]+", i) for i in ids):
            raise Refused(f"{path}: family {name} is lower-case letters, its cards a list of card ids")
        seen.update(set(ids))
    if twice := [i for i, n in seen.items() if n > 1]:
        raise Refused(f"{path}: {twice[0]} is in two families")
    return families


def dataset_jobs(spec: dict, path: str, champion: dict, nosl_champion: dict | None = None) -> list[dict]:
    """The file's jobs, each {job, env, character, asc, game, eps, tau, start, n, options}, in the file's order per
    character. A game is SL's (plan's, with its effort by room), or NoSL's with env = "nosl" (pimc's, nosl_champion's,
    whose budget is its worlds and nodes: no effort); SL's and NoSL's rows take a character's seeds in one block, in the
    file's order. Refuses what the file cannot mean."""
    if unknown := set(spec) - DATASET_KEYS:
        raise Refused(f"{path}: no key {', '.join(sorted(unknown))} (keys: {', '.join(sorted(DATASET_KEYS))})")
    for key in ("name", "deadline", "seeds", "characters", "champion", "games", "runs"):
        if key not in spec:
            raise Refused(f"{path}: {key} is missing")
    name = spec["name"]
    if not re.fullmatch(r"[a-z0-9][a-z0-9.-]*", name):
        raise Refused(f"{path}: name {name} is lower-case letters, digits, . and -")
    if not isinstance(spec["seeds"], int) or spec["seeds"] < 0 or spec["seeds"] % 100000:
        raise Refused(f"{path}: seeds is a ledger block's start, a multiple of 100,000")
    characters = spec["characters"]
    if not characters or set(characters) - set(CHARACTERS) or len(set(characters)) != len(characters):
        raise Refused(f"{path}: characters are some of {', '.join(CHARACTERS)}, each once")
    games = {}
    for game, g in spec["games"].items():
        if not re.fullmatch(r"[a-z0-9]+", game):
            raise Refused(f"{path}: game {game}'s name is lower-case letters and digits")
        env = g.get("env", "sl")
        if env not in ENVS:
            raise Refused(f"{path}: game {game}: env is nosl, or left out (SL)")
        if env == "nosl":
            if set(g) - {"mode"} != {"env", "auto_win"}:
                raise Refused(f"{path}: game {game} of NoSL has env = \"nosl\" and auto_win = [acts], and may have mode; no effort (its budget is nosl_champion's)")
        elif set(g) - {"mode", "env"} != {"auto_win", "effort"} or set(g["effort"]) != set(ROOMS):
            raise Refused(f"{path}: game {game} has auto_win = [acts] and effort = {{ monster, elite, boss }}, and may have mode")
        if any(a not in (1, 2, 3) for a in g["auto_win"]) or any(e not in EFFORTS for e in g.get("effort", {}).values()):
            raise Refused(f"{path}: game {game}: auto_win's acts are 1 to 3, an effort one of {', '.join(EFFORTS)}")
        if g.get("mode", "dread") != "dread":
            raise Refused(f"{path}: game {game}: mode is dread (the Dread mode) or left out (the standard game)")
        games[game] = g
    families = families_of(spec, path)
    tiers = [tier(t, f"{path}: explore", families) for t in spec.get("explore", [])]
    workers = spec.get("workers")
    base = dict(champion["options"])
    if base.get("env") != "SL" or base.get("policy") != "plan" or base.get("macro") != "l0":
        raise Refused(f"{spec['champion']} is not an SL champion of plan and l0")
    bases = {"sl": base}
    if any(g.get("env") == "nosl" for g in games.values()):
        if nosl_champion is None:
            raise Refused(f"{path}: a NoSL game needs nosl_champion")
        bases["nosl"] = dict(nosl_champion["options"])
        if bases["nosl"].get("env") != "NoSL" or bases["nosl"].get("policy") != "pimc" or bases["nosl"].get("macro") != "l0":
            raise Refused(f"{spec['nosl_champion']} is not a NoSL champion of pimc and l0")
    elif "nosl_champion" in spec:
        raise Refused(f"{path}: nosl_champion with no NoSL game")
    jobs, used = [], Counter()
    for k, row in enumerate(spec["runs"]):
        where = f"{path}: runs[{k}]"
        if set(row) - {"game", "asc", "n", "characters", "explore"} or not {"game", "asc", "n"} <= set(row):
            raise Refused(f"{where} has game, asc and n, and may have characters and explore")
        if row["game"] not in games:
            raise Refused(f"{where}: no game {row['game']} in [games]")
        ascs, ns = ([row["asc"]], [row["n"]]) if isinstance(row["asc"], int) else (row["asc"], row["n"])
        if not isinstance(ns, list) or len(ascs) != len(ns) or any(not isinstance(a, int) or not 0 <= a <= 10 for a in ascs) \
                or any(not isinstance(x, int) or x < 1 for x in ns):
            raise Refused(f"{where}: asc and n are one number each, or two lists of the same length; ascensions 0 to 10, n at least 1")
        mine = row.get("characters", characters)
        if set(mine) - set(characters):
            raise Refused(f"{where}: characters {', '.join(sorted(set(mine) - set(characters)))} are not the file's")
        row_tiers = [tier(t, f"{where}: explore", families) for t in row["explore"]] if "explore" in row else tiers
        if not row_tiers:
            raise Refused(f"{where}: no exploration tier, in the row or the file")
        g = games[row["game"]]
        env = g.get("env", "sl")
        if g.get("mode") and any(a != 10 for a in ascs):
            raise Refused(f"{where}: game {row['game']} is the Dread mode, which the game plays at ascension 10 only")
        for character in mine:
            for asc, n in zip(ascs, ns):
                for j, (eps, tau, favor, by, refuse, keep, first) in enumerate(row_tiers):
                    count = n // len(row_tiers) + (j < n % len(row_tiers))
                    if count == 0:
                        continue
                    job = f"{character.lower()}/{env}/selfplay/{name}/a{asc:02d}-{row['game']}-e{round(eps * 100):03d}" + (f"-t{round(tau * 100):03d}" if tau else "") \
                        + lock_suffix(favor, by, refuse, keep, first)
                    effort = {ROOMS[room]: e for room, e in g.get("effort", {}).items()}
                    options = bases[env] | effort | {"character": character, "asc": str(asc)}
                    options |= {"workers": str(workers)} if workers else {}
                    options |= {"explore": f"{eps:g}"} if eps else {}
                    options |= {"macro-explore": f"{tau:g}"} if tau else {}
                    options |= {"favor": ",".join(i for f in favor for i in families[f]), "favor-by": f"{by:g}"} if favor else {}
                    options |= {"refuse": ",".join(i for f in refuse for i in families[f])} if refuse else {}
                    options |= {"keep": ",".join(i for f in keep for i in families[f])} if keep else {}
                    options |= {"upgrade-first": ",".join(i for f in first for i in families[f])} if first else {}
                    options |= {"auto-win": ",".join(map(str, sorted(g["auto_win"])))} if g["auto_win"] else {}
                    options |= {"mode": g["mode"]} if g.get("mode") else {}
                    # NoSL self-play plays every event option out on the node's clone for V's training (run --event-lines)
                    options |= {"event-lines": "true"} if env == "nosl" else {}
                    start = spec["seeds"] + PER_CHARACTER * CHARACTERS.index(character) + used[character]
                    used[character] += count
                    jobs.append({"job": job, "env": env, "character": character, "asc": asc, "game": row["game"], "eps": eps, "tau": tau,
                                 "favor": favor, "favor_by": by, "refuse": refuse, "keep": keep, "upgrade_first": first, "start": start, "n": count, "options": [x for kv in options.items() for x in ("--" + kv[0], kv[1])] + ["--seed", "1"]})
    if over := [c for c, n in used.items() if n > SMOKE_AT]:
        raise Refused(f"{path}: {', '.join(over)} need more than {SMOKE_AT:,} seeds ({', '.join(f'{used[c]:,}' for c in over)})")
    if twice := [j for j, n in Counter(j["job"] for j in jobs).items() if n > 1]:
        raise Refused(f"{path}: rows repeat the job {twice[0]} (the same character, ascension, game and tier): make them one row")
    return jobs


def read_dataset(path: str, rev: str | None) -> tuple[dict, str, list[dict]]:
    """The file (here, or at REV), its text, and its jobs with the champion file's options at the same place."""
    text = git_text(rev, path)
    try:
        spec = tomllib.loads(text)
    except tomllib.TOMLDecodeError as e:
        raise Refused(f"{path}: {e}") from None
    champion = json.loads(git_text(rev, spec.get("champion", "eval/champions/sl.json")))
    nosl = json.loads(git_text(rev, spec["nosl_champion"])) if "nosl_champion" in spec else None
    return spec, text, dataset_jobs(spec, path, champion, nosl)


def lock_suffix(favor: tuple[str, ...], by: float, refuse: tuple[str, ...], keep: tuple[str, ...], first: tuple[str, ...]) -> str:
    """A card lock as a job's name ends: -fA.B-bBBB-rC-kD-uE (the bonus in hundredths), or nothing."""
    return ((f"-f{'.'.join(favor)}-b{round(by * 100):03d}" if favor else "") + (f"-r{'.'.join(refuse)}" if refuse else "")
            + (f"-k{'.'.join(keep)}" if keep else "") + (f"-u{'.'.join(first)}" if first else ""))


def lock_text(j: dict) -> str:
    """A job's card lock as the plan shows it: " favor A.B by 0.2 refuse C keep D upgrade first E", or nothing."""
    return ((f" favor {'.'.join(j['favor'])} by {j['favor_by']:g}" if j["favor"] else "") + (f" refuse {'.'.join(j['refuse'])}" if j["refuse"] else "")
            + (f" keep {'.'.join(j['keep'])}" if j["keep"] else "") + (f" upgrade first {'.'.join(j['upgrade_first'])}" if j["upgrade_first"] else ""))


def show_dataset(spec: dict, jobs: list[dict]) -> None:
    for j in jobs:
        print(f"{j['job']}  {j['start']}:{j['n']}  {' '.join(j['options'])}")
    total = sum(j["n"] for j in jobs)
    print(f"\n{spec['name']}: {len(jobs)} jobs, {total:,} runs; step {spec['name']}, deadline {spec['deadline']}, priority {spec.get('priority', 0)}")
    for title, key in (("environment", lambda j: ENVS[j["env"]]), ("character", lambda j: j["character"]),
                       ("ascension", lambda j: f"A{j['asc']}"), ("game", lambda j: j["game"]),
                       ("tier", lambda j: f"eps {j['eps']:g}" + (f" tau {j['tau']:g}" if j["tau"] else "") + lock_text(j))):
        counts = Counter()
        for j in jobs:
            counts[key(j)] += j["n"]
        items = sorted(counts.items(), key=lambda kv: int(kv[0][1:])) if title == "ascension" else counts.items()
        print(f"  by {title}: " + ", ".join(f"{k} {n:,} ({100 * n / total:.1f} %)" for k, n in items))
    for character in CHARACTERS:
        mine = [j for j in jobs if j["character"] == character]
        if mine:
            first, last = mine[0]["start"], mine[-1]["start"] + mine[-1]["n"] - 1
            print(f"  {character}: seeds {first}-{last} ({last - first + 1:,} of {SMOKE_AT:,}), smoke from {spec['seeds'] + PER_CHARACTER * CHARACTERS.index(character) + SMOKE_AT}")


def plan_dataset(argv: list[str]) -> None:
    if len(argv) not in (1, 2):
        raise SystemExit(__doc__)
    spec, _, jobs = read_dataset(argv[0], argv[1] if len(argv) == 2 else None)
    show_dataset(spec, jobs)


def launch_dataset(argv: list[str]) -> None:
    smoke = "--smoke" in argv
    argv = [a for a in argv if a != "--smoke"]
    if len(argv) != 2:
        raise SystemExit(__doc__)
    path, rev = argv
    full = pushed(rev)
    spec, text, jobs = read_dataset(path, full)
    name = spec["name"]
    if smoke:
        # one run per character, row and ascension, the first tier's, and one more for each other card lock its tiers
        # have, from the smoke's seeds
        firsts, at = {}, Counter()
        for j in jobs:
            firsts.setdefault((j["character"], j["asc"], j["game"], j["favor"], j["favor_by"], j["refuse"], j["keep"], j["upgrade_first"]), j)
        jobs = []
        for (character, asc, game, favor, by, refuse, keep, first), j in firsts.items():
            start = spec["seeds"] + PER_CHARACTER * CHARACTERS.index(character) + SMOKE_AT + at[character]
            at[character] += 1
            jobs.append(j | {"job": f"{character.lower()}/{j['env']}/forks/{name}-smoke/a{asc:02d}-{game}{lock_suffix(favor, by, refuse, keep, first)}",
                             "start": start, "n": 1})
        step, until, priority = f"{name}-smoke", until_epoch("+1h"), None
    else:
        step, until, priority = name, until_epoch(spec["deadline"]), spec.get("priority", 0)
    if there := [j["job"] for j in jobs if (job_dir(j["job"]) / "manifest.json").exists()]:
        raise Refused(f"data/{there[0]} has a manifest already ({len(there)} jobs of the file do)")
    show_dataset(spec, jobs)
    for j in jobs:
        purpose = (f"{name}{' smoke' if smoke else ''}: {ENVS[j['env']]} self-play, {j['character']} A{j['asc']}, game {j['game']}, "
                   f"explore eps {j['eps']:g} tau {j['tau']:g}{lock_text(j)}, from {path}")
        open_job(j["job"], full, f"@{until}", purpose, f"{j['start']}:{j['n']}", j["options"], step, priority)
    if smoke:
        return
    # the file beside the data, with the commit it was launched at
    stamped = f'rev = "{full}"\n' + text
    for character, env in sorted({(j["character"], j["env"]) for j in jobs}):
        folder = f"{character.lower()}/{env}/selfplay/{name}"
        (ROOT / "data" / folder).mkdir(parents=True, exist_ok=True)
        (ROOT / "data" / folder / "dataset.toml").write_text(stamped)
        to_hub(ROOT / "data" / folder / "dataset.toml", folder)
    say(f"dataset {name}: {len(jobs)} jobs in step {step}; its file in each character's data/CHAR/ENV/selfplay/{name}/dataset.toml, here and on the hub")

# --- changing a step or job

def move_deadline(argv: list[str]) -> None:
    if len(argv) != 2:
        raise SystemExit(__doc__)
    name, to = argv[0], until_epoch(argv[1])
    step = load_step(name)
    if step.get("fetched"):
        raise Refused(f"step {name} is fetched")
    if step["deadline_epoch"] is None:
        raise Refused(f"step {name} has no deadline (eval); box.py stop ends it")
    if to <= time.time():
        raise Refused(f"{argv[1]} has passed; box.py stop {name} ends the step now")
    at, before = iso_now(), step["deadline_epoch"]
    step["deadline_epoch"] = to
    push_step(name, step)
    for job in step["jobs"]:
        m = load(job)
        box = m["box"]
        box.setdefault("deadline_changes", []).append({"at": at, "from": iso_utc(before), "to": iso_utc(to)})
        box["deadline"], box["deadline_epoch"] = iso_utc(to), to
        save(job, m)
    say(f"step {name} ({len(step['jobs'])} jobs) now until {when(to)}")


def set_priority(argv: list[str]) -> None:
    if len(argv) != 2 or not re.fullmatch(r"-?\d+", argv[1]):
        raise SystemExit(__doc__)
    step = load_step(argv[0])
    step["priority"] = int(argv[1])
    push_step(argv[0], step)
    for job in step["jobs"]:
        m = load(job)
        m["box"]["priority"] = int(argv[1])
        save(job, m)
    say(f"step {argv[0]}: priority {argv[1]}")


def stop(argv: list[str]) -> None:
    if len(argv) != 2:
        raise SystemExit(__doc__)
    name, reason = argv
    at = iso_now()
    if step_path(name).exists():
        step = load_step(name)
        step["stopped"] = f"{at} {reason}"
        push_step(name, step)
        jobs = step["jobs"]
    else:
        load(name)
        path = job_dir(name) / "deal" / "stopped"
        path.write_text(f"{at} {reason}\n")
        to_hub(path, f"{name}/deal")
        jobs = [name]
    for job in jobs:
        m = load(job)
        m["stop_reason"] = reason
        save(job, m)
    say(f"stopped {name} ({len(jobs)} jobs): runs in play end within about a minute")


# --- looking at the pool

STATUS = r"""
import glob, json, os, sys, time
from collections import Counter
now = time.time()
def read(p):
    try:
        return open(p).read().strip()
    except OSError:
        return None
jobs = [j for j in sys.argv[1:] if j]  # on.sh passes '' when given no argument
steps = {}
for path in glob.glob("pool/steps/*.json"):
    step = json.load(open(path))
    steps[os.path.basename(path)[:-5]] = step
if not jobs:
    jobs = [j for s in steps.values() for j in s["jobs"]]
held = Counter()
for path in glob.glob("pool/held/*"):
    words = (read(path) or "").split()
    if words:
        held[(words[0], os.path.basename(path).rsplit("-", 1)[0])] += 1
out = {"now": now, "steps": steps, "jobs": {}, "hosts": {}}
for job in jobs:
    d = job + "/deal"
    queue = [l for l in (read(d + "/queue.txt") or "").split("\n") if l.strip()]
    front = [l for l in (read(d + "/front.txt") or "").split("\n") if l.strip()]
    j = {"queue": len(queue), "pos": int(read(d + "/pos") or 0), "front": len(front), "stopped": read(d + "/stopped"),
         "refused": {os.path.basename(p): read(p) for p in glob.glob(d + "/refused/*")},
         "held": {h: n for (jj, h), n in held.items() if jj == job},
         "runs": 0, "won": 0, "failed": 0, "floors": 0, "died": Counter(), "sums": Counter(), "chunks": 0, "empty": 0,
         "ends": Counter(), "hour": 0, "back": 0}
    for line in (read(d + "/claims.log") or "").split("\n"):
        if " back " in line:
            j["back"] += 1
    for path in glob.glob(job + "/tally-*.txt"):
        j["chunks"] += 1
        runs = 0
        for line in open(path):
            w = line.split()
            if w[0] == "run":
                runs += 1
                j["failed"] += w[2] == "failed"
                if w[2] == "won":
                    j["won"] += 1
                else:
                    j["died"][w[3]] += 1
                j["floors"] += int(w[4]) if w[4].isdigit() else 0
            elif w[0] == "sum":
                for part in line[4:].strip().split("; "):
                    k, _, v = part.partition(": ")
                    if v.strip().lstrip("-").isdigit():
                        j["sums"][k] += int(v)
            elif w[0] == "end":
                j["ends"][w[2].rstrip(":")] += 1
        j["runs"] += runs
        j["empty"] += runs == 0
        if now - os.path.getmtime(path) < 3600:
            j["hour"] += runs
    out["jobs"][job] = j
for path in glob.glob("pool/seen-*"):
    out["hosts"][os.path.basename(path)[5:]] = read(path)
st = os.statvfs(".")
out["free_g"] = st.f_bavail * st.f_frsize / 2**30
out["size_g"] = st.f_blocks * st.f_frsize / 2**30
print(json.dumps(out))
"""


def group_key(job: str) -> tuple[str, str]:
    """A step's rows when it has many jobs: by character and ascension (an aNN part of the name, as v9g2's), with a
    dataset's game after it (aNN-GAME-eEEE), else the name's first part."""
    parts = job.split("/")
    words = parts[-1].split("-")
    at = next((i for i, w in enumerate(words) if re.fullmatch(r"a\d\d", w)), None)
    if at is None:
        return parts[0].upper(), words[0]
    game = words[at + 1] if at + 1 < len(words) and not re.fullmatch(r"e\d{3}", words[at + 1]) else None
    return parts[0].upper(), words[at] + (f"-{game}" if game else "")


def status(argv: list[str]) -> None:
    jobs = jobs_of(argv) if argv else []
    look = on_hub(STATUS, *jobs)
    now = look["now"]
    shown = jobs or list(look["jobs"])
    for name, step in sorted(look["steps"].items(), key=lambda s: (-s[1]["priority"], s[1]["launched"])):
        mine = [j for j in step["jobs"] if j in shown]
        if not mine:
            continue
        open_ = not step.get("stopped") and (step["deadline_epoch"] is None or now < step["deadline_epoch"])
        left = f", {(step['deadline_epoch'] - now) / 3600:.1f} h left" if step["deadline_epoch"] and open_ else ""
        print(f"step {name}: priority {step['priority']}, {when(step['deadline_epoch'])}{left}, "
              f"{'open' if open_ else 'stopped: ' + step['stopped'] if step.get('stopped') else 'closed'}, {len(step['jobs'])} jobs")
        rows = {}
        for job in mine:
            key = group_key(job) if len(mine) > 20 else (job,)
            rows.setdefault(key, []).append(look["jobs"][job])
        total = Counter()
        for key, js in rows.items():
            runs, won, floors = sum(j["runs"] for j in js), sum(j["won"] for j in js), sum(j["floors"] for j in js)
            dealt, queue = sum(j["pos"] - j["front"] for j in js), sum(j["queue"] for j in js)
            held = sum(sum(j["held"].values()) for j in js)
            hour = sum(j["hour"] for j in js)
            sums = sum((Counter(j["sums"]) for j in js), Counter())
            # A failed run (an error in the driver or a brain) counts among the runs but is dropped by every reader.
            sums["failed"] = sum(j.get("failed", 0) for j in js)
            bad = "; ".join(f"{k}: {v}" for k, v in sums.items() if v)
            total.update({"runs": runs, "won": won, "floors": floors, "dealt": dealt, "queue": queue, "held": held, "hour": hour})
            extra = []
            for j in js:
                if j["stopped"]:
                    extra.append(f"stopped {j['stopped']}")
                for h, why in j["refused"].items():
                    extra.append(f"refused {h}: {why}")
            print(f"  {' '.join(key):<44} {runs:>6} runs {won:>5} won ({100 * won / max(runs, 1):5.1f} %) floor {floors / max(runs, 1):4.1f}"
                  f"  dealt {dealt}/{queue}, {held} in play, {hour} in the last hour" + (f"  {bad}" if bad else "") + "".join(f"\n    {e}" for e in extra))
        if len(rows) > 1:
            print(f"  {'all':<44} {total['runs']:>6} runs {total['won']:>5} won ({100 * total['won'] / max(total['runs'], 1):5.1f} %) "
                  f"floor {total['floors'] / max(total['runs'], 1):4.1f}  dealt {total['dealt']}/{total['queue']}, {total['held']} in play, {total['hour']} in the last hour")
    print("hosts (threads, playing, busy CPUs of all users / CPUs, last look):")
    for host, seen in sorted(look["hosts"].items()):
        t, threads, playing, busy, ncpu = (seen.split() + ["-"] * 5)[:5]
        age = now - float(t)
        if (age > 86400 and threads in ("0", "-")) or (host.startswith("nb") and age > 3600):
            continue  # a host long idle, a notebook long gone
        print(f"  {host:<12} {threads:>3} threads, {playing:>3} playing, {busy:>5} / {ncpu:<3} busy, {age:5.0f} s ago" + ("  SILENT" if age > 900 else ""))
    print(f"hub: {look['free_g']:.0f} G free of {look['size_g']:.0f} G" + (f"; under {HUB_LOW_G} G, TELL THE USER: they enlarge the disk" if look["free_g"] < HUB_LOW_G else ""))


# --- collecting

OVER = r"""
import glob, json, os, sys, time
now = time.time()
def read(p):
    try:
        return open(p).read().strip()
    except OSError:
        return None
steps = {os.path.basename(p)[:-5]: json.load(open(p)) for p in glob.glob("pool/steps/*.json")}
out = {"now": now, "jobs": {}, "seen": {os.path.basename(p)[5:]: read(p) for p in glob.glob("pool/seen-*")}}
for job in [j for j in sys.argv[1:] if j]:
    d = job + "/deal"
    step = next((s for s in steps.values() if job in s["jobs"]), None)
    queue = len([l for l in (read(d + "/queue.txt") or "").split("\n") if l.strip()])
    front = len([l for l in (read(d + "/front.txt") or "").split("\n") if l.strip()])
    pos = int(read(d + "/pos") or 0)
    ended = None
    if read(d + "/stopped") or (step and step.get("stopped")):
        ended = "stopped"
    elif step and step["deadline_epoch"] is not None and now >= step["deadline_epoch"]:
        ended = "deadline"
    elif not front and pos >= queue:
        ended = "used up"
    state = {}
    for line in (read(d + "/claims.log") or "").split("\n"):
        w = line.split()
        if len(w) >= 4:
            state[(w[1], w[2])] = len(w) > 4 and w[4] == "back"
    missing = [f"{s} {i}" for (s, i), back in state.items() if not back and not os.path.exists(f"{job}/done-{s}-{i}.txt")]
    out["jobs"][job] = {"ended": ended, "missing": missing}
print(json.dumps(out))
"""


def copy_small_files(jobs: list[str]) -> None:
    """The small files of the jobs on the hub, here: done-, tally-, deal/; one rsync for them all."""
    rules = []
    for job in jobs:
        parts = job.split("/")
        rules += [f"+ /{'/'.join(parts[:k])}/" for k in range(1, len(parts) + 1)]
        rules += [f"+ /{job}/done-*", f"+ /{job}/tally-*", f"+ /{job}/deal/***"]
    with tempfile.NamedTemporaryFile("w", dir=ROOT / "tmp", suffix=".rules") as f:
        f.write("\n".join(dict.fromkeys(rules)) + "\n- *\n")
        f.flush()
        rsync("-a", f"--filter=merge {f.name}", f"{hub_addr()}:", f"{ROOT / 'data'}/")


def box_numbers(job: str, manifest: dict) -> dict:
    """The box's numbers from the small files here."""
    here, box = job_dir(job), manifest["box"]
    done, hosts, ends, sums, deadline_ends = set(), {}, Counter(), Counter(), []
    won = failed = floors = runs = 0
    for path in here.glob("done-*.txt"):
        shard, index = path.stem[5:].rsplit("-", 1)
        host = shard.rsplit("-", 1)[0]
        indices = [int(x) for x in path.read_text().split()]
        done.update(indices)
        h = hosts.setdefault(host, {"chunks": 0, "runs": 0})
        h["chunks"] += 1
        h["runs"] += len(indices)
    for path in here.glob("tally-*.txt"):
        for line in path.read_text().splitlines():
            w = line.split()
            if w[0] == "run":
                runs += 1
                won += w[2] == "won"
                failed += w[2] == "failed"
                floors += int(w[4]) if w[4].isdigit() else 0
            elif w[0] == "end":
                why = w[2].rstrip(":")
                ends[why] += 1
                if why == "deadline":
                    deadline_ends.append(float(w[1]))
            elif w[0] == "sum":
                for part in line[4:].split("; "):
                    k, _, v = part.partition(": ")
                    if v.strip().lstrip("-").isdigit():
                        sums[k.strip()] += int(v)
    prefix = 0
    for index in sorted(done):
        if index != box["range"]["start"] + prefix:
            break
        prefix += 1
    claims = (here / "deal" / "claims.log").read_text().splitlines() if (here / "deal" / "claims.log").exists() else []
    return {
        "done": len(done), "prefix": prefix, "won": won, "failed": failed, "floor": round(floors / max(runs, 1), 2),
        "hosts": dict(sorted(hosts.items())), "ends": dict(ends), "counts": dict(sums),
        "taken_back": sum(1 for line in claims if " back " in line),
        "stop_spread_s": round(max(deadline_ends) - min(deadline_ends), 2) if len(deadline_ends) > 1 else None,
    }


def fetch(argv: list[str]) -> None:
    if len(argv) != 1:
        raise SystemExit(__doc__)
    name = argv[0]
    jobs = jobs_of([name])
    for job in jobs:
        load(job)
    named, ended_at = 0.0, {}
    while True:
        over = on_hub(OVER, *jobs)
        waiting = {j: v for j, v in over["jobs"].items() if not v["ended"] or v["missing"]}
        if not waiting:
            break
        for job, v in over["jobs"].items():
            if v["ended"]:
                ended_at.setdefault(job, time.time())
        # a job over for 15 min whose chunks are not all back: name them, once an hour
        missing = [(j, m) for j, v in waiting.items() if time.time() >= ended_at.get(j, time.time()) + 900 for m in v["missing"]]
        if missing and time.time() >= named + 3600:
            named = time.time()
            for job, chunk in missing[:30]:
                host = chunk.split()[0].rsplit("-", 1)[0]
                seen = over["seen"].get(host)
                age = f"{over['now'] - float(seen.split()[0]):.0f} s ago" if seen else "never"
                say(f"not on the hub yet: {job} chunk {chunk} (its host looked {age})")
        time.sleep(120)
    copy_small_files(jobs)
    totals = Counter()
    for job in jobs:
        manifest = load(job)
        manifest["box"].update(box_numbers(job, manifest))
        manifest["box"]["ended"] = over["jobs"][job]["ended"]
        if manifest["status"] == "running":
            manifest["fetched"], manifest["status"] = iso_now(), "closed"
        manifest["box"]["stored"] = f"{job}/"
        save(job, manifest)
        box = manifest["box"]
        counted = "; ".join(f"{k}: {v}" for k, v in box["counts"].items())
        failed = f", {box['failed']} FAILED (dropped by every reader)" if box["failed"] else ""
        print(f"{job}: {box['done']} runs of {box['range']['n']}, head {box['prefix']}, {box['won']} won{failed}, floor {box['floor']}; "
              f"ended {box['ended']}; chunk ends {box['ends']}; {box['taken_back']} taken back; {counted}")
        totals.update({"done": box["done"], "won": box["won"], "failed": box["failed"]})
    with tempfile.NamedTemporaryFile("w", dir=ROOT / "tmp", suffix=".list") as f:
        f.write("".join(f"{job}/manifest.json\n" for job in jobs))
        f.flush()
        rsync("-a", f"--files-from={f.name}", f"{ROOT / 'data'}/", f"{hub_addr()}:")
    say(f"{len(jobs)} jobs closed: {totals['done']} runs, {totals['won']} won" + (f", {totals['failed']} failed" if totals["failed"] else "") +
        "; on the hub in each JOB/; pull.sh HOST JOB... unpacks them there")
    if step_path(name).exists():
        close_step(name)
    say(hub_disk())


def close_step(name: str) -> None:
    """A fetched step: its cpu lines of the span here, its file off the hub."""
    step = load_step(name)
    if step.get("fetched"):
        return
    with tempfile.TemporaryDirectory(dir=ROOT / "tmp") as t:
        rsync("-a", "--include=cpu-*.txt", "--exclude=*", f"{hub_addr()}:pool/", f"{t}/")
        lines = []
        for path in sorted(Path(t).glob("cpu-*.txt")):
            host = path.stem[4:]
            lines += [f"{host} {line}" for line in path.read_text().splitlines() if step["launched"] <= float(line.split()[0]) <= time.time()]
    (STEPS / f"{name}-cpu.txt").write_text("".join(line + "\n" for line in lines))
    subprocess.run([str(HUB), "rm", f"pool/steps/{name}.json"], check=True, stdout=subprocess.DEVNULL)
    step["fetched"] = iso_now()
    save_json(step_path(name), step)
    say(f"step {name} fetched and off the hub; its hosts' CPU lines in data/pool/steps/{name}-cpu.txt")


def main(argv: list[str]) -> None:
    if not argv:
        raise SystemExit(__doc__)
    command, rest = argv[0], argv[1:]
    if command == "until":
        print(until_epoch(rest[0]))
    elif command in (commands := {
        "up": up, "threads": set_threads, "down": down, "run": run, "plan": plan_dataset, "launch": launch_dataset,
        "deadline": move_deadline, "priority": set_priority, "stop": stop, "status": status, "fetch": fetch,
    }):
        commands[command](rest)
    else:
        raise SystemExit(__doc__)


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except Refused as refused:
        say(str(refused))
        sys.exit(1)
