"""The combat net's file (docs: learning/reference §7), for `--combat-net`: the net as ONNX, its environment, what CombatNet needs
to feed it (the token tables, the buckets, the intent lags), test states that CombatNet predicts on load to check it
reads them as training did, and the training report.
"""

import base64
import io
import json
import random
from pathlib import Path

import torch

from combat import data, encode
from combat.net import Batch, Outcome

TESTS = 16  # test states in a net file
# Every input's batch dimension and its length dimensions, by input name (the rest are fixed widths).
AXES = {
    **{x: {0: "batch", 1: "cards"} for x in ("cards", "ups", "piles", "mods", "mod_mask", "costs", "mod_amounts")},
    **{x: {0: "batch", 1: "powers"} for x in ("powers", "power_amounts", "power_mask", "power_stacks", "power_cards",
                                              "power_card_mask")},
    "player": {0: "batch"},
    **{x: {0: "batch", 1: "enemies"} for x in ("enemies", "enemy_scalars", "enemy_mask")},
    **{x: {0: "batch", 1: "enemies", 2: "enemy_powers"} for x in ("enemy_powers", "enemy_power_amounts",
                                                                  "enemy_power_mask", "enemy_power_stacks",
                                                                  "enemy_power_cards", "enemy_power_card_mask")},
    **{x: {0: "batch", 1: "enemies", 3: "intents"} for x in ("intents", "intent_values", "intent_mask")},
    **{x: {0: "batch", 1: t} for t, mask in encode.BAGS.items() for x in (t, mask)},
    **{x: {0: "batch", 1: "orbs"} for x in ("orbs", "orb_mask")},
    **{x: {0: "batch", 1: "allies"} for x in ("allies", "ally_scalars", "ally_mask")},
    **{x: {0: "batch", 1: "allies", 2: "ally_powers"} for x in ("ally_powers", "ally_power_amounts", "ally_power_mask")},
    **{x: {0: "batch", 1: "relics"} for x in ("relic_values", "relic_status")},
    "extra": {0: "batch"},
    **{x: {0: "batch"} for x in ("outcome", "potion_used")},
}  # fmt: skip


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    out = io.BytesIO()
    torch.onnx.export(
        Outcome(model),
        example.inputs(),
        out,
        dynamo=False,
        input_names=encode.INPUTS,
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


def net_file(name, character, env, tables, model, kept, report, seed) -> dict:
    """The file's content; its test states are TESTS of the held-out round starts kept with their observations, among
    them, where the states kept hold one, a multi-hit attack, a potion and low HP, predicted on the CPU as CombatNet does."""
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
        "character": character,
        "env": env,
        "buckets": data.BUCKETS,
        "loss_min": data.LOSS_MIN,
        "lags": encode.LAGS,
        "vocab": tables,
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        "tests": [
            {
                "obs": s["obs"],
                "history": s["history"],
                "outcome": [float(v) for v in probs[k]],
                "potion_used": [float(v) for v in gone[k]],
            }
            for k, s in enumerate(picks)
        ],
    }


def write(net: dict, path: Path) -> None:
    with open(path, "w") as f:
        json.dump(net, f)
        f.write("\n")
