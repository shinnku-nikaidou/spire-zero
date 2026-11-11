"""What the scripts of learn/ share: logging, errors, reading JSON lines and list pairs.

Logs go to stderr, so that a script's stdout carries only its result. A LearnError ends a script with its message and
exit code 1; anything else is a bug and ends it with a traceback.
"""

import json
import os
import re
import shlex
import shutil
import subprocess
import sys
import threading
from collections.abc import Iterator
from pathlib import Path

import typer
from loguru import logger

ROOT = Path(__file__).resolve().parent.parent  # the repo
# How a decision log's line starts: its run, its environment and its surface, read without parsing the line.
HEAD = re.compile(r'\{"run":"(\w+)","decision_id":\d+,"env":"(\w+)","surface":"(\w+)"')


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


class Encoder:
    """The nets' inputs of decisions, made by the code that feeds the nets in play, so
    that a state is read one way only: the simulator's `encode`, or the program src/Sts2Ai.Encode, which is the same
    without the game. `command` starts it; one process serves a whole training.

    A decision is a line of JSON as the decision log holds it: a log's line as it is, or any JSON with the fields the
    answer needs (`run_view`, and `surface` and `chosen` for a map move's afterstate; `obs` and `history`)."""

    def __init__(self, command: str):
        try:
            self.process = subprocess.Popen(
                shlex.split(command), stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8"
            )
        except OSError as e:
            raise LearnError(f"cannot start the encoder `{command}`: {e.strerror}") from e
        hello = self.process.stdout.readline()
        if not hello:
            raise LearnError(f"the encoder `{command}` ended without a word")
        self.hello = json.loads(hello)  # its build's commit, the names of a run's numbers, the combat net's lags

    def runs(self, decisions: list[str], source: str = "") -> list[dict | None]:
        """Each macro decision's run as V reads it, `{"run": {"tokens", "state"}}`, and for a map move also `"entered"`,
        its afterstate."""
        return self._ask("R", decisions, source)

    def combats(self, decisions: list[str], source: str = "") -> list[dict | None]:
        """Each combat decision's state as the combat net reads it, `{"tokens": ...}`."""
        return self._ask("C", decisions, source)

    def _ask(self, kind: str, decisions: list[str], source: str) -> list[dict | None]:
        """The answers, in order; None for a decision that is no JSON, a line cut short. One the encoder cannot read
        though it is JSON fails."""

        def feed():
            pipe = self.process.stdin
            try:
                for d in decisions:
                    pipe.write(kind)
                    pipe.write(d if d.endswith("\n") else d + "\n")
                pipe.write("\n")  # an empty line: send the answers so far
                pipe.flush()
            except OSError:
                pass  # the encoder ended: the reader below says so

        feeder = threading.Thread(target=feed)
        feeder.start()
        answers = []
        for d in decisions:
            line = self.process.stdout.readline()
            if not line:
                raise LearnError(f"the encoder ended while reading {source or 'a decision'}")
            answer = json.loads(line)
            if "error" in answer:
                try:
                    json.loads(d)
                except json.JSONDecodeError:
                    answer = None
                else:
                    raise LearnError(f"the encoder cannot read a decision of {source or 'the trainer'}: {answer['error']}")
            answers.append(answer)
        feeder.join()
        return answers

    def close(self) -> None:
        self.process.stdin.close()
        self.process.wait()


def checkout_encoder() -> str:
    """How to start this checkout's encoder, for what runs here (the tests, the book's scripts): STS2_ENCODER, or
    src/Sts2Ai.Encode, built first, which needs .NET and not the game. A training names its own (--encoder): the
    simulator's build that will play the net."""
    if os.environ.get("STS2_ENCODER"):
        return os.environ["STS2_ENCODER"]
    dotnet = shutil.which("dotnet")
    if dotnet is None:
        raise LearnError("the encoder needs .NET to build here; or set STS2_ENCODER to how to start one")
    project = ROOT / "src" / "Sts2Ai.Encode"
    build = [dotnet, "build", str(project), "-c", "Release", "-nologo", "-v", "q"]
    built = subprocess.run(build, capture_output=True, text=True)
    if built.returncode != 0:
        raise LearnError(f"src/Sts2Ai.Encode does not build:\n{built.stdout}{built.stderr}")
    return shlex.join([dotnet, str(project / "bin" / "Release" / "net9.0" / "Sts2Ai.Encode.dll")])


def trained(list_: str, init: dict | None, seed: int, epochs: int, settings: dict, encoder: Encoder) -> dict:
    """What made a net, for its file: the code's commit, the commit of the encoder that
    read the states, the list pair it trained on, the net it went on from (its name) or none, the seed, the epochs it
    ran, and the trainer's settings as they were."""
    return {
        "rev": revision(),
        "encoder": encoder.hello["commit"],
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

