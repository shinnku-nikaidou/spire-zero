"""V's net file, for `run --value`: the net as ONNX, what RunNet needs to feed it (the token
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

from common import LearnError, unpack
from v import spec
from v.net import Batch, Hazards, values


def input_axes() -> dict:
    """The free dimensions of the graph's inputs: every input's batch and lengths (f's graph has them too, f/export.py)."""
    p, axes = spec.PEND, {}
    for prefix in ("", p):
        cards = prefix + "cards"
        axes |= {prefix + name: {0: "batch", 1: cards} for name in ("cards", "ups", "card_mask", "numbers")}
        axes |= {prefix + name: {0: "batch", 1: cards, 2: prefix + "card_mods"} for name in ("mods", "mod_mask", "mod_amounts")}
    for t, mask in spec.BAGS.items():
        axes[t] = axes[mask] = {0: "batch", 1: t}
    axes["relic_values"] = axes["relic_status"] = {0: "batch", 1: "relics"}
    axes["held"] = axes["held_mask"] = {0: "batch", 1: "held"}
    axes["pending"] = axes["pending_mask"] = {0: "batch", 1: "pending"}
    axes[p + "cards_owned"] = {0: "batch", 1: p + "cards"}
    for group, names in (("relics", ("relics", "relic_mask", "relic_values", "relic_status", "relics_owned")),
                         ("potions", ("potions", "potion_mask", "potions_owned")), ("vars", ("vars", "var_mask", "var_values"))):  # fmt: skip
        axes |= {p + name: {0: "batch", 1: p + group} for name in names}
    return axes | {"state": {0: "batch"}}


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    axes = input_axes() | {"hazards": {0: "batch"}}
    out = io.BytesIO()
    torch.onnx.export(
        Hazards(model),
        example.inputs(),
        out,
        dynamo=False,
        input_names=spec.INPUTS,
        output_names=["hazards"],
        dynamic_axes=axes,
    )
    return out.getvalue()


def net_file(name, floors, tables, mean, std, model, held, report, seed, trained) -> dict:
    """The file's content; its test runs are a few held-out decisions (those that kept their view, data.read_runs),
    valued on the CPU as RunNet values them: loading the file, RunNet reads each view again, in its environment, and
    must come to the same value. A few of them are event options picked and not yet played out, when the held-out
    samples have any, so that the check covers those inputs too. `trained` says what made the net (common.trained)."""
    model = model.cpu().eval()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    held = [s for s in held if "view" in s]
    pending = [s for s in held if s.get("pending")]  # an event option's sample (v/data.py)
    plain = [s for s in held if not s.get("pending")]
    draw = random.Random(seed)
    picks = draw.sample(plain, min(8, len(plain))) + draw.sample(pending, min(4, len(pending)))
    b = Batch(picks, index, mean, std, floors, "cpu")
    with torch.no_grad():
        valued = values(model, b)
    return {
        "name": name,
        "floors": floors,
        "vocab": tables,
        "state": {"names": spec.STATE, "mean": [float(v) for v in mean], "std": [float(v) for v in std]},
        "trained": trained,
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        "tests": [
            {"run": unpack(s["view"]), "value": float(v)} | ({"env": s["env"]} if s.get("env", "SL") != "SL" else {})
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
    """A net file's weights by model's parameter names (stored). The second head is not in the file and keeps model's
    own."""
    found = stored(net)
    own = model.state_dict()
    for k, v in own.items():
        if k.startswith("aux_head."):
            found[k] = v
        elif k not in found or found[k].shape != v.shape:
            raise LearnError(f"the net file has no weight {k} of shape {tuple(v.shape)}")
    return {k: found[k] for k in own}
