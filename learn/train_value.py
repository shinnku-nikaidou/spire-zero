"""Train V v2 (docs: learning §1–§2), a net over the run's cards, relics, potions and context, from self-play.

The decision logs (`run --log`) carry the run view and V v0's features on every macro decision; the run records
(`run --records`) carry where each run ended, joined by run id. The net's head gives, per floor, the chance to die on
it if still alive there. Its targets are the run's outcome only: survived every floor from the decision's up to the one
it died on, or every floor if it won; a truncated run says nothing from its last floor on. Each run weighs the same
however many decisions it has, and every fifth run (by run id) is held out, for early stopping and the report, next to
a baseline that knows only the floor. The output is a net file for `run --value`: the net as ONNX, with what RunNet
needs to feed it, and test runs that RunNet checks on load.

    uv run python train_value.py --logs a.jsonl,b.jsonl --records a.jsonl,b.jsonl --name n1 --out ../eval/values/n1.json
"""

import argparse
import base64
import io
import json
import math
import random
import warnings

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402
from torch import nn  # noqa: E402

MAX_UP = 3  # RunNet.MaxUp
TABLES = ["cards", "modifiers", "relics", "potions", "context"]
BAGS = ["relics", "potions", "context"]
# The ONNX graph's inputs, in order (RunNet feeds them by these names).
INPUTS = ["cards", "ups", "card_mask", "mods", "mod_mask", "relics", "relic_mask", "potions", "potion_mask", "context",
          "context_mask", "features"]


def read_lines(paths: str, needle: str | None = None):
    """The JSON lines of the files that contain the needle; a line still being written is skipped."""
    for path in paths.split(","):
        with open(path) as f:
            for line in f:
                if not line.strip() or (needle and needle not in line):
                    continue
                try:
                    yield json.loads(line)
                except json.JSONDecodeError:
                    pass


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def tokens(run: dict):
    """As RunNet.Tokens: cards (id, upgrade level, modifiers), relics, potions, context."""
    cards = []
    for c in run["deck"]:
        mods = []
        if c.get("enchant"):
            mods.append("enchant:" + c["enchant"]["id"])
        if c.get("affliction"):
            mods.append("affliction:" + c["affliction"]["id"])
        cards.append((c["id"], min(c.get("up") or 0, MAX_UP), mods))
    relics = [r["id"] + (":used" if r.get("used_up") else "") for r in run["relics"]]
    potions = [p for p in run["potions"] if p is not None]
    m = run["map"]
    context = ["character:" + run["character"], "act:" + run["act_id"], "boss:" + m["boss"]]
    if m.get("second_boss"):
        context.append("boss:" + m["second_boss"])
    if m.get("ancient"):
        context.append("ancient:" + m["ancient"])
    return {"cards": cards, "relics": relics, "potions": potions, "context": context}


def vocab(samples, min_count: int):
    """Per table, the ids seen at least min_count times in these samples; entry 0 reads every other id."""
    counts = {t: {} for t in TABLES}
    for s in samples:
        tok = s["tokens"]
        for card_id, _, mods in tok["cards"]:
            counts["cards"][card_id] = counts["cards"].get(card_id, 0) + 1
            for m in mods:
                counts["modifiers"][m] = counts["modifiers"].get(m, 0) + 1
        for t in ("relics", "potions", "context"):
            for i in tok[t]:
                counts[t][i] = counts[t].get(i, 0) + 1
    return {t: ["<unk>"] + sorted(i for i, n in c.items() if n >= min_count) for t, c in counts.items()}


def padded(rows, device):
    """Lists of ids as one padded tensor (at least one wide) and its mask."""
    width = max(1, max(len(r) for r in rows))
    ids, mask = torch.zeros(len(rows), width, dtype=torch.long), torch.zeros(len(rows), width)
    for b, r in enumerate(rows):
        ids[b, :len(r)], mask[b, :len(r)] = torch.tensor(r, dtype=torch.long), 1
    return ids.to(device), mask.to(device)


