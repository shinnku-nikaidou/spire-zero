"""The numbers of the combat net's walkthrough (docs: combat-brain/walkthrough): one round start of the net file's tests,
followed through every layer of learn/combat/net.py with the file's own weights, into src/data/combat-walk.json. Stops
if its layers do not end in the outputs the file's tests hold.

    uv run --project learn python docs/scripts/combat-walk.py
"""

import base64
import json
import math
import sys
from pathlib import Path

import onnx
import torch
from onnx import numpy_helper

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "learn"))

from combat import data, encode  # noqa: E402
from combat.net import Batch, Net  # noqa: E402

FILE = ROOT / "weights/silent/c7-2.json"
OUT = ROOT / "docs/src/data/combat-walk.json"
MAIN = 1  # the only test state holding a potion: every step has something to show
OTHERS = {"danger": 5, "mask": 15}  # a boss fight at 42 of 70 HP, and a state at 10 of 70 HP
WIN_BASE, PER_HP, PER_POTION = 0.2, 0.6, 0.02  # LeafWeights' defaults
LOST_HP = 10  # the sibling state of the search step
# The Linear weights the export leaves unnamed, in the graph's order; the file's tests check them.
MATMULS = ["card_phi.0.weight", "card_phi.2.weight", "enemy_scalars.weight", "power_phi.0.weight", "enemy_phi.0.weight", "enemy_phi.2.weight", "ally_scalars.weight", "ally_phi.0.weight"]


def load(file: dict) -> tuple[Net, dict]:
    """The file's graph back in the trainer's Net."""
    model = onnx.load_model_from_string(base64.b64decode(file["onnx"]))
    net = Net({t: len(ids) for t, ids in file["vocab"].items()})
    weights, unnamed = {}, []
    for i in model.graph.initializer:
        t = torch.tensor(numpy_helper.to_array(i).copy())
        if i.name.startswith("net."):
            weights[i.name.removeprefix("net.")] = t
        elif i.name.startswith("onnx::MatMul"):
            unnamed.append(t.T)  # the export folds a Linear's weight into its MatMul, transposed and without its name
    weights |= dict(zip(MATMULS, unnamed, strict=True))
    net.load_state_dict({k: weights[k] for k in net.state_dict()})
    index = {t: {x: k for k, x in enumerate(ids)} for t, ids in file["vocab"].items()}
    return net.eval(), index


def batch_of(test: dict, index: dict) -> Batch:
    tokens = encode.tokens(test["obs"], test["history"])
    held = sum(p is not None for p in test["obs"]["potions"])
    return Batch([{"tokens": tokens, "class": 0, "potion_used": [0] * held, "worth": None}], index, "cpu")


def inputs_of(b: Batch) -> dict[str, torch.Tensor]:
    return dict(zip(encode.INPUTS, b.inputs(), strict=True))


def four(x: torch.Tensor) -> list[float]:
    """A vector as the page shows it: its first four numbers."""
    return [round(float(v), 3) for v in x.flatten()[:4]]


def vec(x: torch.Tensor) -> dict:
    return {"size": x.numel(), "first": four(x), "norm": round(float(x.norm()), 3), "zeros": int((x == 0).sum())}


def outputs(net: Net, b: Batch) -> tuple[torch.Tensor, torch.Tensor]:
    logits, potions = net(*b.inputs())
    return torch.softmax(logits, 1)[0], torch.sigmoid(potions)[0]


def valued(outcome: torch.Tensor, gone: torch.Tensor, obs: dict, index: dict, width: float) -> dict:
    """CombatNet.Value's sum, term by term."""
    pl = obs["player"]
    share = pl["hp"] / max(1, pl["max_hp"])
    win = sum(float(outcome[k]) * (WIN_BASE + PER_HP * (share - (data.LOSS_MIN + (k - 0.5) * width))) for k in range(1, len(outcome)))
    held = [p for p in obs["potions"] if p is not None]
    kept = sum(1 - float(gone[index["potions"].get(p, 0)]) for p in held)
    potion = (1 - float(outcome[0])) * PER_POTION * kept
    return {
        "share": round(share, 4),
        "death": round(float(outcome[0]), 4),
        "win_term": round(win, 4),
        "kept": round(kept, 4),
        "potion_term": round(potion, 4),
        "value": round(win + potion, 4),
        "bound": round(WIN_BASE + PER_HP * share + PER_POTION * len(held), 4),
    }


def is_open(share: float, k: int, width: float) -> bool:
    """Net.reachable's rule for bucket k >= 1."""
    low = data.LOSS_MIN + (k - 1) * width
    return low < share and low + width > share - 1 - 1e-6


