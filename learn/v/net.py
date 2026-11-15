"""V's net: a set encoder over the deck, bags of ids for the rest (each relic with its
numbers and status), the cards the relics keep through the deck's card encoder, the raw state, then a body and two heads. The main head gives, per floor, the chance to die on it if still alive there; V's value is the chance to
survive every floor. The second head learns the next combats' HP costs, which shapes the body only: it is not in the net
file.
"""

import torch
from torch import nn

from common import unpack
from v import spec
from v.data import AUX


def padded(rows, device):
    """Lists of ids as one padded tensor (at least one wide) and its mask."""
    width = max(1, max(len(r) for r in rows))
    ids, mask = torch.zeros(len(rows), width, dtype=torch.long), torch.zeros(len(rows), width)
    for b, r in enumerate(rows):
        ids[b, : len(r)], mask[b, : len(r)] = torch.tensor(r, dtype=torch.long), 1
    return ids.to(device), mask.to(device)


class Batch:
    """Samples (their tokens packed, v/data.py) as tensors: the net's inputs (names: spec.INPUTS), the hazards' targets
    and the second head's, and the weights."""

    def __init__(self, samples, index, mean, std, floors, device):
        toks = [unpack(s["tokens"]) for s in samples]
        n, width = len(samples), max(1, max(len(tok["cards"]) for tok in toks))
        m_n = max(1, max((len(c[2]) for tok in toks for c in tok["cards"]), default=0))
        rows = {name: [] for name in ("cards", "ups", "card_mask", "mods", "mod_mask", "mod_amounts", "numbers")}
        for tok in toks:
            deck, pad = tok["cards"], width - len(tok["cards"])
            rows["cards"].append([index["cards"].get(c[0], 0) for c in deck] + [0] * pad)
            rows["ups"].append([float(c[1]) for c in deck] + [0.0] * pad)
            rows["card_mask"].append([1.0] * len(deck) + [0.0] * pad)
            mods = [[index["modifiers"].get(m, 0) for m in c[2]] for c in deck]
            rows["mods"].append([m + [0] * (m_n - len(m)) for m in mods] + [[0] * m_n] * pad)
            rows["mod_mask"].append([[1.0] * len(m) + [0.0] * (m_n - len(m)) for m in mods] + [[0.0] * m_n] * pad)
            rows["mod_amounts"].append([c[3] + [0.0] * (m_n - len(c[3])) for c in deck] + [[0.0] * m_n] * pad)
            rows["numbers"].append([list(c[4]) for c in deck] + [[0.0] * N_NUMBERS] * pad)
        feed = {
            name: torch.tensor(r, dtype=torch.long if name in ("cards", "mods") else torch.float32).to(device)
            for name, r in rows.items()
        }
        for t, bag_mask in spec.BAGS.items():
            feed[t], feed[bag_mask] = padded(
                [[index[t].get(i, 0) for i in tok[t]] for tok in toks],
                device,
            )
        x = torch.tensor([s["state"] for s in samples], dtype=torch.float64)
        feed["state"] = ((x - mean) / std).float().to(device)
        r_n = max(1, max(len(tok["relics"]) for tok in toks))
        relic_values, relic_status = torch.zeros(n, r_n, 3), torch.zeros(n, r_n, dtype=torch.long)
        for b, tok in enumerate(toks):
            for k, (v, status) in enumerate(zip(tok["relic_values"], tok["relic_status"], strict=True)):
                relic_values[b, k], relic_status[b, k] = torch.tensor(v), index["relic_status"].get(status, 0)
        feed["relic_values"], feed["relic_status"] = relic_values.to(device), relic_status.to(device)
        feed["held"], feed["held_mask"] = padded([[index["cards"].get(c, 0) for c in tok["held"]] for tok in toks], device)
        self.feed = feed

        g = torch.arange(floors)
        start = torch.tensor([s["start"] for s in samples])[:, None]
        end = torch.tensor([s["end"] for s in samples])[:, None]
        died = torch.tensor([s["kind"] == "died" for s in samples])[:, None]
        won = torch.tensor([s["kind"] == "won" for s in samples])[:, None]
        target = (died & (g == end)).float()
        # A censored sample says nothing from the floor it stopped on; one with no reach (end = start) says nothing.
        label_mask = ((g >= start) & (won | (g < end) | (died & (g == end)))).float()
        for b, s in enumerate(samples):
            if "boot" in s:  # a multi-step target (v/bootstrap.py): s' floor and the frozen V's hazards there
                later_floor, hazards = s["boot"]
                target[b], label_mask[b] = boot_targets(s["start"], later_floor, torch.tensor(hazards), floors)
        self.target, self.label_mask = target.to(device), label_mask.to(device)
        self.weight = torch.tensor([s["weight"] for s in samples]).to(device)
        self.start = start[:, 0].to(device)

        aux, aux_mask = torch.zeros(n, AUX), torch.zeros(n, AUX)
        for b, s in enumerate(samples):
            for k, cost in enumerate(s["next"]):
                aux[b, k], aux_mask[b, k] = cost, 1
        self.aux, self.aux_mask = aux.to(device), aux_mask.to(device)

    def inputs(self):
        """The net's inputs, in the order of spec.INPUTS."""
        return tuple(self.feed[name] for name in spec.INPUTS)


