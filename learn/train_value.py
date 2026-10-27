"""Train V v2 (docs: learning §1–§2), a net over the run's cards, relics, potions and context, from self-play.

The decision logs (`run --log`) carry the run view and V v0's features on every macro decision; the run records
(`run --records`) carry where each run ended, joined by run id, each log with the record file written beside it (given
in the same order). The net's head gives, per floor, the chance to die on it if still alive there. Its targets are the run's outcome only: survived every floor from the decision's up to the one
it died on, or every floor if it won; a truncated run says nothing from its last floor on. Each run weighs the same
however many decisions it has, and every fifth run (by run id) is held out, for early stopping and the report, next to
a baseline that knows only the floor. The output is a net file for `run --value`, in the weights history by default
(docs: learning §6): the net as ONNX, with what RunNet needs to feed it, and test runs that RunNet checks on load.

    uv run python train_value.py --logs a.jsonl,b.jsonl --records a.jsonl,b.jsonl --name n1
"""

import base64
import io
import json
import math
import random
import warnings
from enum import StrEnum
from typing import Annotated

import typer
from loguru import logger

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402
from torch import nn  # noqa: E402

import common  # noqa: E402
from common import LearnError, read_lines, runs_by_log  # noqa: E402
from weights import out_path  # noqa: E402

script = common.app()

MAX_UP = 3  # RunNet.MaxUp
# Every scaled number the nets read is capped at ±CAP: a few states show huge ones (an erupting WATERFALL_GIANT's HP).
CAP = 10.0
TABLES = ["cards", "modifiers", "relics", "potions", "context", "vars", "relic_status"]
BAGS = ["relics", "potions", "context"]
# The ONNX graph's inputs, in order (RunNet feeds them by these names).
INPUTS = [
    "cards",
    "ups",
    "card_mask",
    "mods",
    "mod_mask",
    "relics",
    "relic_mask",
    "potions",
    "potion_mask",
    "context",
    "context_mask",
    "features",
    "costs",
    "mod_amounts",
    "card_scalars",
    "vars",
    "var_values",
    "var_mask",
    "relic_values",
    "relic_status",
    "extra",
]


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def cap(x: float) -> float:
    return max(-CAP, min(CAP, x))


def card_numbers(c: dict) -> dict:
    """What a card shows beyond its id, upgrade level and modifiers' ids, as RunNet.Numbers reads it: its cost / 3 (0
    for X or none), its modifiers' amounts / 10, star cost / 3 and whether it costs X, and each of its numbers / 10 by
    name."""
    cost = c.get("cost")
    stars = c.get("stars")
    return {
        "cost": cost / 3 if cost is not None and cost > 0 else 0.0,
        "mod_amounts": [cap(m["amount"] / 10) for m in (c.get("enchant"), c.get("affliction")) if m],
        "scalars": [stars / 3 if stars is not None and stars > 0 else 0.0, 1.0 if c.get("x_cost") else 0.0],
        "vars": [(k, cap(float(v) / 10)) for k, v in (c.get("vars") or {}).items()],
    }


def relic_numbers(r: dict) -> dict:
    """A relic's counter / 10 (0 when none shows), whether one shows, whether it is melted, and its status."""
    counter = r.get("counter")
    return {
        "values": [
            cap(counter / 10) if counter is not None else 0.0,
            1.0 if counter is not None else 0.0,
            1.0 if r.get("melted") else 0.0,
        ],
        "status": r.get("status") or "Normal",
    }


def tokens(run: dict):
    """As RunNet.Tokens: cards (id, upgrade level, modifiers), relics, potions, context; and as RunNet.Numbers: each
    card's and relic's numbers, and the ascension."""
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
    return {
        "cards": cards,
        "relics": relics,
        "potions": potions,
        "context": context,
        "card_numbers": [card_numbers(c) for c in run["deck"]],
        "relic_numbers": [relic_numbers(r) for r in run["relics"]],
        "extra": [run.get("ascension", 0) / 10],
    }


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
        for numbers in tok["card_numbers"]:
            for k, _ in numbers["vars"]:
                counts["vars"][k] = counts["vars"].get(k, 0) + 1
        for numbers in tok["relic_numbers"]:
            counts["relic_status"][numbers["status"]] = counts["relic_status"].get(numbers["status"], 0) + 1
    return {t: ["<unk>"] + sorted(i for i, n in c.items() if n >= min_count) for t, c in counts.items()}


def padded(rows, device):
    """Lists of ids as one padded tensor (at least one wide) and its mask."""
    width = max(1, max(len(r) for r in rows))
    ids, mask = torch.zeros(len(rows), width, dtype=torch.long), torch.zeros(len(rows), width)
    for b, r in enumerate(rows):
        ids[b, : len(r)], mask[b, : len(r)] = torch.tensor(r, dtype=torch.long), 1
    return ids.to(device), mask.to(device)


