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


@pytest.fixture
def enemy():
    return make_enemy


@pytest.fixture
def obs():
    return make_obs
