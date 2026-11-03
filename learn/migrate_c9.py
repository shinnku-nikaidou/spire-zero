"""Once, for c9-0 and v9-0, the nets all five characters share: c8-3 and v8-3 carried to the new inputs and widths with
no training. A new id's row reads as the id did before: a character's context row is <unk>'s, a relic's "Spent" status
row is "Normal"'s, and a modifier token the old nets were never given (keyword, tinker, rider, spent) is zero. The new
inputs' parameters are zero. A wider net keeps every old weight in its place: a new unit takes the fresh net's small
random weights on its way in and zeros on its way out, a new embedding dimension random values that zero columns read.
So each net gives what it gave, but where the combat net now reads more: an enemy lying dead and Swipe's stolen card.

    uv run python migrate_c9.py
"""

import base64
import json
import warnings
from pathlib import Path

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402

import game_ids  # noqa: E402
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

WEIGHTS = Path(__file__).resolve().parent.parent / "weights"
COMBAT_NEW = ["card_damage", "card_block", "card_calc", "card_replay", "power_detail", "stars", "gathered"]
COMBAT_OWNERS = {
    "cards": ["cards", "card_up", "card_damage", "card_block", "card_calc", "card_replay"],
    "modifiers": ["modifiers", "mod_amount"], "powers": ["powers", "power_amount", "power_stacks", "power_detail"],
    "enemies": ["enemies"], "intents": ["intents", "intent_damage", "intent_total"], "relics": ["relics", "relic_counter"],
    "potions": ["potions", "potion_head"], "context": ["context"], "orbs": ["orbs"], "allies": ["allies"],
    "relic_status": ["relic_status"],
}  # fmt: skip
V_OWNERS = {"cards": ["cards", "card_up"], "modifiers": ["modifiers"]} | {t: [f"bags.{t}"] for t in v_encode.BAGS}


def row_of(i: str) -> str | None:
    """The old id a new id's row copies, or None for a zero row."""
    if i.startswith(("keyword:", "tinker:", "rider:")) or i == "spent":
        return None
    return {"Spent": "Normal"}.get(i, "<unk>")


def grown(rows: torch.Tensor, old: list[str], new: list[str]) -> torch.Tensor:
    missing = set(old) - set(new)
    if missing:
        raise SystemExit(f"ids lost: {sorted(missing)}")
    where = {i: k for k, i in enumerate(old)}
    out = []
    for i in new:
        if i in where:
            out.append(rows[where[i]])
        else:
            src = row_of(i)
            out.append(torch.zeros_like(rows[0]) if src is None else rows[where[src]])
    return torch.stack(out)


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


def carried(net, tables, cls, owners, new, widths, extra=()):
    """The file's net in cls at its own widths with tables grown and new parameters zero, then widened to cls's."""
    found = onnx_found(net)
    for table, names in owners.items():
        for name in names:
            for part in (".weight", ".bias"):
                if name + part in found:
                    found[name + part] = grown(found[name + part], net["vocab"][table], tables[table])
    sizes = {t: len(ids) for t, ids in tables.items()}
    narrow = at_widths(cls, *widths)(sizes, *extra).eval()
    own = narrow.state_dict()
    fresh = sorted(k for k in own if k not in found and k not in dict(narrow.named_buffers()))
    if [k for k in fresh if not k.startswith("aux_head.")] != sorted(k for k in own if k.split(".")[0] in new):
        raise SystemExit(f"parameters not in the file: {fresh}")
    narrow.load_state_dict({k: found[k] if k in found else torch.zeros_like(v) for k, v in own.items()})
    torch.manual_seed(0)
    wide = cls(sizes, *extra).eval()
    layout = combat_layout if cls is CombatNet else v_layout
    wide.load_state_dict(widened(narrow, wide, layout))
    with torch.no_grad():
        for name, p in wide.named_parameters():
            if name.split(".")[0] in new:
                p.zero_()
    return wide


