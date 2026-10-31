"""A run view as pure V reads it (docs: macro-brain/network §2), the one spec RunNet follows (Tokens, MapTokens, state).

Only what the run is and its raw state: each card's id, upgrade level and modifiers' ids; the relics and potions; the
character, act, boss(es) and ancient; the map points the player can still reach, each as its type and how many floors
ahead it is; and six numbers (floor, HP, max HP, gold, ascension, potion slots). No card or relic numbers and nothing
computed from the deck: what matters in them is the net's to learn.

The encoders' net (plan v7 §3, net.EncoderNet) reads the same cards, relics, potions, context and numbers, and the map
as a graph instead of a bag (`route`): where the player is, then the points a path leads to from there, each with its
type, floors ahead and quest mark, and the indices of the points it leads to.
"""

MAX_UP = 3  # RunNet.MaxUp: upgrade levels above this read as this one
TABLES = ["cards", "modifiers", "relics", "potions", "context", "map", "route"]
BAGS = {"relics": "relic_mask", "potions": "potion_mask", "context": "context_mask", "map": "map_mask"}  # and masks
STATE = ["floor", "hp", "max_hp", "gold", "ascension", "potion_slots"]
# The ONNX graph's inputs, in order (RunNet feeds them by these names).
INPUTS = ["cards", "ups", "card_mask", "mods", "mod_mask", *(x for bag in BAGS.items() for x in bag), "state"]
MAX_AHEAD = 16  # floors ahead above this read as this one (an act's map has 16 rows after its start)
ROUTE = ["route", "route_ahead", "route_quest", "route_mask", "route_next", "route_next_mask"]
# The encoders' graph's inputs, in order: the map's bag gives way to the route.
ENCODER_INPUTS = [
    *INPUTS[:5],
    "relics",
    "relic_mask",
    "potions",
    "potion_mask",
    "context",
    "context_mask",
    *ROUTE,
    "state",
]


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
    """Each reachable point as its type and how many floors ahead it is ("Elite+3"), ":quest" if a quest marks it."""
    row = m["visited"][-1]["row"] if m["visited"] else min(p["row"] for p in m["points"])
    return [f"{p['type']}+{p['row'] - row}" + (":quest" if p.get("quest") else "") for p in reachable(m)]


def route(m: dict) -> list[tuple[str, int, bool, list[int]]]:
    """The route's nodes: where the player is, then the reachable points in reachable's order, each as its type, how
    many floors ahead of the player it is (at most MAX_AHEAD), whether a quest marks it, and the nodes it leads to."""
    points = {(p["col"], p["row"]): p for p in m["points"]}
    at = here(m, points)
    nodes = [points[at], *reachable(m)]
    index = {(p["col"], p["row"]): i for i, p in enumerate(nodes)}
    return [
        (
            p["type"],
            min(p["row"] - at[1], MAX_AHEAD),
            bool(p.get("quest")),
            [index[(n["col"], n["row"])] for n in p["next"] if (n["col"], n["row"]) in index],
        )
        for p in nodes
    ]


def tokens(view: dict) -> dict:
    """The ids the net reads: cards as (id, upgrade level, modifiers), and the bags of BAGS."""
    cards = []
    for c in view["deck"]:
        mods = [f"{kind}:{c[kind]['id']}" for kind in ("enchant", "affliction") if c.get(kind)]
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
        "route": route(m),
    }


def state(view: dict) -> list[float]:
    """The numbers of STATE, raw: the net file standardizes them."""
    return [
        float(view["floor"]),
        float(view["hp"]),
        float(view["max_hp"]),
        float(view["gold"]),
        float(view.get("ascension", 0)),
        float(len(view["potions"])),
    ]