def tops(outcome: torch.Tensor, n: int, width: float) -> list[dict]:
    ranked = sorted(range(1, len(outcome)), key=lambda k: -float(outcome[k]))[:n]
    return [
        {"bucket": k, "low": round(data.LOSS_MIN + (k - 1) * width, 3), "high": round(data.LOSS_MIN + k * width, 3), "chance": round(float(outcome[k]), 4)}
        for k in ranked
    ]


def shown(test: dict, net: Net, index: dict, width: float) -> dict:
    """A test state as the figure of the two outputs shows it (docs: combat-brain/leaf §4)."""
    obs = test["obs"]
    outcome, gone = outputs(net, batch_of(test, index))
    return {
        "encounter": obs["encounter"],
        "round": obs["round"],
        "player": {k: obs["player"][k] for k in ("hp", "max_hp", "energy")},
        "hand": [c["id"] for c in obs["hand"]],
        "enemies": [
            {"id": e["id"], "hp": e["hp"], "max_hp": e["max_hp"], "intents": e.get("intents") or [], "powers": [{"id": p["id"], "amount": p["amount"]} for p in e["powers"]]}
            for e in obs["enemies"]
            if e["hp"] > 0
        ],
        "potions": [{"id": p, "gone": round(float(gone[index["potions"].get(p, 0)]), 4)} for p in obs["potions"] if p is not None],
        "outcome": [round(float(v), 5) for v in outcome],
        "value": valued(outcome, gone, obs, index, width)["value"],
    }


