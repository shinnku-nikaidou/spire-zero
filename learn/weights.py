"""Where weights files go (docs: learning §6). Each character is trained on its own, every ascension by the same brain,
so its files have a folder of their own: a trainer writes its file to weights/CHARACTER/history/NAME.json unless --out
says otherwise; git ignores weights/, and the history is never overwritten, so each name is used once. A promotion
copies the champion's files onto the character's main names in its environment, weights/CHARACTER/ENV/{value,leaf,
combat}.json (docs: evaluation §5)."""

from pathlib import Path

from common import LearnError

ROOT = Path(__file__).resolve().parent.parent / "weights"


def history(character: str) -> Path:
    """The character's weights history: weights/silent/history for SILENT."""
    return ROOT / character.lower() / "history"


def out_path(name: str, out: str | None, character: str) -> Path:
    """--out if given, else the name's file in the character's history; a file already in a history is never
    replaced."""
    path = Path(out) if out else history(character) / f"{name}.json"
    folder = path.resolve().parent
    if folder.name == "history" and folder.parent.parent == ROOT.resolve() and path.exists():
        raise LearnError(f"{path} exists: the history keeps every weights file, so give these another --name")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path
