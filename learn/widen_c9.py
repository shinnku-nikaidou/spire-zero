"""Once: c9-0 and v9-0 at twice their widths (the user: 2x for five characters), in place in weights/all/, with no
training. A wider net keeps every old weight in its place: a new unit takes the fresh net's small random weights on its
way in and zeros on its way out, a new embedding dimension random values that zero columns read. So each net gives what
it gave; its file's test values are recomputed by the wide net.

    uv run python widen_c9.py
"""

import base64
import json
import warnings
from pathlib import Path

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402

from combat import encode as combat_encode  # noqa: E402
from combat import export as combat_export  # noqa: E402
from combat.net import Batch as CombatBatch  # noqa: E402
from combat.net import Net as CombatNet  # noqa: E402
from v import data as v_data  # noqa: E402
from v import encode as v_encode  # noqa: E402
from v import export as v_export  # noqa: E402
from v.net import Batch as VBatch  # noqa: E402
from v.net import Net as VNet  # noqa: E402
from v.net import values  # noqa: E402

WEIGHTS = Path(__file__).resolve().parent.parent / "weights" / "all"


def segments(old: list[int], new: list[int]) -> list[int]:
    """The new positions of the old columns of an input made of these parts."""
    cols, at = [], 0
    for a, b in zip(old, new, strict=True):
        cols += range(at, at + a)
        at += b
    return cols


def widened(old: torch.nn.Module, fresh: torch.nn.Module, layout) -> dict[str, torch.Tensor]:
    """fresh's weights with old's in their places (layout gives each Linear's input parts for a net)."""
    lo, ln = layout(old), layout(fresh)
    state, buffers = fresh.state_dict(), dict(fresh.named_buffers())
    with torch.no_grad():
        for name, w in old.state_dict().items():
            if name in buffers:
                continue
            v, module = state[name], name.rsplit(".", 1)[0]
            if module in lo:  # a Linear: old rows, old columns at their new places, zeros for the new inputs
                rows = torch.arange(w.shape[0])
                if name.endswith(".weight"):
                    cols = segments(lo[module], ln[module])
                    v[:, [c for c in range(v.shape[1]) if c not in set(cols)]] = 0
                    v[rows[:, None], torch.tensor(cols)[None, :]] = w
                else:
                    v[rows] = w
            else:  # an embedding, or a vector or matrix added into a part's dimensions
                v[..., : w.shape[-1]] = w
    return state


def combat_layout(m):
    d, h, b, s = m.DIM, m.HIDDEN, m.BODY, combat_encode.SLOTS
    return {
        "card_phi.0": [d], "card_phi.2": [h], "power_phi.0": [d],
        "enemy_scalars": [5], "enemy_phi.0": [d, h] + [d] * s, "enemy_phi.2": [2 * h],
        "ally_scalars": [4], "ally_phi.0": [d, h],
        "body.0": [h] * len(combat_encode.PILES) + [m.N_PLAYER, h, 2 * h] + [d] * 4 + [h, m.N_EXTRA], "body.2": [b],
        "head": [b], "potion_head": [b],
    }  # fmt: skip


def v_layout(m):
    d, h, b = m.DIM, m.HIDDEN, m.BODY
    return {
        "phi.0": [d], "phi.2": [h], "body.0": [h, h] + [d] * len(v_encode.BAGS) + [len(v_encode.STATE)],
        "body.2": [b], "head": [b], "aux_head": [b],
    }  # fmt: skip


def at_widths(cls, d, h, b):
    return type(cls.__name__, (cls,), {"DIM": d, "HIDDEN": h, "BODY": b})


def onnx_found(net: dict) -> dict[str, torch.Tensor]:
    """A file's weights by parameter name, as export.weights reads them, before any shape is checked."""
    import onnx
    from onnx import numpy_helper

    graph = onnx.load_from_string(base64.b64decode(net["onnx"])).graph
    inits = {i.name: torch.from_numpy(numpy_helper.to_array(i).copy()) for i in graph.initializer}
    found = {k.removeprefix("net."): v for k, v in inits.items() if k.startswith("net.")}
    adds = {i: n for n in graph.node if n.op_type == "Add" for i in n.input}
    for n in graph.node:
        if n.op_type == "MatMul" and n.input[1] in inits and not n.input[1].startswith("net."):
            bias = next(i for i in adds[n.output[0]].input if i.startswith("net."))
            found[bias.removeprefix("net.").removesuffix(".bias") + ".weight"] = inits[n.input[1]].T
        if n.op_type == "Identity" and n.input[0] in inits:
            found[n.output[0].removeprefix("net.")] = inits[n.input[0]]
    return found


