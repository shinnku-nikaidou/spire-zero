"""Challenge an environment's champion (docs: evaluation §5) with a challenger given by its run options.

The challenger plays the champion's seed set in chunks, and the sim's gate pairs what it has played with the champion's
report after each chunk. A fail or a void verdict stops it there. After a pass it plays the rest of the set, which the
champion's report must cover, and is promoted: the gate writes its report and the champion file, and the leaderboard
is rebuilt. Paths are taken from the repository root, where the sim runs.

    uv run python challenge.py --sim ../sim.sh --champion eval/champions/nosl.json \\
        --promote eval/reports/DATE-nosl-r1-promotion-1-NAME -- --policy pimc --macro l0 --value weights/history/s1.json
"""

import json
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

ROOT = Path(__file__).resolve().parent.parent
script = common.app()


class Metric(StrEnum):
    floor = "floor"
    win = "win"


def play(sim, seed_file, lo, hi, processes, extra, folder):
    """Plays seeds [lo, hi) of the seed file, split over processes; returns their record files. Each process logs to
    the record file's name with .log."""
    procs = []
    for i in range(processes):
        a, b = lo + (hi - lo) * i // processes, lo + (hi - lo) * (i + 1) // processes
        if a == b:
            continue
        records = folder / f"{a:04}-{b:04}.jsonl"
        cmd = [
            *sim,
            "run",
            "--seeds",
            seed_file,
            "--start",
            str(a),
            "--n",
            str(b - a),
            "--records",
            str(records),
            *extra,
        ]
        procs.append((start_sim(cmd, records.with_suffix(".log"), cwd=ROOT), records))
    wait_sims([(proc, records.with_suffix(".log")) for proc, records in procs])
    return [records for _, records in procs]


def gate(sim, champion, metric, extra, records, promote=None):
    """Runs the sim's gate on the records so far; returns its verdict (pass, fail, open, void) and its output."""
    cmd = [
        *sim,
        "gate",
        "--champion",
        champion,
        "--records",
        ",".join(str(r) for r in records),
        "--metric",
        metric.value,
        *(["--promote", promote] if promote else []),
        *extra,
    ]
    out = subprocess.run(cmd, cwd=ROOT, capture_output=True, text=True)
    lines = [
        line for line in out.stdout.splitlines() if line.startswith(("champion:", "challenger:", "gate ", "promoted:"))
    ]
    verdict = next((line.split()[1].rstrip(":") for line in lines if line.startswith("gate ")), None)
    if verdict is None:
        raise LearnError(f"the gate gave no verdict: {out.stdout[-2000:]}{out.stderr[-2000:]}")
    return verdict, "\n".join(lines)


@script.command(help=__doc__)
def main(
    sim: Annotated[str, typer.Option(help="the command that starts the sim, as one string")],
    champion: Annotated[str, typer.Option(help="the champion file, such as eval/champions/nosl.json")],
    chunk: Annotated[int, typer.Option(min=1, help="seeds played between two gates")] = 40,
    processes: Annotated[int, typer.Option(min=1)] = 4,
    metric: Annotated[Metric, typer.Option()] = Metric.floor,
    promote: Annotated[
        str | None,
        typer.Option(help="where the challenger's report goes if it passes; without it, nothing is promoted"),
    ] = None,
    records_dir: Annotated[
        str | None, typer.Option(help="folder for the challenger's run records and sim logs (default: a temporary one)")
    ] = None,
    extra: Annotated[
        list[str] | None,
        typer.Argument(help="the challenger's run options, after --, such as --policy pimc --macro l0"),
    ] = None,
) -> None:
    extra = extra or []
    cmd = shlex.split(sim)
    try:
        file = json.loads((ROOT / champion).read_text())
        report = json.loads((ROOT / file["report"]).read_text())
    except (OSError, KeyError, json.JSONDecodeError) as e:
        raise LearnError(f"cannot read the champion {champion} and its report: {e}") from e
    seed_set, total = report["seed_set"], report["set_size"]
    seed_file = f"eval/seeds/{seed_set}.txt"
    folder = Path(records_dir or tempfile.mkdtemp(prefix="challenge-")).resolve()
    folder.mkdir(parents=True, exist_ok=True)
    logger.info("challenging {} on {} ({} seeds), records in {}", file["champion"], seed_set, total, folder)

    records = []
    for lo in range(0, total, chunk):
        hi = min(total, lo + chunk)
        records += play(cmd, seed_file, lo, hi, processes, extra, folder)
        verdict, lines = gate(cmd, champion, metric, extra, records)
        logger.info("after {} of {} seeds:\n{}", hi, total, lines)
        if verdict in ("fail", "void"):
            raise typer.Exit(1)
        if verdict == "pass":
            break
    else:
        logger.info("no verdict on the whole set")
        raise typer.Exit(1)

    if not promote:
        return
    if hi < total:
        records += play(cmd, seed_file, hi, total, processes, extra, folder)
    verdict, lines = gate(cmd, champion, metric, extra, records, promote=promote)
    logger.info("on the whole set:\n{}", lines)
    if verdict != "pass":
        raise typer.Exit(1)
    subprocess.run([*cmd, "leaderboard"], cwd=ROOT, stdout=subprocess.DEVNULL, check=True)
    logger.info("leaderboard rebuilt: eval/leaderboard.md")


if __name__ == "__main__":
    common.run(script)