def card_number_tensors(rows, index, device):
    """Each sample's cards' numbers (card_numbers), as the inputs costs, mod_amounts, card_scalars, vars, var_values
    and var_mask: padded as the cards are, each card's numbers by name padded to the most any card has."""
    n, width = len(rows), max(1, max(len(r) for r in rows))
    m = max(1, max((len(c["vars"]) for r in rows for c in r), default=0))
    costs, mod_amounts, scalars = torch.zeros(n, width), torch.zeros(n, width, 2), torch.zeros(n, width, 2)
    names, values, mask = torch.zeros(n, width, m, dtype=torch.long), torch.zeros(n, width, m), torch.zeros(n, width, m)
    for b, r in enumerate(rows):
        for k, c in enumerate(r):
            costs[b, k] = c["cost"]
            for j, a in enumerate(c["mod_amounts"][:2]):
                mod_amounts[b, k, j] = a
            scalars[b, k] = torch.tensor(c["scalars"])
            for j, (name, v) in enumerate(c["vars"]):
                names[b, k, j], values[b, k, j], mask[b, k, j] = index["vars"].get(name, 0), v, 1
    return [x.to(device) for x in (costs, mod_amounts, scalars, names, values, mask)]


def relic_number_tensors(rows, index, device):
    """Each sample's relics' numbers (relic_numbers), as the inputs relic_values and relic_status, padded as the relics
    are."""
    n, width = len(rows), max(1, max(len(r) for r in rows))
    values, status = torch.zeros(n, width, 3), torch.zeros(n, width, dtype=torch.long)
    for b, r in enumerate(rows):
        for k, x in enumerate(r):
            values[b, k], status[b, k] = torch.tensor(x["values"]), index["relic_status"].get(x["status"], 0)
    return [values.to(device), status.to(device)]


class CardNumbers(nn.Module):
    """What a card's numbers add to its vector: cost, modifiers' amounts (each times its modifier's amount vector),
    star cost and X, and each number times its name's vector."""

    def __init__(self, sizes, dim):
        super().__init__()
        self.cost = nn.Parameter(torch.randn(dim) * 0.1)
        self.mod_amount = nn.Embedding(sizes["modifiers"], dim)
        self.scalars = nn.Linear(2, dim, bias=False)
        self.vars = nn.Embedding(sizes["vars"], dim)
        for e in (self.mod_amount, self.vars):
            nn.init.normal_(e.weight, std=0.1)

    def forward(self, mods, mod_mask, costs, mod_amounts, card_scalars, var_ids, var_values, var_mask):
        """Costs may be None, for a net that reads them on its own."""
        return (
            (0 if costs is None else costs[..., None] * self.cost)
            + (self.mod_amount(mods) * (mod_amounts * mod_mask)[..., None]).sum(2)
            + self.scalars(card_scalars)
            + (self.vars(var_ids) * (var_values * var_mask)[..., None]).sum(2)
        )


class RelicNumbers(nn.Module):
    """What a relic's numbers add to its vector: its counter times its id's counter vector, whether a counter shows
    and whether it is melted, and its status."""

    def __init__(self, sizes, dim):
        super().__init__()
        self.counter = nn.Embedding(sizes["relics"], dim)
        self.flags = nn.Linear(2, dim, bias=False)
        self.status = nn.Embedding(sizes["relic_status"], dim)
        for e in (self.counter, self.status):
            nn.init.normal_(e.weight, std=0.1)

    def forward(self, relics, relic_values, relic_status):
        return (
            relic_values[..., 0:1] * self.counter(relics)
            + self.flags(relic_values[..., 1:])
            + self.status(relic_status)
        )


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
        self.numbers = (
            card_number_tensors([s["tokens"]["card_numbers"] for s in samples], index, device)
            + relic_number_tensors([s["tokens"]["relic_numbers"] for s in samples], index, device)
            + [torch.tensor([s["tokens"]["extra"] for s in samples]).to(device)]
        )
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
        return (self.cards, self.ups, self.mask, self.mods, self.mod_mask, *bags, self.features, *self.numbers)