class Batch:
    """Samples as tensors: padded cards and bags of ids with their masks, features, targets and weights."""

    def __init__(self, samples, index, names, mean, std, floors, device):
        n, width = len(samples), max(1, max(len(s["tokens"]["cards"]) for s in samples))
        cards, ups, mask = torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width), torch.zeros(n, width)
        mods, mod_mask = torch.zeros(n, width, 2, dtype=torch.long), torch.zeros(n, width, 2)
        for b, s in enumerate(samples):
            tok = s["tokens"]
            for k, (card_id, up, ms) in enumerate(tok["cards"]):
                cards[b, k], ups[b, k], mask[b, k] = index["cards"].get(card_id, 0), up, 1
                for j, m in enumerate(ms[:2]):
                    mods[b, k, j], mod_mask[b, k, j] = index["modifiers"].get(m, 0), 1
        self.cards, self.ups, self.mask = cards.to(device), ups.to(device), mask.to(device)
        self.mods, self.mod_mask = mods.to(device), mod_mask.to(device)
        self.bags = {t: padded([[index[t].get(i, 0) for i in s["tokens"][t]] for s in samples], device) for t in BAGS}
        x = torch.tensor([[s["features"].get(f, 0.0) for f in names] for s in samples], dtype=torch.float64)
        self.features = ((x - mean) / std).float().to(device)
        g = torch.arange(floors)
        start = torch.tensor([s["start"] for s in samples])[:, None]
        end = torch.tensor([s["end"] for s in samples])[:, None]
        kind = [s["kind"] for s in samples]
        died = torch.tensor([k == "died" for k in kind])[:, None]
        won = torch.tensor([k == "won" for k in kind])[:, None]
        self.target = (died & (g == end)).float().to(device)
        self.label_mask = ((g >= start) & (won | (g < end) | (died & (g == end)))).float().to(device)
        self.weight = torch.tensor([s["weight"] for s in samples]).to(device)
        self.start = start[:, 0].to(device)

    def inputs(self):
        """The net's inputs, in the order of INPUTS."""
        bags = [x for t in BAGS for x in self.bags[t]]
        return (self.cards, self.ups, self.mask, self.mods, self.mod_mask, *bags, self.features)


class Net(nn.Module):
    def __init__(self, sizes, n_features, floors, dim=32, hidden=128, body=256, sum_scale=1 / 16):
        super().__init__()
        self.sum_scale = sum_scale
        self.cards = nn.Embedding(sizes["cards"], dim)
        self.card_up = nn.Embedding(sizes["cards"], dim)
        self.modifiers = nn.Embedding(sizes["modifiers"], dim)
        self.relics = nn.Embedding(sizes["relics"], dim)
        self.potions = nn.Embedding(sizes["potions"], dim)
        self.context = nn.Embedding(sizes["context"], dim)
        for e in (self.cards, self.card_up, self.modifiers, self.relics, self.potions, self.context):
            nn.init.normal_(e.weight, std=0.1)
        self.phi = nn.Sequential(nn.Linear(dim, hidden), nn.ReLU(), nn.Linear(hidden, hidden), nn.ReLU())
        self.body = nn.Sequential(nn.Linear(2 * hidden + 3 * dim + n_features, body), nn.ReLU(), nn.Linear(body, body), nn.ReLU())
        self.head = nn.Linear(body, floors)
        nn.init.constant_(self.head.bias, -3.0)

    def forward(self, cards, ups, card_mask, mods, mod_mask, relics, relic_mask, potions, potion_mask, context, context_mask,
                features):
        """Per sample and floor, the logit of dying on that floor if alive there (inputs as INPUTS names them)."""
        x = self.cards(cards) + ups[..., None] * self.card_up(cards) + (self.modifiers(mods) * mod_mask[..., None]).sum(2)
        h = self.phi(x) * card_mask[..., None]
        total = h.sum(1)
        mean = total / card_mask.sum(1, keepdim=True).clamp_min(1)
        parts = [mean, total * self.sum_scale]
        for table, ids, mask in ((self.relics, relics, relic_mask), (self.potions, potions, potion_mask), (self.context, context, context_mask)):
            parts.append((table(ids) * mask[..., None]).sum(1))
        return self.head(self.body(torch.cat(parts + [features], 1)))