def walk(file: dict, net: Net, index: dict) -> dict:
    test = file["tests"][MAIN]
    obs, history = test["obs"], test["history"]
    tok = encode.tokens(obs, history)
    b = batch_of(test, index)
    x = inputs_of(b)
    width = data.WIDTH

    # Cards: every card's vector, then φ, then the sum per pile.
    card_x = (
        net.cards(x["cards"])
        + x["ups"][..., None] * net.card_up(x["cards"])
        + x["costs"][..., None] * net.cost
        + ((net.modifiers(x["mods"]) + x["mod_amounts"][..., None] * net.mod_amount(x["mods"])) * x["mod_mask"][..., None]).sum(2)
        + x["piles"] @ net.pile
    )
    card_phi = net.card_phi(card_x)
    per_pile = torch.bmm(x["piles"].transpose(1, 2), card_phi).flatten(1) * net.SUM_SCALE
    example = next(k for k, c in enumerate(tok["cards"]) if c[1] > 0)
    c_id, c_up, c_pile, c_mods, c_cost, _ = tok["cards"][example]
    t = x["cards"][0, example]
    card = {
        "id": c_id,
        "pile": encode.PILES[c_pile],
        "up": c_up,
        "cost": round(c_cost, 3),
        "entry": int(t),
        "mods": c_mods,
        "id_vector": four(net.cards(t)),
        "up_vector": four(net.card_up(t)),
        "cost_vector": four(net.cost),
        "pile_vector": four(net.pile[c_pile]),
        "x": four(card_x[0, example]),
        "phi": vec(card_phi[0, example]),
    }
    twin = next((k for k, c in enumerate(tok["cards"]) if k != example and c[:2] == (c_id, c_up) and c[2] != c_pile), None)
    piles = [
        {"pile": name, "cards": sum(1 for c in tok["cards"] if c[2] == p), "vector": vec(per_pile[0, p * net.HIDDEN : (p + 1) * net.HIDDEN])}
        for p, name in enumerate(encode.PILES)
    ]

    # Powers: the player's.
    mine = net.power_sum(x["powers"], x["power_amounts"], x["power_mask"], x["power_stacks"], x["power_cards"], x["power_card_mask"])

    # Enemies: a slot is the sum of its intents plus the slot's own vector.
    i = (
        net.intents(x["intents"])
        + x["intent_values"][..., 0:1] * net.intent_damage(x["intents"])
        + x["intent_values"][..., 1:2] * net.intent_total(x["intents"])
    )
    slot_vectors = (i * x["intent_mask"][..., None]).sum(3) + net.slot
    slots = slot_vectors.flatten(2)
    numbers = net.enemies(x["enemies"]) + net.enemy_scalars(x["enemy_scalars"])
    e_powers = net.power_sum(x["enemy_powers"], x["enemy_power_amounts"], x["enemy_power_mask"], x["enemy_power_stacks"], x["enemy_power_cards"], x["enemy_power_card_mask"])
    e_in = torch.cat([numbers, e_powers, slots], 2)
    e_phi = net.enemy_phi(e_in)
    foes = (e_phi * x["enemy_mask"][..., None]).sum(1)
    enemies = []
    for k, e in enumerate(tok["enemies"]):
        enemies.append(
            {
                "id": e["id"],
                "entry": int(x["enemies"][0, k]),
                "numbers": [round(v, 3) for v in e["scalars"]],
                "powers": [{"id": p[0], "amount": round(p[1], 3)} for p in e["powers"]],
                "slots": [[{"type": s[0], "damage": round(s[1], 3), "total": round(s[2], 3)} for s in slot] for slot in e["slots"]],
                "numbers_vector": four(numbers[0, k]),
                "powers_vector": four(e_powers[0, k]),
                "slot_vector": four(slot_vectors[0, k, 0]),
                "phi": vec(e_phi[0, k]),
            }
        )

    # The bags.
    held = (
        net.relics(x["relics"])
        + x["relic_values"][..., 0:1] * net.relic_counter(x["relics"])
        + x["relic_values"][..., 2:3] * net.melted
        + net.relic_status(x["relic_status"])
    )
    relics = (held * x["relic_mask"][..., None]).sum(1)
    potions = (net.potions(x["potions"]) * x["potion_mask"][..., None]).sum(1)
    context = (net.context(x["context"]) * x["context_mask"][..., None]).sum(1)
    orbs = (net.orbs(x["orbs"]) * x["orb_mask"][..., None]).sum(1)
    ally = net.ally_phi(torch.cat([net.allies(x["allies"]) + net.ally_scalars(x["ally_scalars"]), net.power_sum(x["ally_powers"], x["ally_power_amounts"], x["ally_power_mask"])], 2))
    allies = (ally * x["ally_mask"][..., None]).sum(1)

    # The join, the body, the heads.
    parts = [
        ("cards", per_pile), ("player", x["player"]), ("powers", mine), ("enemies", foes), ("relics", relics),
        ("potions", potions), ("context", context), ("orbs", orbs), ("allies", allies), ("extra", x["extra"]),
    ]  # fmt: skip
    z = torch.cat([p for _, p in parts], 1)
    first = net.body[1](net.body[0](z))
    second = net.body[3](net.body[2](first))
    if not torch.equal(second, net.encode(*b.inputs())):
        raise SystemExit("the walk's layers are not Net.encode's")
    raw = net.head(second)
    gone_logits = net.potion_head(second)
    outcome, gone = outputs(net, b)
    expected = torch.tensor(test["outcome"])
    if float((outcome - expected).abs().max()) > 1e-3 or float((gone - torch.tensor(test["potion_used"])).abs().max()) > 1e-3:
        raise SystemExit("the walk does not end in the file's test state")

    share = float(x["player"][0, 0])
    opened = [k for k in range(1, data.BUCKETS + 1) if is_open(share, k, width)]
    if any((float(outcome[k]) > 0) != (k in opened) for k in range(1, data.BUCKETS + 1)):
        raise SystemExit("the page's rule of open buckets is not the graph's")
    potion = next(p for p in obs["potions"] if p is not None)
    entry = index["potions"][potion]
    table = file["vocab"]["potions"]
    ranked = sorted(range(len(table)), key=lambda k: -float(gone[k]))

    # The search's step: the same state with less HP, then without the potion.
    less = json.loads(json.dumps(test))
    less["obs"]["player"]["hp"] -= LOST_HP
    no_potion = json.loads(json.dumps(test))
    no_potion["obs"]["potions"] = [None for _ in obs["potions"]]
    siblings = {}
    for name, sibling in (("less_hp", less), ("no_potion", no_potion)):
        o, g = outputs(net, batch_of(sibling, index))
        siblings[name] = valued(o, g, sibling["obs"], index, width) | {"tops": tops(o, 3, width)}

    others = {}
    for name, k in OTHERS.items():
        other = file["tests"][k]
        o, g = outputs(net, batch_of(other, index))
        pl = other["obs"]["player"]
        others[name] = valued(o, g, other["obs"], index, width) | {
            "encounter": other["obs"]["encounter"],
            "room": other["obs"]["room"],
            "round": other["obs"]["round"],
            "hp": pl["hp"],
            "max_hp": pl["max_hp"],
            "enemies": [e["id"] for e in other["obs"]["enemies"] if e["hp"] > 0],
            "open": sum(float(v) > 0 for v in o[1:]),
            "open_from": round(data.LOSS_MIN + min(j for j in range(50) if float(o[j + 1]) > 0) * width, 3),
            "open_to": round(data.LOSS_MIN + (max(j for j in range(50) if float(o[j + 1]) > 0) + 1) * width, 3),
            "tops": tops(o, 3, width),
        }

    # A label, for an ending this state's file does not hold.
    end_hp = 55
    lost = share - end_hp / obs["player"]["max_hp"]
    label = data.ending(obs, {"died": False, "hp": end_hp, "max_hp": obs["player"]["max_hp"]})
    logits = net.reachable(raw, x["player"])
    ending_loss = float(torch.nn.functional.cross_entropy(logits, torch.tensor([label])))
    potion_loss = float(torch.nn.functional.binary_cross_entropy_with_logits(gone_logits[0, entry], torch.tensor(0.0)))

    pl = obs["player"]
    return {
        "source": f"{FILE.name}, tests[{MAIN}]",
        "state": {
            "encounter": obs["encounter"],
            "room": obs["room"],
            "act": obs["act"],
            "round": obs["round"],
            "hp": pl["hp"],
            "max_hp": pl["max_hp"],
            "block": pl["block"],
            "energy": pl["energy"],
            "max_energy": pl["max_energy"],
            "stars": pl["stars"],
            "gold": pl.get("gold"),
            "ascension": obs.get("ascension", 0),
            "orb_slots": pl.get("orb_slots", 0),
            "powers": [{"id": p["id"], "amount": p["amount"]} for p in pl["powers"]],
            "piles": {p: [c["id"] + ("+" * (c.get("up") or 0)) for c in obs[p]] for p in encode.PILES},
            "enemies": [
                {"id": e["id"], "hp": e["hp"], "max_hp": e["max_hp"], "block": e["block"], "powers": [{"id": p["id"], "amount": p["amount"]} for p in e["powers"]], "intents": e.get("intents") or []}
                for e in obs["enemies"]
                if e["hp"] > 0
            ],
            "relics": [{"id": r["id"], "counter": r.get("counter"), "status": r.get("status")} for r in obs["relics"]],
            "potions": obs["potions"],
        },
        "tokens": {
            "player": [round(v, 3) for v in tok["player"]],
            "extra": [round(v, 3) for v in tok["extra"]],
            "context": tok["context"],
            "context_entries": [int(v) for v in x["context"][0]],
            "relics": tok["relics"],
            "relic_values": [[round(v, 3) for v in r] for r in tok["relic_values"]],
            "relic_status": tok["relic_status"],
            "potions": tok["potions"],
            "orbs": tok["orbs"],
            "allies": [a["id"] for a in tok["allies"]],
        },
        "shapes": {name: list(t.shape) for name, t in x.items()},
        "card": card,
        "twin_in_another_pile": twin is not None,
        "piles": piles,
        "powers": vec(mine),
        "enemies": enemies,
        "foes": vec(foes),
        "bags": {"relics": vec(relics), "potions": vec(potions), "context": vec(context), "orbs": vec(orbs), "allies": vec(allies)},
        "join": [{"part": name, "size": p.shape[1], "first": four(p)} for name, p in parts],
        "z": vec(z),
        "body": [vec(first), vec(second)],
        "head": {
            "death_logit": round(float(raw[0, 0]), 3),
            "logits": [round(float(v), 3) for v in raw[0, 1:]],
            "open": len(opened),
            "open_from": round(data.LOSS_MIN + (opened[0] - 1) * width, 3),
            "open_to": round(data.LOSS_MIN + opened[-1] * width, 3),
            "width": round(width, 3),
            "death": float(f"{float(outcome[0]):.3g}"),
            "tops": tops(outcome, 5, width),
            "chances": [round(float(v), 4) for v in outcome],
        },
        "potion": {
            "id": potion,
            "entry": entry,
            "logit": round(float(gone_logits[0, entry]), 3),
            "chance": round(float(gone[entry]), 4),
            "outputs": len(table),
            "most_likely": [{"id": table[k], "chance": round(float(gone[k]), 3)} for k in ranked[:3]],
        },
        "value": valued(outcome, gone, obs, index, width),
        "siblings": siblings,
        "lost_hp": LOST_HP,
        "others": others,
        "label": {"end_hp": end_hp, "lost": round(lost, 4), "class": label, "low": round(data.LOSS_MIN + (label - 1) * width, 3), "high": round(data.LOSS_MIN + label * width, 3), "chance": float(f"{float(outcome[label]):.3g}"), "ending_loss": round(ending_loss, 3), "potion_loss": round(potion_loss, 4)},
        "tests": [shown(t, net, index, width) for t in file["tests"]],
        "sizes": {
            name: {"shape": list(p.shape), "weights": int(p.numel())}
            for name, p in net.named_parameters()
        },
        "total_weights": int(sum(p.numel() for p in net.parameters())),
        "vocab": {t: len(ids) for t, ids in file["vocab"].items()},
        "fit": {k: file["fit"][k] for k in ("runs", "held_out_runs", "samples", "held_out_samples")},
    }


def main() -> None:
    file = json.loads(FILE.read_text())
    net, index = load(file)
    with torch.no_grad():
        out = walk(file, net, index)
    if not math.isfinite(out["value"]["value"]):
        raise SystemExit("no value")
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n")
    print(f"wrote {OUT}: {OUT.stat().st_size} bytes")


if __name__ == "__main__":
    main()
