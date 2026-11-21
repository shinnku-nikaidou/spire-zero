"""Carries a net to the tables and inputs of this checkout, with no training: a new game build brings new ids, and a
new input brings new weights. A carried net gives every state its old tables could read the value it gave before.

    cd learn && uv run python carry.py combat ../weights/all/c9-2.json --name c10-0
    cd learn && uv run python carry.py v ../weights/all/v9-2.json --name v10-0

An id the old tables lack starts from the old net's row for an id it does not know, which is how the old net read it,
except a context id of the game mode, the Dread mode's dominion or the environment: a state of before has no such token
at all, so its row starts at zero, and as the context is a sum a carried net values a Dread state, or a NoSL one, as the
old net valued it without them. A weight the old net lacks is zero: a card's growth, and the columns of a layer that
reads more numbers than before (the new numbers come last), so that V's event-option inputs read as nothing at first.
Their own tables and rows (FRESH) start as a new net's, random: with both sides zero, neither would ever learn.
Everything else is copied. The file's test states are the old file's, valued again; they must come to the old values,
and the brain checks the same on load.

A net of before the fight score (plans/2026-11-21-1604-fight-score.md) read the player's HP in its body. Carried here,
those columns of the body's first layer go (DROPPED): V's read the standardized HP and max HP, whose mean is 0, so it
reads them at their mean; the combat net's read the HP share and HP / 100, whose contribution at the old file's test
states' mean goes into the layer's bias. The combat net's old death becomes its loss of the whole max HP or more. V's
HP-shaped heads and f's head start as a new net's (FRESH_HEADS). Such a net cannot keep its values, which the training
from it then moves: the carry says how far its test states moved instead of refusing.
"""

import json
from typing import Annotated

import torch
import typer
from loguru import logger

import common
import game_ids
from combat import data as combat_data
from combat import export as combat_export
from combat import net as combat_net
from combat import spec as combat_spec
from common import LearnError
from v import export as v_export
from v import net as v_net
from v import spec as v_spec
from weights import out_path

script = common.app()

# Each net's weights whose rows follow a table: the table, and how many blocks of its rows the weight stacks.
COMBAT_ROWS = {
    **{f"{t}.weight": (t, 1) for t in combat_spec.TABLES},
    **{f"card_{x}.weight": ("cards", 1) for x in ("up", "damage", "block", "calc", "replay", "grown")},
    "mod_amount.weight": ("modifiers", 1),
    "relic_counter.weight": ("relics", 1),
    **{f"power_{x}.weight": ("powers", 1) for x in ("amount", "stacks", "detail")},
    **{f"intent_{x}.weight": ("intents", 1) for x in ("damage", "total")},
    **{f"orb_{x}.weight": ("orbs", 1) for x in ("passive", "evoke")},
    "orb_at.weight": ("orbs", combat_spec.MAX_ORBS),
    "potion_head.weight": ("potions", combat_data.POTION_GROUPS),
    "potion_head.bias": ("potions", combat_data.POTION_GROUPS),
}
V_ROWS = {
    **{f"card{x}.weight": ("cards", 1) for x in ("s", "_up", "_damage", "_block", "_grown")},
    "modifiers.weight": ("modifiers", 1),
    "mod_amount.weight": ("modifiers", 1),
    **{f"bags.{t}.weight": (t, 1) for t in ("relics", "potions", "context", "map")},
    "relic_counter.weight": ("relics", 1),
    "relic_status.weight": ("relic_status", 1),
    "pending.weight": ("pending", 1),
    "event_vars.weight": ("event_vars", 1),
}
# The weights a net of before may lack, and the layers that may read more numbers than before.
NEW = {"card_grown.weight"}
# The weights a net of before may lack that start as a new net's (random): V's event-option inputs, read through
# columns of the body that start at zero.
FRESH = {"pending.weight", "event_vars.weight", "pend_card_owned", "pend_relic_owned", "pend_potion_owned"}
# The ids whose rows start at zero when new: tokens a state of before never had (CombatTokens.ModeTokens, EnvTokens).
ZERO = ("mode:", "dominion:", "env:")
WIDER = {"enemy_scalars.weight", "body.0.weight"}
FRESH_HEADS = ("hp_weights.", "hp_scales.", "save_phi.")  # V's heads of the fight score, new in a net of before
# The body's first layer's columns a net of before the fight score read the HP through: the combat net's HP share and
# HP / 100 (after the piles), V's standardized HP and max HP (after the deck and bags, in the state's order).
COMBAT_DROPPED = [len(combat_spec.PILES) * combat_net.Net.HIDDEN + k for k in range(combat_net.Net.HP)]
V_DROPPED = [2 * v_net.Net.HIDDEN + len(v_spec.BAGS) * v_net.Net.DIM + v_spec.STATE.index(n) for n in ("hp", "max_hp")]


