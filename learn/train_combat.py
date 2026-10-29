"""Train the combat net (docs: learning §1), the combat search's leaf: from the start of a round, how the combat ends.

The decision logs (`run --log`) carry the observation on every combat decision; the run records (`run --records`)
carry how each combat ended, joined by run and by the combats' order, each log with the record file written beside it
(given in the same order). A sample is the first decision of a round, the
state a search leaf stands in, with the public record of the enemies' moves before it (Harness's history, rebuilt from
the earlier rounds' observations as Dispatcher builds it). The net reads the cards in each pile, the player, each
enemy with its current intents and those of its last rounds, the relics, the potions and the encounter; its head gives
the chance of each ending, a death or the share of max HP still to be lost from here in one of the buckets, and the
potions still to be used after a win. Both are relative to the state, so the HP and potions it has now count in full
(a net that predicts what is left learns only part of that, and the search then trades HP away too cheaply). A
fifth of the runs (by run id; --fold picks which) is held out, for early stopping and the report. The output is a net
file for `--combat-net`, in the character's weights folder by default (docs: learning §6): the net as ONNX, with what
the combat side needs to feed it, and test states it checks on load.

    uv run python train_combat.py --logs a.jsonl,b.jsonl --records a.jsonl,b.jsonl --name c1
"""

import base64
import io
import json
import math
import random
import re
import warnings
from typing import Annotated

import typer
from loguru import logger

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402
from torch import nn  # noqa: E402

import common  # noqa: E402
from common import LearnError, runs_by_log  # noqa: E402
from weights import out_path  # noqa: E402

script = common.app()

MAX_UP = 3  # RunNet.MaxUp
# Every scaled number the nets read is capped at ±CAP: a few states show huge ones (an erupting WATERFALL_GIANT's HP).
CAP = 10.0


def cap(x: float) -> float:
    return max(-CAP, min(CAP, x))


def card_numbers(c: dict) -> dict:
    """What a card shows beyond its id, upgrade level and modifiers' ids, as RunNet.Numbers reads it: its cost / 3 (0
    for X or none), its modifiers' amounts / 10, star cost / 3 and whether it costs X, and each of its numbers / 10 by
    name."""
    cost = c.get("cost")
    stars = c.get("stars")
    return {
        "cost": cost / 3 if cost is not None and cost > 0 else 0.0,
        "mod_amounts": [cap(m["amount"] / 10) for m in (c.get("enchant"), c.get("affliction")) if m],
        "scalars": [stars / 3 if stars is not None and stars > 0 else 0.0, 1.0 if c.get("x_cost") else 0.0],
        "vars": [(k, cap(float(v) / 10)) for k, v in (c.get("vars") or {}).items()],
    }


def relic_numbers(r: dict) -> dict:
    """A relic's counter / 10 (0 when none shows), whether one shows, whether it is melted, and its status."""
    counter = r.get("counter")
    return {
        "values": [
            cap(counter / 10) if counter is not None else 0.0,
            1.0 if counter is not None else 0.0,
            1.0 if r.get("melted") else 0.0,
        ],
        "status": r.get("status") or "Normal",
    }


def padded(rows, device):
    """Lists of ids as one padded tensor (at least one wide) and its mask."""
    width = max(1, max(len(r) for r in rows))
    ids, mask = torch.zeros(len(rows), width, dtype=torch.long), torch.zeros(len(rows), width)
    for b, r in enumerate(rows):
        ids[b, : len(r)], mask[b, : len(r)] = torch.tensor(r, dtype=torch.long), 1
    return ids.to(device), mask.to(device)


def card_number_tensors(rows, index, device):
    """Each sample's cards' numbers (card_numbers), as the inputs costs, mod_amounts, card_scalars, vars, var_values
    and var_mask: padded as the cards are, each card's numbers by name padded to the most any card has."""
    n, width = len(rows), max(1, max(len(r) for r in rows))
    m = max(1, max((len(c["vars"]) for r in rows for c in r), default=0))
    costs, mod_amounts, scalars = torch.zeros(n, width), torch.zeros(n, width, 2), torch.zeros(n, width, 2)
    names, values, mask = torch.zeros(n, width, m, dtype=torch.long), torch.zeros(n, width, m), torch.zeros(n, width, m)
    for b, r in enumerate(rows):
        for k, c in enumerate(r):
            costs[b, k] = c["cost"]
            for j, a in enumerate(c["mod_amounts"][:2]):
                mod_amounts[b, k, j] = a
            scalars[b, k] = torch.tensor(c["scalars"])
            for j, (name, v) in enumerate(c["vars"]):
                names[b, k, j], values[b, k, j], mask[b, k, j] = index["vars"].get(name, 0), v, 1
    return [x.to(device) for x in (costs, mod_amounts, scalars, names, values, mask)]


def relic_number_tensors(rows, index, device):
    """Each sample's relics' numbers (relic_numbers), as the inputs relic_values and relic_status, padded as the relics
    are."""
    n, width = len(rows), max(1, max(len(r) for r in rows))
    values, status = torch.zeros(n, width, 3), torch.zeros(n, width, dtype=torch.long)
    for b, r in enumerate(rows):
        for k, x in enumerate(r):
            values[b, k], status[b, k] = torch.tensor(x["values"]), index["relic_status"].get(x["status"], 0)
    return [values.to(device), status.to(device)]


class CardNumbers(nn.Module):
    """What a card's numbers add to its vector: cost, modifiers' amounts (each times its modifier's amount vector),
    star cost and X, and each number times its name's vector."""

    def __init__(self, sizes, dim):
        super().__init__()
        self.cost = nn.Parameter(torch.randn(dim) * 0.1)
        self.mod_amount = nn.Embedding(sizes["modifiers"], dim)
        self.scalars = nn.Linear(2, dim, bias=False)
        self.vars = nn.Embedding(sizes["vars"], dim)
        for e in (self.mod_amount, self.vars):
            nn.init.normal_(e.weight, std=0.1)

    def forward(self, mods, mod_mask, costs, mod_amounts, card_scalars, var_ids, var_values, var_mask):
        """Costs may be None, for a net that reads them on its own."""
        return (
            (0 if costs is None else costs[..., None] * self.cost)
            + (self.mod_amount(mods) * (mod_amounts * mod_mask)[..., None]).sum(2)
            + self.scalars(card_scalars)
            + (self.vars(var_ids) * (var_values * var_mask)[..., None]).sum(2)
        )


