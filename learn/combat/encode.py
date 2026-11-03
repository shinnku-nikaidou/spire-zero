"""A round's start as the combat net reads it (docs: combat-brain/network), the one spec CombatNet follows.

The ids of what is in the fight and its raw state: each card's id, upgrade level, modifiers (enchantment, affliction,
keywords, what Tinker Time made of it, its enchantment spent) and pile; the player's HP, block, energy, stars and the
round; each power's id and amount (and the stacks, kept or stolen card and moved number a tooltip states apart); each
enemy's id, HP, block, how many rounds it has shown, its powers, and its intents now and in its last LAGS rounds (type,
damage per hit, total damage), one lying dead until it revives included; the relics with their counters and statuses,
potions, orbs, allies, the encounter and the character; the ascension, orb slots and gold. A card's current cost, damage,
block, calculated number, replays and star cost, its modifiers' amounts and each orb's passive, evoke and place in the
queue are state too; no other numbers printed on cards or relics: what matters in them is the net's to learn. Numbers
are scaled to about one (HP / 100, block / 20, …) and capped at ±CAP; CombatNet scales them the same way.
"""

MAX_UP = 3  # RunNet.MaxUp: upgrade levels above this read as this one
LAGS = 3  # the earlier rounds of each enemy's intents the net reads
SLOTS = 1 + LAGS
MAX_ORBS = 10  # the places of the orb queue the net tells apart: later ones read as the last
CAP = 10.0  # a few states show huge numbers (an erupting WATERFALL_GIANT's HP)
PILES = ["hand", "draw", "discard", "exhaust"]
NUMBERS = 5  # a card's numbers beyond its cost and damage (card)
TABLES = ["cards", "modifiers", "powers", "enemies", "intents", "relics", "potions", "context", "orbs", "allies", "relic_status"]
BAGS = {"relics": "relic_mask", "potions": "potion_mask", "context": "context_mask"}
# The ONNX graph's inputs, in order: CombatNet feeds them by these names.
INPUTS = [
    "cards",
    "ups",
    "piles",
    "mods",
    "mod_mask",
    "powers",
    "power_amounts",
    "power_mask",
    "player",
    "enemies",
    "enemy_scalars",
    "enemy_mask",
    "enemy_powers",
    "enemy_power_amounts",
    "enemy_power_mask",
    "intents",
    "intent_values",
    "intent_mask",
    *(x for bag in BAGS.items() for x in bag),
    "power_stacks",
    "power_cards",
    "power_card_mask",
    "enemy_power_stacks",
    "enemy_power_cards",
    "enemy_power_card_mask",
    "orbs",
    "orb_mask",
    "allies",
    "ally_scalars",
    "ally_mask",
    "ally_powers",
    "ally_power_amounts",
    "ally_power_mask",
    "extra",
    "costs",
    "mod_amounts",
    "relic_values",
    "relic_status",
    "damages",
    "orb_values",
    "numbers",
    "power_details",
    "enemy_power_details",
]


def cap(x: float) -> float:
    return max(-CAP, min(CAP, x))


def modifiers(c: dict) -> list[str]:
    """A card's modifier tokens: its enchantment and affliction first (in this order, as their amounts), then its
    keywords, its own and those it has for this turn only, the card type and rider Tinker Time made of it, and "spent"
    for its enchantment's effect used."""
    tokens = [f"{kind}:{c[kind]['id']}" for kind in ("enchant", "affliction") if c.get(kind)]
    tokens += [f"keyword:{k}" for k in (c.get("keywords") or []) + (c.get("this_turn") or [])]
    if c.get("tinker"):
        tokens += [f"tinker:{c['tinker']['type']}", f"rider:{c['tinker']['rider']}"]
    return tokens + (["spent"] if c.get("spent") else [])


