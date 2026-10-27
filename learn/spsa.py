"""Tune V's weights (docs: learning §2, step 4) by SPSA on whole runs.

Each step perturbs every weight at once, by c times its scale in a random sign pattern, plays the same fresh train
seeds with the plus and the minus weights (CRN: the luck the two share cancels out), and moves the weights toward the
side that went further. While runs rarely win, the objective is the mean floor reached. The sim plays the runs with
`run --macro l0 --value`, plus whatever run options follow `--` (the combat policy and its budget). The output is a
weights file for `run --value`; whether it is better is the gate's to decide (docs: evaluation §5).

    uv run python spsa.py --sim ../sim.sh --steps 100 --seeds 32 --processes 8 --name s1 \\
        --out ../eval/values/s1.json --log s1.jsonl -- --policy play
"""

import argparse
import json
import random
import shlex
import subprocess
import tempfile
from pathlib import Path


def run_side(sim, weights_file, seed_base, first, n, processes, extra, work, tag):
    """Starts the runs of train seeds [first, first + n) with these weights, split over processes."""
    chunks = [(first + n * i // processes, first + n * (i + 1) // processes) for i in range(processes)]
    procs = []
    for i, (lo, hi) in enumerate(chunks):
        if hi == lo:
            continue
        records = work / f"{tag}-{i}.jsonl"
        cmd = [*sim, "run", "--seed", str(seed_base), "--start", str(lo), "--n", str(hi - lo), "--macro", "l0",
               "--value", str(weights_file), "--records", str(records), *extra]
        procs.append((subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL), records))
    return procs


def collect(procs):
    runs = []
    for proc, records in procs:
        if proc.wait() != 0:
            raise SystemExit(f"the sim failed ({proc.args})")
        runs += [json.loads(line) for line in records.read_text().splitlines() if line.strip()]
    return runs


def mean_floor(runs):
    return sum(r["floor"] for r in runs) / len(runs)


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--sim", required=True, help="the command that starts the sim, as one string")
    p.add_argument("--from", dest="start", help="weights file to start from (default: V's initial weights)")
    p.add_argument("--steps", type=int, default=100)
    p.add_argument("--seeds", type=int, default=32, help="train seeds per step, played by both sides")
    p.add_argument("--processes", type=int, default=8, help="sim processes per step, split between the sides")
    p.add_argument("--seed-base", type=int, default=1, help="the train seeds' base (docs: evaluation §4)")
    p.add_argument("--first-seed", type=int, default=0, help="step k plays seeds from first + k * seeds")
    p.add_argument("--a", type=float, default=0.02, help="step size, in units of a weight's scale per floor")
    p.add_argument("--c", type=float, default=0.2, help="perturbation, in units of a weight's scale")
    p.add_argument("--max-move", type=float, default=0.1, help="largest change of a weight per step, in its scale")
    p.add_argument("--name", required=True, help="the weights' name, as the champion will show it")
    p.add_argument("--out", required=True)
    p.add_argument("--log", help="one line per step")
    p.add_argument("--work", help="folder for the sides' weights and records (default: a temporary one)")
    p.add_argument("extra", nargs="*", help="run options after --, such as --policy pimc --worlds 4")
    args = p.parse_args()

    sim = shlex.split(args.sim)
    if args.start:
        start = json.loads(Path(args.start).read_text())
    else:
        start = json.loads(subprocess.run([*sim, "weights"], capture_output=True, text=True, check=True).stdout)
    weights = dict(start["weights"])
    # The floor is the same for every option of a decision, so its weight changes no choice.
    names = sorted(n for n in weights if n != "floor")
    scale = {n: abs(weights[n]) or 1.0 for n in names}
    work = Path(args.work or tempfile.mkdtemp(prefix="spsa-"))
    work.mkdir(parents=True, exist_ok=True)
    log = open(args.log, "a") if args.log else None
    rng = random.Random(args.name)
    # Spall's gain sequences, with the usual exponents and a stability constant of a tenth of the steps.
    big_a = args.steps / 10

    for k in range(args.steps):
        a_k = args.a / (k + 1 + big_a) ** 0.602
        c_k = args.c / (k + 1) ** 0.101
        delta = {n: rng.choice((-1, 1)) for n in names}
        first = args.first_seed + k * args.seeds
        procs = []
        for sign, tag in ((1, "plus"), (-1, "minus")):
            side = {n: weights[n] + sign * c_k * scale[n] * delta[n] for n in names}
            path = work / f"{tag}.json"
            path.write_text(json.dumps({"name": f"{args.name}-{k}{'+' if sign > 0 else '-'}", "weights": side}))
            procs.append(run_side(sim, path, args.seed_base, first, args.seeds, max(1, args.processes // 2), args.extra, work, tag))
        plus, minus = collect(procs[0]), collect(procs[1])
        f_plus, f_minus = mean_floor(plus), mean_floor(minus)
        for n in names:
            move = a_k * (f_plus - f_minus) / (2 * c_k * delta[n])
            weights[n] += max(-args.max_move, min(args.max_move, move)) * scale[n]
        failed = sum(r["outcome"] not in ("won", "lost") for r in plus + minus)
        line = {"step": k, "seeds": [first, first + args.seeds], "floor_plus": f_plus, "floor_minus": f_minus,
                "wins": sum(r["outcome"] == "won" for r in plus + minus), "failed": failed, "weights": weights}
        if log:
            log.write(json.dumps(line) + "\n")
            log.flush()
        print(f"step {k}: floor {f_plus:.2f} vs {f_minus:.2f}" + (f", {failed} failed" if failed else ""), flush=True)

    out = {"name": args.name, "target": "mean floor reached", "tuned": {
        "method": "spsa", "from": start.get("name"), "steps": args.steps, "seeds": args.seeds, "seed_base": args.seed_base,
        "first_seed": args.first_seed, "a": args.a, "c": args.c, "run_options": args.extra}, "weights": weights}
    Path(args.out).write_text(json.dumps(out, indent=2) + "\n")
    for n in names:
        print(f"  {n:16} {start['weights'][n]:+.4f} -> {weights[n]:+.4f}")


if __name__ == "__main__":
    main()
