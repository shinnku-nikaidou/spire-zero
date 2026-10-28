"""Train V v2 (docs: learning §1–§2), a net over the run's cards, relics, potions and context, from self-play.

The decision logs (`run --log`) carry the run view and V v0's features on every macro decision; the run records
(`run --records`) carry where each run ended, joined by run id, each log with the record file written beside it (given
in the same order). The net's head gives, per floor, the chance to die on it if still alive there. Its targets are the run's outcome only: survived every floor from the decision's up to the one
it died on, or every floor if it won; a truncated run says nothing from its last floor on. Each run weighs the same
however many decisions it has, and every fifth run (by run id) is held out, for early stopping and the report, next to
a baseline that knows only the floor. A second head learns from the run's next combats too (the share of max HP each
one cost, which the run records hold), at --aux-weight in the loss; it shapes the body only, and the net file holds
the hazards alone. The output is a net file for `run --value`, in the character's weights folder by default
(docs: learning §6): the net as ONNX, with what RunNet needs to feed it, and test runs that RunNet checks on load.

Two more sources, both optional (docs: learning §2). --forks reads counterfactual pairs (`fork`): two branches from one
decision with the same seed, played to the end of the act. Each branch's afterstate is a sample like a decision's,
censored where the branch stopped, at --fork-weight; and the difference of the two afterstates' expected floors is
fitted to the pair's played-out difference, at --pair-weight: a branch that died reached its floor, one that lived
through the act what V expects of the run at its horizon (bootstrapped). --afterstates reads every option of the
logged decisions (`afterstates`, valued by the V that played the logs); at --kl-weight, V's softmax over each
decision's options is pulled towards the policy that played the logs: the softmax of the logging V's values at the
log's own exploration temperature. V's softmax is at the temperature that matches it: the log's own for a V v2 net,
which values in floors as V does, and --kl-temperature for V v0. Fork pairs and decisions are split by run id as the
decisions are.

--boss-sims reads simulated boss fights (`bosslab.py collect`): for decisions of the logs, each option's afterstate
(from --boss-afterstates) fought its act's boss a few times with the same seeds. The sims' absolute odds are far from
real play's (a weaker planner, fixed HP), so they give no hazard label: only the options' differences within a decision
count. Each option's fights have a mean boss score (a win 0.5 to 1 by the HP left, a loss 0 to 0.5 by the boss's HP
taken), and V's expected floors, centred on the decision's mean, are fitted to the centred scores times 2k floors per
unit of score, at --boss-weight: k is what beating the act's boss is worth in the runs, the mean floor reached by the
runs that beat it less the boss floor, and a win against a loss is about half a unit of score. The score is also one
more output of the second head, which shapes the body only. The report says how often V's best option on held-out
decisions is the sims' best, and how well V's centred values correlate with the sims'.

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
AUX = 6  # the next combats whose HP cost the second head learns
ACT = 2  # with --aux-act, the second head also learns whether the act's boss was beaten, and the HP share left after it
BOSS_FLOORS = {0: 17, 1: 33}  # the floor of each act's boss fight
# The second head's outputs past the next combats, in order, with their widths: main() sets the ones in use.
EXTRA_AUX: list[tuple[str, int]] = []
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


def next_combats(record: dict, floor: int) -> list[float]:
    """The share of max HP each of the run's next combats after the floor cost, at most AUX of them (a death costs
    what was left)."""
    costs = []
    for c in record.get("combats", []):
        if c["floor"] <= floor or len(costs) == AUX:
            continue
        end = c.get("end") or {}
        lost = c["hp"] - (0 if end.get("died") else end.get("hp", c["hp"]))
        costs.append(min(max(lost / max(c["max_hp"], 1), 0.0), 1.0))
    return costs


def act_boss(record: dict, act: int) -> list[float] | None:
    """Whether the run beat its act's last boss fight, and the share of max HP left after it (0 if it did not); None
    when the run stopped before an outcome (truncated, not dead) without a boss fight of that act."""
    bosses = [c for c in record.get("combats", []) if c.get("act") == act and c.get("room") == "Boss"]
    if bosses and not (bosses[-1].get("end") or {}).get("died") and bosses[-1].get("end"):
        end = bosses[-1]["end"]
        return [1.0, min(max(end["hp"] / max(end["max_hp"], 1), 0.0), 1.0)]
    if record["outcome"] == "lost" or bosses:
        return [0.0, 0.0]
    return None


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def trained_on(run_id: str, share: float, start: float) -> bool:
    """Whether a training run is in the share of the training runs used, for learning curves: its place in [0, 1),
    from the run id, lies in [start, start + share). Disjoint starts give disjoint halves, for split-half tests."""
    place = int(run_id, 16) // 5 % 1000
    return start * 1000 <= place < (start + share) * 1000


def merged(view: dict, changes: dict) -> dict:
    """A run view with an afterstate's changes as `fork` and `afterstates` write them: the fields that differ, to one
    level inside an object field; a null removes a field."""
    out = dict(view)
    for k, v in changes.items():
        if v is None:
            out.pop(k, None)
        elif isinstance(v, dict) and isinstance(view.get(k), dict):
            out[k] = {kk: vv for kk, vv in {**view[k], **v}.items() if vv is not None}
        else:
            out[k] = v
    return out


def temperature(brain: str) -> float | None:
    """The macro exploration temperature a log's brain name carries ("l0 explore 0.25"), or None when it did not explore."""
    parts = brain.split()
    return float(parts[parts.index("explore") + 1]) if "explore" in parts else None


