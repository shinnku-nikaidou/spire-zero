"""V's net (docs: macro-brain/network §1): a set encoder over the deck, bags of ids for the rest, the raw state, then a body and
two heads. The main head gives, per floor, the chance to die on it if still alive there; V's value is the floor expected
to be reached, or (objective "win") the chance to survive every floor. The second head learns the next combats' HP costs
and the boss score, which shapes the body only: it is not in the net file.
"""

import math

import torch
from torch import nn

from v import encode
from v.data import AUX

N_AUX = AUX + 1  # the second head: the next combats' HP costs, then the boss score
BOSS_AT = AUX


def padded(rows, device):
    """Lists of ids as one padded tensor (at least one wide) and its mask."""
    width = max(1, max(len(r) for r in rows))
    ids, mask = torch.zeros(len(rows), width, dtype=torch.long), torch.zeros(len(rows), width)
    for b, r in enumerate(rows):
        ids[b, : len(r)], mask[b, : len(r)] = torch.tensor(r, dtype=torch.long), 1
    return ids.to(device), mask.to(device)


class Batch:
    """Samples as tensors: the net's inputs (names: encode.INPUTS), the hazards' targets and the second head's, and the
    weights."""

    def __init__(self, samples, index, mean, std, floors, device):
        n, width = len(samples), max(1, max(len(s["tokens"]["cards"]) for s in samples))
        m_n = max(1, max((len(ms) for s in samples for _, _, ms in s["tokens"]["cards"]), default=0))
        rows = {name: [] for name in ("cards", "ups", "card_mask", "mods", "mod_mask")}
        for s in samples:
            deck, pad = s["tokens"]["cards"], width - len(s["tokens"]["cards"])
            rows["cards"].append([index["cards"].get(card_id, 0) for card_id, _, _ in deck] + [0] * pad)
            rows["ups"].append([float(up) for _, up, _ in deck] + [0.0] * pad)
            rows["card_mask"].append([1.0] * len(deck) + [0.0] * pad)
            mods = [[index["modifiers"].get(m, 0) for m in ms] for _, _, ms in deck]
            rows["mods"].append([m + [0] * (m_n - len(m)) for m in mods] + [[0] * m_n] * pad)
            rows["mod_mask"].append([[1.0] * len(m) + [0.0] * (m_n - len(m)) for m in mods] + [[0.0] * m_n] * pad)
        feed = {
            name: torch.tensor(r, dtype=torch.long if name in ("cards", "mods") else torch.float32).to(device)
            for name, r in rows.items()
        }
        for t, bag_mask in encode.BAGS.items():
            feed[t], feed[bag_mask] = padded(
                [[index[t].get(i, 0) for i in s["tokens"][t]] for s in samples],
                device,
            )
        x = torch.tensor([s["state"] for s in samples], dtype=torch.float64)
        feed["state"] = ((x - mean) / std).float().to(device)
        self.feed = feed

        g = torch.arange(floors)
        start = torch.tensor([s["start"] for s in samples])[:, None]
        end = torch.tensor([s["end"] for s in samples])[:, None]
        died = torch.tensor([s["kind"] == "died" for s in samples])[:, None]
        won = torch.tensor([s["kind"] == "won" for s in samples])[:, None]
        self.target = (died & (g == end)).float().to(device)
        # A censored sample says nothing from the floor it stopped on; one with no reach (end = start) says nothing.
        self.label_mask = ((g >= start) & (won | (g < end) | (died & (g == end)))).float().to(device)
        self.weight = torch.tensor([s["weight"] for s in samples]).to(device)
        # A boss sim's target: its option's boss score less its decision's mean, in floors (0 for other samples).
        self.boss_target = torch.tensor([s.get("boss_target", 0.0) for s in samples], dtype=torch.float64).to(device)
        self.start = start[:, 0].to(device)

        aux, aux_mask = torch.zeros(n, N_AUX), torch.zeros(n, N_AUX)
        for b, s in enumerate(samples):
            for k, cost in enumerate(s["next"]):
                aux[b, k], aux_mask[b, k] = cost, 1
            if s["boss_score"] is not None:
                aux[b, BOSS_AT], aux_mask[b, BOSS_AT] = s["boss_score"], 1
        self.aux, self.aux_mask = aux.to(device), aux_mask.to(device)

    def inputs(self):
        """The net's inputs, in the order of encode.INPUTS."""
        return tuple(self.feed[name] for name in encode.INPUTS)


