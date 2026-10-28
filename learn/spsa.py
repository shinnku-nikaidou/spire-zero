"""Tune V's weights or PIMC's leaf weights (docs: learning §2, step 4) by SPSA.

Each step perturbs every weight at once, by c times its scale in a random sign pattern, plays the same fresh train
seeds with the plus and the minus weights (CRN: the luck the two share cancels out), and moves the weights toward the
better side. Two objectives:

- floor: whole runs, `run --macro l0` with the weights (`--value` or `--leaf`), scored by the mean floor reached while
  runs rarely win;
- panel: leaf weights only, on the combat panel (`panel --player pimc --leaf`), much cheaper than runs. A won combat
  scores the share of its starting HP left, any other end -1.

The options after `--` go to every sim command: the combat policy and its budget, the panel's rooms. The output is a
weights file for `--value` or `--leaf`, in the character's weights folder by default (docs: learning §6); whether it is
better is the gate's to decide (docs: evaluation §5).

    uv run python spsa.py --sim ../sim.sh --steps 100 --seeds 32 --processes 8 --name s1 --log s1.jsonl -- --policy play
    uv run python spsa.py --sim ../sim.sh --param leaf --objective panel --steps 100 --seeds 48 --name leaf1 \\
        --log leaf1.jsonl -- --rooms regular,elite
"""

import json
import random
import shlex
import subprocess
import tempfile
from enum import StrEnum
from pathlib import Path
from typing import Annotated

import typer
from loguru import logger

import common
from common import LearnError, start_sim, wait_sims
from weights import out_path

script = common.app()


class Param(StrEnum):
    value = "value"
    leaf = "leaf"


class Objective(StrEnum):
    floor = "floor"
    panel = "panel"


