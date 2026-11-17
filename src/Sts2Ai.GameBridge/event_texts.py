"""Writes EventTexts.json next to this file, from the game's `events` text table: every event option's text key (the
game finds an option's title by "KEY.title"), the names `tables` lists for V's event-option rows; and for each text that
fills in variables, the variables' names, by the text's key ("WELLSPRING.pages.INITIAL.options.BATHE.description":
["BatheCurses"]), by which event buttons read the numbers they show (EventActions.Numbers): the headless game loads no
text, and the mod reads the same file, so that a button shows the same numbers in both. Keys and names only: no text of
the game's.

Run it again whenever the game is ported (its text may change with it), from the game's pack:

    python3 src/Sts2Ai.GameBridge/event_texts.py [PACK]

PACK defaults to $STS2_PCK, else Steam's SlayTheSpire2.pck.
"""

import json
import os
import re
import struct
import sys
from pathlib import Path

PACK = Path.home() / ".local/share/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.pck"
TABLE = "localization/eng/events.json"
VARIABLE = re.compile(r"\{([A-Za-z0-9_]+)")  # "{Name}" or "{Name:format}", as SmartFormat reads them


def read(pack: Path, wanted: str) -> bytes:
    """A file of a Godot pack: its file table, then each file at base + offset."""
    with open(pack, "rb") as f:
        head = f.read(40)
        if head[:4] != b"GDPC":
            raise SystemExit(f"{pack} is not a Godot pack")
        base, at = struct.unpack("<Q", head[24:32])[0], struct.unpack("<Q", head[32:40])[0]
        f.seek(at)
        for _ in range(struct.unpack("<I", f.read(4))[0]):
            path = f.read(struct.unpack("<I", f.read(4))[0]).rstrip(b"\0").decode()
            offset, size = struct.unpack("<QQ", f.read(16))
            f.read(20)
            if path == wanted:
                f.seek(base + offset)
                return f.read(size)
    raise SystemExit(f"{pack} holds no {wanted}")


def main() -> None:
    pack = Path(sys.argv[1] if len(sys.argv) > 1 else os.environ.get("STS2_PCK", PACK))
    texts = json.loads(read(pack, TABLE))
    names = {}
    for key, text in sorted(texts.items()):
        found = sorted(set(VARIABLE.findall(text)))
        if found:
            names[key] = found
    options = sorted(k.removesuffix(".title") for k in texts if ".options." in k and k.endswith(".title"))
    out = Path(__file__).with_name("EventTexts.json")
    lines = [f'"options": {json.dumps(options)}', '"shown": {\n' + ",\n".join(f"{json.dumps(k)}: {json.dumps(v)}" for k, v in names.items()) + "\n}"]
    out.write_text("{\n" + ",\n".join(lines) + "\n}\n")
    print(f"wrote {out}: {len(options)} options, {len(names)} texts that show variables, of {len(texts)}")


if __name__ == "__main__":
    main()
