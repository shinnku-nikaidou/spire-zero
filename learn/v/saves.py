"""f's labels (plans/2026-11-21-1604-fight-score.md, 2.3): of each macro decision, each potion it holds, the HP that
potion saved in the fight where its run later drank it, as the combat net says at that fight's first round start.

A fight's result is what the player carries out of it, HP + max HP, 0 for a death: the score a fight's search ranks by,
without its potions. A potion saved the combat net's expected result of the fight with it, less that with it taken out
of the belt. One that left the belt outside a fight (thrown away, swapped on a reward, or drunk there) saved nothing:
0; so did one the run still held when it was won or lost. One still held where the run's record stops short of an end is not known: None.

Copies of one potion go in turn: the first copy held is the first to leave. A potion gained after the decision is
no potion of it, but a copy of one held leaves in its turn all the same.
"""

import functools
import json
from collections import Counter

import torch

from combat import data as combat_data
from combat import export as combat_export
from combat import spec as combat_spec
from combat.net import Batch as CombatBatch
from combat.net import Net as CombatNet
from common import HEAD, pack

BATCH = 256


@functools.cache
def combat_net(path: str):
    """The combat net of a file, on the CPU, with its tables' index; once a process."""
    net = combat_export.read(path)
    tables = net["vocab"]
    model = CombatNet({t: len(ids) for t, ids in tables.items()})
    model.load_state_dict(combat_export.weights(net, model))
    model.eval()
    return model, {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}


def carried(model, index, tokens: list) -> list[float]:
    """The combat net's expected result of each state (HP + max HP after a win, 0 for a death), as the leaf reads its
    endings: a win's HP is the share held less the bucket's middle, at least 1 and at most the max HP."""
    out = []
    mids = combat_data.LOSS_MIN + (torch.arange(combat_data.BUCKETS, dtype=torch.float64) + 0.5) * combat_data.WIDTH
    for k in range(0, len(tokens), BATCH):
        samples = [{"tokens": pack(t), "class": 0, "potion_used": []} for t in tokens[k : k + BATCH]]
        b = CombatBatch(samples, index, "cpu")
        with torch.no_grad():
            p = torch.softmax(model(*b.inputs())[0], 1).double()
        player = b.tensors[combat_spec.INPUTS.index("player")].double()
        top = (player[:, 2:3] * 100).round()  # max HP / 100
        hp = ((player[:, 0:1] - mids[None, :]) * top).round().clamp(min=1)
        hp = torch.minimum(hp, top)
        out += ((p[:, 1:] * (hp + top)).sum(1)).tolist()
    return out


def timeline(log: str, runs: dict) -> dict[str, list[dict]]:
    """Per run of RUNS, in order: its macro decisions (`decision`, `belt`) and its fights (`line`: the first round
    start's line, `belt` there; `after`: the belt of the first run view after it, None for a fight its run died in)."""
    out = {}
    with open(log) as f:
        for line in f:
            m = HEAD.match(line)
            if not m or m[1] not in runs or not line.endswith("\n"):
                continue
            run, surface = m[1], m[3]
            if surface == "card_choice":
                continue
            events = out.setdefault(run, [])
            e = json.loads(line)
            if surface == "combat":
                fight = events[-1] if events and events[-1]["kind"] == "fight" and events[-1]["after"] is None else None
                if fight is None or e["obs"]["round"] < fight["round"]:
                    events.append({"kind": "fight", "line": line, "belt": e["obs"]["potions"], "after": None, "round": e["obs"]["round"]})
                else:
                    fight["round"] = e["obs"]["round"]
                continue
            view = e.get("run_view")
            if view is None:
                continue
            if events and events[-1]["kind"] == "fight" and events[-1]["after"] is None:
                events[-1]["after"] = view["potions"]
            events.append({"kind": "macro", "decision": e["decision_id"], "belt": view["potions"]})
    return out


def departures(events: list[dict], start: int, potion: str) -> list[tuple[str, int | None]]:
    """After the macro decision at EVENTS[START], each time a copy of POTION leaves the belt, in order: ("fight", k)
    drunk in the fight at k, ("away", None) gone outside a fight."""
    out = []
    held = Counter(p for p in events[start]["belt"] if p)[potion]
    for k in range(start + 1, len(events)):
        e = events[k]
        if e["kind"] == "fight":
            at = Counter(p for p in e["belt"] if p)[potion]
            out += [("away", None)] * max(0, held - at)
            after = at if e["after"] is None else Counter(p for p in e["after"] if p)[potion]
            out += [("fight", k)] * max(0, at - after)
            held = after
        else:
            now = Counter(p for p in e["belt"] if p)[potion]
            out += [("away", None)] * max(0, held - now)
            held = now
    return out


def labels(log: str, runs: dict, encoder, net: str) -> dict[tuple[str, int], list[tuple[str, float | None]]]:
    """Per (run, decision) of a log's macro decisions that hold a potion, each potion held in the belt's order with
    what it saved (the module's doc), by the combat net of the file NET. RUNS: the log's runs' records."""
    model, index = combat_net(net)
    asked = {}  # (run, fight's index, potion) -> where its two states are among `lines`
    lines = []
    plan = {}
    for run, events in timeline(log, runs).items():
        ended = runs[run].get("outcome") in ("won", "lost")
        for i, e in enumerate(events):
            if e["kind"] != "macro" or not any(e["belt"]):
                continue
            gone = {}
            row = []
            for p in (p for p in e["belt"] if p):
                ways = gone.setdefault(p, departures(events, i, p))
                way = ways.pop(0) if ways else ("end" if ended else "open", None)
                if way[0] == "fight" and (run, way[1], p) not in asked:
                    fight = events[way[1]]
                    obs = json.loads(fight["line"])
                    without = list(obs["obs"]["potions"])
                    without[without.index(p)] = None
                    asked[(run, way[1], p)] = len(lines)
                    lines += [fight["line"], json.dumps(obs | {"obs": obs["obs"] | {"potions": without}})]
                row.append((p, way))
            plan[(run, e["decision"])] = row
    results = carried(model, index, [r["tokens"] for r in encoder.combats(lines, log)]) if lines else []
    out = {}
    for (run, decision), row in plan.items():
        got = []
        for p, (kind, k) in row:
            if kind == "fight":
                at = asked[(run, k, p)]
                got.append((p, results[at] - results[at + 1]))
            else:
                got.append((p, None if kind == "open" else 0.0))
        out[(run, decision)] = got
    return out