class Hazards(nn.Module):
    """The net as ONNX holds it: the hazards themselves."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        return torch.sigmoid(self.net(*inputs))


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    axes = {"cards": {0: "batch", 1: "cards"}, "ups": {0: "batch", 1: "cards"}, "card_mask": {0: "batch", 1: "cards"},
            "mods": {0: "batch", 1: "cards"}, "mod_mask": {0: "batch", 1: "cards"}, "features": {0: "batch"}, "hazards": {0: "batch"}}
    for t, mask in (("relics", "relic_mask"), ("potions", "potion_mask"), ("context", "context_mask")):
        axes[t] = axes[mask] = {0: "batch", 1: t}
    out = io.BytesIO()
    torch.onnx.export(Hazards(model), example.inputs(), out, dynamo=False, input_names=INPUTS, output_names=["hazards"],
                      dynamic_axes=axes)
    return out.getvalue()


def loss_of(logits, b: Batch):
    """Per sample, the masked cross-entropy of the hazards."""
    per_floor = nn.functional.binary_cross_entropy_with_logits(logits, b.target, reduction="none")
    return (per_floor * b.label_mask).sum(1)


def expected_floor(hazards: torch.Tensor, start: torch.Tensor):
    """As RunNet.Value with objective floor: the start floor plus the chance to survive each floor from it on."""
    h = hazards.double()
    g = torch.arange(h.shape[1], device=h.device)
    survive = torch.where(g >= start[:, None], 1 - h, torch.ones_like(h)).cumprod(1)
    return start.double() + (survive * (g >= start[:, None])).sum(1)


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--logs", required=True, help="decision logs, comma-separated")
    p.add_argument("--records", required=True, help="run records, comma-separated")
    p.add_argument("--name", required=True, help="the net's name, as the champion will show it")
    p.add_argument("--out", required=True)
    p.add_argument("--floors", type=int, default=64)
    p.add_argument("--objective", default="floor", choices=["floor", "win"])
    p.add_argument("--min-count", type=int, default=3, help="ids seen fewer times read as unknown")
    p.add_argument("--epochs", type=int, default=40)
    p.add_argument("--patience", type=int, default=5, help="epochs without a better held-out loss before stopping")
    p.add_argument("--batch", type=int, default=256)
    p.add_argument("--lr", type=float, default=1e-3)
    p.add_argument("--weight-decay", type=float, default=1e-4)
    p.add_argument("--seed", type=int, default=0)
    args = p.parse_args()
    torch.manual_seed(args.seed)
    random.seed(args.seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    runs = {r["run_id"]: r for r in read_lines(args.records) if r.get("run_id") and not r.get("failure")}
    samples = []
    for e in read_lines(args.logs, '"run_view"'):
        r = runs.get(e["run"])
        if r is None or "run_view" not in e:
            continue
        kind = {"won": "won", "lost": "died"}.get(r["outcome"], "censored")
        start = min(max(e["run_view"]["floor"], 0), args.floors - 1)
        samples.append({"run": e["run"], "view": e["run_view"], "tokens": tokens(e["run_view"]), "features": e["features"],
                        "start": start, "end": max(start, min(r["floor"], args.floors - 1)), "kind": kind})
    per_run = {}
    for s in samples:
        per_run[s["run"]] = per_run.get(s["run"], 0) + 1
    for s in samples:
        s["weight"] = 1.0 / per_run[s["run"]]
    train = [s for s in samples if not held_out(s["run"])]
    test = [s for s in samples if held_out(s["run"])]
    if not train or not test:
        raise SystemExit(f"{len(train)} train and {len(test)} held-out samples: too few runs")

    tables = vocab(train, args.min_count)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    names = sorted({f for s in samples for f in s["features"]})
    x = torch.tensor([[s["features"].get(f, 0.0) for f in names] for s in train], dtype=torch.float64)
    w = torch.tensor([s["weight"] for s in train], dtype=torch.float64)
    mean = (w[:, None] * x).sum(0) / w.sum()
    std = ((w[:, None] * (x - mean) ** 2).sum(0) / w.sum()).sqrt()
    std = torch.where(std > 1e-9, std, torch.ones_like(std))

    def batches(part, shuffle):
        order = list(range(len(part)))
        if shuffle:
            random.shuffle(order)
        for i in range(0, len(order), args.batch):
            yield Batch([part[k] for k in order[i:i + args.batch]], index, names, mean, std, args.floors, device)

    test_batches = list(batches(test, False))
    test_weight = sum(s["weight"] for s in test)

    # Baseline: per floor, the hazard the training runs show, whatever the state.
    alive, deaths = torch.zeros(args.floors, dtype=torch.float64), torch.zeros(args.floors, dtype=torch.float64)
    for b in batches(train, False):
        alive += (b.label_mask.double().cpu() * b.weight.double().cpu()[:, None]).sum(0)
        deaths += (b.target.double().cpu() * b.label_mask.double().cpu() * b.weight.double().cpu()[:, None]).sum(0)
    base_hazard = ((deaths + 0.5) / (alive + 1)).clamp(1e-4, 1 - 1e-4)
    base_logits = torch.log(base_hazard / (1 - base_hazard)).float().to(device)

    def evaluate(model):
        total, base, rows = 0.0, 0.0, []
        with torch.no_grad():
            for b in test_batches:
                logits = model(*b.inputs()) if model else base_logits.expand(len(b.weight), -1)
                total += float((loss_of(logits, b) * b.weight).sum())
                rows.append(expected_floor(torch.sigmoid(logits), b.start))
        return total / test_weight, torch.cat(rows)

    model = Net({t: len(ids) for t, ids in tables.items()}, len(names), args.floors).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    best, best_state, stale = math.inf, None, 0
    for epoch in range(args.epochs):
        model.train()
        for b in batches(train, True):
            loss = (loss_of(model(*b.inputs()), b) * b.weight).sum() / b.weight.sum()
            opt.zero_grad()
            loss.backward()
            opt.step()
        model.eval()
        held, _ = evaluate(model)
        print(f"epoch {epoch}: held-out loss {held:.4f}", flush=True)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= args.patience:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: held-out loss against the baseline, and how well the expected floor predicts the floor reached.
    held, predicted = evaluate(model)
    base_loss, base_predicted = evaluate(None)
    actual = torch.tensor([float(runs[s["run"]]["floor"]) for s in test], dtype=torch.float64, device=predicted.device)
    weights = torch.tensor([s["weight"] for s in test], dtype=torch.float64, device=predicted.device)

    def r2(pred):
        m = (weights * actual).sum() / weights.sum()
        return float(1 - (weights * (actual - pred) ** 2).sum() / (weights * (actual - m) ** 2).sum())

    report = {
        "runs": len({s["run"] for s in samples}),
        "held_out_runs": len({s["run"] for s in test}),
        "samples": len(samples),
        "won": sum(1 for r in runs.values() if r["outcome"] == "won"),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_loss_floor_only": base_loss,
        "r2_held_out": r2(predicted),
        "r2_held_out_floor_only": r2(base_predicted),
    }
    print(json.dumps(report))

    # Test runs for RunNet's check on load: a few held-out decisions, valued on the CPU as RunNet values them.
    model = model.cpu()
    picks = random.Random(args.seed).sample(test, min(8, len(test)))
    tests = []
    with torch.no_grad():
        b = Batch(picks, index, names, mean, std, args.floors, "cpu")
        hazards = torch.sigmoid(model(*b.inputs()))
        floors = expected_floor(hazards, b.start)
        for k, s in enumerate(picks):
            value = float(floors[k]) if args.objective == "floor" else float((1 - hazards[k, int(s["start"]):].double()).prod())
            tests.append({"run": s["view"], "value": value})

    out = {"name": args.name, "objective": args.objective, "floors": args.floors,
           "vocab": tables, "features": names, "mean": [float(v) for v in mean], "std": [float(v) for v in std],
           "fit": report, "onnx": base64.b64encode(onnx_bytes(model, b)).decode(), "tests": tests}
    with open(args.out, "w") as f:
        json.dump(out, f)
        f.write("\n")


if __name__ == "__main__":
    main()
