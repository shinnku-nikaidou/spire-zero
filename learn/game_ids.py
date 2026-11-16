"""The nets' token tables, the same for every character: every id of the game build, in learn/game_ids.json (written by the
sim's `tables` command), so that every character shares one shape. Entry 0 reads any other id, as one newer than the file."""

import json
from pathlib import Path

IDS = json.loads((Path(__file__).parent / "game_ids.json").read_text())
AHEAD = 21  # map tokens name a point up to this many floors ahead: past the rows of an act and its boss


def table(ids) -> list[str]:
    return ["<unk>", *sorted(ids)]


def modifiers() -> list[str]:
    """What a card carries beyond its id: its enchantment, affliction and keywords, what Tinker Time made of it, and
    (in combat) its enchantment spent."""
    return (
        [f"enchant:{i}" for i in IDS["enchantments"]]
        + [f"affliction:{i}" for i in IDS["afflictions"]]
        + [f"keyword:{i}" for i in IDS["keywords"]]
        + [f"tinker:{i}" for i in IDS["card_types"]]
        + [f"rider:{i}" for i in IDS["riders"]]
        + ["spent"]
    )


def relics() -> list[str]:
    return IDS["relics"] + [f"{i}:used" for i in IDS["used_up_relics"]]


def mode_ids() -> list[str]:
    """Both nets' context ids of the game mode and the Dread mode's dominion (CombatTokens.ModeTokens)."""
    return [f"mode:{i}" for i in IDS["modes"]] + [f"dominion:{i}" for i in IDS["dominions"]]


def combat_tables() -> dict[str, list[str]]:
    """The combat net's. Of the context the encounter, which says the room and the act, the character, and the game
    mode and dominion; a relic's status also reads "Spent" for a once-a-combat effect used."""
    return {
        "cards": table(IDS["cards"]),
        "modifiers": table(modifiers()),
        "powers": table(IDS["powers"]),
        "enemies": table(IDS["monsters"]),
        "intents": table(IDS["intents"]),
        "relics": table(relics()),
        "potions": table(IDS["potions"]),
        "context": table(
            [f"encounter:{i}" for i in IDS["encounters"]] + [f"character:{i}" for i in IDS["characters"]] + mode_ids()
        ),
        "orbs": table(IDS["orbs"]),
        "allies": table(IDS["monsters"]),
        "relic_status": table([*IDS["relic_status"], "Spent"]),
    }


def macro_tables() -> dict[str, list[str]]:
    """V's; a relic's status as the combat net's."""
    return {
        "cards": table(IDS["cards"]),
        "modifiers": table(modifiers()),
        "relics": table(relics()),
        "relic_status": table([*IDS["relic_status"], "Spent"]),
        "potions": table(IDS["potions"]),
        "context": table(
            [f"act:{i}" for i in IDS["acts"]]
            + [f"ancient:{i}" for i in IDS["ancients"]]
            + [f"boss:{i}" for i in IDS["bosses"]]
            + [f"character:{i}" for i in IDS["characters"]]
            + mode_ids()
        ),
        "map": table(f"{p}+{k}{quest}" for p in IDS["points"] for k in range(AHEAD) for quest in ("", ":quest")),
    }