class RelicNumbers(nn.Module):
    """What a relic's numbers add to its vector: its counter times its id's counter vector, whether a counter shows
    and whether it is melted, and its status."""

    def __init__(self, sizes, dim):
        super().__init__()
        self.counter = nn.Embedding(sizes["relics"], dim)
        self.flags = nn.Linear(2, dim, bias=False)
        self.status = nn.Embedding(sizes["relic_status"], dim)
        for e in (self.counter, self.status):
            nn.init.normal_(e.weight, std=0.1)

    def forward(self, relics, relic_values, relic_status):
        return (
            relic_values[..., 0:1] * self.counter(relics)
            + self.flags(relic_values[..., 1:])
            + self.status(relic_status)
        )


PILES = ["hand", "draw", "discard", "exhaust"]
TABLES = [
    "cards",
    "modifiers",
    "powers",
    "enemies",
    "intents",
    "relics",
    "potions",
    "context",
    "vars",
    "relic_status",
    "orbs",
    "allies",
]
BAGS = ["relics", "potions", "context"]
# The ONNX graph's inputs, in order (the combat side feeds them by these names).
INPUTS = [
    "cards",
    "ups",
    "costs",
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
    "relics",
    "relic_mask",
    "potions",
    "potion_mask",
    "context",
    "context_mask",
    "mod_amounts",
    "card_scalars",
    "vars",
    "var_values",
    "var_mask",
    "power_stacks",
    "power_cards",
    "power_card_mask",
    "enemy_power_stacks",
    "enemy_power_cards",
    "enemy_power_card_mask",
    "relic_values",
    "relic_status",
    "orbs",
    "orb_values",
    "orb_mask",
    "allies",
    "ally_scalars",
    "ally_mask",
    "ally_powers",
    "ally_power_amounts",
    "ally_power_mask",
    "extra",
]
# LeafValue's default weights: what a combat's end is worth, for the report.
WIN_BASE, PER_HP, PER_POTION = 0.2, 0.6, 0.02
HEAD = re.compile(r'\{"run":"(\w+)","decision_id":\d+,"env":"\w+","surface":"(\w+)"')


def combats(paths: str):
    """Per run, its combats in order, each the list of its combat decisions. A combat closes at the next macro
    decision, or when the round goes back, as Dispatcher's history does."""
    runs = {}
    for path in paths.split(","):
        with open(path) as f:
            for line in f:
                m = HEAD.match(line)
                if not m:
                    continue
                run, surface = m.groups()
                blocks = runs.setdefault(run, [[]])
                if surface == "card_choice":
                    continue
                if surface != "combat":
                    if blocks[-1]:
                        blocks.append([])
                    continue
                try:
                    e = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if blocks[-1] and e["obs"]["round"] < blocks[-1][-1]["obs"]["round"]:
                    blocks.append([])
                blocks[-1].append(e)
    return {run: [b for b in blocks if b] for run, blocks in runs.items()}


def witness(history: dict, obs: dict):
    """As Dispatcher.Witness, on a round's first decision: each living enemy's intents, by uid."""
    for enemy in obs["enemies"]:
        if enemy.get("uid") is None or enemy.get("intents") is None or enemy["hp"] <= 0:
            continue
        history.setdefault(enemy["uid"], {"uid": enemy["uid"], "id": enemy.get("id"), "turns": []})["turns"].append(
            {"round": obs["round"], "intents": enemy["intents"]}
        )


def card_token(c: dict, pile: int):
    mods = []
    if c.get("enchant"):
        mods.append("enchant:" + c["enchant"]["id"])
    if c.get("affliction"):
        mods.append("affliction:" + c["affliction"]["id"])
    cost = c.get("cost")
    return (c["id"], min(c.get("up") or 0, MAX_UP), pile, cost / 3 if cost is not None and cost > 0 else 0.0, mods)


def intent_token(i: dict):
    """An intent: its type, its damage per hit over 10 and its total damage over 20 (0 for no attack)."""
    damage = i.get("damage") or 0
    hits = i["hits"] if i.get("hits") is not None else 1
    return (i["type"], cap(damage / 10), cap(damage * hits / 20))


def power_tokens(powers: list):
    return [(p["id"], cap(p["amount"] / 10)) for p in powers]


def power_numbers(powers: list):
    """Each power's stacks / 10 where its tooltip states them apart from its icon (else 0), and the id of the card it
    keeps (Nightmare's), if any."""
    return [
        (cap(p["stacks"] / 10) if p.get("stacks") is not None else 0.0, (p.get("card") or {}).get("id")) for p in powers
    ]


def creature_scalars(c: dict) -> list[float]:
    """A creature's HP share, HP / 100, max HP / 100 and block / 20."""
    return [cap(x) for x in (c["hp"] / max(1, c["max_hp"]), c["hp"] / 100, c["max_hp"] / 100, c["block"] / 20)]


