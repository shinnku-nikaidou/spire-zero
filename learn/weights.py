"""Where weights files go (docs: learning §6). A trainer writes its file to weights/history/NAME.json unless --out says
otherwise; git ignores weights/, and the history is never overwritten, so each name is used once. A promotion copies
the champion's files onto the environment's main names, weights/ENV/{value,leaf,combat}.json (docs: evaluation §5)."""

from pathlib import Path

from common import LearnError

HISTORY = Path(__file__).resolve().parent.parent / "weights" / "history"


def out_path(name: str, out: str | None) -> Path:
    """--out if given, else the name's file in the history; a file already in the history is never replaced."""
    path = Path(out) if out else HISTORY / f"{name}.json"
    if path.resolve().parent == HISTORY and path.exists():
        raise LearnError(f"{path} exists: the history keeps every weights file, so give these another --name")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path