def pull_temperature(brain: str, v0_temperature: float) -> float:
    """V's softmax temperature, in floors, at which it would play as the brain that logged a decision. A V v2 net
    ("l0 n2 explore 0.006") values in expected floors as V does, so it is the log's own temperature; V v0
    ("l0 explore 0.25") values in its own units, which --kl-temperature converts."""
    parts = brain.split()
    return v0_temperature if parts[1] == "explore" else float(parts[parts.index("explore") + 1])


def segment_softmax_share(values, below, group, groups) -> torch.Tensor:
    """Per group, the softmax's mass on the options marked below (values already divided by the temperature)."""
    p = segment_log_softmax(values, group, groups).exp()
    return torch.zeros(groups, dtype=p.dtype, device=p.device).index_add(0, group, p * below)


def explored(values, temperature, group, groups) -> torch.Tensor:
    """Per group, the chance a softmax at the temperature (one per option) draws an option more than temperature / 100
    below the group's best: what self-play counts as explored (Softmax.Explored)."""
    best = torch.full((groups,), -math.inf, dtype=values.dtype, device=values.device)
    best = best.scatter_reduce(0, group, values, reduce="amax")
    return segment_softmax_share(values / temperature, best[group] - values > temperature / 100, group, groups)


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
        aux, aux_mask = torch.zeros(n, AUX), torch.zeros(n, AUX)
        for b, s in enumerate(samples):
            for k, cost in enumerate(s["next"]):
                aux[b, k], aux_mask[b, k] = cost, 1
        for key, width in EXTRA_AUX:
            extra, extra_mask = torch.zeros(n, width), torch.zeros(n, width)
            for b, s in enumerate(samples):
                if s.get(key) is not None:
                    extra[b], extra_mask[b] = torch.tensor(s[key], dtype=torch.float), 1
            aux, aux_mask = torch.cat([aux, extra], 1), torch.cat([aux_mask, extra_mask], 1)
        self.aux, self.aux_mask = aux.to(device), aux_mask.to(device)

    def inputs(self):
        """The net's inputs, in the order of INPUTS."""
        bags = [x for t in BAGS for x in self.bags[t]]
        return (self.cards, self.ups, self.mask, self.mods, self.mod_mask, *bags, self.features, *self.numbers)