def card(c: dict, pile: int):
    """A card: its id, upgrade level, pile, its modifiers' tokens, its cost / 3 (0 for X or none), its enchantment's and
    affliction's amounts / 10, its damage / 20 (Osty's for an Osty attack, 0 for none), and its NUMBERS: block / 20,
    calculated number / 20, replays / 2, star cost / 5 and the damage its enchantment gathered / 20 (Momentum's)."""
    held = [c[kind] for kind in ("enchant", "affliction") if c.get(kind)]
    cost = c["cost"] / 3 if c.get("cost") and c["cost"] > 0 else 0.0
    v = c.get("vars") or {}
    numbers = (
        float(v.get("Block", 0)) / 20,
        float(c.get("calc") or 0) / 20,
        (c.get("replay") or 0) / 2,
        (c.get("stars") or 0) / 5,
        ((c.get("enchant") or {}).get("extra") or 0) / 20,
    )
    return (
        c["id"],
        min(c.get("up") or 0, MAX_UP),
        pile,
        modifiers(c),
        cost,
        [cap(m["amount"] / 10) for m in held],
        cap(float(v.get("Damage", v.get("OstyDamage", 0))) / 20),
        [cap(x) for x in numbers],
    )


def intent(i: dict):
    """An intent: its type, its damage per hit / 10 and its total damage / 20 (0 for no attack)."""
    damage = i.get("damage") or 0
    hits = i["hits"] if i.get("hits") is not None else 1
    return (i["type"], cap(damage / 10), cap(damage * hits / 20))


def powers(ps: list):
    """Each power: its id, its amount / 10, the stacks its tooltip states apart from its icon / 10 (else 0), the id of
    the card it keeps (Nightmare's) or stole (Swipe's), if any, and the number moved from its own / 20 (else 0)."""
    return [
        (
            p["id"],
            cap(p["amount"] / 10),
            cap(p["stacks"] / 10) if p.get("stacks") is not None else 0.0,
            (p.get("card") or p.get("stolen") or {}).get("id"),
            cap(float(p["detail"]) / 20) if p.get("detail") is not None else 0.0,
        )
        for p in ps
    ]


def creature(c: dict) -> list[float]:
    """A creature's HP share, HP / 100, max HP / 100 and block / 20."""
    return [cap(x) for x in (c["hp"] / max(1, c["max_hp"]), c["hp"] / 100, c["max_hp"] / 100, c["block"] / 20)]


def relic_values(r: dict) -> list[float]:
    """A relic's counter / 10 (0 when none shows), whether one shows, whether it is melted."""
    counter = r.get("counter")
    return [cap(counter / 10) if counter is not None else 0.0, float(counter is not None), float(bool(r.get("melted")))]


def tokens(obs: dict, history: list) -> dict:
    """The net's tokens of a round's start. History is the record of the rounds before this one; an enemy's slots are
    its intents now, then those of its last LAGS rounds, most recent first."""
    pl = obs["player"]
    past = {h["uid"]: h["turns"] for h in history}
    enemies = []
    for e in obs["enemies"]:
        turns = past.get(e.get("uid"), [])
        slots = [e.get("intents") or []] + [t["intents"] for t in reversed(turns[-LAGS:])]
        enemies.append(
            {
                "id": e.get("id") or "<none>",
                "scalars": creature(e) + [cap(len(turns) / 10)],
                "powers": powers(e["powers"]),
                "slots": [[intent(i) for i in s] for s in slots],
            }
        )
    return {
        "cards": [card(c, p) for p, pile in enumerate(PILES) for c in obs[pile]],
        "powers": powers(pl["powers"]),
        "player": creature(pl)
        + [cap(x) for x in (pl["energy"] / 3, pl["max_energy"] / 3, pl["stars"] / 5, obs["round"] / 10)],
        "enemies": enemies,
        "relics": [r["id"] + (":used" if r.get("used_up") else "") for r in obs["relics"]],
        "relic_values": [relic_values(r) for r in obs["relics"]],
        "relic_status": [r.get("status") or ("Spent" if r.get("spent") else "Normal") for r in obs["relics"]],
        "potions": [p for p in obs["potions"] if p is not None],
        "context": [
            "character:" + pl["character"],
            "encounter:" + (obs.get("encounter") or "<none>"),
            "room:" + (obs.get("room") or "<none>"),
            f"act:{obs['act']}",
        ],
        "orbs": [o["id"] for o in pl.get("orbs") or []],
        "orb_values": [[cap(float(o["passive"]) / 10), cap(float(o["evoke"]) / 10)] for o in pl.get("orbs") or []],
        "allies": [
            {"id": a.get("id") or "<player>", "scalars": creature(a), "powers": powers(a["powers"])}
            for a in obs.get("allies") or []
            if a["hp"] > 0
        ],
        "extra": [obs.get("ascension", 0) / 10, pl.get("orb_slots", 0) / 10, cap(pl.get("gold", 0) / 100)],
    }
