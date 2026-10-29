"""Builders of the small observations the tests need, shaped as the decision logs hold them."""

import pytest


def make_enemy(uid, hp=10, intents=({"type": "Attack", "damage": 6, "hits": 1},), monster="M"):
    return {
        "uid": uid,
        "id": monster,
        "hp": hp,
        "max_hp": 20,
        "block": 0,
        "powers": [],
        "intents": None if intents is None else list(intents),
    }


def make_obs(round=1, hp=50, max_hp=70, enemies=(), potions=(None, None, None)):
    player = {
        "uid": 0,
        "character": "SILENT",
        "hp": hp,
        "max_hp": max_hp,
        "block": 0,
        "energy": 3,
        "max_energy": 3,
        "stars": 0,
        "gold": 0,
        "powers": [],
        "orb_slots": 0,
        "orbs": [],
    }
    return {
        "encounter": "E",
        "room": "Monster",
        "act": 0,
        "ascension": 0,
        "round": round,
        "player": player,
        "hand": [],
        "draw": [],
        "discard": [],
        "exhaust": [],
        "potions": list(potions),
        "relics": [],
        "allies": [],
        "enemies": list(enemies),
    }


def point(col, row, kind, *nxt, quest=None):
    p = {"col": col, "row": row, "type": kind, "next": [{"col": c, "row": r} for c, r in nxt]}
    return p | ({"quest": quest} if quest else {})


# Start (0,0) -> (0,1) Monster or (1,1) Elite; both -> (0,2) RestSite -> (0,3) Boss; (2,1) leads nowhere from here.
MAP = [
    point(0, 0, "Ancient", (0, 1), (1, 1)),
    point(0, 1, "Monster", (0, 2)),
    point(1, 1, "Elite", (0, 2), quest=True),
    point(2, 1, "Shop", (0, 2)),
    point(0, 2, "RestSite", (0, 3)),
    point(0, 3, "Boss"),
]


def make_view(visited=((0, 0),), **over):
    v = {
        "character": "SILENT",
        "ascension": 3,
        "act": 0,
        "act_id": "OVERGROWTH",
        "floor": 1,
        "hp": 50,
        "max_hp": 70,
        "gold": 99,
        "deck": [
            {"id": "STRIKE", "up": 5, "enchant": {"id": "SHARP", "amount": 2}},
            {"id": "NEUTRALIZE", "affliction": {"id": "HEX"}},
        ],
        "relics": [{"id": "RING", "counter": 3}, {"id": "VASE", "used_up": True}],
        "potions": ["FIRE", None, None],
        "map": {"points": MAP, "visited": [{"col": c, "row": r} for c, r in visited], "boss": "B", "ancient": "NEOW"},
    }
    return v | over


@pytest.fixture
def enemy():
    return make_enemy


@pytest.fixture
def obs():
    return make_obs
