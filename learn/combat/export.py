"""The combat net's file, for `--combat-net`: the net as ONNX, its environment, what CombatNet needs
to feed it (the token tables, the buckets, the intent lags), test states that CombatNet predicts on load to check it
reads them as training did, and the training report.
"""

import base64
import io
import json
import random
from pathlib import Path

import onnx
import torch
from onnx import numpy_helper

from combat import data, spec
from combat.net import Batch, Outcome
from common import LearnError

TESTS = 16  # test states in a net file
# Every input's batch dimension and its length dimensions, by input name (the rest are fixed widths).
AXES = {
    **{x: {0: "batch", 1: "cards"} for x in ("cards", "ups", "piles", "costs", "damages", "numbers")},
    **{x: {0: "batch", 1: "cards", 2: "card_mods"} for x in ("mods", "mod_mask", "mod_amounts")},
    **{x: {0: "batch", 1: "powers"} for x in ("powers", "power_amounts", "power_mask", "power_stacks", "power_cards",
                                              "power_card_mask", "power_details")},
    "player": {0: "batch"},
    **{x: {0: "batch", 1: "enemies"} for x in ("enemies", "enemy_scalars", "enemy_mask")},
    **{x: {0: "batch", 1: "enemies", 2: "enemy_powers"} for x in ("enemy_powers", "enemy_power_amounts",
                                                                  "enemy_power_mask", "enemy_power_stacks",
                                                                  "enemy_power_cards", "enemy_power_card_mask",
                                                                  "enemy_power_details")},
    **{x: {0: "batch", 1: "enemies", 3: "intents"} for x in ("intents", "intent_values", "intent_mask")},
    **{x: {0: "batch", 1: t} for t, mask in spec.BAGS.items() for x in (t, mask)},
    **{x: {0: "batch", 1: "orbs"} for x in ("orbs", "orb_mask", "orb_values")},
    **{x: {0: "batch", 1: "allies"} for x in ("allies", "ally_scalars", "ally_mask")},
    **{x: {0: "batch", 1: "allies", 2: "ally_powers"} for x in ("ally_powers", "ally_power_amounts", "ally_power_mask")},
    **{x: {0: "batch", 1: "relics"} for x in ("relic_values", "relic_status")},
    "extra": {0: "batch"},
    **{x: {0: "batch"} for x in ("outcome", "potion_used")},  # potion_used: (batch, group, potion)
}  # fmt: skip


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    out = io.BytesIO()
    torch.onnx.export(
        Outcome(model),
        example.inputs(),
        out,
        dynamo=False,
        input_names=spec.INPUTS,
        output_names=["outcome", "potion_used"],
        dynamic_axes=AXES,
    )
    return out.getvalue()


def multi_hit(s: dict) -> bool:
    """Whether a sample's enemies show an attack of several hits now, the case the test states must not miss."""
    return any((i.get("hits") or 1) > 1 for e in s["obs"]["enemies"] for i in e.get("intents") or [])


def holds_a_potion(s: dict) -> bool:
    return any(p is not None for p in s["obs"]["potions"])


def low_hp(s: dict) -> bool:
    return s["share"] < 0.3


def net_file(name, env, tables, model, kept, report, seed, trained) -> dict:
    """The file's content; its test states are TESTS of the held-out round starts kept with their observations, among
    them, where the states kept hold one, a multi-hit attack, a potion and low HP, predicted on the CPU as CombatNet does,
    each in its environment. `env` names the environments whose games trained it ("SL+NoSL"). `trained` says what made
    the net (common.trained)."""
    model = model.cpu().eval()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    rng = random.Random(seed)
    picks = rng.sample(kept, min(TESTS, len(kept)))
    for k, wanted in zip(range(len(picks)), (multi_hit, holds_a_potion, low_hp), strict=False):
        found = [s for s in kept if wanted(s)]
        if found and not any(wanted(s) for s in picks):
            picks[k] = rng.choice(found)
    b = Batch(picks, index, "cpu")
    with torch.no_grad():
        logits, potions = model(*b.inputs())
    probs, gone = torch.softmax(logits, 1), torch.sigmoid(potions)
    return {
        "name": name,
        "env": env,
        "buckets": data.BUCKETS,
        "loss_min": data.LOSS_MIN,
        "potion_groups": data.POTION_GROUPS,
        "lags": spec.LAGS,
        "vocab": tables,
        "trained": trained,
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        "tests": [
            {
                "obs": s["obs"],
                "history": s["history"],
                "outcome": [float(v) for v in probs[k]],
                "potion_used": [float(v) for v in gone[k].flatten()],
            }
            | ({"env": s["env"]} if s.get("env", "SL") != "SL" else {})
            for k, s in enumerate(picks)
        ],
    }


def read(path: str) -> dict:
    with open(path) as f:
        return json.load(f)


def stored(net: dict) -> dict[str, torch.Tensor]:
    """A net file's weights by parameter name, from its ONNX: the named initializers, the Linear layers the export
    turned into MatMul + Add (their weights transposed, named by their Add's bias), and the weights the export found
    equal to another and keeps as its Identity (a carried net's all-zero rows)."""
    graph = onnx.load_from_string(base64.b64decode(net["onnx"])).graph
    inits = {i.name: torch.from_numpy(numpy_helper.to_array(i).copy()) for i in graph.initializer}
    found = {k.removeprefix("net."): v for k, v in inits.items() if k.startswith("net.")}
    adds = {i: n for n in graph.node if n.op_type == "Add" for i in n.input}
    for n in graph.node:
        if n.op_type == "MatMul" and n.input[1] in inits and not n.input[1].startswith("net."):
            bias = next(i for i in adds[n.output[0]].input if i.startswith("net."))
            found[bias.removeprefix("net.").removesuffix(".bias") + ".weight"] = inits[n.input[1]].T
        if n.op_type == "Identity" and n.input[0] in inits and n.output[0].startswith("net."):
            found[n.output[0].removeprefix("net.")] = inits[n.input[0]]
    return found


def weights(net: dict, model) -> dict[str, torch.Tensor]:
    """A net file's weights by model's parameter names (stored). The buffers are model's own."""
    found = stored(net)
    own = model.state_dict()
    for k, v in own.items():
        if k in dict(model.named_buffers()):
            found[k] = v
        elif k not in found or found[k].shape != v.shape:
            raise LearnError(f"the net file has no weight {k} of shape {tuple(v.shape)}")
    return {k: found[k] for k in own}


def write(net: dict, path: Path) -> None:
    with open(path, "w") as f:
        json.dump(net, f)
        f.write("\n")
