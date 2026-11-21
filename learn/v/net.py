"""V's net: a set encoder over the deck, bags of ids for the rest (each relic with its
numbers and status), the cards the relics keep through the deck's card encoder, the raw state, and an event option
picked and not yet played out (its key, the cards it names through the deck's card encoder, its relics and potions as
the run's, its numbers by their rows), then a body and three heads. The main head gives, per floor, the chance to die on
it if still alive there; V's value is the chance to survive every floor. The second head learns the next combats' HP
costs, which shapes the body only: it is not in the net file. The third gives each potion asked about the HP it will
save in the fight where it is drunk, f, which a fight's score counts (plans/2026-11-21-1604-fight-score.md, 2.3).

The body never reads the HP and max HP (plans/2026-11-21-1604-fight-score.md, 2.4): V learned from its games that a low
HP after a boss goes with runs that fought for later, and read the HP as that, so that it valued less HP higher. Here
the HP and max HP move each floor's hazard only through rising pieces of them (`pieces`) times weights that cannot be
negative, which the rest of the run sets: whatever the run, more HP or max HP never makes a floor more deadly, so V
never values them lower, while what they are worth when is still learned. f does not read them either, so that a fight's
score cannot rise as the HP falls.
"""

import sts2_batch
import torch
from torch import nn

from v import spec
from v.data import AUX

HP, MAX_HP = spec.STATE.index("hp"), spec.STATE.index("max_hp")
BODY_STATE = [k for k in range(len(spec.STATE)) if k not in (HP, MAX_HP)]  # the run's numbers the body reads
# The pieces' knots on the standardized HP and max HP (no real run is under -3: HP is 1 or more)
HP_KNOTS = (-3.0, -2.0, -1.5, -1.0, -0.5, 0.0, 0.5, 1.0, 1.5, 2.0)
MAX_HP_KNOTS = (-3.0, -1.0, 0.0, 1.0, 2.0)
RANKS = 4  # the HP's shapes the floors' hazards mix
SAVE_SCALE = 100.0  # f's head gives HP / SAVE_SCALE


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

        # f's: the potions asked about, a sample's held ones with what each saved later (v/saves.py; None: not known)
        saves = [s.get("saves", []) for s in samples]
        width = max(1, max(len(x) for x in saves))
        potions = index["potions"]
        feed["asked"] = torch.tensor([[potions.get(p, 0) for p, _ in x] + [0] * (width - len(x)) for x in saves]).to(device)
        self.saved = torch.tensor([[v or 0.0 for _, v in x] + [0.0] * (width - len(x)) for x in saves]).to(device)
        self.saved_mask = torch.tensor([[float(v is not None) for _, v in x] + [0.0] * (width - len(x)) for x in saves]).to(device)

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
        width = 2 * h + len(spec.BAGS) * d + len(BODY_STATE) + h + d + h + 3 * d
        self.body = nn.Sequential(nn.Linear(width, self.BODY), nn.ReLU(), nn.Linear(self.BODY, self.BODY), nn.ReLU())
        self.head = nn.Linear(self.BODY, floors)
        self.aux_head = nn.Linear(self.BODY, AUX)
        self.register_buffer("hp_knots", torch.tensor(HP_KNOTS))
        self.register_buffer("max_hp_knots", torch.tensor(MAX_HP_KNOTS))
        n = len(HP_KNOTS) + len(MAX_HP_KNOTS) + len(HP_KNOTS) * len(MAX_HP_KNOTS)
        self.hp_weights = nn.Linear(self.BODY, RANKS * n)  # each shape's weight on each piece, softplus
        self.hp_scales = nn.Linear(self.BODY, floors * RANKS)  # each floor's weight on each shape, softplus
        self.save_phi = nn.Sequential(nn.Linear(self.BODY + d, h), nn.ReLU(), nn.Linear(h, 1))
        with torch.no_grad():  # the HP starts out moving the hazards little
            for layer, bias in ((self.hp_weights, -6.0), (self.hp_scales, 0.0)):
                layer.weight.zero_()
                layer.bias.fill_(bias)

    def forward(self, *inputs):
        """Per sample and floor, the logit of dying on that floor if alive there (inputs as spec.INPUTS)."""
        return self.hazards(self.encoded(*inputs), inputs[spec.INPUTS.index("state")])

    def both(self, *inputs):
        """The hazards' logits and the second head's logits."""
        z = self.encoded(*inputs)
        return self.hazards(z, inputs[spec.INPUTS.index("state")]), self.aux_head(z)

    def all(self, *inputs):
        """The hazards' logits, the second head's logits and each asked potion's f (in HP)."""
        z = self.encoded(*inputs)
        x = dict(zip(spec.INPUTS, inputs, strict=True))
        return self.hazards(z, x["state"]), self.aux_head(z), self.saves(z, x["asked"])

    def hazards(self, z, state):
        """Each floor's logit: the body's, less the HP and max HP's rising pieces weighed by what the body says
        (softplus: never negative)."""
        hp, top = pieces(state[:, HP], self.hp_knots), pieces(state[:, MAX_HP], self.max_hp_knots)
        rising = torch.cat([hp, top, (hp[:, :, None] * top[:, None, :]).flatten(1)], 1)  # all >= 0 and rising
        shapes = (nn.functional.softplus(self.hp_weights(z)).view(z.shape[0], RANKS, -1) * rising[:, None, :]).sum(2)
        scales = nn.functional.softplus(self.hp_scales(z)).view(z.shape[0], -1, RANKS)
        return self.head(z) - (scales * shapes[:, None, :]).sum(2)

    def saves(self, z, asked):
        """Each asked potion's f, the HP it will save in the fight where it is drunk: never below 0, never reading the
        HP (the body does not)."""
        x = torch.cat([z[:, None, :].expand(-1, asked.shape[1], -1), self.bags["potions"](asked)], 2)
        return nn.functional.softplus(self.save_phi(x)[..., 0]) * SAVE_SCALE

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
        parts.append(x["state"][:, BODY_STATE])
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


def pieces(x, knots):
    """Rising pieces of X (n), on the knots: between each knot and the next, how far X is into it (0 to its width); past
    the last, how far. Each is 0 or more and never falls as X rises."""
    inner = torch.minimum((x[:, None] - knots[None, :-1]).clamp_min(0), (knots[1:] - knots[:-1])[None, :])
    return torch.cat([inner, (x[:, None] - knots[-1:][None, :]).clamp_min(0)], 1)


class Hazards(nn.Module):
    """The net as ONNX holds it: the hazards themselves (RunNet.Value), and each asked potion's f (RunNet.Saves)."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        logits, _, saves = self.net.all(*inputs)
        return torch.sigmoid(logits), saves


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


def save_loss(saves, b: Batch):
    """Per sample, the squared error of f over the held potions whose saving is known, in SAVE_SCALE HP."""
    return (((saves - b.saved) / SAVE_SCALE) ** 2 * b.saved_mask).sum(1)

