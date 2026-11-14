"""The numbers of V's walkthrough (the NetWalk figure): the first test run of the champion's V file
(eval/champions/sl.json's value), read by the encoder as the trainer reads it, and followed through every layer of
learn/v/net.py's Net with the file's own weights, into src/data/v-walk.json. Stops if the layers do not end in the
value the file's test holds.

    uv run --project learn python docs/scripts/v-walk.py
"""

import json
import math
import sys
from collections import Counter
from pathlib import Path

import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "learn"))

import common  # noqa: E402
from v import export, spec  # noqa: E402
from v.data import sample  # noqa: E402
from v.net import Batch, Net, values  # noqa: E402

CHAMPION = json.loads((ROOT / "eval/champions/sl.json").read_text())
FILE_PATH = ROOT / CHAMPION["options"]["value"]
FILE = json.loads(FILE_PATH.read_text())
OUT = ROOT / "docs/src/data/v-walk.json"
TEST = 0  # the file's first test run
SHOWN = 5  # cards whose vectors the figure shows
BOSSES = [17, 33, 48]  # the acts' boss floors, as the book's example run meets them
STATE_NAMES = {"floor": "floor", "hp": "HP", "max_hp": "max HP", "gold": "gold", "ascension": "ascension",
               "potion_slots": "potion slots"}  # fmt: skip


def four(t: torch.Tensor) -> list[float]:
    return [round(float(x), 3) for x in t[:4]]


def norm(t: torch.Tensor) -> float:
    return round(float(t.norm()), 2)


def card_name(c: list) -> str:
    """A card token as the figure names it: its id, a + per upgrade, its modifiers in brackets."""
    cid, up, mods = c[0], c[1], c[2]
    plus = "" if not up else "+" if up == 1 else f"+{up}"
    named = [m.split(":", 1)[-1] for m in mods]
    return cid + plus + (f" ({', '.join(named)})" if named else "")


def counted(names: list[str]) -> list[str]:
    """Equal names once, with ×N; the most frequent first, then by name."""
    n = Counter(names)
    return [f"{x} ×{k}" if k > 1 else x for x, k in sorted(n.items(), key=lambda p: (-p[1], p[0]))]


