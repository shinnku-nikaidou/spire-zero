"""Challenge an environment's champion (docs: evaluation §5) with a challenger given by its run options.

The challenger plays the champion's seed set in chunks, and the sim's gate pairs what it has played with the champion's
report after each chunk. A fail or a void verdict stops it there. After a pass it plays the rest of the set, which the
champion's report must cover, and is promoted: the gate writes its report and the champion file, and the leaderboard
is rebuilt. Paths are taken from the repository root, where the sim runs.

    uv run python challenge.py --sim ../sim.sh --champion eval/champions/nosl.json \\
        --promote eval/reports/DATE-nosl-r1-promotion-1-NAME -- --policy pimc --macro l0 --value eval/values/s1.json
"""

import argparse
import json
import shlex
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def play(sim, seed_file, lo, hi, processes, extra, folder):
    """Plays seeds [lo, hi) of the seed file, split over processes; returns their record files."""
    procs = []
    for i in range(processes):
        a, b = lo + (hi - lo) * i // processes, lo + (hi - lo) * (i + 1) // processes
        if a == b:
            continue
        records = folder / f"{a:04}-{b:04}.jsonl"
        cmd = [*sim, "run", "--seeds", seed_file, "--start", str(a), "--n", str(b - a), "--records", str(records), *extra]
        procs.append((subprocess.Popen(cmd, cwd=ROOT, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL), records))
    for proc, _ in procs:
        if proc.wait() != 0:
            raise SystemExit(f"the sim failed ({shlex.join(proc.args)})")
    return [records for _, records in procs]


def gate(sim, args, records, promote=None):
    """Runs the sim's gate on the records so far; returns its verdict (pass, fail, open, void) and its output."""
    cmd = [*sim, "gate", "--champion", args.champion, "--records", ",".join(str(r) for r in records),
           "--metric", args.metric, *(["--promote", promote] if promote else []), *args.extra]
    out = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
    lines = [line for line in out.stdout.splitlines() if line.startswith(("champion:", "challenger:", "gate ", "promoted:"))]
    verdict = next((line.split()[1].rstrip(":") for line in lines if line.startswith("gate ")), None)
    if verdict is None:
        raise SystemExit(f"the gate gave no verdict: {out.stdout[-2000:]}{out.stderr[-2000:]}")
    return verdict, "\n".join(lines)


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--sim", required=True, help="the command that starts the sim, as one string")
    p.add_argument("--champion", required=True, help="the champion file, such as eval/champions/nosl.json")
    p.add_argument("--chunk", type=int, default=40, help="seeds played between two gates")
    p.add_argument("--processes", type=int, default=4)
    p.add_argument("--metric", default="floor", choices=["floor", "win"])
    p.add_argument("--promote", help="where the challenger's report goes if it passes; without it, nothing is promoted")
    p.add_argument("--records-dir", help="folder for the challenger's run records (default: a temporary one)")
    p.add_argument("extra", nargs="*", help="the challenger's run options, after --, such as --policy pimc --macro l0")
    args = p.parse_args()

    sim = shlex.split(args.sim)
    champion = json.loads((ROOT / args.champion).read_text())
    report = json.loads((ROOT / champion["report"]).read_text())
    seed_set, total = report["seed_set"], report["set_size"]
    seed_file = f"eval/seeds/{seed_set}.txt"
    folder = Path(args.records_dir or tempfile.mkdtemp(prefix="challenge-")).resolve()
    folder.mkdir(parents=True, exist_ok=True)
    print(f"challenging {champion['champion']} on {seed_set} ({total} seeds), records in {folder}", flush=True)

    records = []
    for lo in range(0, total, args.chunk):
        hi = min(total, lo + args.chunk)
        records += play(sim, seed_file, lo, hi, args.processes, args.extra, folder)
        verdict, report = gate(sim, args, records)
        print(f"after {hi} of {total} seeds:\n{report}", flush=True)
        if verdict in ("fail", "void"):
            return 1
        if verdict == "pass":
            break
    else:
        print("no verdict on the whole set")
        return 1

    if not args.promote:
        return 0
    if hi < total:
        records += play(sim, seed_file, hi, total, args.processes, args.extra, folder)
    verdict, report = gate(sim, args, records, promote=args.promote)
    print(f"on the whole set:\n{report}", flush=True)
    if verdict != "pass":
        return 1
    subprocess.run([*sim, "leaderboard"], cwd=ROOT, stdout=subprocess.DEVNULL, check=True)
    print("leaderboard rebuilt: eval/leaderboard.md")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
