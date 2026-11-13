"""Carries a net to the tables and inputs of this checkout, with no training: a new game build brings new ids, and a
new input brings new weights. A carried net gives every state its old tables could read the value it gave before.

    cd learn && uv run python carry.py combat ../weights/all/c9-2.json --name c10-0
    cd learn && uv run python carry.py v ../weights/all/v9-2.json --name v10-0

An id the old tables lack starts from the old net's row for an id it does not know, which is how the old net read it.
A weight the old net lacks is zero: a card's growth, and the columns of a layer that reads more numbers than before
(the new numbers come last). Everything else is copied. The file's test states are the old file's, valued again; they
must come to the old values, and the brain checks the same on load.
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
}
# The weights a net of before may lack, and the layers that may read more numbers than before.
NEW = {"card_grown.weight"}
WIDER = {"enemy_scalars.weight", "body.0.weight"}


def carried(old: dict[str, torch.Tensor], own: dict[str, torch.Tensor], rows, was: dict, now: dict, keep=()) -> dict:
    """The weights of a net of this checkout (`own`, by name) from an older net's (`old`), whose tables were `was`."""
    out = {}
    for name, mine in own.items():
        if any(name.startswith(k) for k in keep):
            out[name] = mine
            continue
        theirs = old.get(name)
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
    model.load_state_dict(
        carried(combat_export.stored(old), model.state_dict(), COMBAT_ROWS, old["vocab"], tables, buffers)
    )
    model.eval()

    reader = common.Encoder(common.checkout_encoder())
    asked = [json.dumps({"obs": t["obs"], "history": t["history"]}) for t in old["tests"]]
    # The targets are not read here: a batch is only the tests' inputs.
    samples = [
        {
            **t,
            **a,
            "class": 0,
            "potion_used": [],
            "share": t["obs"]["player"]["hp"] / max(1, t["obs"]["player"]["max_hp"]),
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
    if worst > 1e-4:
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
    model = v_net.Net({t: len(ids) for t, ids in tables.items()}, old["floors"])
    model.load_state_dict(
        carried(v_export.stored(old), model.state_dict(), V_ROWS, old["vocab"], tables, ["aux_head."])
    )
    model.eval()

    reader = common.Encoder(common.checkout_encoder())
    answers = reader.runs([json.dumps({"run_view": t["run"]}) for t in old["tests"]])
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
            "tokens": a["run"]["tokens"],
            "state": [float(a["run"]["state"][n]) for n in v_spec.STATE],
            "view_json": json.dumps(t["run"]),
            "start": min(max(int(a["run"]["state"]["floor"]), 0), old["floors"] - 1),
            "end": old["floors"] - 1,
            "kind": "won",
            "weight": 1.0,
            "next": [],
        }
        for t, a in zip(old["tests"], answers, strict=True)
    ]
    net = v_export.net_file(name, old["floors"], tables, mean, std, model, samples, old["fit"], 0, from_(old))
    was = {json.dumps(t["run"], sort_keys=True): t["value"] for t in old["tests"]}
    worst = max(abs(t["value"] - was[json.dumps(t["run"], sort_keys=True)]) for t in net["tests"])
    if worst > 1e-4:
        raise LearnError(f"the carried net is {worst} off the old net on its test runs")
    v_export.write(net, out_file)
    logger.info("wrote {}: {} carried, {} off its test runs at most", out_file, old["name"], worst)


if __name__ == "__main__":
    common.run(script)