def run_side(sim, objective, param, seed_base, character, extra, weights_file, first, n, processes, work, tag):
    """Starts train seeds [first, first + n) with these weights, split over processes: runs or panel combats. Each
    process writes its records to work/TAG-I.jsonl and its output to work/TAG-I.log."""
    chunks = [(first + n * i // processes, first + n * (i + 1) // processes) for i in range(processes)]
    procs = []
    for i, (lo, hi) in enumerate(chunks):
        if hi == lo:
            continue
        records = work / f"{tag}-{i}.jsonl"
        seeds = ["--seed", str(seed_base), "--start", str(lo), "--n", str(hi - lo), "--character", character]
        if objective == Objective.panel:
            cmd = [*sim, "panel", *seeds, "--player", "pimc", "--leaf", str(weights_file), "--out", str(records)]
        else:
            cmd = [
                *sim,
                "run",
                *seeds,
                "--macro",
                "l0",
                f"--{param.value}",
                str(weights_file),
                "--records",
                str(records),
            ]
        procs.append((start_sim([*cmd, *extra], work / f"{tag}-{i}.log"), records))
    return procs


def collect(procs):
    wait_sims([(proc, records.with_suffix(".log")) for proc, records in procs])
    return [json.loads(line) for _, records in procs for line in records.read_text().splitlines() if line.strip()]


def score(objective, results):
    """The objective's mean: the floor a run reached, or a panel combat's HP share left on a win and -1 otherwise."""
    if objective == Objective.panel:
        return sum(r["hp"] / r["hp_start"] if r["outcome"] == "Win" else -1 for r in results) / len(results)
    return sum(r["floor"] for r in results) / len(results)


def failures(objective, results):
    if objective == Objective.panel:
        return sum(r["outcome"] == "Failed" for r in results)
    return sum(r["outcome"] not in ("won", "lost") for r in results)


def successes(objective, results):
    return sum(r["outcome"] == ("Win" if objective == Objective.panel else "won") for r in results)


@script.command(help=__doc__)
def main(
    sim: Annotated[str, typer.Option(help="the command that starts the sim, as one string")],
    name: Annotated[str, typer.Option(help="the weights' name, as the champion will show it")],
    param: Annotated[Param, typer.Option(help="V's weights or PIMC's leaf weights")] = Param.value,
    objective: Annotated[Objective, typer.Option()] = Objective.floor,
    start_file: Annotated[
        str | None, typer.Option("--from", help="weights file to start from (default: the sim's initial ones)")
    ] = None,
    steps: Annotated[int, typer.Option(min=1)] = 100,
    seeds: Annotated[
        int, typer.Option(min=1, help="train seeds (runs or panel combats) per step, for both sides")
    ] = 32,
    processes: Annotated[int, typer.Option(min=2, help="sim processes per step, split between the sides")] = 8,
    seed_base: Annotated[int, typer.Option(help="the train seeds' base (docs: evaluation §4)")] = 1,
    first_seed: Annotated[int, typer.Option(help="step k plays seeds from first + k * seeds")] = 0,
    a: Annotated[
        float | None,
        typer.Option(
            help="step size, in units of a weight's scale per objective unit "
            "(default: 0.02 per floor, 0.5 per panel score, which spans 2 against a run's ~50 floors)"
        ),
    ] = None,
    c: Annotated[float, typer.Option(help="perturbation, in units of a weight's scale")] = 0.2,
    max_move: Annotated[float, typer.Option(help="largest change of a weight per step, in its scale")] = 0.1,
    out: Annotated[
        str | None, typer.Option(help="where the file goes (default: weights/CHARACTER/NAME.json)")
    ] = None,
    character: Annotated[str, typer.Option(help="the character that plays: each has a brain of its own")] = "SILENT",
    log: Annotated[str | None, typer.Option(help="one line per step")] = None,
    work: Annotated[
        str | None, typer.Option(help="folder for the sides' weights, records and sim logs (default: a temporary one)")
    ] = None,
    extra: Annotated[
        list[str] | None, typer.Argument(help="sim options after --, such as --policy pimc --worlds 4")
    ] = None,
) -> None:
    extra = extra or []
    out_file = out_path(name, out, character)
    if objective == Objective.panel and param != Param.leaf:
        raise LearnError("the panel plays combats, which V does not decide: --objective panel needs --param leaf")
    a = a or (0.5 if objective == Objective.panel else 0.02)

    cmd = shlex.split(sim)
    if start_file:
        start = json.loads(Path(start_file).read_text())
    else:
        initial = [*cmd, "weights", *(["--leaf"] if param == Param.leaf else [])]
        done = subprocess.run(initial, capture_output=True, text=True)
        if done.returncode != 0:
            raise LearnError(f"{shlex.join(initial)} failed: {done.stderr[-2000:]}")
        start = json.loads(done.stdout)
    weights = dict(start["weights"])
    # The floor is the same for every option of a decision, so its weight changes no choice.
    names = sorted(n for n in weights if n != "floor")
    scale = {n: abs(weights[n]) or 1.0 for n in names}
    folder = Path(work or tempfile.mkdtemp(prefix="spsa-"))
    folder.mkdir(parents=True, exist_ok=True)
    logger.info(
        "tuning {} from {} by {}, {} steps; sides in {}", param.value, start.get("name"), objective.value, steps, folder
    )
    step_log = open(log, "a") if log else None
    rng = random.Random(name)
    # Spall's gain sequences, with the usual exponents and a stability constant of a tenth of the steps.
    big_a = steps / 10

    for k in range(steps):
        a_k = a / (k + 1 + big_a) ** 0.602
        c_k = c / (k + 1) ** 0.101
        delta = {n: rng.choice((-1, 1)) for n in names}
        first = first_seed + k * seeds
        procs = []
        for sign, tag in ((1, "plus"), (-1, "minus")):
            side = {n: weights[n] + sign * c_k * scale[n] * delta[n] for n in names}
            path = folder / f"{tag}.json"
            path.write_text(json.dumps({"name": f"{name}-{k}{'+' if sign > 0 else '-'}", "weights": side}))
            procs.append(
                run_side(
                    cmd,
                    objective,
                    param,
                    seed_base,
                    character,
                    extra,
                    path,
                    first,
                    seeds,
                    max(1, processes // 2),
                    folder,
                    tag,
                )
            )
        plus, minus = collect(procs[0]), collect(procs[1])
        f_plus, f_minus = score(objective, plus), score(objective, minus)
        for n in names:
            move = a_k * (f_plus - f_minus) / (2 * c_k * delta[n])
            weights[n] += max(-max_move, min(max_move, move)) * scale[n]
            if param == Param.leaf:
                # Every leaf number is a rate, a share or a value above a loss: it stays positive.
                weights[n] = max(weights[n], scale[n] / 100)
        failed = failures(objective, plus + minus)
        line = {
            "step": k,
            "seeds": [first, first + seeds],
            "objective": objective.value,
            "plus": f_plus,
            "minus": f_minus,
            "wins": successes(objective, plus + minus),
            "failed": failed,
            "weights": weights,
        }
        if step_log:
            step_log.write(json.dumps(line) + "\n")
            step_log.flush()
        logger.info(
            "step {}: {} {:.3f} vs {:.3f}{}",
            k,
            objective.value,
            f_plus,
            f_minus,
            f", {failed} failed" if failed else "",
        )

    target = (
        "combat panel: HP share left on a win, -1 otherwise" if objective == Objective.panel else "mean floor reached"
    )
    tuned = {
        "name": name,
        "character": character,
        "target": target,
        "tuned": {
            "method": "spsa",
            "param": param.value,
            "objective": objective.value,
            "from": start.get("name"),
            "steps": steps,
            "seeds": seeds,
            "seed_base": seed_base,
            "first_seed": first_seed,
            "a": a,
            "c": c,
            "options": extra,
        },
        "weights": weights,
    }
    out_file.write_text(json.dumps(tuned, indent=2) + "\n")
    logger.info("wrote {}", out_file)
    for n in names:
        logger.info("  {:16} {:+.4f} -> {:+.4f}", n, start["weights"][n], weights[n])


if __name__ == "__main__":
    common.run(script)
