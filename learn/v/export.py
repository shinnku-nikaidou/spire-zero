"""V's net file (docs: learning/reference §7), for `run --value`: the net as ONNX, what RunNet needs to feed it (the token
tables, the state's mean and std), test runs that RunNet values on load to check it reads them as training did, and
the training report.
"""

import base64
import io
import json
import random
from pathlib import Path

import onnx
import torch
from onnx import numpy_helper

from common import LearnError
from v import encode
from v.net import Batch, Hazards, values


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


def net_file(name, character, floors, tables, mean, std, model, held, report, seed, objective="floor") -> dict:
    """The file's content; its test runs are a few held-out decisions, valued on the CPU as RunNet values them."""
    model = model.cpu().eval()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    picks = random.Random(seed).sample(held, min(8, len(held)))
    b = Batch(picks, index, mean, std, floors, "cpu")
    with torch.no_grad():
        valued = values(model, b, objective)
    return {
        "name": name,
        "character": character,
        "objective": objective,
        "floors": floors,
        "vocab": tables,
        "state": {"names": encode.STATE, "mean": [float(v) for v in mean], "std": [float(v) for v in std]},
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        # a run's sample keeps its view as JSON (data.read_runs)
        "tests": [
            {"run": json.loads(s["view_json"]) if "view_json" in s else s["view"], "value": float(v)}
            for s, v in zip(picks, valued, strict=True)
        ],
    }


def write(net: dict, path: Path) -> None:
    with open(path, "w") as f:
        json.dump(net, f)
        f.write("\n")


def read(path: str) -> dict:
    with open(path) as f:
        return json.load(f)


def weights(net: dict, model) -> dict[str, torch.Tensor]:
    """A net file's weights by model's parameter names, from its ONNX: the named initializers, and the Linear layers the
    export turned into MatMul + Add (their weights transposed, named by their Add's bias). The second head is not in the
    file and keeps model's own."""
    graph = onnx.load_from_string(base64.b64decode(net["onnx"])).graph
    inits = {i.name: torch.from_numpy(numpy_helper.to_array(i).copy()) for i in graph.initializer}
    found = {k.removeprefix("net."): v for k, v in inits.items() if k.startswith("net.")}
    adds = {i: n for n in graph.node if n.op_type == "Add" for i in n.input}
    for n in graph.node:
        if n.op_type == "MatMul" and n.input[1] in inits and not n.input[1].startswith("net."):
            bias = next(i for i in adds[n.output[0]].input if i.startswith("net."))
            found[bias.removeprefix("net.").removesuffix(".bias") + ".weight"] = inits[n.input[1]].T
    own = model.state_dict()
    for k, v in own.items():
        if k.startswith("aux_head."):
            found[k] = v
        elif k not in found or found[k].shape != v.shape:
            raise LearnError(f"the net file has no weight {k} of shape {tuple(v.shape)}")
    return {k: found[k] for k in own}