class Net(nn.Module):
    def __init__(self, sizes, n_features, floors, dim=32, hidden=128, body=256, sum_scale=1 / 16, n_extra=1):
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
        self.card_numbers = CardNumbers(sizes, dim)
        self.relic_numbers = RelicNumbers(sizes, dim)
        self.phi = nn.Sequential(nn.Linear(dim, hidden), nn.ReLU(), nn.Linear(hidden, hidden), nn.ReLU())
        self.body = nn.Sequential(
            nn.Linear(2 * hidden + 3 * dim + n_features + n_extra, body), nn.ReLU(), nn.Linear(body, body), nn.ReLU()
        )
        self.head = nn.Linear(body, floors)
        nn.init.constant_(self.head.bias, -3.0)

    def forward(
        self,
        cards,
        ups,
        card_mask,
        mods,
        mod_mask,
        relics,
        relic_mask,
        potions,
        potion_mask,
        context,
        context_mask,
        features,
        costs,
        mod_amounts,
        card_scalars,
        var_ids,
        var_values,
        var_mask,
        relic_values,
        relic_status,
        extra,
    ):
        """Per sample and floor, the logit of dying on that floor if alive there (inputs as INPUTS names them)."""
        x = (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + (self.modifiers(mods) * mod_mask[..., None]).sum(2)
            + self.card_numbers(mods, mod_mask, costs, mod_amounts, card_scalars, var_ids, var_values, var_mask)
        )
        h = self.phi(x) * card_mask[..., None]
        total = h.sum(1)
        mean = total / card_mask.sum(1, keepdim=True).clamp_min(1)
        parts = [mean, total * self.sum_scale]
        relic = self.relics(relics) + self.relic_numbers(relics, relic_values, relic_status)
        parts.append((relic * relic_mask[..., None]).sum(1))
        for table, ids, mask in ((self.potions, potions, potion_mask), (self.context, context, context_mask)):
            parts.append((table(ids) * mask[..., None]).sum(1))
        return self.head(self.body(torch.cat(parts + [features, extra], 1)))


class Hazards(nn.Module):
    """The net as ONNX holds it: the hazards themselves."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        return torch.sigmoid(self.net(*inputs))


def onnx_bytes(model, example: Batch) -> bytes:
    """The model as an ONNX graph, every input's batch and length dimensions left free."""
    axes = {
        "cards": {0: "batch", 1: "cards"},
        "ups": {0: "batch", 1: "cards"},
        "card_mask": {0: "batch", 1: "cards"},
        "mods": {0: "batch", 1: "cards"},
        "mod_mask": {0: "batch", 1: "cards"},
        "features": {0: "batch"},
        "hazards": {0: "batch"},
        "costs": {0: "batch", 1: "cards"},
        "mod_amounts": {0: "batch", 1: "cards"},
        "card_scalars": {0: "batch", 1: "cards"},
        "relic_values": {0: "batch", 1: "relics"},
        "relic_status": {0: "batch", 1: "relics"},
        "extra": {0: "batch"},
    }
    for name in ("vars", "var_values", "var_mask"):
        axes[name] = {0: "batch", 1: "cards", 2: "vars"}
    for t, mask in (("relics", "relic_mask"), ("potions", "potion_mask"), ("context", "context_mask")):
        axes[t] = axes[mask] = {0: "batch", 1: t}
    out = io.BytesIO()
    torch.onnx.export(
        Hazards(model),
        example.inputs(),
        out,
        dynamo=False,
        input_names=INPUTS,
        output_names=["hazards"],
        dynamic_axes=axes,
    )
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


class Objective(StrEnum):
    floor = "floor"
    win = "win"


