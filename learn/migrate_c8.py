"""Once, for c8-1 and v8-1: c7-2 and v7-1f carried to the game's full tables and the combat net's new inputs, with no
training. A new id's row is a copy of <unk>'s, which is how it read; a new `+0` map token's is zero, as v7-1f left those
out; the new inputs' parameters are zero. So each net gives what it gave.

    uv run python migrate_c8.py
"""

import base64
import json
import warnings
from pathlib import Path

import onnx
from onnx import numpy_helper

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402

import game_ids  # noqa: E402
from combat import encode  # noqa: E402
from combat import export as combat_export  # noqa: E402
from combat.net import Batch, Net  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent / "weights" / "silent"
NEW = ["damage", "orb_passive.weight", "orb_evoke.weight", "orb_at.weight"]
OWNERS = {
    "cards": ["cards", "card_up"], "modifiers": ["modifiers", "mod_amount"],
    "powers": ["powers", "power_amount", "power_stacks"], "enemies": ["enemies"],
    "intents": ["intents", "intent_damage", "intent_total"], "relics": ["relics", "relic_counter"],
    "potions": ["potions", "potion_head"], "context": ["context"], "orbs": ["orbs"], "allies": ["allies"],
    "relic_status": ["relic_status"],
}  # fmt: skip


def grown(rows, old, new, zero=lambda i: False):
    """A table's rows by id: a new id reads as <unk> did, or as nothing where zero says so."""
    missing = set(old) - set(new)
    if missing:
        raise SystemExit(f"ids lost: {sorted(missing)}")
    where = {i: k for k, i in enumerate(old)}
    return torch.stack([rows[where[i]] if i in where else torch.zeros_like(rows[0]) if zero(i) else rows[0] for i in new])


def combat_weights(net):
    """c7-2's weights by parameter name, from its ONNX, as v/export.py reads a V's."""
    graph = onnx.load_from_string(base64.b64decode(net["onnx"])).graph
    inits = {i.name: torch.from_numpy(numpy_helper.to_array(i).copy()) for i in graph.initializer}
    found = {k.removeprefix("net."): v for k, v in inits.items() if k.startswith("net.")}
    adds = {i: n for n in graph.node if n.op_type == "Add" for i in n.input}
    for n in graph.node:
        if n.op_type == "MatMul" and n.input[1] in inits and not n.input[1].startswith("net."):
            bias = next(i for i in adds[n.output[0]].input if i.startswith("net."))
            found[bias.removeprefix("net.").removesuffix(".bias") + ".weight"] = inits[n.input[1]].T
    return found


def combat():
    net = json.loads((ROOT / "c7-2.json").read_text())
    old, tables = net["vocab"], game_ids.combat_tables()
    found = combat_weights(net)
    for table, names in OWNERS.items():
        for name in names:
            for part in (".weight", ".bias"):
                if name + part in found:
                    found[name + part] = grown(found[name + part], old[table], tables[table])
    model = Net({t: len(ids) for t, ids in tables.items()}).eval()
    own = model.state_dict()
    fresh = sorted(k for k in own if k not in found)
    if fresh != sorted(NEW):
        raise SystemExit(f"parameters not in the file: {fresh}")
    model.load_state_dict({k: found[k] if k in found else torch.zeros_like(v) for k, v in own.items()})
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    samples = []
    for t in net["tests"]:
        tokens = encode.tokens(t["obs"], t["history"])
        samples.append({"tokens": tokens, "class": 0, "potion_used": [0.0] * len(tokens["potions"])})
    example = Batch(samples, index, "cpu")
    with torch.no_grad():
        logits, potions = model(*example.inputs())
    outcome, gone = torch.softmax(logits, 1), torch.sigmoid(potions)
    worst, tests = 0.0, []
    for k, t in enumerate(net["tests"]):
        worst = max(worst, max(abs(a - b) for a, b in zip(outcome[k].tolist(), t["outcome"], strict=True)))
        for i, potion in enumerate(old["potions"]):
            worst = max(worst, abs(gone[k, index["potions"][potion]].item() - t["potion_used"][i]))
        tests.append(t | {"outcome": outcome[k].tolist(), "potion_used": gone[k].tolist()})
    print(f"c8-1: largest difference from c7-2's {len(tests)} test states {worst:.3g}")
    if worst > 1e-5:
        raise SystemExit("c8-1 is not c7-2")
    onnx_bytes = combat_export.onnx_bytes(model, example)
    out = net | {"name": "c8-1", "vocab": tables, "onnx": base64.b64encode(onnx_bytes).decode(), "tests": tests}
    combat_export.write(out, ROOT / "c8-1.json")


def macro():
    net = json.loads((ROOT / "v7-1f.json").read_text())
    old, tables = net["vocab"], game_ids.macro_tables()
    owners = {"net.cards.weight": "cards", "net.card_up.weight": "cards", "net.modifiers.weight": "modifiers"}
    owners |= {f"net.bags.{t}.weight": t for t in ("relics", "potions", "context", "map")}
    model = onnx.load_from_string(base64.b64decode(net["onnx"]))
    done = set()
    for i in model.graph.initializer:
        if i.name in owners:
            t = owners[i.name]
            zero = (lambda x: "+0" in x) if t == "map" else (lambda x: False)
            rows = grown(torch.from_numpy(numpy_helper.to_array(i).copy()), old[t], tables[t], zero)
            i.CopyFrom(numpy_helper.from_array(rows.numpy(), i.name))
            done.add(i.name)
    if done != set(owners):
        raise SystemExit(f"tables not in the graph: {set(owners) - done}")
    out = net | {"name": "v8-1", "vocab": tables, "onnx": base64.b64encode(model.SerializeToString()).decode()}
    (ROOT / "v8-1.json").write_text(json.dumps(out) + "\n")


combat()
macro()