class DeckAttention(nn.Module):
    """One self-attention block over the deck's card vectors (pre-norm, residual, masked), so a card's vector can
    depend on the others before they are pooled; written out by hand so that it exports to plain ONNX ops."""

    def __init__(self, dim, heads=4):
        super().__init__()
        self.heads = heads
        self.norm1, self.norm2 = nn.LayerNorm(dim), nn.LayerNorm(dim)
        self.qkv, self.out = nn.Linear(dim, 3 * dim), nn.Linear(dim, dim)
        self.ff = nn.Sequential(nn.Linear(dim, 2 * dim), nn.ReLU(), nn.Linear(2 * dim, dim))

    def forward(self, x, mask):
        n, k, d = x.shape
        q, key, v = self.qkv(self.norm1(x)).reshape(n, k, 3, self.heads, d // self.heads).permute(2, 0, 3, 1, 4)
        scores = q @ key.transpose(-1, -2) / math.sqrt(d // self.heads) + (mask[:, None, None, :] - 1) * 1e9
        x = x + self.out((scores.softmax(-1) @ v).transpose(1, 2).reshape(n, k, d))
        return x + self.ff(self.norm2(x))


class Net(nn.Module):
    def __init__(
        self,
        sizes,
        n_features,
        floors,
        dim=32,
        hidden=128,
        body=256,
        sum_scale=1 / 16,
        n_extra=1,
        n_aux=AUX,
        attention=0,
    ):
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
        self.attention = nn.ModuleList(DeckAttention(dim) for _ in range(attention))
        self.phi = nn.Sequential(nn.Linear(dim, hidden), nn.ReLU(), nn.Linear(hidden, hidden), nn.ReLU())
        self.body = nn.Sequential(
            nn.Linear(2 * hidden + 3 * dim + n_features + n_extra, body), nn.ReLU(), nn.Linear(body, body), nn.ReLU()
        )
        self.head = nn.Linear(body, floors)
        nn.init.constant_(self.head.bias, -3.0)
        self.aux_head = nn.Linear(body, n_aux)

    def forward(self, *inputs):
        """Per sample and floor, the logit of dying on that floor if alive there (inputs as INPUTS names them)."""
        return self.head(self.state(*inputs))

    def both(self, *inputs):
        """The hazards' logits and the second head's logits of the next combats' HP costs."""
        h = self.state(*inputs)
        return self.head(h), self.aux_head(h)

    def state(
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
        """The body's output, which both heads read."""
        x = (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + (self.modifiers(mods) * mod_mask[..., None]).sum(2)
            + self.card_numbers(mods, mod_mask, costs, mod_amounts, card_scalars, var_ids, var_values, var_mask)
        )
        for block in self.attention:
            x = block(x, card_mask)
        h = self.phi(x) * card_mask[..., None]
        total = h.sum(1)
        mean = total / card_mask.sum(1, keepdim=True).clamp_min(1)
        parts = [mean, total * self.sum_scale]
        relic = self.relics(relics) + self.relic_numbers(relics, relic_values, relic_status)
        parts.append((relic * relic_mask[..., None]).sum(1))
        for table, ids, mask in ((self.potions, potions, potion_mask), (self.context, context, context_mask)):
            parts.append((table(ids) * mask[..., None]).sum(1))
        return self.body(torch.cat(parts + [features, extra], 1))


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


def aux_loss_of(aux_logits, b: Batch):
    """Per sample, the squared error of the next combats' HP costs, over the combats the run had."""
    return ((torch.sigmoid(aux_logits) - b.aux) ** 2 * b.aux_mask).sum(1)


def expected_floor(hazards: torch.Tensor, start: torch.Tensor):
    """As RunNet.Value with objective floor: the start floor plus the chance to survive each floor from it on."""
    h = hazards.double()
    g = torch.arange(h.shape[1], device=h.device)
    survive = torch.where(g >= start[:, None], 1 - h, torch.ones_like(h)).cumprod(1)
    return start.double() + (survive * (g >= start[:, None])).sum(1)


def floors_within(hazards: torch.Tensor, start: torch.Tensor, horizon: torch.Tensor):
    """The floor reached within the horizon: the start floor plus the chance to survive each floor from it up to the
    horizon's last floor. A branch that died on floor f reached f; one that lived through the horizon, the floor after."""
    h = hazards.double()
    g = torch.arange(h.shape[1], device=h.device)
    inside = (g >= start[:, None]) & (g <= horizon[:, None])
    survive = torch.where(inside, 1 - h, torch.ones_like(h)).cumprod(1)
    return start.double() + (survive * inside).sum(1)


def segment_log_softmax(values: torch.Tensor, group: torch.Tensor, groups: int):
    """The log-softmax of values within each group (group[i] is value i's group, groups in 0..groups-1)."""
    top = torch.full((groups,), -math.inf, dtype=values.dtype, device=values.device).scatter_reduce(
        0, group, values, "amax"
    )
    shifted = values - top[group]
    total = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, shifted.exp())
    return shifted - total.log()[group]


def state_sample(
    run: str, view: dict, features: dict, floors: int, reached: int | None = None, died: bool = False
) -> dict:
    """A run view as a sample, with an outcome when it was played out: died on floor reached, or lived through the
    floor before it."""
    start = min(max(view["floor"], 0), floors - 1)
    end = start if reached is None else max(start, min(reached, floors - 1))
    return {
        "run": run,
        "view": view,
        "tokens": tokens(view),
        "features": features,
        "start": start,
        "end": end,
        "kind": "died" if died else "censored",
        "next": [],
        "weight": 1.0,
    }


def boss_score(fight: dict, max_hp: int) -> float:
    """A simulated boss fight as one number in [0, 1]: a win is 0.5 plus half the share of max HP left, a loss half
    the share of the boss's HP taken."""
    if fight["won"]:
        return 0.5 + 0.5 * min(max(fight["hp"] / max(max_hp, 1), 0.0), 1.0)
    return 0.5 * (1 - min(max(fight["enemy_hp"] / max(fight["enemy_max_hp"], 1), 0.0), 1.0))


def boss_worth(records, floors: int) -> dict[int, float]:
    """Per act, what beating its boss is worth in floors: the mean floor reached by the runs that beat it, less the
    boss floor (where the runs that lost to it died)."""
    worth = {}
    for act, boss in BOSS_FLOORS.items():
        reached = [min(r["floor"], floors - 1) for r in records if (act_boss(r, act) or [0])[0] == 1]
        worth[act] = max(sum(reached) / len(reached) - boss, 0.0) if reached else 0.0
    return worth


def read_boss_sims(paths: str, afterstates: str, views: dict, floors: int, worth: dict) -> list[dict]:
    """The simulated boss fights (`bosslab.py collect`): per decision, each option's afterstate (the logged view with
    the option's changes from `afterstates`) as a sample with no hazard label, its fights' mean boss score for the
    second head, and its target: the score less the decision's mean, times 2 * worth[act] floors per unit."""
    sims = [d for d in read_lines(paths) if (d["run"], d["decision_id"]) in views and d["act"] in BOSS_FLOORS]
    sims = [d for d in sims if sum(1 for o in d["options"] if o["fights"]) >= 2]
    wanted = {(d["run"], d["decision_id"]) for d in sims}
    options = {}
    for e in read_lines(afterstates):
        if (e["run"], e["decision_id"]) in wanted:
            options[(e["run"], e["decision_id"])] = {o["action"]: o for o in e["options"]}
    groups = []
    for d in sims:
        view = views[(d["run"], d["decision_id"])][0]
        after = options.get((d["run"], d["decision_id"]), {})
        group = []
        for o in d["options"]:
            if not o["fights"] or o["action"] not in after:
                continue
            a = merged(view, after[o["action"]].get("after") or {})
            sample = state_sample(d["run"], a, after[o["action"]]["features"], floors)
            scores = [boss_score(f, a["max_hp"]) for f in o["fights"]]
            group.append(sample | {"boss_score": [sum(scores) / len(scores)], "logged_value": o.get("value")})
        if len(group) >= 2:
            mean = sum(x["boss_score"][0] for x in group) / len(group)
            for x in group:
                x["weight"] = 1.0 / len(group)
                x["boss_target"] = 2 * worth[d["act"]] * (x["boss_score"][0] - mean)
            groups.append({"run": d["run"], "samples": group})
    return groups


def centred(values: torch.Tensor, group: torch.Tensor, groups: int) -> torch.Tensor:
    """Values less their group's mean."""
    total = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, values)
    count = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, torch.ones_like(values))
    return values - (total / count)[group]


def boss_report(model, groups, make) -> dict:
    """On held-out decisions with simulated boss fights: how often V's best option is the sims' best (by mean boss
    score, decisions with one best only), next to the logging V's (the afterstates' values); and the correlation of
    V's centred values with the targets, over all their options."""
    agree = logged = n = 0
    vs, ts = [], []
    with torch.no_grad():
        for g in groups:
            b = make(g["samples"])
            values = expected_floor(torch.sigmoid(model(*b.inputs())), b.start)
            vs.append(values - values.mean())
            ts.append(torch.tensor([s["boss_target"] for s in g["samples"]], dtype=torch.float64, device=values.device))
            scores = [s["boss_score"][0] for s in g["samples"]]
            best = max(scores)
            if scores.count(best) > 1:
                continue
            values = values.tolist()
            n += 1
            agree += scores[values.index(max(values))] == best
            old = [s["logged_value"] for s in g["samples"]]
            if None not in old:
                logged += scores[old.index(max(old))] == best
    v, t = torch.cat(vs), torch.cat(ts)
    corr = float((v * t).sum() / ((v * v).sum() * (t * t).sum()).sqrt().clamp_min(1e-12))
    return {"boss_decisions": n, "boss_agree": agree / max(n, 1), "boss_agree_logged": logged / max(n, 1),
            "boss_corr": corr}


