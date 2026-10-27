"""Tune V's weights or PIMC's leaf weights (docs: learning §2, step 4) by SPSA.

Each step perturbs every weight at once, by c times its scale in a random sign pattern, plays the same fresh train
seeds with the plus and the minus weights (CRN: the luck the two share cancels out), and moves the weights toward the
better side. Two objectives:

- floor: whole runs, `run --macro l0` with the weights (`--value` or `--leaf`), scored by the mean floor reached while
  runs rarely win;
- panel: leaf weights only, on the combat panel (`panel --player pimc --leaf`), much cheaper than runs. A won combat
  scores the share of its starting HP left, any other end -1.

The options after `--` go to every sim command: the combat policy and its budget, the panel's rooms. The output is a
weights file for `--value` or `--leaf`, in the weights history by default (docs: learning §6); whether it is better is
the gate's to decide (docs: evaluation §5).

    uv run python spsa.py --sim ../sim.sh --steps 100 --seeds 32 --processes 8 --name s1 --log s1.jsonl -- --policy play
    uv run python spsa.py --sim ../sim.sh --param leaf --objective panel --steps 100 --seeds 48 --name leaf1 \\
        --log leaf1.jsonl -- --rooms regular,elite
"""

import argparse
import json
import random
import shlex
import subprocess
import tempfile
from pathlib import Path

from weights import out_path


def run_side(sim, args, weights_file, first, n, processes, work, tag):
    """Starts train seeds [first, first + n) with these weights, split over processes: runs or panel combats."""
    chunks = [(first + n * i // processes, first + n * (i + 1) // processes) for i in range(processes)]
    procs = []
    for i, (lo, hi) in enumerate(chunks):
        if hi == lo:
            continue
        records = work / f"{tag}-{i}.jsonl"
        seeds = ["--seed", str(args.seed_base), "--start", str(lo), "--n", str(hi - lo)]
        if args.objective == "panel":
            cmd = [*sim, "panel", *seeds, "--player", "pimc", "--leaf", str(weights_file), "--out", str(records)]
        else:
            cmd = [*sim, "run", *seeds, "--macro", "l0", f"--{args.param}", str(weights_file), "--records", str(records)]
        procs.append((subprocess.Popen([*cmd, *args.extra], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL), records))
    return procs


def collect(procs):
    runs = []
    for proc, records in procs:
        if proc.wait() != 0:
            raise SystemExit(f"the sim failed ({proc.args})")
        runs += [json.loads(line) for line in records.read_text().splitlines() if line.strip()]
    return runs


def score(objective, results):
    """The objective's mean: the floor a run reached, or a panel combat's HP share left on a win and -1 otherwise."""
    if objective == "panel":
        return sum(r["hp"] / r["hp_start"] if r["outcome"] == "Win" else -1 for r in results) / len(results)
    return sum(r["floor"] for r in results) / len(results)


def failures(objective, results):
    if objective == "panel":
        return sum(r["outcome"] == "Failed" for r in results)
    return sum(r["outcome"] not in ("won", "lost") for r in results)


def successes(objective, results):
    return sum(r["outcome"] == ("Win" if objective == "panel" else "won") for r in results)


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--sim", required=True, help="the command that starts the sim, as one string")
    p.add_argument("--param", default="value", choices=["value", "leaf"], help="V's weights or PIMC's leaf weights")
    p.add_argument("--objective", default="floor", choices=["floor", "panel"])
    p.add_argument("--from", dest="start", help="weights file to start from (default: the sim's initial ones)")
    p.add_argument("--steps", type=int, default=100)
    p.add_argument("--seeds", type=int, default=32, help="train seeds (runs or panel combats) per step, for both sides")
    p.add_argument("--processes", type=int, default=8, help="sim processes per step, split between the sides")
    p.add_argument("--seed-base", type=int, default=1, help="the train seeds' base (docs: evaluation §4)")
    p.add_argument("--first-seed", type=int, default=0, help="step k plays seeds from first + k * seeds")
    p.add_argument("--a", type=float, help="step size, in units of a weight's scale per objective unit "
                   "(default: 0.02 per floor, 0.5 per panel score, which spans 2 against a run's ~50 floors)")
    p.add_argument("--c", type=float, default=0.2, help="perturbation, in units of a weight's scale")
    p.add_argument("--max-move", type=float, default=0.1, help="largest change of a weight per step, in its scale")
    p.add_argument("--name", required=True, help="the weights' name, as the champion will show it")
    p.add_argument("--out", help="where the file goes (default: weights/history/NAME.json)")
    p.add_argument("--log", help="one line per step")
    p.add_argument("--work", help="folder for the sides' weights and records (default: a temporary one)")
    p.add_argument("extra", nargs="*", help="sim options after --, such as --policy pimc --worlds 4")
    args = p.parse_args()
    out_file = out_path(args.name, args.out)
    if args.objective == "panel" and args.param != "leaf":
        p.error("the panel plays combats, which V does not decide: --objective panel needs --param leaf")
    args.a = args.a or (0.5 if args.objective == "panel" else 0.02)

    sim = shlex.split(args.sim)
    if args.start:
        start = json.loads(Path(args.start).read_text())
    else:
        initial = [*sim, "weights", *(["--leaf"] if args.param == "leaf" else [])]
        start = json.loads(subprocess.run(initial, capture_output=True, text=True, check=True).stdout)
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
            procs.append(run_side(sim, args, path, first, args.seeds, max(1, args.processes // 2), work, tag))
        plus, minus = collect(procs[0]), collect(procs[1])
        f_plus, f_minus = score(args.objective, plus), score(args.objective, minus)
        for n in names:
            move = a_k * (f_plus - f_minus) / (2 * c_k * delta[n])
            weights[n] += max(-args.max_move, min(args.max_move, move)) * scale[n]
            if args.param == "leaf":
                # Every leaf number is a rate, a share or a value above a loss: it stays positive.
                weights[n] = max(weights[n], scale[n] / 100)
        failed = failures(args.objective, plus + minus)
        line = {"step": k, "seeds": [first, first + args.seeds], "objective": args.objective, "plus": f_plus,
                "minus": f_minus, "wins": successes(args.objective, plus + minus), "failed": failed, "weights": weights}
        if log:
            log.write(json.dumps(line) + "\n")
            log.flush()
        print(f"step {k}: {args.objective} {f_plus:.3f} vs {f_minus:.3f}" + (f", {failed} failed" if failed else ""), flush=True)

    target = "combat panel: HP share left on a win, -1 otherwise" if args.objective == "panel" else "mean floor reached"
    out = {"name": args.name, "target": target, "tuned": {
        "method": "spsa", "param": args.param, "objective": args.objective, "from": start.get("name"), "steps": args.steps,
        "seeds": args.seeds, "seed_base": args.seed_base, "first_seed": args.first_seed, "a": args.a, "c": args.c,
        "options": args.extra}, "weights": weights}
    out_file.write_text(json.dumps(out, indent=2) + "\n")
    for n in names:
        print(f"  {n:16} {start['weights'][n]:+.4f} -> {weights[n]:+.4f}")


if __name__ == "__main__":
    main()