@script.command(help=__doc__)
def main(
    logs: Annotated[str, typer.Option(help="decision logs, comma-separated")],
    records: Annotated[str, typer.Option(help="run records, comma-separated")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    out: Annotated[
        str | None, typer.Option(help="where the file goes (default: weights/CHARACTER/history/NAME.json)")
    ] = None,
    character: Annotated[
        str, typer.Option(help="the character whose runs these are: each has a brain of its own")
    ] = "SILENT",
    floors: Annotated[int, typer.Option(min=1)] = 64,
    objective: Annotated[Objective, typer.Option()] = Objective.floor,
    min_count: Annotated[int, typer.Option(help="ids seen fewer times read as unknown")] = 3,
    epochs: Annotated[int, typer.Option(min=1)] = 40,
    patience: Annotated[int, typer.Option(min=1, help="epochs without a better held-out loss before stopping")] = 5,
    batch: Annotated[int, typer.Option(min=1)] = 256,
    lr: float = 1e-3,
    weight_decay: float = 1e-4,
    seed: int = 0,
) -> None:
    out_file = out_path(name, out, character)
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    samples, won = [], 0
    for i, (log, runs) in enumerate(runs_by_log(logs, records, character)):
        won += sum(1 for r in runs.values() if r["outcome"] == "won" and not r.get("failure"))
        for e in read_lines(log, '"run_view"'):
            r = runs.get(e["run"])
            if r is None or r.get("failure") or "run_view" not in e:
                continue
            kind = {"won": "won", "lost": "died"}.get(r["outcome"], "censored")
            start = min(max(e["run_view"]["floor"], 0), floors - 1)
            samples.append(
                {
                    "key": (i, e["run"]),
                    "run": e["run"],
                    "view": e["run_view"],
                    "tokens": tokens(e["run_view"]),
                    "features": e["features"],
                    "start": start,
                    "end": max(start, min(r["floor"], floors - 1)),
                    "floor": r["floor"],
                    "kind": kind,
                }
            )
    per_run = {}
    for s in samples:
        per_run[s["key"]] = per_run.get(s["key"], 0) + 1
    for s in samples:
        s["weight"] = 1.0 / per_run[s["key"]]
    train = [s for s in samples if not held_out(s["run"])]
    test = [s for s in samples if held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")

    tables = vocab(train, min_count)
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
        for i in range(0, len(order), batch):
            yield Batch([part[k] for k in order[i : i + batch]], index, names, mean, std, floors, device)

    test_batches = list(batches(test, False))
    test_weight = sum(s["weight"] for s in test)

    # Baseline: per floor, the hazard the training runs show, whatever the state.
    alive, deaths = torch.zeros(floors, dtype=torch.float64), torch.zeros(floors, dtype=torch.float64)
    for b in batches(train, False):
        alive += (b.label_mask.double().cpu() * b.weight.double().cpu()[:, None]).sum(0)
        deaths += (b.target.double().cpu() * b.label_mask.double().cpu() * b.weight.double().cpu()[:, None]).sum(0)
    base_hazard = ((deaths + 0.5) / (alive + 1)).clamp(1e-4, 1 - 1e-4)
    base_logits = torch.log(base_hazard / (1 - base_hazard)).float().to(device)

    def evaluate(model):
        total, rows = 0.0, []
        with torch.no_grad():
            for b in test_batches:
                logits = model(*b.inputs()) if model else base_logits.expand(len(b.weight), -1)
                total += float((loss_of(logits, b) * b.weight).sum())
                rows.append(expected_floor(torch.sigmoid(logits), b.start))
        return total / test_weight, torch.cat(rows)

    model = Net({t: len(ids) for t, ids in tables.items()}, len(names), floors).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=weight_decay)
    best, best_state, stale = math.inf, None, 0
    for epoch in range(epochs):
        model.train()
        for b in batches(train, True):
            loss = (loss_of(model(*b.inputs()), b) * b.weight).sum() / b.weight.sum()
            opt.zero_grad()
            loss.backward()
            opt.step()
        model.eval()
        held, _ = evaluate(model)
        logger.info("epoch {}: held-out loss {:.4f}", epoch, held)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= patience:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: held-out loss against the baseline, and how well the expected floor predicts the floor reached.
    held, predicted = evaluate(model)
    base_loss, base_predicted = evaluate(None)
    actual = torch.tensor([float(s["floor"]) for s in test], dtype=torch.float64, device=predicted.device)
    weights = torch.tensor([s["weight"] for s in test], dtype=torch.float64, device=predicted.device)

    def r2(pred):
        m = (weights * actual).sum() / weights.sum()
        return float(1 - (weights * (actual - pred) ** 2).sum() / (weights * (actual - m) ** 2).sum())

    report = {
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "won": won,
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_loss_floor_only": base_loss,
        "r2_held_out": r2(predicted),
        "r2_held_out_floor_only": r2(base_predicted),
    }
    print(json.dumps(report))

    # Test runs for RunNet's check on load: a few held-out decisions, valued on the CPU as RunNet values them.
    model = model.cpu()
    picks = random.Random(seed).sample(test, min(8, len(test)))
    tests = []
    with torch.no_grad():
        b = Batch(picks, index, names, mean, std, floors, "cpu")
        hazards = torch.sigmoid(model(*b.inputs()))
        expected = expected_floor(hazards, b.start)
        for k, s in enumerate(picks):
            value = (
                float(expected[k])
                if objective == Objective.floor
                else float((1 - hazards[k, int(s["start"]) :].double()).prod())
            )
            tests.append({"run": s["view"], "value": value})

    net = {
        "name": name,
        "character": character,
        "objective": objective.value,
        "floors": floors,
        "vocab": tables,
        "features": names,
        "mean": [float(v) for v in mean],
        "std": [float(v) for v in std],
        "fit": report,
        "onnx": base64.b64encode(onnx_bytes(model, b)).decode(),
        "tests": tests,
    }
    with open(out_file, "w") as f:
        json.dump(net, f)
        f.write("\n")
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