def read_forks(paths: str, floors: int) -> list[dict]:
    """The fork pairs: each branch's afterstate as a sample with its outcome (died on its floor, or lived through the
    act), the run at the horizon of each branch that lived, V v0's difference (the champion's ranking: the first
    branch is its best), the act's last floor (from the branches that lived through it), and two differences played
    out: the floors reached within the act, and the same plus the share of max HP left at the horizon."""
    entries = list(read_lines(paths))
    last = {}
    for e in entries:
        for b in e["branches"]:
            if not b["died"]:
                last[e["act"]] = max(last.get(e["act"], 0), b["floor"])
    pairs = []
    for e in entries:
        options = {o["action"]: o for o in e["options"]}
        branches = []
        for b in e["branches"]:
            o = options[b["action"]]
            reached = b["floor"] if b["died"] else b["floor"] + 1
            view = merged(e["run_view"], o["after"])
            branches.append(
                {
                    "sample": state_sample(e["run"], view, o["features"], floors, reached, b["died"]),
                    "reached": float(reached),
                    "score": reached + (0.0 if b["died"] else b["hp"] / max(b["max_hp"], 1)),
                    "horizon": None
                    if b["died"]
                    else state_sample(e["run"], b["horizon"], b["horizon_features"], floors),
                    "v0": o["value"],
                }
            )
        a, b = branches
        horizon = last.get(e["act"], max(a["reached"], b["reached"]))
        pairs.append(
            {
                "run": e["run"],
                "branches": branches,
                "last": min(int(horizon), floors - 1),
                "within": a["reached"] - b["reached"],
                "score": a["score"] - b["score"],
                "v0": a["v0"] - b["v0"],
            }
        )
    return pairs


def read_afterstates(paths: str, views: dict, floors: int, v0_temperature: float) -> list[dict]:
    """The logged decisions with two options or more, each with its options' afterstates, the chance the logs'
    policy gave each (the softmax of the values of the V the log's brain names, at its exploration temperature), V's
    temperature that matches it (`pull_temperature`), the option it drew and the floor its run reached.

    The afterstates must be valued by the V that played the logs. Where a log says a pick did not explore, it was
    within the temperature / 100 of the best, so the share of such picks that are not the afterstates' best tells:
    near 0 when they are, and a warning above 1% (g0's logs, whose observations have gained fields since, are at 12%)."""
    groups, checked, off = [], {}, {}
    for e in read_lines(paths):
        key = (e["run"], e["decision_id"])
        if len(e["options"]) < 2 or key not in views:
            continue
        view, brain, outcome = views[key]
        t = temperature(brain)
        actions = [o["action"] for o in e["options"]]
        if t is None or e["chosen"] not in actions:
            continue
        values = torch.tensor([o["value"] for o in e["options"]], dtype=torch.float64)
        if not e["explored"]:
            checked[brain] = checked.get(brain, 0) + 1
            off[brain] = off.get(brain, 0) + int(float(values.max() - values[actions.index(e["chosen"])]) > t / 100)
        groups.append(
            {
                "run": e["run"],
                "surface": e["surface"],
                "view": view,
                "actions": actions,
                "chosen": actions.index(e["chosen"]),
                "outcome": float(outcome),
                "options": [(o["after"], o["features"]) for o in e["options"]],
                "values": values.tolist(),
                "temperature": t,
                "behaviour": torch.softmax(values / t, 0).tolist(),
                "tau": pull_temperature(brain, v0_temperature),
            }
        )
    for brain, n in sorted(checked.items()):
        if off[brain] > 0.01 * n:
            logger.warning(
                "{} of the {} picks {} logged without exploring are not the afterstates' best: are they valued by its V?",
                off[brain],
                n,
                brain,
            )
    return groups


class PairBatch:
    """Fork pairs as one batch of their branches, the first branches then the second ones; the horizons of the
    branches that lived, in the same order; and each pair's differences."""

    def __init__(self, pairs, make, device):
        branches = [p["branches"][0] for p in pairs] + [p["branches"][1] for p in pairs]
        self.both = make([b["sample"] for b in branches])
        self.reached = torch.tensor([b["reached"] for b in branches], dtype=torch.float64, device=device)
        self.lived = torch.tensor([b["horizon"] is not None for b in branches], device=device)
        horizons = [b["horizon"] for b in branches if b["horizon"] is not None]
        self.horizons = make(horizons) if horizons else None
        self.last = torch.tensor([p["last"] for p in pairs] * 2, device=device)
        self.within, self.score, self.v0 = (
            torch.tensor([p[k] for p in pairs], dtype=torch.float64, device=device) for k in ("within", "score", "v0")
        )


