"""Builders of the small observations the tests need, shaped as the decision logs hold them, and the encoder that
reads them for the nets: the real one (src/Sts2Ai.Encode, which needs .NET and not the game), since what a state is read
as is the simulator's code and nothing here."""

import functools
import json

import pytest

import common


@functools.cache
def encoder_command() -> str:
    """How to start the encoder: this checkout's, built once for all the tests."""
    return common.checkout_encoder()


@functools.cache
def encoder() -> common.Encoder:
    """One encoder for all the tests."""
    return common.Encoder(encoder_command())


def read_run(view: dict, env="SL") -> dict:
    """A run view as V reads it in ENV: its tokens and its numbers by name."""
    return encoder().runs([json.dumps({"run_view": view, "env": env})])[0]["run"]


# An event option as its button shows it (EventOptionView): a card of the deck named, a relic held and one offered, a
# potion of the belt, and a number.
OPTION = {
    "index": 0,
    "key": "WELLSPRING.pages.INITIAL.options.BATHE",
    "cards": [{"card": {"id": "STRIKE", "up": 1}, "owned": True}, {"card": {"id": "GUILTY"}}],
    "relics": [{"relic": {"id": "RING", "counter": 3}, "owned": True}, {"relic": {"id": "VASE"}}],
    "potions": [{"id": "FIRE_POTION", "owned": True}],
    "numbers": {"WELLSPRING.BatheCurses": 1},
}


def round_start(obs: dict, history=(), env="SL") -> dict:
    """A round's start as combat/data.py holds it: the observation, the history logged with it, its environment and its
    tokens."""
    history = list(history)
    tokens = encoder().combats([json.dumps({"obs": obs, "history": history, "env": env})])[0]["tokens"]
    return {"obs": obs, "history": history, "env": env, "tokens": tokens}


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
            {"id": "NEUTRALIZE", "affliction": {"id": "HEX", "amount": 1}},
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


@functools.cache
def combat_net_path() -> str:
    """A combat net file with random weights, for what V's training reads f's labels by (v/saves.py)."""
    import tempfile

    import torch

    import game_ids
    from combat import data, export
    from combat.net import Net

    end = {"died": False, "hp": 30, "max_hp": 70, "potions": 0}
    starts = [round_start(make_obs(round=r, hp=40 + r, enemies=[make_enemy(1)], potions=("FIRE", None, None))) for r in (1, 2)]
    kept = [data.sample("a", s, end, {"potions": []}) | {"obs": s["obs"], "history": s["history"]} for s in starts]
    tables = game_ids.combat_tables()
    torch.manual_seed(0)
    net = export.net_file("c-test", "SL", tables, Net({t: len(i) for t, i in tables.items()}), kept, {}, 0, {})
    path = tempfile.NamedTemporaryFile("w", suffix=".json", delete=False).name
    export.write(net, __import__("pathlib").Path(path))
    return path
