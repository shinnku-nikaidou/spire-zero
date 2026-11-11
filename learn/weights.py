"""Where weights files go. The combat net and V are each one net for every character and
ascension, so a trainer writes its file to weights/all/NAME.json unless --out says otherwise. A file in weights/all is
never replaced, so each name is used once and always means the same weights; the champion files name them where they
are.

weights/all is not in git: the files live in the repository's release "weights". weights/put.sh NAME adds a new net
there once it is to be kept (a challenger going to a gate, a champion), and weights/fetch.sh brings the nets the
champion files name, or the ones named, into a checkout."""

from pathlib import Path

from common import LearnError

ROOT = Path(__file__).resolve().parent.parent / "weights"
SHARED = "all"


def out_path(name: str, out: str | None) -> Path:
    """--out if given, else the name's file in weights/all; a file already in weights/all is never replaced."""
    path = Path(out) if out else ROOT / SHARED / f"{name}.json"
    if path.resolve().parent == (ROOT / SHARED).resolve() and path.exists():
        raise LearnError(f"{path} exists: a weights name is used once, so give these another --name")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path
