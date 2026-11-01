"""What the scripts of learn/ share: logging, errors, reading JSON lines, and running the sim.

Logs go to stderr, so that a script's stdout carries only its result. A LearnError ends a script with its message and
exit code 1; anything else is a bug and ends it with a traceback.
"""

import json
import os
import re
import shlex
import subprocess
import sys
from collections.abc import Iterator, Sequence
from pathlib import Path

import typer
from loguru import logger

ROOT = Path(__file__).resolve().parent.parent  # the repo


class LearnError(Exception):
    """A failure a script reports in one line and exits on: bad input, a file in the way, a sim that failed."""


def app() -> typer.Typer:
    """A script's command line: one command, whose help is the script's docstring (`@script.command(help=__doc__)`)."""
    return typer.Typer(add_completion=False, pretty_exceptions_show_locals=False, rich_markup_mode=None)


def run(script: typer.Typer) -> None:
    """Runs a script's command line, with logs on stderr at LOG_LEVEL (INFO by default)."""
    logger.remove()
    logger.add(
        sys.stderr,
        level=os.environ.get("LOG_LEVEL", "INFO"),
        format="<green>{time:HH:mm:ss}</green> <level>{level: <7}</level> {message}",
    )
    try:
        script()
    except LearnError as e:
        logger.error(str(e))
        sys.exit(1)


def read_lines(paths: str, needle: str | None = None) -> Iterator[dict]:
    """The JSON lines of the files (comma-separated) that contain the needle; a line still being written is skipped."""
    for path in paths.split(","):
        try:
            f = open(path)
        except OSError as e:
            raise LearnError(f"cannot read {path}: {e.strerror}") from e
        with f:
            for line in f:
                if not line.strip() or (needle and needle not in line):
                    continue
                try:
                    yield json.loads(line)
                except json.JSONDecodeError:
                    pass


def runs_by_log(logs: str, records: str, character: str) -> list[tuple[str, dict[str, dict]]]:
    """Each decision log (comma-separated) with the runs, by run id, of the record file at the same place in records:
    the one its `run` wrote beside it. A run id is a hash of the seed alone, so files of other runs can hold another
    run of the same seed (another policy, an exploration scan); a log is joined only with its own records. A run id
    twice in one record file is refused, and so is a run of another character than the one being trained: each
    character has a brain of its own (docs: learning/reference §6)."""
    log_paths, record_paths = logs.split(","), records.split(",")
    if len(log_paths) != len(record_paths):
        raise LearnError(
            f"{len(log_paths)} decision logs and {len(record_paths)} record files: "
            "give each log the record file its run wrote, in the same order"
        )
    pairs = []
    for log, path in zip(log_paths, record_paths, strict=True):
        runs = {}
        for r in read_lines(path):
            run_id = r.get("run_id")
            if not run_id:
                continue
            if run_id in runs:
                raise LearnError(f"run {run_id} is in {path} twice")
            if r.get("character") != character:
                raise LearnError(f"run {run_id} in {path} was played as {r.get('character')}, not {character}")
            runs[run_id] = r
        pairs.append((log, runs))
    return pairs


def lists(prefix: str) -> tuple[str, str]:
    """A list pair's logs and records, comma-separated: PREFIX-logs.txt and PREFIX-recs.txt, one path per line, line i
    of one pairing with line i of the other. Relative paths (the prefix's too) are the repo's."""
    base = Path(prefix) if Path(prefix).is_absolute() else ROOT / prefix
    out = []
    for suffix in ("logs", "recs"):
        path = base.parent / f"{base.name}-{suffix}.txt"
        if not path.exists():
            raise LearnError(f"no list {path}")
        lines = [x.strip() for x in path.read_text().splitlines() if x.strip()]
        out.append(",".join(x if Path(x).is_absolute() else str(ROOT / x) for x in lines))
    return out[0], out[1]


def train_lists(prefix: str) -> tuple[str, str]:
    """The list pair a net trains on. Test runs are never trained on: a path inside an eval job is refused."""
    logs, records = lists(prefix)
    for path in f"{logs},{records}".split(","):
        if re.search(r"/(sl|nosl)/eval/", path):
            raise LearnError(f"{prefix} holds test runs, which no net trains on: {path}")
    return logs, records


def start_sim(cmd: Sequence[str], log: Path, cwd: Path | None = None) -> subprocess.Popen:
    """Starts a sim command, its output (stdout and stderr) in a log file."""
    with open(log, "w") as out:
        return subprocess.Popen(list(cmd), cwd=cwd, stdout=out, stderr=subprocess.STDOUT)


def wait_sims(procs: Sequence[tuple[subprocess.Popen, Path]]) -> None:
    """Waits for every sim process, then fails if any did, with the end of the first one's log."""
    failed = [(proc, log) for proc, log in procs if proc.wait() != 0]
    if failed:
        proc, log = failed[0]
        tail = "".join(log.read_text(errors="replace").splitlines(keepends=True)[-12:])
        raise LearnError(
            f"{len(failed)} of {len(procs)} sim processes failed; the first, {shlex.join(proc.args)}, "
            f"logged to {log}:\n{tail}"
        )
