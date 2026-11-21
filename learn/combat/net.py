"""The combat net: set encoders over the cards (per pile), the powers, the enemies (each with its
intent slots) and the allies, bags of ids for the rest, then a body and two heads (combat/data.py): the chance of each ending and,
after a win in each group of buckets (data.POTION_GROUPS), each potion's of being gone, so the leaf weighs the HP lost and
the potions drunk together.
"""

import sts2_batch
import torch
from torch import nn

from combat import spec
from combat.data import BUCKETS, LOSS_MIN, POTION_GROUPS, WIDTH, potion_group


class Batch:
    """Samples (their tokens packed, combat/data.py) as tensors, in the order of spec.INPUTS, and their targets. The
    tokens are looked up in the tables and padded by learn/batch (Rust, sts2_batch), as CombatNet does for one state."""

    def __init__(self, samples, index, device):
        inputs = sts2_batch.combat_inputs([s["tokens"] for s in samples], index)
        self.tensors = [inputs[name].to(device) for name in spec.INPUTS]
        self.target = torch.tensor([s["class"] for s in samples], dtype=torch.long, device=device)
        self.potions = self.tensors[spec.INPUTS.index("potions")]
        self.potion_mask = self.tensors[spec.INPUTS.index("potion_mask")]
        w = self.potion_mask.shape[1]
        used = [s["potion_used"] for s in samples]
        self.potion_used = torch.tensor([u + [0] * (w - len(u)) for u in used], dtype=torch.float, device=device)

    def inputs(self):
        """The net's inputs, in the order of spec.INPUTS."""
        return tuple(self.tensors)


def mlp(*sizes):
    layers = []
    for a, b in zip(sizes, sizes[1:], strict=False):
        layers += [nn.Linear(a, b), nn.ReLU()]
    return nn.Sequential(*layers)


