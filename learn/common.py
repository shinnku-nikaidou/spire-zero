"""What the scripts of learn/ share: logging, errors, reading JSON lines and list pairs.

Logs go to stderr, so that a script's stdout carries only its result. A LearnError ends a script with its message and
exit code 1; anything else is a bug and ends it with a traceback.
"""

import json
import os
import re
import subprocess
import sys
from collections.abc import Iterator
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


def runs_by_log(logs: str, records: str) -> list[tuple[str, dict[str, dict]]]:
    """Each decision log (comma-separated) with the runs, by run id, of the record file at the same place in records:
    the one its `run` wrote beside it. A run id is a hash of the seed alone, so files of other runs can hold another
    run of the same seed (another policy, an exploration scan); a log is joined only with its own records. A run id
    twice in one record file is refused. Every character's runs are read: the nets are shared by all of them."""
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
            run_id = r["run_id"]
            if run_id in runs:
                raise LearnError(f"run {run_id} is in {path} twice")
            runs[run_id] = r
        pairs.append((log, runs))
    return pairs


def revision() -> str:
    """The commit this code is. learn/REV holds it in a `git archive` (export-subst, learn/.gitattributes), which is how
    learn/ reaches a training host; a checkout asks git, and says "-dirty" when learn/ has uncommitted changes."""
    text = (Path(__file__).parent / "REV").read_text().strip()
    if not text.startswith("$Format"):
        return text

    def git(*args: str) -> str:
        return subprocess.run(["git", "-C", str(ROOT), *args], capture_output=True, text=True, check=True).stdout.strip()

    return git("rev-parse", "HEAD") + ("-dirty" if git("status", "--porcelain", "--", "learn") else "")


def trained(list_: str, init: dict | None, seed: int, epochs: int, settings: dict) -> dict:
    """What made a net, for its file (docs: learning/reference §7): the code's commit, the list pair it trained on, the
    net it went on from (its name) or none, the seed, the epochs it ran, and the trainer's settings as they were."""
    return {
        "rev": revision(),
        "list": list_,
        "init": init["name"] if init else None,
        "seed": seed,
        "epochs": epochs,
        "settings": settings,
    }


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