def tokens(obs: dict, history: list, lags: int, rounds: bool):
    """The net's tokens of a round's start (the combat side reads it the same way). History is the record of the
    rounds before this one; an enemy's slots are its intents now, then those of its last rounds, most recent first."""
    cards = [card_token(c, p) for p, pile in enumerate(PILES) for c in obs[pile]]
    numbers = [card_numbers(c) for pile in PILES for c in obs[pile]]
    pl = obs["player"]
    max_hp = max(1, pl["max_hp"])
    player = [
        cap(x)
        for x in (
            pl["hp"] / max_hp,
            pl["hp"] / 100,
            pl["max_hp"] / 100,
            pl["block"] / 20,
            pl["energy"] / 3,
            pl["max_energy"] / 3,
            pl["stars"] / 5,
            obs["round"] / 10 if rounds else 0.0,
        )
    ]
    past = {h["uid"]: h["turns"] for h in history}
    enemies = []
    for e in obs["enemies"]:
        if e["hp"] <= 0:
            continue
        turns = past.get(e.get("uid"), [])
        slots = (
            [e.get("intents") or []] + [t["intents"] for t in reversed(turns[-lags:])]
            if lags
            else [e.get("intents") or []]
        )
        enemy_max = max(1, e["max_hp"])
        enemies.append(
            {
                "id": e.get("id") or "<none>",
                "scalars": [
                    cap(x)
                    for x in (
                        e["hp"] / enemy_max,
                        e["hp"] / 100,
                        e["max_hp"] / 100,
                        e["block"] / 20,
                        len(turns) / 10 if rounds else 0.0,
                    )
                ],
                "powers": power_tokens(e["powers"]),
                "power_numbers": power_numbers(e["powers"]),
                "slots": [[intent_token(i) for i in s] for s in slots],
            }
        )
    relics = [r["id"] + (":used" if r.get("used_up") else "") for r in obs["relics"]]
    potions = [p for p in obs["potions"] if p is not None]
    context = [
        "character:" + pl["character"],
        "encounter:" + (obs.get("encounter") or "<none>"),
        "room:" + (obs.get("room") or "<none>"),
        f"act:{obs['act']}",
    ]
    allies = [
        {"id": a.get("id") or "<player>", "scalars": creature_scalars(a), "powers": power_tokens(a["powers"])}
        for a in obs.get("allies") or []
        if a["hp"] > 0
    ]
    return {
        "cards": cards,
        "powers": power_tokens(pl["powers"]),
        "player": player,
        "enemies": enemies,
        "relics": relics,
        "potions": potions,
        "context": context,
        # Read only by nets that take them as inputs: the cards' and relics' numbers, the powers' stacks and kept cards,
        # orbs, allies, and the ascension, orb slots and gold.
        "card_numbers": numbers,
        "power_numbers": power_numbers(pl["powers"]),
        "relic_numbers": [relic_numbers(r) for r in obs["relics"]],
        "orbs": [(o["id"], cap(float(o["passive"]) / 10), cap(float(o["evoke"]) / 10)) for o in pl.get("orbs") or []],
        "allies": allies,
        "extra": [obs.get("ascension", 0) / 10, pl.get("orb_slots", 0) / 10, cap(pl.get("gold", 0) / 100)],
    }


def share(hp: int, max_hp: int) -> float:
    return hp / max(1, max_hp)


def outcome(obs: dict, end: dict, buckets: int, loss_min: float):
    """The class of a combat's end from a state: 0 for a death, else 1 + the bucket of the HP share lost since, the
    buckets spanning [loss_min, 1) (below 0 for a combat that healed)."""
    if end["died"]:
        return 0
    lost = share(obs["player"]["hp"], obs["player"]["max_hp"]) - share(end["hp"], end["max_hp"])
    return 1 + max(0, min(buckets - 1, int((lost - loss_min) / (1 - loss_min) * buckets)))


def next_intents(obs: dict, nxt: dict | None):
    """For each living enemy of a round's start, in the order tokens lists them, its intents at the next round's start;
    None where it is not alive then, or there is no next round in the combat."""
    alive = {}
    for e in (nxt or {}).get("enemies", []):
        if e.get("uid") is not None and e["hp"] > 0 and e.get("intents") is not None:
            alive[e["uid"]] = e["intents"]
    return [alive.get(e.get("uid")) for e in obs["enemies"] if e["hp"] > 0]


def held_potions(obs: dict) -> int:
    return sum(1 for p in obs["potions"] if p is not None)


def end_value(end: dict):
    """What LeafValue makes of a combat's end."""
    return 0.0 if end["died"] else WIN_BASE + PER_HP * end["hp"] / max(1, end["max_hp"]) + PER_POTION * end["potions"]


def leaf0(obs: dict):
    """LeafValue.Of with its default weights on a state in progress: the hand-made leaf, for the report."""
    pl = obs["player"]
    max_hp = max(1, pl["max_hp"])

    def amount(powers, pid):
        return next((p["amount"] for p in powers if p["id"] == pid), 0)

    enemies = []
    for e in obs["enemies"]:
        if e["hp"] <= 0:
            continue
        poison = amount(e["powers"], "POISON_POWER")
        hits = sum((i.get("damage") or 0) * (i.get("hits") or 1) for i in e.get("intents") or [])
        enemies.append((max(0, e["hp"] + e["block"] - min(e["hp"], 0.8 * poison * (poison + 1) / 2)), max(3, hits)))
    dps = max(1, 10 + 2 * amount(pl["powers"], "STRENGTH_POWER"))
    turns = taken = 0.0
    for hp, hits in sorted(enemies, key=lambda x: -x[1] / max(1, x[0])):
        turns += hp / dps
        taken += hits * turns
    share = (pl["hp"] - 0.5 * max(0, taken - pl["block"])) / max_hp
    win = WIN_BASE + PER_HP * share if share >= 0 else WIN_BASE * math.exp(PER_HP * share / WIN_BASE)
    return win + PER_POTION * sum(1 for p in obs["potions"] if p is not None)


def vocab(samples, min_count: int):
    """Per table, the ids seen at least min_count times in these samples; entry 0 reads every other id."""
    counts = {t: {} for t in TABLES}

    def count(t, i):
        counts[t][i] = counts[t].get(i, 0) + 1

    for s in samples:
        tok = s["tokens"]
        for card_id, _, _, _, mods in tok["cards"]:
            count("cards", card_id)
            for m in mods:
                count("modifiers", m)
        for pid, _ in tok["powers"]:
            count("powers", pid)
        for e in tok["enemies"]:
            count("enemies", e["id"])
            for pid, _ in e["powers"]:
                count("powers", pid)
            for slot in e["slots"]:
                for kind, _, _ in slot:
                    count("intents", kind)
        for t in BAGS:
            for i in tok[t]:
                count(t, i)
        for numbers in tok["card_numbers"]:
            for k, _ in numbers["vars"]:
                count("vars", k)
        for numbers in tok["relic_numbers"]:
            count("relic_status", numbers["status"])
        for _, card in tok["power_numbers"] + [x for e in tok["enemies"] for x in e["power_numbers"]]:
            if card:
                count("cards", card)
        for orb, _, _ in tok["orbs"]:
            count("orbs", orb)
        for a in tok["allies"]:
            count("allies", a["id"])
            for pid, _ in a["powers"]:
                count("powers", pid)
    return {t: ["<unk>"] + sorted(i for i, n in c.items() if n >= min_count) for t, c in counts.items()}