class Net(nn.Module):
    DIM, HIDDEN, BODY = 64, 128, 512
    SUM_SCALE = 1 / 8  # each pile's sum, scaled to about a mean's size
    N_PLAYER, N_EXTRA = 8, 6
    CLOSED = -1e4  # the logit of an ending the state rules out: no weight in float32's softmax

    def __init__(self, sizes: dict[str, int]):
        super().__init__()
        d, h = self.DIM, self.HIDDEN
        for table in spec.TABLES:
            if table != "powers":
                setattr(self, table, nn.Embedding(sizes[table], d))
        self.card_up = nn.Embedding(sizes["cards"], d)
        self.card_damage = nn.Embedding(sizes["cards"], d)
        self.card_block = nn.Embedding(sizes["cards"], d)
        self.card_calc = nn.Embedding(sizes["cards"], d)
        self.card_replay = nn.Embedding(sizes["cards"], d)
        self.card_grown = nn.Embedding(sizes["cards"], d)
        self.mod_amount = nn.Embedding(sizes["modifiers"], d)
        self.relic_counter = nn.Embedding(sizes["relics"], d)
        self.powers = nn.Embedding(sizes["powers"], d)
        self.power_amount = nn.Embedding(sizes["powers"], d)
        self.power_stacks = nn.Embedding(sizes["powers"], d)
        self.power_detail = nn.Embedding(sizes["powers"], d)
        self.intent_damage = nn.Embedding(sizes["intents"], d)
        self.intent_total = nn.Embedding(sizes["intents"], d)
        self.orb_passive = nn.Embedding(sizes["orbs"], d)
        self.orb_evoke = nn.Embedding(sizes["orbs"], d)
        self.orb_at = nn.Embedding(spec.MAX_ORBS * sizes["orbs"], d)  # an orb's kind at its place in the queue
        for m in self.modules():
            if isinstance(m, nn.Embedding):
                nn.init.normal_(m.weight, std=0.1)
        self.pile = nn.Parameter(torch.randn(len(spec.PILES), d) * 0.1)
        self.slot = nn.Parameter(torch.randn(spec.SLOTS, d) * 0.1)
        self.cost = nn.Parameter(torch.randn(d) * 0.1)
        self.damage = nn.Parameter(torch.randn(d) * 0.1)
        self.stars = nn.Parameter(torch.randn(d) * 0.1)
        self.gathered = nn.Parameter(torch.randn(d) * 0.1)
        self.melted = nn.Parameter(torch.randn(d) * 0.1)
        self.card_phi = mlp(d, h, h)
        self.power_phi = mlp(d, h)
        self.enemy_scalars = nn.Linear(spec.ENEMY_SCALARS, d)
        self.enemy_phi = mlp(d + h + spec.SLOTS * d, 2 * h, 2 * h)
        self.ally_scalars = nn.Linear(4, d)
        self.ally_phi = mlp(d + h, h)
        width = len(spec.PILES) * h + self.N_PLAYER + h + 2 * h + 4 * d + h + self.N_EXTRA
        self.body = mlp(width, self.BODY, self.BODY)
        self.head = nn.Linear(self.BODY, 1 + BUCKETS)
        self.potion_head = nn.Linear(self.BODY, POTION_GROUPS * sizes["potions"])
        self.register_buffer("bottoms", LOSS_MIN + torch.arange(BUCKETS) * WIDTH)

    def power_sum(self, ids, amounts, mask, stacks=None, cards=None, card_mask=None, details=None):
        """The powers through power_phi, summed; with their stacks, kept cards and moved numbers where they are given."""
        x = self.powers(ids) + amounts[..., None] * self.power_amount(ids)
        if stacks is not None:
            x = x + stacks[..., None] * self.power_stacks(ids) + self.cards(cards) * card_mask[..., None]
            x = x + details[..., None] * self.power_detail(ids)
        return (self.power_phi(x) * mask[..., None]).sum(-2)

    def forward(self, *inputs):
        """Per sample, the logits of the ending classes and, after a win in each group of buckets, of each potion id
        being gone, as (sample, group, potion) (inputs as spec.INPUTS)."""
        z = self.encode(*inputs)
        potions = self.potion_head(z).view(z.shape[0], POTION_GROUPS, -1)
        return self.reachable(self.head(z), inputs[spec.INPUTS.index("player")]), potions

    def reachable(self, logits, player):
        """The ending classes with the buckets the HP held rules out closed: a win ends with an HP share in (0, 1], so
        with less lost than the share held and no more healed than it lacks."""
        share = player[:, 0:1]
        open_ = (self.bottoms < share) & (self.bottoms + WIDTH > share - 1 - 1e-6)
        wins = logits[:, 1:]
        return torch.cat([logits[:, :1], torch.where(open_, wins, torch.full_like(wins, self.CLOSED))], 1)

    def encode(
        self, cards, ups, piles, mods, mod_mask, powers, power_amounts, power_mask, player,
        enemies, enemy_scalars, enemy_mask, enemy_powers, enemy_power_amounts, enemy_power_mask,
        intents, intent_values, intent_mask, relics, relic_mask, potions, potion_mask, context, context_mask,
        power_stacks, power_cards, power_card_mask, enemy_power_stacks, enemy_power_cards, enemy_power_card_mask,
        orbs, orb_mask, allies, ally_scalars, ally_mask, ally_powers, ally_power_amounts, ally_power_mask, extra,
        costs, mod_amounts, relic_values, relic_status, damages, orb_values, numbers, power_details, enemy_power_details,
    ):  # fmt: skip
        """The body's vector of each sample."""
        x = (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + costs[..., None] * self.cost
            + damages[..., None] * (self.damage + self.card_damage(cards))
            + numbers[..., 0:1] * self.card_block(cards)
            + numbers[..., 1:2] * self.card_calc(cards)
            + numbers[..., 2:3] * self.card_replay(cards)
            + numbers[..., 3:4] * self.stars
            + numbers[..., 4:5] * self.gathered
            + numbers[..., 5:6] * self.card_grown(cards)
            + ((self.modifiers(mods) + mod_amounts[..., None] * self.mod_amount(mods)) * mod_mask[..., None]).sum(2)
            + piles @ self.pile
        )
        per_pile = torch.bmm(piles.transpose(1, 2), self.card_phi(x)).flatten(1) * self.SUM_SCALE
        i = (
            self.intents(intents)
            + intent_values[..., 0:1] * self.intent_damage(intents)
            + intent_values[..., 1:2] * self.intent_total(intents)
        )
        slots = ((i * intent_mask[..., None]).sum(3) + self.slot).flatten(2)
        e = torch.cat(
            [
                self.enemies(enemies) + self.enemy_scalars(enemy_scalars),
                self.power_sum(
                    enemy_powers,
                    enemy_power_amounts,
                    enemy_power_mask,
                    enemy_power_stacks,
                    enemy_power_cards,
                    enemy_power_card_mask,
                    enemy_power_details,
                ),  # fmt: skip
                slots,
            ],
            2,
        )
        foes = (self.enemy_phi(e) * enemy_mask[..., None]).sum(1)
        mine = self.power_sum(
            powers, power_amounts, power_mask, power_stacks, power_cards, power_card_mask, power_details
        )
        held = (
            self.relics(relics)
            + relic_values[..., 0:1] * self.relic_counter(relics)
            + relic_values[..., 2:3] * self.melted
            + self.relic_status(relic_status)
        )
        parts = [per_pile, player, mine, foes, (held * relic_mask[..., None]).sum(1)]
        for table, ids, mask in (
            (self.potions, potions, potion_mask),
            (self.context, context, context_mask),
        ):
            parts.append((table(ids) * mask[..., None]).sum(1))
        places = torch.arange(orbs.shape[1], device=orbs.device).clamp_max(spec.MAX_ORBS - 1)[None, :]
        orb = (
            self.orbs(orbs)
            + orb_values[..., 0:1] * self.orb_passive(orbs)
            + orb_values[..., 1:2] * self.orb_evoke(orbs)
            + self.orb_at(places * self.orbs.num_embeddings + orbs)
        )
        parts.append((orb * orb_mask[..., None]).sum(1))
        ally = self.ally_phi(
            torch.cat(
                [
                    self.allies(allies) + self.ally_scalars(ally_scalars),
                    self.power_sum(ally_powers, ally_power_amounts, ally_power_mask),
                ],
                2,
            )
        )
        parts += [(ally * ally_mask[..., None]).sum(1), extra]
        return self.body(torch.cat(parts, 1))


class Outcome(nn.Module):
    """The net as ONNX holds it: the chance of each ending, and after a win in each group of buckets each potion id's of
    being gone."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        logits, potions = self.net(*inputs)
        return torch.softmax(logits, 1), torch.sigmoid(potions)


POTION_WEIGHT = 1


def won_group(potions, target):
    """Per sample, the potion logits of the group of buckets its ending falls in (group 0 for a death, which no loss reads)."""
    return potions[torch.arange(potions.shape[0], device=potions.device), potion_group(target.clamp_min(1))]


def loss(out, b: Batch):
    """Per sample, the endings' cross-entropy plus, after a win, POTION_WEIGHT times each held potion's cross-entropy of
    being gone, by the potion head of the win's group of buckets."""
    logits, potions = out
    won = (b.target > 0).float()
    gone = nn.functional.binary_cross_entropy_with_logits(won_group(potions, b.target).gather(1, b.potions), b.potion_used, reduction="none")
    used = (gone * b.potion_mask).sum(1)
    return nn.functional.cross_entropy(logits, b.target, reduction="none") + POTION_WEIGHT * won * used
