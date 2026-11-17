"""V's net: a set encoder over the deck, bags of ids for the rest (each relic with its
numbers and status), the cards the relics keep through the deck's card encoder, the raw state, and an event option
picked and not yet played out (its key, the cards it names through the deck's card encoder, its relics and potions as
the run's, its numbers by their rows), then a body and two heads. The main head gives, per floor, the chance to die on
it if still alive there; V's value is the chance to survive every floor. The second head learns the next combats' HP
costs, which shapes the body only: it is not in the net file.
"""

import sts2_batch
import torch
from torch import nn

from v import spec
from v.data import AUX


class Batch:
    """Samples (their tokens packed, v/data.py) as tensors: the net's inputs (names: spec.INPUTS), the hazards' targets
    and the second head's, and the weights. The tokens are looked up in the tables and padded by learn/batch (Rust,
    sts2_batch), as RunNet does for one run."""

    def __init__(self, samples, index, mean, std, floors, device):
        feed = {name: t.to(device) for name, t in sts2_batch.v_inputs([s["tokens"] for s in samples], index).items()}
        x = torch.tensor([s["state"] for s in samples], dtype=torch.float64)
        feed["state"] = ((x - mean) / std).float().to(device)
        self.feed = feed

        g = torch.arange(floors)
        start = torch.tensor([s["start"] for s in samples])[:, None]
        end = torch.tensor([s["end"] for s in samples])[:, None]
        died = torch.tensor([s["kind"] == "died" for s in samples])[:, None]
        won = torch.tensor([s["kind"] == "won" for s in samples])[:, None]
        target = (died & (g == end)).float()
        # A censored sample says nothing from the floor it stopped on; one with no reach (end = start) says nothing.
        label_mask = ((g >= start) & (won | (g < end) | (died & (g == end)))).float()
        boot = [b for b, s in enumerate(samples) if "boot" in s]
        if boot:  # multi-step targets (v/bootstrap.py): s' floor and the frozen V's hazards there
            later = torch.tensor([samples[b]["boot"][0] for b in boot])[:, None]
            hazards = torch.tensor([list(samples[b]["boot"][1]) for b in boot])
            target[boot], label_mask[boot] = boot_targets(start[boot], later, hazards, floors)
        self.target, self.label_mask = target.to(device), label_mask.to(device)
        self.weight = torch.tensor([s["weight"] for s in samples]).to(device)
        self.start = start[:, 0].to(device)

        costs = [s["next"] for s in samples]
        self.aux = torch.tensor([c + [0.0] * (AUX - len(c)) for c in costs], dtype=torch.float32).to(device)
        self.aux_mask = torch.tensor([[1.0] * len(c) + [0.0] * (AUX - len(c)) for c in costs]).to(device)

    def inputs(self):
        """The net's inputs, in the order of spec.INPUTS."""
        return tuple(self.feed[name] for name in spec.INPUTS)


