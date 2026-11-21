"""f's net file, for `run --f-net`: V's format (v/export.py: the token tables, the state's mean and std, the net as
ONNX), with the potions asked about as one more input and their f as the output, test runs that the brain gives f
again on load (each with f of the potions it holds, in the belt's order), and the training report.
"""

import base64
import io
import random

import torch
from torch import nn

from common import LearnError, unpack
from f.net import INPUTS, Batch, Net
from v import export as v_export
from v import spec

read, write = v_export.read, v_export.write


class Graph(nn.Module):
    """The net as ONNX holds it, its weights named net.* as V's are (v/export.py's `stored` reads them back)."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        return self.net(*inputs)


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    axes = v_export.input_axes() | {"asked": {0: "batch", 1: "asked"}, "saves": {0: "batch", 1: "asked"}}
    out = io.BytesIO()
    torch.onnx.export(
        Graph(model), example.inputs(), out, dynamo=False, input_names=INPUTS, output_names=["saves"], dynamic_axes=axes
    )
    return out.getvalue()


def net_file(name, tables, mean, std, model, held, report, seed, trained) -> dict:
    """The file's content; its test runs are a few held-out decisions that kept their view (v/data.py) and hold a
    potion, some of them event options picked and not yet played out when there are any, each potion it holds asked
    about in the belt's order as the brain asks (the run's potion tokens), with f on the CPU."""
    model = model.cpu().eval()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    held = [s for s in held if "view" in s and unpack(s["tokens"])["potions"]]
    pending = [s for s in held if s.get("pending")]
    plain = [s for s in held if not s.get("pending")]
    draw = random.Random(seed)
    picks = draw.sample(plain, min(8, len(plain))) + draw.sample(pending, min(4, len(pending)))
    picks = [s | {"saves": [(p, None) for p in unpack(s["tokens"])["potions"]]} for s in picks]
    b = Batch(picks, index, mean, std, "cpu")
    with torch.no_grad():
        f = model(*b.inputs())
    return {
        "name": name,
        "vocab": tables,
        "state": {"names": spec.STATE, "mean": [float(v) for v in mean], "std": [float(v) for v in std]},
        "trained": trained,
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        "tests": [
            {"run": unpack(s["view"]), "saves": [float(x) for x in f[k, : len(s["saves"])]]}
            | ({"env": s["env"]} if s.get("env", "SL") != "SL" else {})
            for k, s in enumerate(picks)
        ],
    }


def weights(file: dict, model: Net) -> dict[str, torch.Tensor]:
    """The weights f goes on from: a V file's body (every weight of it under model's names; V's own heads are not
    model's), with model's own head; or an f file's, its head too."""
    found = v_export.stored(file)
    own = model.state_dict()
    out = {}
    for k, v in own.items():
        if k in found and found[k].shape == v.shape:
            out[k] = found[k]
        elif k.startswith("head.") or k == "seen":
            out[k] = v
        else:
            raise LearnError(f"{file['name']} has no weight {k} of shape {tuple(v.shape)}")
    return out
