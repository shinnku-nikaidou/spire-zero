"""Pure V's net file (docs: learning §6), for `run --value`: the net as ONNX, what RunNet needs to feed it (the token
tables, the state's mean and std), test runs that RunNet values on load to check it reads them as training did, and
the training report.
"""

import base64
import io
import json
import random
from pathlib import Path

import torch

from v import encode
from v.net import Batch, Hazards, expected_floor


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    axes = {name: {0: "batch", 1: "cards"} for name in ("cards", "ups", "card_mask", "mods", "mod_mask")}
    for t, mask in encode.BAGS.items():
        axes[t] = axes[mask] = {0: "batch", 1: t}
    axes |= {"state": {0: "batch"}, "hazards": {0: "batch"}}
    out = io.BytesIO()
    torch.onnx.export(
        Hazards(model),
        example.inputs(),
        out,
        dynamo=False,
        input_names=encode.INPUTS,
        output_names=["hazards"],
        dynamic_axes=axes,
    )
    return out.getvalue()


def net_file(name, character, floors, tables, mean, std, model, held, report, seed) -> dict:
    """The file's content; its test runs are a few held-out decisions, valued on the CPU as RunNet values them."""
    model = model.cpu().eval()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    picks = random.Random(seed).sample(held, min(8, len(held)))
    b = Batch(picks, index, mean, std, floors, "cpu")
    with torch.no_grad():
        values = expected_floor(torch.sigmoid(model(*b.inputs())), b.start)
    return {
        "name": name,
        "character": character,
        "objective": "floor",
        "floors": floors,
        "vocab": tables,
        "state": {"names": encode.STATE, "mean": [float(v) for v in mean], "std": [float(v) for v in std]},
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        "tests": [{"run": s["view"], "value": float(v)} for s, v in zip(picks, values, strict=True)],
    }


def write(net: dict, path: Path) -> None:
    with open(path, "w") as f:
        json.dump(net, f)
        f.write("\n")