class GroupBatch:
    """Logged decisions as one batch of their options' afterstates, with each option's decision, the chance the
    logs' policy gave it, and per option the logs' temperature and V's that matches it."""

    def __init__(self, groups, make, floors, device):
        samples, group, behaviour = [], [], []
        for k, g in enumerate(groups):
            for (after, features), p in zip(g["options"], g["behaviour"], strict=True):
                samples.append(state_sample(g["run"], merged(g["view"], after), features, floors))
                group.append(k)
                behaviour.append(p)
        self.options = make(samples)
        self.group = torch.tensor(group, device=device)
        self.behaviour = torch.tensor(behaviour, dtype=torch.float64, device=device)
        self.temperature, self.tau = (
            torch.tensor([g[key] for g in groups for _ in g["options"]], dtype=torch.float64, device=device)
            for key in ("temperature", "tau")
        )
        self.v0 = torch.tensor([v for g in groups for v in g["values"]], dtype=torch.float64, device=device)
        self.groups = len(groups)
        self.sizes = [len(g["options"]) for g in groups]
        self.surfaces = [g["surface"] for g in groups]
        self.actions = [g["actions"] for g in groups]
        self.chosen = [g["chosen"] for g in groups]
        self.outcomes = [g["outcome"] for g in groups]


def pair_target(model, pb: PairBatch):
    """Each pair's difference in the floor its branches reach: the floor a branch died on, or, past the horizon, V's
    expected floor at the run there (bootstrapped, without a gradient)."""
    with torch.no_grad():
        target = pb.reached.clone()
        if pb.horizons is not None:
            target[pb.lived] = expected_floor(torch.sigmoid(model(*pb.horizons.inputs())), pb.horizons.start)
    n = len(pb.v0)
    return target[:n] - target[n:]


def fork_report(model, batches) -> dict:
    """On the held-out pairs: how often a predicted difference has the played one's sign, each pair weighed by the
    played difference's size and ties left out. V's expected floor (what L0 compares) and V v0's values (the champion's
    ranking: its best is the first branch) against the floors reached within the act plus the HP share left; the
    floors reached within the act predicted against the played ones, and the share of their variance explained."""
    within, expected, v0, real_within, real_score = [], [], [], [], []
    with torch.no_grad():
        for pb in batches:
            h = torch.sigmoid(model(*pb.both.inputs()))
            n = len(pb.v0)
            w, f = floors_within(h, pb.both.start, pb.last), expected_floor(h, pb.both.start)
            within.append(w[:n] - w[n:])
            expected.append(f[:n] - f[n:])
            v0.append(pb.v0)
            real_within.append(pb.within)
            real_score.append(pb.score)
    if not v0:
        return {}
    pw, pf, pv, rw, rs = (torch.cat(x).cpu() for x in (within, expected, v0, real_within, real_score))

    def sign_accuracy(pred, real):
        size = real.abs()
        return float((size * (torch.sign(pred) == torch.sign(real))).sum() / size.sum().clamp_min(1e-9))

    return {
        "fork_sign_accuracy": sign_accuracy(pf, rs),
        "fork_sign_accuracy_v0": sign_accuracy(pv, rs),
        "fork_sign_accuracy_within": sign_accuracy(pw, rw),
        "fork_r2_within": 1 - float(((pw - rw) ** 2).sum() / ((rw - rw.mean()) ** 2).sum().clamp_min(1e-9)),
        "fork_mean_score_diff": float(rs.mean()),
        "fork_ties": float((rs == 0).double().mean()),
    }


def pull_report(model, batches) -> dict:
    """On the held-out decisions, per surface: the KL from the logs' policy to V's softmax, how often V's best option
    is the logging V's (V v0's, or the champion's), and on card rewards how often each would skip. And the slope of
    V's values on the logging V's within a decision (both less their decision's mean): V v0's exploration temperature
    times it is that temperature in floors.

    And the self-play temperature for V (`run --macro-explore`): the one at which V would explore as often as the
    logs' policies did (the share of decisions drawn more than the temperature / 100 below the best), so that the
    next generation explores as this one did.

    And an off-policy check of V's ranking (inverse propensity scoring): on the decisions where the logs' policy gave
    V v0's best option a chance between 2% and 98%, the floor the runs reached after drawing another option against
    after drawing the best, each weighed by the inverse of its chance, next to what V expects of the same: the other
    options at their chances, less the best. The draws do not depend on the run's luck, so the played difference is
    unbiased, if noisy."""
    stats, xy, xx, ips, valued, logs_explored = {}, 0.0, 0.0, [], [], 0.0
    with torch.no_grad():
        for gb in batches:
            values = expected_floor(torch.sigmoid(model(*gb.options.inputs())), gb.options.start)
            valued.append((values, gb))
            logs_explored += float(explored(gb.v0, gb.temperature, gb.group, gb.groups).sum())
            count = torch.tensor(gb.sizes, dtype=torch.float64, device=values.device)
            centred = [
                x
                - (torch.zeros(gb.groups, dtype=torch.float64, device=x.device).index_add(0, gb.group, x) / count)[
                    gb.group
                ]
                for x in (values, gb.v0)
            ]
            xy, xx = xy + float((centred[0] * centred[1]).sum()), xx + float((centred[1] ** 2).sum())
            logp = segment_log_softmax(values / gb.tau, gb.group, gb.groups)
            p = gb.behaviour
            kl = torch.zeros(gb.groups, dtype=torch.float64, device=p.device).index_add(
                0, gb.group, p * (p.clamp_min(1e-12).log() - logp)
            )
            for k, (v, q) in enumerate(zip(values.split(gb.sizes), p.split(gb.sizes), strict=True)):
                row = stats.setdefault(gb.surfaces[k], {"decisions": 0, "kl": 0.0, "agree": 0, "skip": 0, "skip_v0": 0})
                best, best_v0 = gb.actions[k][int(v.argmax())], gb.actions[k][int(q.argmax())]
                row["decisions"] += 1
                row["kl"] += float(kl[k])
                row["agree"] += best == best_v0
                row["skip"] += best.startswith("card_alt")
                row["skip_v0"] += best_v0.startswith("card_alt")
                g = int(q.argmax())
                pg = float(q[g])
                if 0.02 < pg < 0.98:
                    drew_best = gb.chosen[k] == g
                    played = gb.outcomes[k] * ((0.0 if drew_best else 1 / (1 - pg)) - (1 / pg if drew_best else 0.0))
                    others = float((q * v).sum() - pg * v[g]) / (1 - pg)
                    ips.append((gb.surfaces[k], played, others - float(v[g])))
    n = sum(row["decisions"] for row in stats.values())
    report = {"pull_kl": sum(row["kl"] for row in stats.values()) / max(1, n), "pull_slope": xy / max(xx, 1e-12)}
    report["pull_agree"] = sum(row["agree"] for row in stats.values()) / max(1, n)

    def share(t: float) -> float:
        return sum(float(explored(v, torch.full_like(v, t), gb.group, gb.groups).sum()) for v, gb in valued) / max(1, n)

    # The share grows with the temperature: bisect on its log.
    target, lo, hi = logs_explored / max(1, n), math.log(1e-6), math.log(10.0)
    for _ in range(40):
        mid = (lo + hi) / 2
        lo, hi = (mid, hi) if share(math.exp(mid)) < target else (lo, mid)
    report["explore_share_logs"] = target
    report["explore_temperature"] = math.exp((lo + hi) / 2)
    if len(ips) > 1:
        played = torch.tensor([p for _, p, _ in ips], dtype=torch.float64)
        report["ips"] = {
            "decisions": len(ips),
            "played": float(played.mean()),
            "played_se": float(played.std() / len(ips) ** 0.5),
            "expected": sum(e for _, _, e in ips) / len(ips),
        }
    report["pull"] = {
        surface: {
            "decisions": row["decisions"],
            "kl": row["kl"] / row["decisions"],
            "agree": row["agree"] / row["decisions"],
        }
        | (
            {"skip": row["skip"] / row["decisions"], "skip_v0": row["skip_v0"] / row["decisions"]}
            if surface == "card_reward"
            else {}
        )
        for surface, row in sorted(stats.items())
    }
    return report