class Batch:
    """Samples as tensors, padded with their masks, and their targets."""

    def __init__(self, samples, index, slots, device):
        n = len(samples)
        toks = [s["tokens"] for s in samples]
        width = max(1, max(len(t["cards"]) for t in toks))
        cards, ups, costs = torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width), torch.zeros(n, width)
        piles = torch.zeros(n, width, len(PILES))
        mods, mod_mask = torch.zeros(n, width, 2, dtype=torch.long), torch.zeros(n, width, 2)
        for b, t in enumerate(toks):
            for k, (card_id, up, pile, cost, ms) in enumerate(t["cards"]):
                cards[b, k], ups[b, k], costs[b, k], piles[b, k, pile] = index["cards"].get(card_id, 0), up, cost, 1
                for j, m in enumerate(ms[:2]):
                    mods[b, k, j], mod_mask[b, k, j] = index["modifiers"].get(m, 0), 1
        powers, power_amounts, power_mask = self.powers([t["powers"] for t in toks], index)
        player = torch.tensor([t["player"] for t in toks])
        e_n = max(1, max(len(t["enemies"]) for t in toks))
        q_n = max(1, max((len(e["powers"]) for t in toks for e in t["enemies"]), default=0))
        i_n = max(1, max((len(s) for t in toks for e in t["enemies"] for s in e["slots"]), default=0))
        enemies, enemy_mask = torch.zeros(n, e_n, dtype=torch.long), torch.zeros(n, e_n)
        enemy_scalars = torch.zeros(n, e_n, 5)
        enemy_powers, enemy_power_amounts = torch.zeros(n, e_n, q_n, dtype=torch.long), torch.zeros(n, e_n, q_n)
        enemy_power_mask = torch.zeros(n, e_n, q_n)
        intents, intent_values = torch.zeros(n, e_n, slots, i_n, dtype=torch.long), torch.zeros(n, e_n, slots, i_n, 2)
        intent_mask = torch.zeros(n, e_n, slots, i_n)
        for b, t in enumerate(toks):
            for k, e in enumerate(t["enemies"]):
                enemies[b, k], enemy_mask[b, k] = index["enemies"].get(e["id"], 0), 1
                enemy_scalars[b, k] = torch.tensor(e["scalars"])
                for j, (pid, amount) in enumerate(e["powers"]):
                    enemy_powers[b, k, j], enemy_power_amounts[b, k, j], enemy_power_mask[b, k, j] = (
                        index["powers"].get(pid, 0),
                        amount,
                        1,
                    )
                for s, slot in enumerate(e["slots"][:slots]):
                    for j, (kind, damage, total) in enumerate(slot):
                        intents[b, k, s, j], intent_mask[b, k, s, j] = index["intents"].get(kind, 0), 1
                        intent_values[b, k, s, j] = torch.tensor([damage, total])
        self.tensors = [
            cards,
            ups,
            costs,
            piles,
            mods,
            mod_mask,
            powers,
            power_amounts,
            power_mask,
            player,
            enemies,
            enemy_scalars,
            enemy_mask,
            enemy_powers,
            enemy_power_amounts,
            enemy_power_mask,
            intents,
            intent_values,
            intent_mask,
        ]
        self.tensors = [x.to(device) for x in self.tensors]
        for table in BAGS:
            self.tensors.extend(padded([[index[table].get(i, 0) for i in t[table]] for t in toks], device))
        # The inputs after the bags: what the first nets did not read.
        cards_numbers = card_number_tensors([t["card_numbers"] for t in toks], index, "cpu")[1:]
        stacks, kept, kept_mask = self.power_numbers([t["power_numbers"] for t in toks], index)
        enemy_stacks, enemy_kept = torch.zeros(n, e_n, q_n), torch.zeros(n, e_n, q_n, dtype=torch.long)
        enemy_kept_mask = torch.zeros(n, e_n, q_n)
        for b, t in enumerate(toks):
            for k, e in enumerate(t["enemies"]):
                for j, (st, card) in enumerate(e["power_numbers"]):
                    enemy_stacks[b, k, j] = st
                    if card:
                        enemy_kept[b, k, j], enemy_kept_mask[b, k, j] = index["cards"].get(card, 0), 1
        relics = relic_number_tensors([t["relic_numbers"] for t in toks], index, "cpu")
        o_n = max(1, max(len(t["orbs"]) for t in toks))
        orbs, orb_values, orb_mask = torch.zeros(n, o_n, dtype=torch.long), torch.zeros(n, o_n, 2), torch.zeros(n, o_n)
        for b, t in enumerate(toks):
            for k, (orb, passive, evoke) in enumerate(t["orbs"]):
                orbs[b, k], orb_values[b, k], orb_mask[b, k] = (
                    index["orbs"].get(orb, 0),
                    torch.tensor([passive, evoke]),
                    1,
                )
        a_n = max(1, max(len(t["allies"]) for t in toks))
        aq_n = max(1, max((len(a["powers"]) for t in toks for a in t["allies"]), default=0))
        allies, ally_scalars, ally_mask = (
            torch.zeros(n, a_n, dtype=torch.long),
            torch.zeros(n, a_n, 4),
            torch.zeros(n, a_n),
        )
        ally_powers, ally_power_amounts = torch.zeros(n, a_n, aq_n, dtype=torch.long), torch.zeros(n, a_n, aq_n)
        ally_power_mask = torch.zeros(n, a_n, aq_n)
        for b, t in enumerate(toks):
            for k, a in enumerate(t["allies"]):
                allies[b, k], ally_scalars[b, k], ally_mask[b, k] = (
                    index["allies"].get(a["id"], 0),
                    torch.tensor(a["scalars"]),
                    1,
                )
                for j, (pid, amount) in enumerate(a["powers"]):
                    ally_powers[b, k, j], ally_power_amounts[b, k, j], ally_power_mask[b, k, j] = (
                        index["powers"].get(pid, 0),
                        amount,
                        1,
                    )
        extra = torch.tensor([t["extra"] for t in toks])
        self.tensors.extend(
            x.to(device)
            for x in [
                *cards_numbers,
                stacks,
                kept,
                kept_mask,
                enemy_stacks,
                enemy_kept,
                enemy_kept_mask,
                *relics,
                orbs,
                orb_values,
                orb_mask,
                allies,
                ally_scalars,
                ally_mask,
                ally_powers,
                ally_power_amounts,
                ally_power_mask,
                extra,
            ]
        )
        # Each enemy's intents at the next round's start, for the next-intent head: their types, and the total damage.
        types = len(index["intents"])
        next_types, next_total, next_mask = torch.zeros(n, e_n, types), torch.zeros(n, e_n), torch.zeros(n, e_n)
        for b, s in enumerate(samples):
            for k, nxt in enumerate(s.get("next") or []):
                if nxt is None:
                    continue
                next_mask[b, k] = 1
                for kind, _, total in (intent_token(i) for i in nxt):
                    next_types[b, k, index["intents"].get(kind, 0)] = 1
                    next_total[b, k] += total
        self.next_types, self.next_total, self.next_mask = (
            next_types.to(device),
            next_total.to(device),
            next_mask.to(device),
        )
        self.target = torch.tensor([s["class"] for s in samples], dtype=torch.long, device=device)
        self.used = torch.tensor(
            [held_potions(s["obs"]) - s["end"]["potions"] for s in samples], dtype=torch.float, device=device
        )
        self.share = torch.tensor(
            [share(s["obs"]["player"]["hp"], s["obs"]["player"]["max_hp"]) for s in samples], device=device
        )
        self.held = torch.tensor([float(held_potions(s["obs"])) for s in samples], device=device)

    @staticmethod
    def powers(rows, index):
        width = max(1, max(len(r) for r in rows))
        ids, amounts, mask = (
            torch.zeros(len(rows), width, dtype=torch.long),
            torch.zeros(len(rows), width),
            torch.zeros(len(rows), width),
        )
        for b, r in enumerate(rows):
            for k, (pid, amount) in enumerate(r):
                ids[b, k], amounts[b, k], mask[b, k] = index["powers"].get(pid, 0), amount, 1
        return ids, amounts, mask

    @staticmethod
    def power_numbers(rows, index):
        """The powers' stacks and kept cards (power_numbers), padded as Batch.powers pads the powers."""
        width = max(1, max(len(r) for r in rows))
        stacks, cards, mask = (
            torch.zeros(len(rows), width),
            torch.zeros(len(rows), width, dtype=torch.long),
            torch.zeros(len(rows), width),
        )
        for b, r in enumerate(rows):
            for k, (st, card) in enumerate(r):
                stacks[b, k] = st
                if card:
                    cards[b, k], mask[b, k] = index["cards"].get(card, 0), 1
        return stacks, cards, mask

    def inputs(self):
        """The net's inputs, in the order of INPUTS."""
        return tuple(self.tensors)