def walk() -> dict:
    net = Net({t: len(ids) for t, ids in FILE["vocab"].items()}, FILE["floors"])
    net.load_state_dict(export.weights(FILE, net))
    net.eval()
    index = {t: {x: k for k, x in enumerate(ids)} for t, ids in FILE["vocab"].items()}
    mean = torch.tensor(FILE["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(FILE["state"]["std"], dtype=torch.float64)
    test = FILE["tests"][TEST]
    view = test["run"]
    (read,) = common.Encoder(common.checkout_encoder()).runs([json.dumps({"run_view": view})])
    s = sample("", read["run"], FILE["floors"])
    b = Batch([s], index, mean, std, FILE["floors"], "cpu")
    tok = read["run"]["tokens"]
    f = b.feed

    with torch.no_grad():
        cards, ups, numbers = f["cards"][0], f["ups"][0], f["numbers"][0]
        mods, mod_mask, mod_amounts = f["mods"][0], f["mod_mask"][0], f["mod_amounts"][0]
        x = (
            net.cards(cards)
            + ups[:, None] * net.card_up(cards)
            + numbers[:, 0:1] * net.card_damage(cards)
            + numbers[:, 1:2] * net.card_block(cards)
            + numbers[:, 2:3] * net.card_grown(cards)
            + ((net.modifiers(mods) + mod_amounts[..., None] * net.mod_amount(mods)) * mod_mask[..., None]).sum(1)
        )
        h = net.phi(x)
        n = len(tok["cards"])
        total = h[:n].sum(0)
        parts = [total / n, total * Net.SUM_SCALE]
        groups = []
        for t, mask_name in spec.BAGS.items():
            ids, mask = f[t][0], f[mask_name][0]
            rows = net.bags[t](ids)
            if t == "relics":
                rows = rows + f["relic_values"][0][:, 0:1] * net.relic_counter(ids) + f["relic_values"][0][:, 2:3] * net.melted
                rows = rows + net.relic_status(f["relic_status"][0])
            g = (rows * mask[:, None]).sum(0)
            parts.append(g)
            groups.append([t, four(g), norm(g)])
        kept = (net.phi(net.cards(f["held"][0])) * f["held_mask"][0][:, None]).sum(0)
        z = torch.cat(parts + [f["state"][0], kept])
        b1 = net.body[1](net.body[0](z))
        b2 = net.body[3](net.body[2](b1))
        hazards = torch.sigmoid(net.head(b2)).double()
        start = s["start"]
        p_win = float(torch.prod(1 - hazards[start:]))
        checked = float(values(net, b)[0])
    if abs(p_win - test["value"]) > 1e-6 or abs(checked - test["value"]) > 1e-6:
        raise SystemExit(f"the walk ends in {p_win} (the net {checked}), the file's test holds {test['value']}")

    # The cards whose vectors are shown: each different kind of term once, the most frequent cards first.
    order = sorted(range(n), key=lambda k: (-sum(json.dumps(c) == json.dumps(tok["cards"][k]) for c in tok["cards"]), k))
    shown, seen = [], set()
    for k in order:
        c = tok["cards"][k]
        terms = (bool(c[1]), bool(c[4][0]), bool(c[4][1]), bool(c[4][2]), bool(c[2]))
        if card_name(c) in {card_name(tok["cards"][j]) for j in shown} or (terms in seen and len(shown) < SHOWN - 1):
            continue
        shown.append(k)
        seen.add(terms)
        if len(shown) == SHOWN:
            break

    def formula(c: list) -> str:
        up, (d, bl, g), mods = c[1], c[4], c[2]
        amounts = list(c[3]) + [0.0] * (len(mods) - len(c[3]))  # as Batch pads them: a keyword has none
        out = "E" + ("" if not up else " + U" if up == 1 else f" + {up}·U")
        out += "".join(f" + {v:.2f}·{name}" for v, name in ((d, "D"), (bl, "K"), (g, "G")) if v)
        out += "".join(f" + M[{m}]" + (f" + {a:.2f}·A[{m}]" if a else "") for m, a in zip(mods, amounts, strict=True))
        return out

    m = view["map"]
    points = {(p["col"], p["row"]): p["type"] for p in m["points"]}
    last = m["visited"][-1] if m["visited"] else None
    rv, rs = tok["relic_values"], tok["relic_status"]
    counters = [f"{r} {v[0]:.2f}" for r, v in zip(tok["relics"], rv, strict=True) if v[0]]
    statuses = Counter(rs)
    melted = sum(1 for v in rv if v[2])
    state = dict(zip(spec.STATE, s["state"], strict=True))
    normed = dict(zip(spec.STATE, (float(v) for v in f["state"][0]), strict=True))
    floors = range(start, FILE["floors"])
    reach = {boss: float(torch.prod(1 - hazards[start:boss])) for boss in BOSSES if boss >= start}
    beat = {boss: 1 - float(hazards[boss]) for boss in BOSSES if boss >= start}

    def run_of(lo: int, hi: int) -> list[float]:
        return [round(float(hazards[g]), 4) for g in range(lo, hi)]

    spans, lo = [], start
    for boss in [*(x for x in BOSSES if x >= start), FILE["floors"]]:
        if lo < boss:
            spans.append({"from": lo, "to": boss - 1, "h": run_of(lo, boss)})
        if boss < FILE["floors"]:
            spans.append({"from": boss, "to": boss, "h": run_of(boss, boss + 1), "boss": True})
        lo = boss + 1

    return {
        "source": f"the champion's V, {FILE_PATH.relative_to(ROOT)}, tests[{TEST}]; vectors show their first four numbers",
        "run": {
            "character": view["character"],
            "ascension": view["ascension"],
            "floor": view["floor"],
            "hp": view["hp"],
            "max_hp": view["max_hp"],
            "gold": view["gold"],
            "deck": n,
            "relics": len(tok["relics"]),
            "potions": sum(1 for p in view["potions"] if p),
            "slots": len(view["potions"]),
            "after": points.get((last["col"], last["row"])) if last else None,
            "entering": bool(m.get("entering")),
        },
        "tags": {
            "cards": counted([card_name(c) for c in tok["cards"]]),
            "damage": counted([f"{card_name(c)} {c[4][0]:.2f}" for c in tok["cards"] if c[4][0]]),
            "block": counted([f"{card_name(c)} {c[4][1]:.2f}" for c in tok["cards"] if c[4][1]]),
            "grown": counted([f"{card_name(c)} {c[4][2]:.2f}" for c in tok["cards"] if c[4][2]]),
            "modified": sum(1 for c in tok["cards"] if c[2]),
            "relics": tok["relics"],
            "counters": counters,
            "statuses": dict(statuses),
            "melted": melted,
            "kept": tok["held"],
            "potions": tok["potions"],
            "context": tok["context"],
            "map": tok["map"],
        },
        "lookup": [[t, index[table].get(t, 0)] for table, t in (
            ("cards", tok["cards"][0][0]),
            ("cards", tok["cards"][shown[1]][0]),
            *(("modifiers", mm) for mm in [m for c in tok["cards"] for m in c[2]][:1]),
            ("relics", tok["relics"][0]),
            ("context", tok["context"][0]),
            ("map", tok["map"][0]),
            ("map", tok["map"][1]),
        )],  # fmt: skip
        "card_vectors": [[card_name(tok["cards"][k]), formula(tok["cards"][k]), four(x[k])] for k in shown],
        "phi": [[card_name(tok["cards"][k]), four(h[k]), norm(h[k])] for k in shown],
        "pool": {"n": n, "mean": four(parts[0]), "sum": four(parts[1])},
        "groups": groups,
        "numbers": [[STATE_NAMES.get(k, k), state[k], round(normed[k], 3)] for k in spec.STATE if not k.startswith("auto_win")]
        + [["auto-win", [state[f"auto_win_{a}"] for a in range(spec.ACTS)], [round(normed[f"auto_win_{a}"], 3) for a in range(spec.ACTS)]]],
        "ascension_scaled": bool(FILE["state"]["std"][spec.STATE.index("ascension")] != 1 or FILE["state"]["mean"][spec.STATE.index("ascension")] != 0),
        "body": {
            "width": int(z.numel()),
            "parts": [int(p.numel()) for p in parts[:2]] + [len(spec.BAGS), Net.DIM, len(spec.STATE), int(kept.numel())],
            "kept_zero": not tok["held"],
            "b1": [int((b1 > 0).sum()), int(b1.numel())],
            "b2": [int((b2 > 0).sum()), int(b2.numel())],
        },
        "hazards": {"start": start, "floors": len(floors), "spans": spans, "bosses": [x for x in BOSSES if x >= start]},
        "reach": {str(k): round(v, 4) for k, v in reach.items()},
        "beat": {str(k): round(v, 4) for k, v in beat.items()},
        "value": round(p_win, 4),
        "test_value": test["value"],
    }


def main() -> None:
    data = walk()
    OUT.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n")
    print(f"wrote {OUT.relative_to(ROOT)}: value {data['value']} (the file's test {data['test_value']:.6f})")
    print(json.dumps({k: data[k] for k in ("run", "reach", "beat", "body")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