def boot_targets(start, later, hazards, floors: int) -> tuple[torch.Tensor, torch.Tensor]:
    """Bootstrapped samples' targets and weights per floor (v/bootstrap.py; START and LATER [m, 1], the floors of each
    sample and of its s', HAZARDS [m, floors]): lived through from its start to s', then from s' on the frozen V's
    hazards there, each floor weighed by the frozen V's chance that the run is alive on it."""
    g = torch.arange(floors)
    after = g >= later
    target = torch.where(after, hazards, torch.zeros(floors))
    # alive on a floor: the product of the chances to live through each floor from s' to the one before (1 up to s')
    lived = torch.cumprod(torch.where(after, 1 - hazards, torch.ones(floors)), 1)
    alive = torch.cat([torch.ones(len(hazards), 1), lived[:, :-1]], 1)
    return target, torch.where(g >= start, alive, torch.zeros(floors))


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
        # An event option picked and not yet played out: its key's row (entry 0, a key the table does not list, is
        # held at zero) and its numbers' rows; whether a card, relic or potion it names is the player's own.
        self.pending = nn.Embedding(sizes["pending"], d, padding_idx=0)
        self.event_vars = nn.Embedding(sizes["event_vars"], d)
        for e in (self.cards, self.card_up, self.card_damage, self.card_block, self.card_grown, self.modifiers, self.mod_amount,
                  *self.bags.values(), self.relic_counter, self.relic_status, self.pending, self.event_vars):  # fmt: skip
            nn.init.normal_(e.weight, std=0.1)
        with torch.no_grad():
            self.pending.weight[0] = 0
        self.melted = nn.Parameter(torch.randn(d) * 0.1)
        self.pend_card_owned = nn.Parameter(torch.randn(d) * 0.1)
        self.pend_relic_owned = nn.Parameter(torch.randn(d) * 0.1)
        self.pend_potion_owned = nn.Parameter(torch.randn(d) * 0.1)
        self.phi = nn.Sequential(nn.Linear(d, h), nn.ReLU(), nn.Linear(h, h), nn.ReLU())
        # the deck's mean and scaled sum, the bags, the state, the kept cards' sum, then the event option's key, cards'
        # sum, relics, potions and numbers
        width = 2 * h + len(spec.BAGS) * d + len(spec.STATE) + h + d + h + 3 * d
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

    def card_x(self, cards, ups, mods, mod_mask, mod_amounts, numbers):
        """A card's reading before the card encoder: its id's row, its upgrade level, damage, block and growth times
        its id's rows for them, and its modifiers with their amounts."""
        return (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + numbers[..., 0:1] * self.card_damage(cards)
            + numbers[..., 1:2] * self.card_block(cards)
            + numbers[..., 2:3] * self.card_grown(cards)
            + ((self.modifiers(mods) + mod_amounts[..., None] * self.mod_amount(mods)) * mod_mask[..., None]).sum(2)
        )

    def relic_rows(self, ids, values, status):
        """Relics as the combat net reads them: the id's row, the counter times the id's counter row, the melted row if
        melted, and the status's row."""
        return (
            self.bags["relics"](ids)
            + values[..., 0:1] * self.relic_counter(ids)
            + values[..., 2:3] * self.melted
            + self.relic_status(status)
        )

    def encoded(self, *inputs):
        """The body's output, which both heads read (inputs as spec.INPUTS)."""
        x = dict(zip(spec.INPUTS, inputs, strict=True))
        p = spec.PEND
        h = self.phi(self.card_x(*(x[k] for k in ("cards", "ups", "mods", "mod_mask", "mod_amounts", "numbers"))))
        h = h * x["card_mask"][..., None]
        total = h.sum(1)
        parts = [total / x["card_mask"].sum(1, keepdim=True).clamp_min(1), total * self.SUM_SCALE]
        for t, mask in spec.BAGS.items():
            ids = x[t]
            rows = self.relic_rows(ids, x["relic_values"], x["relic_status"]) if t == "relics" else self.bags[t](ids)
            parts.append((rows * x[mask][..., None]).sum(1))
        parts.append(x["state"])
        parts.append((self.phi(self.cards(x["held"])) * x["held_mask"][..., None]).sum(1))
        # the event option picked and not yet played out: all zero for none
        parts.append((self.pending(x["pending"]) * x["pending_mask"][..., None]).sum(1))
        named = self.card_x(*(x[p + k] for k in ("cards", "ups", "mods", "mod_mask", "mod_amounts", "numbers")))
        named = named + x[p + "cards_owned"][..., None] * self.pend_card_owned
        parts.append((self.phi(named) * x[p + "card_mask"][..., None]).sum(1))
        relics = self.relic_rows(x[p + "relics"], x[p + "relic_values"], x[p + "relic_status"])
        relics = relics + x[p + "relics_owned"][..., None] * self.pend_relic_owned
        parts.append((relics * x[p + "relic_mask"][..., None]).sum(1))
        potions = self.bags["potions"](x[p + "potions"]) + x[p + "potions_owned"][..., None] * self.pend_potion_owned
        parts.append((potions * x[p + "potion_mask"][..., None]).sum(1))
        parts.append((self.event_vars(x[p + "vars"]) * (x[p + "var_values"] * x[p + "var_mask"])[..., None]).sum(1))
        return self.body(torch.cat(parts, 1))


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

