"""The nets' token tables, the same for every character: every id of the game build, in learn/game_ids.json (written by the
sim's `tables` command), so that five brains share one shape. Entry 0 reads any other id, as one newer than the file."""

import json
from pathlib import Path

IDS = json.loads((Path(__file__).parent / "game_ids.json").read_text())
AHEAD = 21  # map tokens name a point up to this many floors ahead: past the rows of an act and its boss


def table(ids) -> list[str]:
    return ["<unk>", *sorted(ids)]


def modifiers() -> list[str]:
    return [f"enchant:{i}" for i in IDS["enchantments"]] + [f"affliction:{i}" for i in IDS["afflictions"]]


def relics() -> list[str]:
    return IDS["relics"] + [f"{i}:used" for i in IDS["used_up_relics"]]


def combat_tables() -> dict[str, list[str]]:
    """The combat net's. Of the context only the encounter: it says the room, the act and, per character's net, the
    character."""
    return {
        "cards": table(IDS["cards"]),
        "modifiers": table(modifiers()),
        "powers": table(IDS["powers"]),
        "enemies": table(IDS["monsters"]),
        "intents": table(IDS["intents"]),
        "relics": table(relics()),
        "potions": table(IDS["potions"]),
        "context": table(f"encounter:{i}" for i in IDS["encounters"]),
        "orbs": table(IDS["orbs"]),
        "allies": table(IDS["monsters"]),
        "relic_status": table(IDS["relic_status"]),
    }


def macro_tables() -> dict[str, list[str]]:
    """V's."""
    return {
        "cards": table(IDS["cards"]),
        "modifiers": table(modifiers()),
        "relics": table(relics()),
        "potions": table(IDS["potions"]),
        "context": table(
            [f"act:{i}" for i in IDS["acts"]]
            + [f"ancient:{i}" for i in IDS["ancients"]]
            + [f"boss:{i}" for i in IDS["bosses"]]
            + [f"character:{i}" for i in IDS["characters"]]
        ),
        "map": table(f"{p}+{k}{quest}" for p in IDS["points"] for k in range(AHEAD) for quest in ("", ":quest")),
    }
