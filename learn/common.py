"""What the scripts of learn/ share: logging, errors, reading JSON lines, and running the sim.

Logs go to stderr, so that a script's stdout carries only its result. A LearnError ends a script with its message and
exit code 1; anything else is a bug and ends it with a traceback.
"""

import json
import os
import shlex
import subprocess
import sys
from collections.abc import Iterator, Sequence
from pathlib import Path

import typer
from loguru import logger


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