def combat(src: str, name: str):
    net = json.loads((WEIGHTS / "silent" / f"{src}.json").read_text())
    tables = game_ids.combat_tables()
    widths = (net_width(net, "cards.weight", 1), net_width(net, "card_phi.0.bias", 0), net_width(net, "body.0.bias", 0))
    model = carried(net, tables, CombatNet, COMBAT_OWNERS, COMBAT_NEW, widths)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    samples = []
    for t in net["tests"]:
        tokens = combat_encode.tokens(t["obs"], t["history"])
        samples.append({"tokens": tokens, "class": 0, "potion_used": [0.0] * len(tokens["potions"])})
    example = CombatBatch(samples, index, "cpu")
    with torch.no_grad():
        logits, potions = model(*example.inputs())
    outcome, gone = torch.softmax(logits, 1), torch.sigmoid(potions)
    worst, tests, reads_more = 0.0, [], 0
    for k, t in enumerate(net["tests"]):
        more = any(e["hp"] <= 0 for e in t["obs"]["enemies"]) or any(
            p.get("stolen") for e in t["obs"]["enemies"] for p in e["powers"]
        )
        reads_more += more
        error = max(abs(a - b) for a, b in zip(outcome[k].tolist(), t["outcome"], strict=True))
        for i, potion in enumerate(net["vocab"]["potions"]):
            error = max(error, abs(gone[k, index["potions"][potion]].item() - t["potion_used"][i]))
        worst = max(worst, 0.0 if more else error)
        tests.append(t | {"outcome": outcome[k].tolist(), "potion_used": gone[k].tolist()})
    weights = sum(p.numel() for p in model.parameters())
    print(f"{name}: widths {widths} -> {model.DIM, model.HIDDEN, model.BODY}, {weights} weights; largest difference "
          f"from {src}'s {len(tests) - reads_more} test states that read nothing new {worst:.3g}")  # fmt: skip
    if worst > 1e-5:
        raise SystemExit(f"{name} is not {src}")
    data = combat_export.onnx_bytes(model, example)
    out = net | {
        "name": name,
        "character": "ALL",
        "vocab": tables,
        "onnx": base64.b64encode(data).decode(),
        "tests": tests,
    }
    path = WEIGHTS / "all" / f"{name}.json"
    path.parent.mkdir(exist_ok=True)
    combat_export.write(out, path)


def macro(src: str, name: str):
    net = json.loads((WEIGHTS / "silent" / f"{src}.json").read_text())
    tables = game_ids.macro_tables()
    widths = (net_width(net, "cards.weight", 1), net_width(net, "phi.0.bias", 0), net_width(net, "body.0.bias", 0))
    model = carried(net, tables, VNet, V_OWNERS, [], widths, extra=(net["floors"],))
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    mean = torch.tensor(net["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(net["state"]["std"], dtype=torch.float64)
    samples = [v_data.sample(f"{k:016x}", t["run"], net["floors"]) for k, t in enumerate(net["tests"])]
    example = VBatch(samples, index, mean, std, net["floors"], "cpu")
    with torch.no_grad():
        valued = values(model, example, net["objective"])
    worst = max(abs(float(v) - t["value"]) for v, t in zip(valued, net["tests"], strict=True))
    weights = sum(p.numel() for n, p in model.named_parameters() if not n.startswith("aux_head."))
    print(f"{name}: widths {widths} -> {model.DIM, model.HIDDEN, model.BODY}, {weights} weights; largest difference "
          f"from {src}'s {len(samples)} test runs {worst:.3g}")  # fmt: skip
    if worst > 1e-5:
        raise SystemExit(f"{name} is not {src}")
    data = v_export.onnx_bytes(model, example)
    tests = [t | {"value": float(v)} for v, t in zip(valued, net["tests"], strict=True)]
    out = net | {
        "name": name,
        "character": "ALL",
        "vocab": tables,
        "onnx": base64.b64encode(data).decode(),
        "tests": tests,
    }
    v_export.write(out, WEIGHTS / "all" / f"{name}.json")


def net_width(net: dict, name: str, axis: int) -> int:
    return onnx_found(net)[name].shape[axis]


combat("c8-3", "c9-0")
macro("v8-3", "v9-0")
