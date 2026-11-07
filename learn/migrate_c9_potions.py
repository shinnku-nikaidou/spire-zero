"""Once: the combat net reads the potion slots (a fourth number in `extra`), and its potion head gives each potion's
chance of being gone after a win in each group of buckets (data.POTION_GROUPS), so the leaf weighs the HP lost and the
potions drunk together. The combat nets in weights/all/ and the test net combat-tiny take the new weights in place,
with no training: the body's column for the potion slots, and a potion head per group. The files in weights/all/ take
the column at zero and each group a copy of their one head, so each gives what it gave; their test states' potion
chances are written per group. The test net's column and groups are its own head plus noise, so that CombatNet and
combat/encode.py are held to read the new number and the groups alike.

    uv run python migrate_c9_potions.py
"""

import base64
import json
import warnings
from pathlib import Path

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import onnx  # noqa: E402
import torch  # noqa: E402
from onnx import numpy_helper  # noqa: E402

from combat import data, export  # noqa: E402
from combat.net import Batch, Net  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent


def found(net: dict) -> dict[str, torch.Tensor]:
    """A file's weights by parameter name, as export.weights reads them, before their shapes are checked."""
    graph = onnx.load_from_string(base64.b64decode(net["onnx"])).graph
    inits = {i.name: torch.from_numpy(numpy_helper.to_array(i).copy()) for i in graph.initializer}
    out = {k.removeprefix("net."): v for k, v in inits.items() if k.startswith("net.")}
    adds = {i: n for n in graph.node if n.op_type == "Add" for i in n.input}
    for n in graph.node:
        if n.op_type == "MatMul" and n.input[1] in inits and not n.input[1].startswith("net."):
            bias = next(i for i in adds[n.output[0]].input if i.startswith("net."))
            out[bias.removeprefix("net.").removesuffix(".bias") + ".weight"] = inits[n.input[1]].T
        if n.op_type == "Identity" and n.input[0] in inits and n.output[0].startswith("net."):
            out[n.output[0].removeprefix("net.")] = inits[n.input[0]]
    return out


def extend(path: Path, rows: str) -> None:
    net = json.loads(path.read_text())
    if "potion_groups" in net:
        raise SystemExit(f"{path}: already extended")
    old = found(net)
    widths = (old["cards.weight"].shape[1], old["card_phi.0.bias"].shape[0], old["body.0.bias"].shape[0])
    cls = type("Net", (Net,), dict(zip(("DIM", "HIDDEN", "BODY"), widths, strict=True)))
    model = cls({t: len(ids) for t, ids in net["vocab"].items()}).eval()
    state = model.state_dict()
    buffers = dict(model.named_buffers())
    torch.manual_seed(0)

    def noise(x):
        return torch.zeros_like(x) if rows == "zero" else torch.randn_like(x) * 0.3

    for k, v in state.items():
        if k in buffers:
            old.pop(k, None)
            continue
        w = old.pop(k)
        if k == "body.0.weight":
            slots = noise(torch.zeros(w.shape[0], v.shape[1] - w.shape[1]))
            state[k] = torch.cat([w, slots], 1)
        elif k in ("potion_head.weight", "potion_head.bias"):
            state[k] = torch.cat([w + noise(w) for _ in range(data.POTION_GROUPS)], 0)
        else:
            state[k] = w
        if state[k].shape != v.shape:
            raise SystemExit(f"{path}: {k} is {tuple(state[k].shape)}, the net's {tuple(v.shape)}")
    if old:
        raise SystemExit(f"{path}: weights the net has no place for: {sorted(old)}")
    model.load_state_dict(state)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in net["vocab"].items()}
    tests = net["tests"]
    samples = [data.sample("a", t["obs"], t["history"], {"died": True, "potions": 0}, None) for t in tests]
    b = Batch(samples, index, "cpu")
    with torch.no_grad():
        logits, potions = model(*b.inputs())
    probs, gone = torch.softmax(logits, 1), torch.sigmoid(potions)
    worst = max(abs(float(p) - o) for k, t in enumerate(tests) for p, o in zip(probs[k], t["outcome"], strict=True))
    first = max(abs(float(p) - u) for k, t in enumerate(tests) for p, u in zip(gone[k, 0], t["potion_used"], strict=True))
    print(f"{net['name']}: widths {widths}, {rows}; largest difference on {len(tests)} test states: endings {worst:.3g}, "
          f"potions of the first group {first:.3g}")
    if rows == "zero" and max(worst, first) > 1e-5:
        raise SystemExit(f"extended {net['name']} is not {net['name']}")
    graph = export.onnx_bytes(model, b)
    tests = [t | {"outcome": [float(v) for v in probs[k]], "potion_used": [float(v) for v in gone[k].flatten()]}
             for k, t in enumerate(tests)]  # fmt: skip
    keys = list(net)
    out = net | {"potion_groups": data.POTION_GROUPS, "onnx": base64.b64encode(graph).decode(), "tests": tests}
    at = keys.index("loss_min") + 1
    export.write({k: out[k] for k in [*keys[:at], "potion_groups", *keys[at:]]}, path)


for name in ("c9-0", "c9-1", "c9-2"):
    extend(ROOT / "weights" / "all" / f"{name}.json", "zero")
extend(ROOT / "src" / "Sts2Ai.Tests" / "Fixtures" / "combat-tiny.json", "random")