class Objective(StrEnum):
    floor = "floor"
    win = "win"


@script.command(help=__doc__)
def main(
    logs: Annotated[str, typer.Option(help="decision logs, comma-separated")],
    records: Annotated[str, typer.Option(help="run records, comma-separated")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    out: Annotated[
        str | None, typer.Option(help="where the file goes (default: weights/CHARACTER/NAME.json)")
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
    lr: float = 3e-5,
    weight_decay: float = 1e-4,
    aux_weight: Annotated[
        float, typer.Option(min=0, help="the weight of the next combats' HP costs in the loss; 0: no second head")
    ] = 3.0,
    aux_act: Annotated[
        bool, typer.Option(help="the second head also learns the act boss's outcome and the HP share left after it")
    ] = False,
    width: Annotated[
        float, typer.Option(min=0.1, help="scales the net's widths (embeddings 32, card net 128, body 256)")
    ] = 1.0,
    attention: Annotated[
        int, typer.Option(min=0, help="self-attention blocks over the deck's cards before they are pooled")
    ] = 0,
    boss_sims: Annotated[
        str | None, typer.Option(help="simulated boss fights per decision option (`bosslab.py collect`), comma-separated")
    ] = None,
    boss_afterstates: Annotated[
        str | None, typer.Option(help="the afterstates (`afterstates`) of those decisions, comma-separated")
    ] = None,
    boss_weight: Annotated[
        float, typer.Option(min=0, help="the weight of a decision's simulated fights, all its options together")
    ] = 1.0,
    state_out: Annotated[
        str | None, typer.Option(help="also write the torch net, both heads, for analysis (torch.save)")
    ] = None,
    forks: Annotated[str | None, typer.Option(help="counterfactual pairs (`fork`), comma-separated")] = None,
    fork_weight: Annotated[
        float, typer.Option(min=0, help="the weight of each fork branch's outcome in the loss")
    ] = 1.0,
    pair_weight: Annotated[
        float,
        typer.Option(min=0, help="the weight of the pairs' differences, as a share of their variance left unexplained"),
    ] = 1.0,
    afterstates: Annotated[
        str | None, typer.Option(help="every option of the logged decisions (`afterstates`), comma-separated")
    ] = None,
    kl_weight: Annotated[float, typer.Option(min=0, help="the weight of the pull towards the logs' policy")] = 0.0,
    kl_temperature: Annotated[
        float,
        typer.Option(
            min=0.001,
            help="V's softmax temperature, in floors, over the options of decisions V v0 logged (its exploration "
            "temperature in floors); a V v2 net's logs use their own",
        ),
    ] = 1.0,
    side_batch: Annotated[int, typer.Option(min=1, help="fork pairs, or logged decisions, per step")] = 64,
    train_share: Annotated[
        float, typer.Option(min=0, max=1, help="share of the training runs used, for learning curves")
    ] = 1.0,
    train_start: Annotated[
        float, typer.Option(min=0, max=1, help="where that share starts among the training runs (by run id)")
    ] = 0.0,
    seed: int = 0,
) -> None:
    out_file = out_path(name, out, character)
    if boss_sims and not boss_afterstates:
        raise LearnError("--boss-sims needs --boss-afterstates")
    EXTRA_AUX[:] = ([("act_boss", ACT)] if aux_act else []) + ([("boss_score", 1)] if boss_sims else [])
    n_aux = AUX + sum(w for _, w in EXTRA_AUX)
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    samples, won, views, all_runs = [], 0, {}, []
    for i, (log, runs) in enumerate(runs_by_log(logs, records, character)):
        all_runs += [r for r in runs.values() if not r.get("failure")]
        won += sum(1 for r in runs.values() if r["outcome"] == "won" and not r.get("failure"))
        for e in read_lines(log, '"run_view"'):
            r = runs.get(e["run"])
            if r is None or r.get("failure") or "run_view" not in e:
                continue
            views[(e["run"], e["decision_id"])] = (e["run_view"], e["brain"], r["floor"])
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
                    "next": next_combats(r, e["run_view"]["floor"]),
                }
                | ({"act_boss": act_boss(r, e["run_view"]["act"])} if aux_act else {})
            )
    per_run = {}
    for s in samples:
        per_run[s["key"]] = per_run.get(s["key"], 0) + 1
    for s in samples:
        s["weight"] = 1.0 / per_run[s["key"]]
    train = [s for s in samples if not held_out(s["run"]) and trained_on(s["run"], train_share, train_start)]
    test = [s for s in samples if held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")

    pairs = read_forks(forks, floors) if forks else []
    groups = read_afterstates(afterstates, views, floors, kl_temperature) if afterstates else []
    worth = boss_worth(all_runs, floors)
    bosses = read_boss_sims(boss_sims, boss_afterstates, views, floors, worth) if boss_sims else []
    if boss_sims and not bosses:
        raise LearnError("no decision of --boss-sims has two options with fights and a logged view")
    train_bosses = [g for g in bosses if not held_out(g["run"])]
    test_bosses = [g for g in bosses if held_out(g["run"])]
    if (forks and not pairs) or (afterstates and not groups):
        raise LearnError(
            f"{len(pairs)} fork pairs and {len(groups)} logged decisions with options: nothing to learn from"
        )
    train_pairs = [p for p in pairs if not held_out(p["run"])]
    test_pairs = [p for p in pairs if held_out(p["run"])]
    train_groups = [g for g in groups if not held_out(g["run"])]
    test_groups = [g for g in groups if held_out(g["run"])]
    # The pairs' differences are fitted in units of their played-out differences' mean square (floors reached and HP
    # share left), so the weight does not depend on their scale.
    pair_scale = max(0.01, sum(p["score"] ** 2 for p in train_pairs) / max(1, len(train_pairs)))

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

    def pair_batch(chunk):
        return PairBatch(chunk, lambda part: Batch(part, index, names, mean, std, floors, device), device)

    def group_batch(chunk):
        return GroupBatch(chunk, lambda part: Batch(part, index, names, mean, std, floors, device), floors, device)

    def side(part, make):
        """Endless batches of fork pairs or logged decisions, shuffled anew on each pass."""
        while part:
            order = random.sample(part, len(part))
            for i in range(0, len(order), side_batch):
                yield make(order[i : i + side_batch])

    test_pair_batches = [pair_batch(test_pairs[i : i + side_batch]) for i in range(0, len(test_pairs), side_batch)]
    test_group_batches = [group_batch(test_groups[i : i + side_batch]) for i in range(0, len(test_groups), side_batch)]
    train_pair_stream, train_group_stream = side(train_pairs, pair_batch), side(train_groups, group_batch)

    def boss_batch_of(part):
        return Batch(part, index, names, mean, std, floors, device)

    def boss_batch(chunk):
        return boss_batch_of([s for g in chunk for s in g["samples"]])

    def boss_chunk(chunk):
        group = torch.tensor([k for k, g in enumerate(chunk) for _ in g["samples"]], device=device)
        target = torch.tensor([s["boss_target"] for g in chunk for s in g["samples"]], dtype=torch.float64)
        return boss_batch(chunk), group, target.to(device), len(chunk)

    train_boss_stream = side(train_bosses, boss_chunk)
    # The fit is in units of the targets' mean square, as the pairs' is, so the weight does not depend on their scale.
    boss_scale = max(
        1e-4, sum(s["boss_target"] ** 2 * s["weight"] for g in train_bosses for s in g["samples"]) / max(1, len(train_bosses))
    )

    def boss_loss(model, chunk):
        """A chunk of decisions' loss, per decision: V's centred values against the targets, in units of their mean
        square, plus the second head's boss score at --aux-weight."""
        b, group, target, decisions = chunk
        logits, aux = model.both(*b.inputs())
        values = centred(expected_floor(torch.sigmoid(logits), b.start), group, decisions)
        fit = ((values - target) ** 2 * b.weight).sum() / boss_scale
        return (fit + aux_weight * (aux_loss_of(aux, b) * b.weight).sum()) / decisions

    def evaluate_bosses(model):
        with torch.no_grad():
            total = sum(float(boss_loss(model, boss_chunk([g]))) for g in test_bosses)
        return total / max(1, len(test_bosses))

    # Baseline: per floor, the hazard the training runs show, whatever the state.
    alive, deaths = torch.zeros(floors, dtype=torch.float64), torch.zeros(floors, dtype=torch.float64)
    for b in batches(train, False):
        alive += (b.label_mask.double().cpu() * b.weight.double().cpu()[:, None]).sum(0)
        deaths += (b.target.double().cpu() * b.label_mask.double().cpu() * b.weight.double().cpu()[:, None]).sum(0)
    base_hazard = ((deaths + 0.5) / (alive + 1)).clamp(1e-4, 1 - 1e-4)
    base_logits = torch.log(base_hazard / (1 - base_hazard)).float().to(device)

    def evaluate(model):
        total, aux_total, rows = 0.0, 0.0, []
        with torch.no_grad():
            for b in test_batches:
                if model:
                    logits, aux = model.both(*b.inputs())
                    aux_total += float((aux_loss_of(aux, b) * b.weight).sum())
                else:
                    logits = base_logits.expand(len(b.weight), -1)
                total += float((loss_of(logits, b) * b.weight).sum())
                rows.append(expected_floor(torch.sigmoid(logits), b.start))
        return total / test_weight, torch.cat(rows), aux_total / test_weight

    def side_losses(model, pb, gb):
        """The fork branches' loss, the pairs' share of variance unexplained and the pull's cross-entropy, each a
        mean (0 without the batch)."""
        fork, pair, kl = (torch.zeros((), device=device) for _ in range(3))
        if pb is not None:
            logits = model(*pb.both.inputs())
            fork = loss_of(logits, pb.both).mean()
            n = len(pb.v0)
            values = expected_floor(torch.sigmoid(logits), pb.both.start)
            pair = ((values[:n] - values[n:] - pair_target(model, pb)) ** 2).mean() / pair_scale
        if gb is not None:
            values = expected_floor(torch.sigmoid(model(*gb.options.inputs())), gb.options.start)
            logp = segment_log_softmax(values / gb.tau, gb.group, gb.groups)
            kl = -(gb.behaviour * logp).sum() / gb.groups
        return fork, pair, kl

    def evaluate_side(model):
        fork = pair = kl = 0.0
        with torch.no_grad():
            for pb in test_pair_batches:
                f, p, _ = side_losses(model, pb, None)
                fork, pair = fork + float(f) * len(pb.v0), pair + float(p) * len(pb.v0)
            for gb in test_group_batches:
                kl += float(side_losses(model, None, gb)[2]) * gb.groups
        return fork / max(1, len(test_pairs)), pair / max(1, len(test_pairs)), kl / max(1, len(test_groups))

    model = Net(
        {t: len(ids) for t, ids in tables.items()},
        len(names),
        floors,
        dim=round(32 * width),
        hidden=round(128 * width),
        body=round(256 * width),
        n_aux=n_aux,
        attention=attention,
    ).to(device)
    # The net starts from the baseline and learns how the state moves each floor's hazard away from it.
    with torch.no_grad():
        model.head.bias.copy_(base_logits)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=weight_decay)
    best, best_state, stale = math.inf, None, 0
    for epoch in range(epochs):
        model.train()
        for b in batches(train, True):
            logits, aux = model.both(*b.inputs())
            per_sample = loss_of(logits, b) + aux_weight * aux_loss_of(aux, b)
            loss = (per_sample * b.weight).sum() / b.weight.sum()
            fork, pair, kl = side_losses(
                model,
                next(train_pair_stream, None) if fork_weight > 0 or pair_weight > 0 else None,
                next(train_group_stream, None) if kl_weight > 0 else None,
            )
            loss = loss + fork_weight * fork + pair_weight * pair + kl_weight * kl
            if train_bosses and boss_weight > 0:
                loss = loss + boss_weight * boss_loss(model, next(train_boss_stream))
            opt.zero_grad()
            loss.backward()
            opt.step()
        model.eval()
        held, _, held_aux = evaluate(model)
        held_fork, held_pair, held_kl = evaluate_side(model)
        # Early stopping weighs the held-out losses as training does, but for the pairs': their targets are V's own
        # values at the horizon, which move as V learns, so their loss does not compare across epochs.
        held_boss = evaluate_bosses(model) if test_bosses else 0.0
        total = held + fork_weight * held_fork + kl_weight * held_kl + boss_weight * held_boss
        logger.info(
            "epoch {}: held-out loss {:.4f}, next combats {:.4f}, forks {:.4f}, pairs {:.4f}, pull {:.4f}, boss sims {:.4f}",
            epoch,
            held,
            held_aux,
            held_fork,
            held_pair,
            held_kl,
            held_boss,
        )
        if total < best - 1e-4:
            best, best_state, stale = total, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= patience:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: held-out loss against the baseline, and how well the expected floor predicts the floor reached.
    held, predicted, held_aux = evaluate(model)
    base_loss, base_predicted, _ = evaluate(None)
    actual = torch.tensor([float(s["floor"]) for s in test], dtype=torch.float64, device=predicted.device)
    weights = torch.tensor([s["weight"] for s in test], dtype=torch.float64, device=predicted.device)

    def r2(pred):
        m = (weights * actual).sum() / weights.sum()
        return float(1 - (weights * (actual - pred) ** 2).sum() / (weights * (actual - m) ** 2).sum())

    report = {
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "train_runs": len({s["key"] for s in train}),
        "train_share": [train_start, train_share],
        "samples": len(samples),
        "won": won,
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_loss_floor_only": base_loss,
        "aux_weight": aux_weight,
        "aux_act": aux_act,
        "width": width,
        "attention": attention,
        "held_out_next_combats": held_aux,
        "r2_held_out": r2(predicted),
        "r2_held_out_floor_only": r2(base_predicted),
    }
    if pairs:
        report |= {"forks": len(pairs), "held_out_forks": len(test_pairs), "fork_weight": fork_weight}
        report |= {"pair_weight": pair_weight, **fork_report(model, test_pair_batches)}
    if bosses:
        report |= {"boss_sims": len(bosses), "held_out_boss_sims": len(test_bosses), "boss_weight": boss_weight}
        report |= {"boss_worth": worth, "held_out_boss_loss": evaluate_bosses(model)}
        report |= boss_report(model, test_bosses, boss_batch_of)
    if groups:
        report |= {"decisions_pulled": len(groups), "kl_weight": kl_weight, "kl_temperature": kl_temperature}
        report |= pull_report(model, test_group_batches)
    print(json.dumps(report))

    if state_out:
        torch.save(
            {"state": model.state_dict(), "tables": tables, "names": names, "mean": mean, "std": std,
             "floors": floors, "width": width, "attention": attention, "n_aux": n_aux, "report": report},
            state_out,
        )

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
