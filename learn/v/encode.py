"""A run view as V reads it (docs: macro-brain/network §2), the one spec RunNet follows (Tokens, MapTokens, state).

Only what the run is and its raw state: each card's id, upgrade level and modifiers (enchantment, affliction, keywords,
what Tinker Time made of it); the relics and potions; the
character, act, boss(es) and ancient; the map points the player can still reach, each as its type and how many floors
ahead it is; and six numbers (floor, HP, max HP, gold, ascension, potion slots). No card or relic numbers and nothing
computed from the deck: what matters in them is the net's to learn.

A map move's afterstate (`entered`) is marked as entering the point: the point's own type reads as "Elite+0", so that
options with the same points after them differ. The mark never stands for a room already played: a run view logged in
a room comes after its combat, and a run that died there logs none, so a net that read the type of the room it is in
would take entering an elite for having beaten it.
"""

MAX_UP = 3  # RunNet.MaxUp: upgrade levels above this read as this one
TABLES = ["cards", "modifiers", "relics", "potions", "context", "map"]
BAGS = {"relics": "relic_mask", "potions": "potion_mask", "context": "context_mask", "map": "map_mask"}  # and masks
STATE = ["floor", "hp", "max_hp", "gold", "ascension", "potion_slots"]
# The ONNX graph's inputs, in order (RunNet feeds them by these names).
INPUTS = ["cards", "ups", "card_mask", "mods", "mod_mask", *(x for bag in BAGS.items() for x in bag), "state"]


def here(m: dict, points: dict) -> tuple[int, int]:
    """Where the player is: the last visited point, or the start (the first point by row, col) before any."""
    if m["visited"]:
        return (m["visited"][-1]["col"], m["visited"][-1]["row"])
    return min(points, key=lambda k: (k[1], k[0]))


def reachable(m: dict) -> list[dict]:
    """The map points a path leads to from where the player is (the last visited point, or the start before any),
    the player's own point left out, sorted by (row, col)."""
    points = {(p["col"], p["row"]): p for p in m["points"]}
    seen, todo = set(), [here(m, points)]
    while todo:
        for n in points[todo.pop()]["next"]:
            key = (n["col"], n["row"])
            if key not in seen and key in points:
                seen.add(key)
                todo.append(key)
    return sorted((points[k] for k in seen), key=lambda p: (p["row"], p["col"]))


def map_tokens(m: dict) -> list[str]:
    """Each reachable point as its type and how many floors ahead it is ("Elite+3"), ":quest" if a quest marks it; first
    the point being entered, in a map move's afterstate ("Elite+0")."""
    row = m["visited"][-1]["row"] if m["visited"] else min(p["row"] for p in m["points"])
    points = {(p["col"], p["row"]): p for p in m["points"]}
    nodes = ([points[here(m, points)]] if m.get("entering") else []) + reachable(m)
    return [f"{p['type']}+{p['row'] - row}" + (":quest" if p.get("quest") else "") for p in nodes]


def entered(view: dict, action: str) -> dict:
    """The afterstate of the map move `map:COL,ROW`: the point added to the path and marked as entered (as
    MacroBrain.After does)."""
    col, row = (int(x) for x in action.removeprefix("map:").split(","))
    m = view["map"] | {"visited": [*view["map"]["visited"], {"col": col, "row": row}], "entering": True}
    return view | {"map": m}


def tokens(view: dict) -> dict:
    """The ids the net reads: cards as (id, upgrade level, modifiers), and the bags of BAGS."""
    cards = []
    for c in view["deck"]:
        mods = [f"{kind}:{c[kind]['id']}" for kind in ("enchant", "affliction") if c.get(kind)]
        mods += [f"keyword:{k}" for k in c.get("keywords") or []]
        if c.get("tinker"):
            mods += [f"tinker:{c['tinker']['type']}", f"rider:{c['tinker']['rider']}"]
        cards.append((c["id"], min(c.get("up") or 0, MAX_UP), mods))
    m = view["map"]
    context = ["character:" + view["character"], "act:" + view["act_id"], "boss:" + m["boss"]]
    if m.get("second_boss"):
        context.append("boss:" + m["second_boss"])
    if m.get("ancient"):
        context.append("ancient:" + m["ancient"])
    return {
        "cards": cards,
        "relics": [r["id"] + (":used" if r.get("used_up") else "") for r in view["relics"]],
        "potions": [p for p in view["potions"] if p is not None],
        "context": context,
        "map": map_tokens(m),
    }


def state(view: dict) -> list[float]:
    """The numbers of STATE, raw: the net file standardizes them."""
    return [
        float(view["floor"]),
        float(view["hp"]),
        float(view["max_hp"]),
        float(view["gold"]),
        float(view["ascension"]),
        float(len(view["potions"])),
    ]