class Net(nn.Module):
    DIM, HIDDEN, BODY = 64, 256, 512
    SUM_SCALE = 1 / 16  # the deck's sum next to its mean, scaled to about a mean's size

    def __init__(self, sizes: dict[str, int], floors: int):
        super().__init__()
        d, h = self.DIM, self.HIDDEN
        self.cards = nn.Embedding(sizes["cards"], d)
        self.card_up = nn.Embedding(sizes["cards"], d)
        self.modifiers = nn.Embedding(sizes["modifiers"], d)
        self.bags = nn.ModuleDict({t: nn.Embedding(sizes[t], d) for t in encode.BAGS})
        for e in (self.cards, self.card_up, self.modifiers, *self.bags.values()):
            nn.init.normal_(e.weight, std=0.1)
        self.phi = nn.Sequential(nn.Linear(d, h), nn.ReLU(), nn.Linear(h, h), nn.ReLU())
        width = 2 * h + len(encode.BAGS) * d + len(encode.STATE)
        self.body = nn.Sequential(nn.Linear(width, self.BODY), nn.ReLU(), nn.Linear(self.BODY, self.BODY), nn.ReLU())
        self.head = nn.Linear(self.BODY, floors)
        self.aux_head = nn.Linear(self.BODY, N_AUX)

    def forward(self, *inputs):
        """Per sample and floor, the logit of dying on that floor if alive there (inputs as encode.INPUTS)."""
        return self.head(self.encoded(*inputs))

    def both(self, *inputs):
        """The hazards' logits and the second head's logits."""
        x = self.encoded(*inputs)
        return self.head(x), self.aux_head(x)

    def encoded(self, cards, ups, card_mask, mods, mod_mask, *rest):
        """The body's output, which both heads read."""
        *bags, state = rest
        x = (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + (self.modifiers(mods) * mod_mask[..., None]).sum(2)
        )
        h = self.phi(x) * card_mask[..., None]
        total = h.sum(1)
        parts = [total / card_mask.sum(1, keepdim=True).clamp_min(1), total * self.SUM_SCALE]
        for t, (ids, mask) in zip(encode.BAGS, zip(bags[::2], bags[1::2], strict=True), strict=True):
            parts.append((self.bags[t](ids) * mask[..., None]).sum(1))
        return self.body(torch.cat(parts + [state], 1))


class Hazards(nn.Module):
    """The net as ONNX holds it: the hazards themselves (RunNet.Value)."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        return torch.sigmoid(self.net(*inputs))


def expected_floor(hazards: torch.Tensor, start: torch.Tensor):
    """As RunNet.Value: the start floor plus the chance to survive each floor from it on."""
    h = hazards.double()
    g = torch.arange(h.shape[1], device=h.device)
    survive = torch.where(g >= start[:, None], 1 - h, torch.ones_like(h)).cumprod(1)
    return start.double() + (survive * (g >= start[:, None])).sum(1)


def win_chance(hazards: torch.Tensor, start: torch.Tensor):
    """As RunNet.Value for "win": the chance to survive every floor from the start on (a won run survived them all)."""
    h = hazards.double()
    g = torch.arange(h.shape[1], device=h.device)
    return torch.where(g >= start[:, None], 1 - h, torch.ones_like(h)).prod(1)


def values(model, b: Batch, objective: str = "floor"):
    """V's value of a batch, as RunNet.Value gives it: the floor expected, or the chance to win (objective "win")."""
    return (win_chance if objective == "win" else expected_floor)(torch.sigmoid(model(*b.inputs())), b.start)


def hazard_loss(logits, b: Batch):
    """Per sample, the masked cross-entropy of the hazards."""
    per_floor = nn.functional.binary_cross_entropy_with_logits(logits, b.target, reduction="none")
    return (per_floor * b.label_mask).sum(1)


def aux_loss(aux_logits, b: Batch):
    """Per sample, the squared error of the second head over the targets it has."""
    return ((torch.sigmoid(aux_logits) - b.aux) ** 2 * b.aux_mask).sum(1)


def segment_log_softmax(values: torch.Tensor, group: torch.Tensor, groups: int):
    """The log-softmax of values within each group (group[i] is value i's group, groups in 0..groups-1)."""
    top = torch.full((groups,), -math.inf, dtype=values.dtype, device=values.device).scatter_reduce(
        0, group, values, "amax"
    )
    shifted = values - top[group]
    total = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, shifted.exp())
    return shifted - total.log()[group]


def explored(values, temperature, group, groups) -> torch.Tensor:
    """Per group, the chance MacroBrain.Pick explores at the temperature (one per option): a softmax over the logs of V's
    values that draws an option more than temperature / 100 below the group's best log. An option worth nothing is
    never drawn, and a group worth nothing draws nothing."""
    best = torch.full((groups,), -math.inf, dtype=values.dtype, device=values.device)
    best = best.scatter_reduce(0, group, values, reduce="amax")[group]
    logs = torch.where(best > 0, values / best, torch.ones_like(values)).clamp_min(0).log()  # 0 at the best
    p = segment_log_softmax(logs / temperature, group, groups).exp()
    below = -logs > temperature / 100
    return torch.zeros(groups, dtype=p.dtype, device=p.device).index_add(0, group, p * below)


def centred(values: torch.Tensor, group: torch.Tensor, groups: int) -> torch.Tensor:
    """Values less their group's mean."""
    total = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, values)
    count = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, torch.ones_like(values))
    return values - (total / count)[group]


def pearson(x: torch.Tensor, y: torch.Tensor) -> float:
    x, y = x - x.mean(), y - y.mean()
    return float((x * y).sum() / ((x * x).sum() * (y * y).sum()).sqrt().clamp_min(1e-12))