def wide(net: dict, cls, phi: str, extra=()):
    """The file's net at its own widths, widened to cls's."""
    found = onnx_found(net)
    widths = (found["cards.weight"].shape[1], found[f"{phi}.0.bias"].shape[0], found["body.0.bias"].shape[0])
    sizes = {t: len(ids) for t, ids in net["vocab"].items()}
    narrow = at_widths(cls, *widths)(sizes, *extra).eval()
    own = narrow.state_dict()
    missing = sorted(k for k in own if k not in found and k not in dict(narrow.named_buffers()))
    if [k for k in missing if not k.startswith("aux_head.")]:
        raise SystemExit(f"parameters not in the file: {missing}")
    narrow.load_state_dict({k: found.get(k, v) for k, v in own.items()})
    torch.manual_seed(0)
    model = cls(sizes, *extra).eval()
    model.load_state_dict(widened(narrow, model, combat_layout if cls is CombatNet else v_layout))
    print(f"{net['name']}: widths {widths} -> {model.DIM, model.HIDDEN, model.BODY}", end="")
    return model


def combat(name: str):
    path = WEIGHTS / f"{name}.json"
    net = json.loads(path.read_text())
    model = wide(net, CombatNet, "card_phi")
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in net["vocab"].items()}
    samples = []
    for t in net["tests"]:
        tokens = combat_encode.tokens(t["obs"], t["history"])
        samples.append({"tokens": tokens, "class": 0, "potion_used": [0.0] * len(tokens["potions"])})
    example = CombatBatch(samples, index, "cpu")
    with torch.no_grad():
        logits, potions = model(*example.inputs())
    outcome, gone = torch.softmax(logits, 1), torch.sigmoid(potions)
    worst, tests = 0.0, []
    for k, t in enumerate(net["tests"]):
        worst = max(worst, max(abs(a - b) for a, b in zip(outcome[k].tolist(), t["outcome"], strict=True)))
        worst = max(worst, max(abs(a - b) for a, b in zip(gone[k].tolist(), t["potion_used"], strict=True)))
        tests.append(t | {"outcome": outcome[k].tolist(), "potion_used": gone[k].tolist()})
    print(f", {sum(p.numel() for p in model.parameters())} weights; largest difference on {len(tests)} test states "
          f"{worst:.3g}")  # fmt: skip
    if worst > 1e-5:
        raise SystemExit(f"wide {name} is not {name}")
    data = combat_export.onnx_bytes(model, example)
    combat_export.write(net | {"onnx": base64.b64encode(data).decode(), "tests": tests}, path)


def macro(name: str):
    path = WEIGHTS / f"{name}.json"
    net = json.loads(path.read_text())
    model = wide(net, VNet, "phi", extra=(net["floors"],))
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in net["vocab"].items()}
    mean = torch.tensor(net["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(net["state"]["std"], dtype=torch.float64)
    samples = [v_data.sample(f"{k:016x}", t["run"], net["floors"]) for k, t in enumerate(net["tests"])]
    example = VBatch(samples, index, mean, std, net["floors"], "cpu")
    with torch.no_grad():
        valued = values(model, example, net["objective"])
    worst = max(abs(float(v) - t["value"]) for v, t in zip(valued, net["tests"], strict=True))
    weights = sum(p.numel() for n, p in model.named_parameters() if not n.startswith("aux_head."))
    print(f", {weights} weights; largest difference on {len(samples)} test runs {worst:.3g}")
    if worst > 1e-5:
        raise SystemExit(f"wide {name} is not {name}")
    data = v_export.onnx_bytes(model, example)
    tests = [t | {"value": float(v)} for v, t in zip(valued, net["tests"], strict=True)]
    v_export.write(net | {"onnx": base64.b64encode(data).decode(), "tests": tests}, path)


combat("c9-0")
macro("v9-0")