def mlp(*sizes):
    layers = []
    for a, b in zip(sizes, sizes[1:], strict=False):
        layers += [nn.Linear(a, b), nn.ReLU()]
    return nn.Sequential(*layers)


class Net(nn.Module):
    def __init__(self, sizes, slots, buckets, n_player=8, width=1.0, sum_scale=1 / 8, n_extra=3):
        dim, hidden, body = round(32 * width), round(64 * width), round(256 * width)
        super().__init__()
        self.sum_scale = sum_scale
        self.cards = nn.Embedding(sizes["cards"], dim)
        self.card_up = nn.Embedding(sizes["cards"], dim)
        self.modifiers = nn.Embedding(sizes["modifiers"], dim)
        self.pile = nn.Parameter(torch.randn(len(PILES), dim) * 0.1)
        self.cost = nn.Parameter(torch.randn(dim) * 0.1)
        self.powers = nn.Embedding(sizes["powers"], dim)
        self.power_amount = nn.Embedding(sizes["powers"], dim)
        self.enemies = nn.Embedding(sizes["enemies"], dim)
        self.intents = nn.Embedding(sizes["intents"], dim)
        self.intent_damage = nn.Embedding(sizes["intents"], dim)
        self.intent_total = nn.Embedding(sizes["intents"], dim)
        self.slot = nn.Parameter(torch.randn(slots, dim) * 0.1)
        self.relics = nn.Embedding(sizes["relics"], dim)
        self.potions = nn.Embedding(sizes["potions"], dim)
        self.context = nn.Embedding(sizes["context"], dim)
        self.power_stacks = nn.Embedding(sizes["powers"], dim)
        self.orbs = nn.Embedding(sizes["orbs"], dim)
        self.orb_passive = nn.Embedding(sizes["orbs"], dim)
        self.orb_evoke = nn.Embedding(sizes["orbs"], dim)
        self.allies = nn.Embedding(sizes["allies"], dim)
        self.card_numbers = CardNumbers(sizes, dim)
        self.relic_numbers = RelicNumbers(sizes, dim)
        for m in self.modules():
            if isinstance(m, nn.Embedding):
                nn.init.normal_(m.weight, std=0.1)
        self.card_phi = mlp(dim, hidden, hidden)
        self.power_phi = mlp(dim, hidden)
        self.enemy_scalars = nn.Linear(5, dim)
        self.enemy_phi = mlp(dim + hidden + slots * dim, 2 * hidden, 2 * hidden)
        self.ally_scalars = nn.Linear(4, dim)
        self.ally_phi = mlp(dim + hidden, hidden)
        self.body = mlp(len(PILES) * hidden + n_player + hidden + 2 * hidden + 4 * dim + hidden + n_extra, body, body)
        self.head = nn.Linear(body, 1 + buckets)
        self.potion_head = nn.Linear(body, 1)
        # Training only: each enemy's next intents from its own vector, which makes the net learn the monsters' moves.
        self.next_head = nn.Linear(2 * hidden, sizes["intents"] + 1)

    def power_sum(self, ids, amounts, mask, stacks=None, cards=None, card_mask=None):
        """The powers through power_phi, summed; with their stacks and kept cards where the inputs give them."""
        x = self.powers(ids) + amounts[..., None] * self.power_amount(ids)
        if stacks is not None:
            x = x + stacks[..., None] * self.power_stacks(ids) + self.cards(cards) * card_mask[..., None]
        return (self.power_phi(x) * mask[..., None]).sum(-2)

    def forward(self, *inputs):
        """Per sample, the logits of the ending classes and the potions used after a win (inputs as INPUTS names them)."""
        z, _ = self.encode(*inputs)
        return self.head(z), self.potion_head(z)[:, 0]

    def encode(
        self,
        cards,
        ups,
        costs,
        piles,
        mods,
        mod_mask,
        powers,
        power_amounts,
        power_mask,
        player,
        enemies,
        enemy_scalars,
        enemy_mask,
        enemy_powers,
        enemy_power_amounts,
        enemy_power_mask,
        intents,
        intent_values,
        intent_mask,
        relics,
        relic_mask,
        potions,
        potion_mask,
        context,
        context_mask,
        mod_amounts,
        card_scalars,
        var_ids,
        var_values,
        var_mask,
        power_stacks,
        power_cards,
        power_card_mask,
        enemy_power_stacks,
        enemy_power_cards,
        enemy_power_card_mask,
        relic_values,
        relic_status,
        orbs,
        orb_values,
        orb_mask,
        allies,
        ally_scalars,
        ally_mask,
        ally_powers,
        ally_power_amounts,
        ally_power_mask,
        extra,
    ):
        """The body's vector of each sample, and each enemy's own vector."""
        x = (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + costs[..., None] * self.cost
            + (self.modifiers(mods) * mod_mask[..., None]).sum(2)
            + piles @ self.pile
            + self.card_numbers(mods, mod_mask, None, mod_amounts, card_scalars, var_ids, var_values, var_mask)
        )
        h = self.card_phi(x)
        per_pile = torch.bmm(piles.transpose(1, 2), h).flatten(1) * self.sum_scale
        i = (
            self.intents(intents)
            + intent_values[..., 0:1] * self.intent_damage(intents)
            + intent_values[..., 1:2] * self.intent_total(intents)
        )
        slots = ((i * intent_mask[..., None]).sum(3) + self.slot).flatten(2)
        e = torch.cat(
            [
                self.enemies(enemies) + self.enemy_scalars(enemy_scalars),
                self.power_sum(
                    enemy_powers,
                    enemy_power_amounts,
                    enemy_power_mask,
                    enemy_power_stacks,
                    enemy_power_cards,
                    enemy_power_card_mask,
                ),
                slots,
            ],
            2,
        )
        each = self.enemy_phi(e)
        foes = (each * enemy_mask[..., None]).sum(1)
        mine = self.power_sum(powers, power_amounts, power_mask, power_stacks, power_cards, power_card_mask)
        parts = [per_pile, player, mine, foes]
        relic = self.relics(relics) + self.relic_numbers(relics, relic_values, relic_status)
        parts.append((relic * relic_mask[..., None]).sum(1))
        for table, ids, mask in ((self.potions, potions, potion_mask), (self.context, context, context_mask)):
            parts.append((table(ids) * mask[..., None]).sum(1))
        orb = (
            self.orbs(orbs)
            + orb_values[..., 0:1] * self.orb_passive(orbs)
            + orb_values[..., 1:2] * self.orb_evoke(orbs)
        )
        parts.append((orb * orb_mask[..., None]).sum(1))
        ally = self.ally_phi(
            torch.cat(
                [
                    self.allies(allies) + self.ally_scalars(ally_scalars),
                    self.power_sum(ally_powers, ally_power_amounts, ally_power_mask),
                ],
                2,
            )
        )
        parts += [(ally * ally_mask[..., None]).sum(1), extra]
        return self.body(torch.cat(parts, 1)), each