def dropped(old: dict[str, torch.Tensor], own: dict[str, torch.Tensor], columns: list[int], held=None) -> bool:
    """A net of before the fight score (the module's doc): its body's first layer without the HP's COLUMNS, their part
    at the inputs HELD (one per column; none: 0) put in the bias. False, and OLD as it was, for a net of after."""
    w = old["body.0.weight"]
    if w.shape[1] != own["body.0.weight"].shape[1] + len(columns):
        return False
    if held is not None:
        old["body.0.bias"] = old["body.0.bias"] + w[:, columns] @ torch.tensor(held, dtype=w.dtype)
    old["body.0.weight"] = w[:, [k for k in range(w.shape[1]) if k not in columns]]
    return True


def carried(old: dict[str, torch.Tensor], own: dict[str, torch.Tensor], rows, was: dict, now: dict, keep=()) -> dict:
    """The weights of a net of this checkout (`own`, by name) from an older net's (`old`), whose tables were `was`."""
    out = {}
    for name, mine in own.items():
        if any(name.startswith(k) for k in keep) or (name.startswith(FRESH_HEADS) and name not in old):
            out[name] = mine
            continue
        theirs = old.get(name)
        if theirs is None and name in FRESH:
            out[name] = mine
            continue
        if theirs is None and name not in NEW:
            raise LearnError(f"the old net has no weight {name}")
        if name in rows:
            table, blocks = rows[name]
            if theirs is None:
                out[name] = torch.zeros_like(mine)
                continue
            index = {i: k for k, i in enumerate(was[table])}
            source = torch.tensor([b * len(was[table]) + index.get(i, 0) for b in range(blocks) for i in now[table]])
            theirs = theirs[source]
            fresh = [i not in index and i.startswith(ZERO) for _ in range(blocks) for i in now[table]]
            theirs[torch.tensor(fresh)] = 0
        if theirs.shape != mine.shape:
            grown = (
                name in WIDER
                and theirs.dim() == 2
                and theirs.shape[0] == mine.shape[0]
                and theirs.shape[1] < mine.shape[1]
            )
            if not grown:
                raise LearnError(f"{name} is {tuple(theirs.shape)} in the old net and {tuple(mine.shape)} here")
            theirs = torch.cat([theirs, torch.zeros(mine.shape[0], mine.shape[1] - theirs.shape[1])], 1)
        out[name] = theirs.to(mine.dtype)
    return out


def from_(old: dict) -> dict:
    """What made a carried net, for its file: the old net's own record, and the net it was carried from."""
    return {**old.get("trained", {}), "rev": common.revision(), "carried": old["name"]}


