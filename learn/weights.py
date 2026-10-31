"""Where weights files go (docs: learning/reference §6). Each character is trained on its own, every ascension by the same brain,
so its files have a folder of their own: a trainer writes its file to weights/CHARACTER/NAME.json unless --out says
otherwise. A file there is never replaced, so each name is used once and always means the same weights; the champion
files name them where they are (docs: evaluation/champions §1)."""

from pathlib import Path

from common import LearnError

ROOT = Path(__file__).resolve().parent.parent / "weights"


def folder(character: str) -> Path:
    """The character's weights folder: weights/silent for SILENT."""
    return ROOT / character.lower()


def out_path(name: str, out: str | None, character: str) -> Path:
    """--out if given, else the name's file in the character's folder; a file already in a character's folder is never
    replaced."""
    path = Path(out) if out else folder(character) / f"{name}.json"
    if path.resolve().parent.parent == ROOT.resolve() and path.exists():
        raise LearnError(f"{path} exists: a weights name is used once, so give these another --name")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path
