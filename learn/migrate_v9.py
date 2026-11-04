"""Once: V reads the numbers a run grows on a card (the user: v9-0 extended, v9-1 trained from it), so v9-0 in
weights/all/ and the test net pure-v-tiny-win take the new rows of v/net.py in place, with no training: each card's
damage and block rows and each modifier's amount row. v9-0's are zero, so it gives what it gave and its test values
are recomputed by the extended net. The test net's are random, and its first test run gets a Goopy of amount 4 on its
first card, so that RunNet and v/encode.py are held to read the new numbers alike.

    uv run python migrate_v9.py
"""

import base64
import json
import warnings
from pathlib import Path

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import onnx  # noqa: E402
import torch  # noqa: E402
from onnx import numpy_helper  # noqa: E402

from v import data, export  # noqa: E402
from v.net import Batch, Net, values  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
NEW = ("card_damage.weight", "card_block.weight", "mod_amount.weight")


def found(net: dict) -> dict[str, torch.Tensor]:
    """A file's weights by parameter name, as export.weights reads them, before the new rows are checked for."""
    graph = onnx.load_from_string(base64.b64decode(net["onnx"])).graph
    inits = {i.name: torch.from_numpy(numpy_helper.to_array(i).copy()) for i in graph.initializer}
    out = {k.removeprefix("net."): v for k, v in inits.items() if k.startswith("net.")}
    adds = {i: n for n in graph.node if n.op_type == "Add" for i in n.input}
    for n in graph.node:
        if n.op_type == "MatMul" and n.input[1] in inits and not n.input[1].startswith("net."):
            bias = next(i for i in adds[n.output[0]].input if i.startswith("net."))
            out[bias.removeprefix("net.").removesuffix(".bias") + ".weight"] = inits[n.input[1]].T
    return out


def extend(path: Path, rows: str) -> None:
    net = json.loads(path.read_text())
    old = found(net)
    widths = (old["cards.weight"].shape[1], old["phi.0.bias"].shape[0], old["body.0.bias"].shape[0])
    cls = type("Net", (Net,), dict(zip(("DIM", "HIDDEN", "BODY"), widths, strict=True)))
    model = cls({t: len(ids) for t, ids in net["vocab"].items()}, net["floors"]).eval()
    state = model.state_dict()
    torch.manual_seed(0)
    for k, v in state.items():
        if k in NEW:
            state[k] = torch.zeros_like(v) if rows == "zero" else torch.randn_like(v) * 0.3
        elif not k.startswith("aux_head."):
            state[k] = old.pop(k)
    if old:
        raise SystemExit(f"{path}: weights the net has no place for: {sorted(old)}")
    model.load_state_dict(state)
    tests = net["tests"]
    if rows == "random":
        card = tests[0]["run"]["deck"][0]
        card["enchant"] = {"id": "GOOPY", "amount": 4}
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in net["vocab"].items()}
    mean = torch.tensor(net["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(net["state"]["std"], dtype=torch.float64)
    samples = [data.sample(f"{k:016x}", t["run"], net["floors"]) for k, t in enumerate(tests)]
    example = Batch(samples, index, mean, std, net["floors"], "cpu")
    with torch.no_grad():
        valued = values(model, example)
    worst = max(abs(float(v) - t["value"]) for v, t in zip(valued, tests, strict=True))
    print(f"{net['name']}: widths {widths}, {rows} rows; largest difference on {len(tests)} test runs {worst:.3g}")
    if rows == "zero" and worst > 1e-5:
        raise SystemExit(f"extended {net['name']} is not {net['name']}")
    data_ = export.onnx_bytes(model, example)
    tests = [t | {"value": float(v)} for v, t in zip(valued, tests, strict=True)]
    export.write(net | {"onnx": base64.b64encode(data_).decode(), "tests": tests}, path)


extend(ROOT / "weights" / "all" / "v9-0.json", "zero")
extend(ROOT / "src" / "Sts2Ai.Tests" / "Fixtures" / "pure-v-tiny-win.json", "random")