@script.command()
def combat(
    file: Annotated[str, typer.Argument(help="the combat net file to carry")],
    name: Annotated[str, typer.Option(help="the carried net's name")],
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/all/NAME.json)")] = None,
):
    """The combat net of FILE on this checkout's tables and inputs."""
    old, tables = combat_export.read(file), game_ids.combat_tables()
    out_file = out_path(name, out)
    model = combat_net.Net({t: len(ids) for t, ids in tables.items()})
    buffers = [k for k, _ in model.named_buffers()]
    weights = combat_export.stored(old)
    players = [t["obs"]["player"] for t in old["tests"]]
    held = [sum(min(1.0, p["hp"] / max(1, p["max_hp"])) for p in players) / len(players), sum(min(10.0, p["hp"] / 100) for p in players) / len(players)]
    restructured = dropped(weights, model.state_dict(), COMBAT_DROPPED, held)
    model.load_state_dict(carried(weights, model.state_dict(), COMBAT_ROWS, old["vocab"], tables, buffers))
    model.eval()

    reader = common.Encoder(common.checkout_encoder())
    asked = [json.dumps({"obs": t["obs"], "history": t["history"], "env": t.get("env", "SL")}) for t in old["tests"]]
    # The targets are not read here: a batch is only the tests' inputs.
    samples = [
        {
            **t,
            **a,
            "tokens": common.pack(a["tokens"]),
            "class": 0,
            "potion_used": [],
            "share": t["obs"]["player"]["hp"] / max(1, t["obs"]["player"]["max_hp"]),
            "env": t.get("env", "SL"),
        }
        for t, a in zip(old["tests"], reader.combats(asked), strict=True)
    ]
    reader.close()
    net = combat_export.net_file(name, old["env"], tables, model, samples, old["fit"], 0, from_(old))
    by_obs = {json.dumps(t["obs"], sort_keys=True): t for t in net["tests"]}
    groups, potions = combat_data.POTION_GROUPS, {i: k for k, i in enumerate(old["vocab"]["potions"])}
    worst = 0.0
    for t in old["tests"]:
        new = by_obs[json.dumps(t["obs"], sort_keys=True)]
        gone = [
            t["potion_used"][g * len(potions) + potions.get(i, 0)] for g in range(groups) for i in tables["potions"]
        ]
        pairs = zip(t["outcome"] + gone, new["outcome"] + new["potion_used"], strict=True)
        worst = max(worst, max(abs(a - b) for a, b in pairs))
    if worst > 1e-4 and not restructured:
        raise LearnError(f"the carried net is {worst} off the old net on its test states")
    combat_export.write(net, out_file)
    logger.info("wrote {}: {} carried, {} off its test states at most", out_file, old["name"], worst)


@script.command()
def v(
    file: Annotated[str, typer.Argument(help="the V file to carry")],
    name: Annotated[str, typer.Option(help="the carried net's name")],
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/all/NAME.json)")] = None,
):
    """V of FILE on this checkout's tables and inputs."""
    old, tables = v_export.read(file), game_ids.macro_tables()
    out_file = out_path(name, out)
    torch.manual_seed(0)  # the weights that start as a new net's (FRESH): the same each carry
    model = v_net.Net({t: len(ids) for t, ids in tables.items()}, old["floors"])
    weights = v_export.stored(old)
    restructured = dropped(weights, model.state_dict(), V_DROPPED)
    buffers = [k for k, _ in model.named_buffers()]
    model.load_state_dict(carried(weights, model.state_dict(), V_ROWS, old["vocab"], tables, ["aux_head.", *buffers]))
    model.eval()

    reader = common.Encoder(common.checkout_encoder())
    answers = reader.runs([json.dumps({"run_view": t["run"], "env": t.get("env", "SL")}) for t in old["tests"]])
    reader.close()
    if old["state"]["names"] != v_spec.STATE:
        raise LearnError(f"the net reads the numbers {old['state']['names']}, this checkout's {v_spec.STATE}")
    mean, std = (
        torch.tensor(old["state"]["mean"], dtype=torch.float64),
        torch.tensor(old["state"]["std"], dtype=torch.float64),
    )
    # The targets are not read here: a batch is only the tests' inputs.
    samples = [
        {
            "tokens": common.pack(a["run"]["tokens"]),
            "state": [float(a["run"]["state"][n]) for n in v_spec.STATE],
            "view": common.pack(t["run"]),
            "start": min(max(int(a["run"]["state"]["floor"]), 0), old["floors"] - 1),
            "end": old["floors"] - 1,
            "kind": "won",
            "weight": 1.0,
            "next": [],
            "env": t.get("env", "SL"),
            "pending": "pending" in t["run"],
        }
        for t, a in zip(old["tests"], answers, strict=True)
    ]
    net = v_export.net_file(name, old["floors"], tables, mean, std, model, samples, old["fit"], 0, from_(old))
    was = {json.dumps(t["run"], sort_keys=True): t["value"] for t in old["tests"]}
    worst = max(abs(t["value"] - was[json.dumps(t["run"], sort_keys=True)]) for t in net["tests"])
    if worst > 1e-4 and not restructured:
        raise LearnError(f"the carried net is {worst} off the old net on its test runs")
    v_export.write(net, out_file)
    logger.info("wrote {}: {} carried, {} off its test runs at most", out_file, old["name"], worst)


if __name__ == "__main__":
    common.run(script)
