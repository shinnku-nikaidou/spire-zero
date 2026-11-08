"""Once: V reads the game's rule, whether each act's plain monster fights are won at their start (encode.STATE's
auto_win_0 to auto_win_2), so the V files in weights/all/ and the test net pure-v-tiny-win take three more state
numbers in place, with no training: their mean 0 and std 1, and the body's columns for them. The files in weights/all/
take the columns at zero, so each gives what it gave on every run; the test net's are random, and its first test run
plays by the rule in acts 0 and 1, so that RunNet and v/encode.py are held to read it alike.

    uv run python migrate_v9_rule.py
"""

import base64
import json
import warnings
from pathlib import Path

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import onnx  # noqa: E402
import torch  # noqa: E402
from onnx import numpy_helper  # noqa: E402

from v import data, encode, export  # noqa: E402
from v.net import Batch, Net, values  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
NEW = [f"auto_win_{a}" for a in range(encode.ACTS)]


def found(net: dict) -> dict[str, torch.Tensor]:
    """A file's weights by parameter name, as export.weights reads them, before the new columns are checked for."""
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
    names = net["state"]["names"]
    if names != encode.STATE[: len(encode.STATE) - len(NEW)]:
        raise SystemExit(f"{path}: reads the state {names} already")
    old = found(net)
    d, h, body = old["cards.weight"].shape[1], old["phi.0.bias"].shape[0], old["body.0.bias"].shape[0]
    cls = type("Net", (Net,), {"DIM": d, "HIDDEN": h, "BODY": body})
    model = cls({t: len(ids) for t, ids in net["vocab"].items()}, net["floors"]).eval()
    state = model.state_dict()
    torch.manual_seed(0)
    for k in list(state):
        if k == "body.0.weight":
            w = old.pop(k)
            # the body reads the deck's mean and scaled sum, the bags, the state, then the kept cards' sum (net.Net.forward)
            at = 2 * h + len(encode.BAGS) * d + len(names)
            cols = torch.zeros(w.shape[0], len(NEW)) if rows == "zero" else torch.randn(w.shape[0], len(NEW)) * 0.3
            state[k] = torch.cat([w[:, :at], cols, w[:, at:]], 1)
        elif not k.startswith("aux_head."):
            state[k] = old.pop(k)
    if old:
        raise SystemExit(f"{path}: weights the net has no place for: {sorted(old)}")
    model.load_state_dict(state)
    net["state"] = {
        "names": encode.STATE,
        "mean": net["state"]["mean"] + [0.0] * len(NEW),
        "std": net["state"]["std"] + [1.0] * len(NEW),
    }
    tests = net["tests"]
    if rows == "random":
        tests[0]["run"]["auto_win"] = [0, 1]
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in net["vocab"].items()}
    mean = torch.tensor(net["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(net["state"]["std"], dtype=torch.float64)
    samples = [data.sample(f"{k:016x}", t["run"], net["floors"]) for k, t in enumerate(tests)]
    example = Batch(samples, index, mean, std, net["floors"], "cpu")
    with torch.no_grad():
        valued = values(model, example)
    worst = max(abs(float(v) - t["value"]) for v, t in zip(valued, tests, strict=True))
    print(f"{net['name']}: widths {(d, h, body)}, {rows} columns; largest difference on {len(tests)} test runs {worst:.3g}")
    if rows == "zero" and worst > 1e-5:
        raise SystemExit(f"extended {net['name']} is not {net['name']}")
    if rows == "zero":
        # the same on a run played by the rule: the columns are zero
        ruled = [data.sample(s["run"], s["view"] | {"auto_win": [0, 1]}, net["floors"]) for s in samples]
        with torch.no_grad():
            again = values(model, Batch(ruled, index, mean, std, net["floors"], "cpu"))
        if (again - valued).abs().max() > 1e-6:
            raise SystemExit(f"extended {net['name']} reads the rule")
    graph = export.onnx_bytes(model, example)
    tests = [t | {"value": float(v)} for v, t in zip(valued, tests, strict=True)]
    export.write(net | {"onnx": base64.b64encode(graph).decode(), "tests": tests}, path)


for name in ("v9-0", "v9-1", "v9-2"):
    extend(ROOT / "weights" / "all" / f"{name}.json", "zero")
extend(ROOT / "src" / "Sts2Ai.Tests" / "Fixtures" / "pure-v-tiny-win.json", "random")