def boot_targets(start: int, later_floor: int, hazards: torch.Tensor, floors: int) -> tuple[torch.Tensor, torch.Tensor]:
    """A bootstrapped sample's targets and weights per floor (v/bootstrap.py): lived through from its start to s', then
    from s' on the frozen V's hazards there, each floor weighed by the frozen V's chance that the run is alive on it."""
    g = torch.arange(floors)
    target = torch.where(g >= later_floor, hazards, torch.zeros(floors))
    alive = torch.ones(floors)
    alive[later_floor + 1 :] = torch.cumprod(1 - hazards[later_floor:-1], 0)
    return target, torch.where(g >= start, alive, torch.zeros(floors))


N_NUMBERS = 3  # a card's damage, block and growth (a card's token)


class Net(nn.Module):
    DIM, HIDDEN, BODY = 64, 256, 512
    SUM_SCALE = 1 / 16  # the deck's sum next to its mean, scaled to about a mean's size

    def __init__(self, sizes: dict[str, int], floors: int):
        super().__init__()
        d, h = self.DIM, self.HIDDEN
        self.cards = nn.Embedding(sizes["cards"], d)
        self.card_up = nn.Embedding(sizes["cards"], d)
        self.card_damage = nn.Embedding(sizes["cards"], d)
        self.card_block = nn.Embedding(sizes["cards"], d)
        self.card_grown = nn.Embedding(sizes["cards"], d)
        self.modifiers = nn.Embedding(sizes["modifiers"], d)
        self.mod_amount = nn.Embedding(sizes["modifiers"], d)
        self.bags = nn.ModuleDict({t: nn.Embedding(sizes[t], d) for t in spec.BAGS})
        self.relic_counter = nn.Embedding(sizes["relics"], d)
        self.relic_status = nn.Embedding(sizes["relic_status"], d)
        for e in (self.cards, self.card_up, self.card_damage, self.card_block, self.card_grown, self.modifiers, self.mod_amount,
                  *self.bags.values(), self.relic_counter, self.relic_status):  # fmt: skip
            nn.init.normal_(e.weight, std=0.1)
        self.melted = nn.Parameter(torch.randn(d) * 0.1)
        self.phi = nn.Sequential(nn.Linear(d, h), nn.ReLU(), nn.Linear(h, h), nn.ReLU())
        # the deck's mean and scaled sum, the bags, the state, then the kept cards' sum
        width = 2 * h + len(spec.BAGS) * d + len(spec.STATE) + h
        self.body = nn.Sequential(nn.Linear(width, self.BODY), nn.ReLU(), nn.Linear(self.BODY, self.BODY), nn.ReLU())
        self.head = nn.Linear(self.BODY, floors)
        self.aux_head = nn.Linear(self.BODY, AUX)

    def forward(self, *inputs):
        """Per sample and floor, the logit of dying on that floor if alive there (inputs as spec.INPUTS)."""
        return self.head(self.encoded(*inputs))

    def both(self, *inputs):
        """The hazards' logits and the second head's logits."""
        x = self.encoded(*inputs)
        return self.head(x), self.aux_head(x)

    def encoded(self, cards, ups, card_mask, mods, mod_mask, mod_amounts, numbers, *rest):
        """The body's output, which both heads read. A relic reads as the combat net reads it: its id's row, its counter
        times its id's counter row, the melted row if melted, and its status's row."""
        *bags, state, relic_values, relic_status, held, held_mask = rest
        x = (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + numbers[..., 0:1] * self.card_damage(cards)
            + numbers[..., 1:2] * self.card_block(cards)
            + numbers[..., 2:3] * self.card_grown(cards)
            + ((self.modifiers(mods) + mod_amounts[..., None] * self.mod_amount(mods)) * mod_mask[..., None]).sum(2)
        )
        h = self.phi(x) * card_mask[..., None]
        total = h.sum(1)
        parts = [total / card_mask.sum(1, keepdim=True).clamp_min(1), total * self.SUM_SCALE]
        for t, (ids, mask) in zip(spec.BAGS, zip(bags[::2], bags[1::2], strict=True), strict=True):
            rows = self.bags[t](ids)
            if t == "relics":
                rows = (
                    rows
                    + relic_values[..., 0:1] * self.relic_counter(ids)
                    + relic_values[..., 2:3] * self.melted
                    + self.relic_status(relic_status)
                )
            parts.append((rows * mask[..., None]).sum(1))
        kept = (self.phi(self.cards(held)) * held_mask[..., None]).sum(1)
        return self.body(torch.cat(parts + [state, kept], 1))


class Hazards(nn.Module):
    """The net as ONNX holds it: the hazards themselves (RunNet.Value)."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        return torch.sigmoid(self.net(*inputs))


def win_chance(hazards: torch.Tensor, start: torch.Tensor):
    """As RunNet.Value: the chance to survive every floor from the start on (a won run survived them all)."""
    h = hazards.double()
    g = torch.arange(h.shape[1], device=h.device)
    return torch.where(g >= start[:, None], 1 - h, torch.ones_like(h)).prod(1)


def values(model, b: Batch):
    """V's value of a batch, as RunNet.Value gives it: the chance to win."""
    return win_chance(torch.sigmoid(model(*b.inputs())), b.start)


def hazard_loss(logits, b: Batch):
    """Per sample, the masked cross-entropy of the hazards."""
    per_floor = nn.functional.binary_cross_entropy_with_logits(logits, b.target, reduction="none")
    return (per_floor * b.label_mask).sum(1)


def aux_loss(aux_logits, b: Batch):
    """Per sample, the squared error of the second head over the targets it has."""
    return ((torch.sigmoid(aux_logits) - b.aux) ** 2 * b.aux_mask).sum(1)