class Outcome(nn.Module):
    """The net as ONNX holds it: the chance of each ending, and the potions used after a win."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        logits, potions = self.net(*inputs)
        return torch.softmax(logits, 1), potions


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    axes = {
        "cards": {0: "batch", 1: "cards"},
        "ups": {0: "batch", 1: "cards"},
        "costs": {0: "batch", 1: "cards"},
        "piles": {0: "batch", 1: "cards"},
        "mods": {0: "batch", 1: "cards"},
        "mod_mask": {0: "batch", 1: "cards"},
        "player": {0: "batch"},
        "outcome": {0: "batch"},
        "potions_used": {0: "batch"},
    }
    for name in ("powers", "power_amounts", "power_mask"):
        axes[name] = {0: "batch", 1: "powers"}
    for name in ("enemies", "enemy_scalars", "enemy_mask"):
        axes[name] = {0: "batch", 1: "enemies"}
    for name in ("enemy_powers", "enemy_power_amounts", "enemy_power_mask"):
        axes[name] = {0: "batch", 1: "enemies", 2: "enemy_powers"}
    for name in ("intents", "intent_values", "intent_mask"):
        axes[name] = {0: "batch", 1: "enemies", 3: "intents"}
    for t, mask in (("relics", "relic_mask"), ("potions", "potion_mask"), ("context", "context_mask")):
        axes[t] = axes[mask] = {0: "batch", 1: t}
    for name in ("mod_amounts", "card_scalars"):
        axes[name] = {0: "batch", 1: "cards"}
    for name in ("vars", "var_values", "var_mask"):
        axes[name] = {0: "batch", 1: "cards", 2: "vars"}
    for name in ("power_stacks", "power_cards", "power_card_mask"):
        axes[name] = {0: "batch", 1: "powers"}
    for name in ("enemy_power_stacks", "enemy_power_cards", "enemy_power_card_mask"):
        axes[name] = {0: "batch", 1: "enemies", 2: "enemy_powers"}
    for name in ("relic_values", "relic_status"):
        axes[name] = {0: "batch", 1: "relics"}
    for name in ("orbs", "orb_values", "orb_mask"):
        axes[name] = {0: "batch", 1: "orbs"}
    for name in ("allies", "ally_scalars", "ally_mask"):
        axes[name] = {0: "batch", 1: "allies"}
    for name in ("ally_powers", "ally_power_amounts", "ally_power_mask"):
        axes[name] = {0: "batch", 1: "allies", 2: "ally_powers"}
    axes["extra"] = {0: "batch"}
    out = io.BytesIO()
    torch.onnx.export(
        Outcome(model),
        example.inputs(),
        out,
        dynamo=False,
        input_names=INPUTS,
        output_names=["outcome", "potions_used"],
        dynamic_axes=axes,
    )
    return out.getvalue()


def values(probs: torch.Tensor, used: torch.Tensor, b: "Batch", buckets: int, loss_min: float):
    """What LeafValue's weights make of the predicted ending: the HP share now less each bucket's middle, and the
    potions held now less those used, after a win."""
    mids = loss_min + (1 - loss_min) * (torch.arange(buckets, device=probs.device) + 0.5) / buckets
    won = 1 - probs[:, 0]
    return (
        won * WIN_BASE + PER_HP * (probs[:, 1:] * (b.share[:, None] - mids)).sum(1) + won * PER_POTION * (b.held - used)
    )


def next_intent_report(model, test, test_batches, index, batch):
    """How well the next-intent head predicts each held-out enemy's next intents: the share whose set of types it gets
    exactly, and the mean error of the total damage, against guessing that the enemy shows the same as now."""
    rows = []
    with torch.no_grad():
        for start, b in zip(range(0, len(test), batch), test_batches, strict=True):
            out = model.next_head(model.encode(*b.inputs())[1]).cpu()
            for bi, s in enumerate(test[start : start + batch]):
                for k, e in enumerate(s["tokens"]["enemies"]):
                    if not b.next_mask[bi, k]:
                        continue
                    truth = b.next_types[bi, k].cpu() > 0.5
                    now = torch.zeros_like(truth)
                    for kind, _, _ in e["slots"][0]:
                        now[index["intents"].get(kind, 0)] = True
                    total, total_now = float(b.next_total[bi, k]), sum(t for _, _, t in e["slots"][0])
                    rows.append(
                        (
                            e["id"],
                            bool(((out[bi, k, :-1] > 0) == truth).all()),
                            bool((now == truth).all()),
                            20 * abs(float(out[bi, k, -1]) - total),
                            20 * abs(total_now - total),
                        )
                    )
    by = {}
    for enemy, hit, hit_now, _, _ in rows:
        by.setdefault(enemy, []).append((hit, hit_now))
    top = sorted(by.items(), key=lambda x: -len(x[1]))[:20]
    n = max(1, len(rows))
    return {
        "enemies": len(rows),
        "type_accuracy": sum(r[1] for r in rows) / n,
        "type_accuracy_now": sum(r[2] for r in rows) / n,
        "damage_mae": sum(r[3] for r in rows) / n,
        "damage_mae_now": sum(r[4] for r in rows) / n,
        "by_enemy": {e: [len(v), sum(h for h, _ in v) / len(v), sum(h for _, h in v) / len(v)] for e, v in top},
    }


@script.command(help=__doc__)
def main(
    logs: Annotated[str, typer.Option(help="decision logs, comma-separated")],
    records: Annotated[str, typer.Option(help="run records, comma-separated")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    out: Annotated[
        str | None, typer.Option(help="where the file goes (default: weights/CHARACTER/NAME.json)")
    ] = None,
    character: Annotated[
        str, typer.Option(help="the character whose runs these are: each has a brain of its own")
    ] = "SILENT",
    buckets: Annotated[int, typer.Option(min=1, help="buckets of the HP share lost from here after a win")] = 50,
    loss_min: Annotated[float, typer.Option(max=0, help="the lowest bucket's bottom: a share healed")] = -0.25,
    lags: Annotated[int, typer.Option(min=0, help="earlier rounds of each enemy's intents the net reads")] = 3,
    rounds: Annotated[
        bool, typer.Option("--round/--no-round", help="show the round and how many rounds each enemy has shown")
    ] = True,
    min_count: Annotated[int, typer.Option(help="ids seen fewer times read as unknown")] = 3,
    epochs: Annotated[int, typer.Option(min=1)] = 60,
    patience: Annotated[int, typer.Option(min=1, help="epochs without a better held-out loss before stopping")] = 6,
    batch: Annotated[int, typer.Option(min=1)] = 256,
    lr: float = 1e-3,
    weight_decay: float = 1e-4,
    potion_weight: Annotated[float, typer.Option(help="weight of the potions' squared error in the loss")] = 0.1,
    fold: Annotated[int, typer.Option(min=0, max=4, help="which fifth of the runs (by run id) is held out")] = 0,
    next_weight: Annotated[float, typer.Option(min=0, help="weight of the next-intent head's loss (0: no head)")] = 0.0,
    train_share: Annotated[
        float, typer.Option(min=0, max=1, help="share of the training runs used, for learning curves")
    ] = 1.0,
    width: Annotated[float, typer.Option(min=0.05, help="multiplies every layer's width")] = 1.0,
    seed: int = 0,
) -> None:
    out_file = out_path(name, out, character)
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    samples, skipped = [], 0
    for i, (log, runs) in enumerate(runs_by_log(logs, records, character)):
        for run, blocks in combats(log).items():
            r = runs.get(run)
            if r is None or r.get("failure"):
                continue
            fights = r["combats"]
            if len(blocks) != len(fights) or any(
                b[0]["obs"]["encounter"] != f["encounter"] for b, f in zip(blocks, fights, strict=True)
            ):
                skipped += 1
                continue
            for block, fight in zip(blocks, fights, strict=True):
                if "end" not in fight:
                    continue
                starts, last = [], -1
                for e in block:
                    if e["obs"]["round"] != last:
                        starts.append(e["obs"])
                        last = e["obs"]["round"]
                history = {}
                for k, obs in enumerate(starts):
                    past = [{"uid": h["uid"], "id": h["id"], "turns": list(h["turns"])} for h in history.values()]
                    samples.append(
                        {
                            "key": (i, run),
                            "run": run,
                            "obs": obs,
                            "history": past,
                            "tokens": tokens(obs, past, lags, rounds),
                            "next": next_intents(obs, starts[k + 1] if k + 1 < len(starts) else None),
                            "end": fight["end"],
                            "class": outcome(obs, fight["end"], buckets, loss_min),
                            "value": end_value(fight["end"]),
                        }
                    )
                    witness(history, obs)

    def held_out(run_id: str) -> bool:
        return int(run_id, 16) % 5 == fold

    train = [s for s in samples if not held_out(s["run"]) and int(s["run"], 16) // 5 % 1000 < train_share * 1000]
    test = [s for s in samples if held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")
    logger.info(
        "{} round starts from {} runs; {} runs skipped, their combats not matching the record",
        len(samples),
        len({s["key"] for s in samples}),
        skipped,
    )

    tables = vocab(train, min_count)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    slots = 1 + lags

    def batches(part, shuffle):
        order = list(range(len(part)))
        if shuffle:
            random.shuffle(order)
        for i in range(0, len(order), batch):
            yield Batch([part[k] for k in order[i : i + batch]], index, slots, device)

    def loss_of(logits, potions, b: Batch):
        won = (b.target > 0).float()
        return (
            nn.functional.cross_entropy(logits, b.target, reduction="none")
            + potion_weight * won * (potions - b.used) ** 2
        )

    def next_loss(model, each, b: Batch):
        """Per sample, the next-intent head's loss: each type's cross-entropy, and the total damage's squared error."""
        pred = model.next_head(each)
        types = nn.functional.binary_cross_entropy_with_logits(pred[..., :-1], b.next_types, reduction="none").sum(-1)
        return ((types + (pred[..., -1] - b.next_total) ** 2) * b.next_mask).sum(1)

    test_batches = list(batches(test, False))

    def evaluate(model):
        """The held-out loss, the cross-entropy of each held-out sample, and the net's value of each."""
        total, ces, rows = 0.0, [], []
        with torch.no_grad():
            for b in test_batches:
                logits, potions = model(*b.inputs())
                total += float(loss_of(logits, potions, b).sum())
                ces.append(nn.functional.cross_entropy(logits, b.target, reduction="none"))
                rows.append(values(torch.softmax(logits, 1), potions, b, buckets, loss_min))
        return total / len(test), torch.cat(ces).double().cpu(), torch.cat(rows).double().cpu()

    model = Net({t: len(ids) for t, ids in tables.items()}, slots, buckets, width=width).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=weight_decay)
    best, best_state, stale = math.inf, None, 0
    for epoch in range(epochs):
        model.train()
        for b in batches(train, True):
            z, each = model.encode(*b.inputs())
            loss = loss_of(model.head(z), model.potion_head(z)[:, 0], b)
            if next_weight:
                loss = loss + next_weight * next_loss(model, each, b)
            loss = loss.mean()
            opt.zero_grad()
            loss.backward()
            opt.step()
        model.eval()
        held, _, _ = evaluate(model)
        logger.info("epoch {}: held-out loss {:.4f}", epoch, held)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= patience:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: held-out loss against a prior that knows only the training endings, and how well the net's value
    # and LeafValue's predict what the combat's end was worth.
    held, ces, predicted = evaluate(model)
    ce = float(ces.mean())
    # By room, since a monster's move order should matter most in the long fights of elites and bosses.
    rooms = [s["obs"].get("room") or "<none>" for s in test]
    ce_by_room = {r: float(ces[[k for k, x in enumerate(rooms) if x == r]].mean()) for r in sorted(set(rooms))}
    next_report = next_intent_report(model, test, test_batches, index, batch) if next_weight else None
    counts = torch.bincount(torch.tensor([s["class"] for s in train]), minlength=1 + buckets).double() + 0.5
    prior = counts / counts.sum()
    ce_prior = -float(sum(math.log(prior[s["class"]]) for s in test)) / len(test)
    actual = torch.tensor([s["value"] for s in test], dtype=torch.float64)
    hand = torch.tensor([leaf0(s["obs"]) for s in test], dtype=torch.float64)
    died = torch.tensor([s["class"] == 0 for s in test], dtype=torch.float64)

    def corr(a, b):
        a, b = a - a.mean(), b - b.mean()
        return float((a * b).sum() / (a.norm() * b.norm()).clamp_min(1e-12))

    report = {
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "held_out_samples": len(test),
        "deaths": int(sum(1 for s in samples if s["class"] == 0)),
        "fold": fold,
        "lags": lags,
        "round": rounds,
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_ce": ce,
        "held_out_ce_prior": ce_prior,
        "held_out_ce_by_room": ce_by_room,
        "held_out_samples_by_room": {r: rooms.count(r) for r in sorted(set(rooms))},
        "value_mse": float(((predicted - actual) ** 2).mean()),
        "value_corr": corr(predicted, actual),
        "value_mse_leaf0": float(((hand - actual) ** 2).mean()),
        "value_corr_leaf0": corr(hand, actual),
        "death_corr": corr(1 - predicted, died),
        "next_intents": next_report,
    }
    print(json.dumps(report))

    # Test states for the combat side's check on load: a few held-out round starts, as the CPU computes them.
    model = model.cpu()
    picks = random.Random(seed).sample(test, min(8, len(test)))
    with torch.no_grad():
        b = Batch(picks, index, slots, "cpu")
        logits, potions = model(*b.inputs())
        probs = torch.softmax(logits, 1)
    tests = [
        {
            "obs": s["obs"],
            "history": s["history"],
            "outcome": [float(v) for v in probs[k]],
            "potions_used": float(potions[k]),
        }
        for k, s in enumerate(picks)
    ]

    net = {
        "name": name,
        "character": character,
        "buckets": buckets,
        "loss_min": loss_min,
        "lags": lags,
        "round": rounds,
        "vocab": tables,
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        "tests": tests,
    }
    with open(out_file, "w") as f:
        json.dump(net, f)
        f.write("\n")
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
