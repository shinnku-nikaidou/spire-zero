"""The combat net (docs: combat-brain/network): set encoders over the cards (per pile), the powers, the enemies (each with its
intent slots) and the allies, bags of ids for the rest, then a body and two heads (combat/data.py): the chance of each ending and,
after a win in each group of buckets (data.POTION_GROUPS), each potion's of being gone, so the leaf weighs the HP lost and
the potions drunk together.
"""

import torch
from torch import nn

from combat import encode
from combat.data import BUCKETS, LOSS_MIN, POTION_GROUPS, WIDTH, potion_group


def padded(rows, device):
    """Lists of ids as one padded tensor (at least one wide) and its mask."""
    width = max(1, max(len(r) for r in rows))
    ids, mask = torch.zeros(len(rows), width, dtype=torch.long), torch.zeros(len(rows), width)
    for b, r in enumerate(rows):
        ids[b, : len(r)], mask[b, : len(r)] = torch.tensor(r, dtype=torch.long), 1
    return ids.to(device), mask.to(device)


def power_tensors(rows, index):
    """Powers (encode.powers) as ids, amounts, stacks, kept cards and their masks, and moved numbers, padded to the most
    any row has."""
    n, width = len(rows), max(1, max(len(r) for r in rows))
    ids, amounts, mask = torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width), torch.zeros(n, width)
    stacks, cards, card_mask = torch.zeros(n, width), torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width)
    details = torch.zeros(n, width)
    for b, r in enumerate(rows):
        for k, (pid, amount, st, card, detail) in enumerate(r):
            ids[b, k], amounts[b, k], mask[b, k], stacks[b, k] = index["powers"].get(pid, 0), amount, 1, st
            details[b, k] = detail
            if card:
                cards[b, k], card_mask[b, k] = index["cards"].get(card, 0), 1
    return ids, amounts, mask, stacks, cards, card_mask, details


class Batch:
    """Samples as tensors, in the order of encode.INPUTS, and their targets."""

    def __init__(self, samples, index, device):
        n, toks = len(samples), [s["tokens"] for s in samples]
        width = max(1, max(len(t["cards"]) for t in toks))
        cards, ups = torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width)
        piles = torch.zeros(n, width, len(encode.PILES))
        m_n = max(1, max((len(c[3]) for t in toks for c in t["cards"]), default=0))
        mods, mod_mask = torch.zeros(n, width, m_n, dtype=torch.long), torch.zeros(n, width, m_n)
        costs, mod_amounts, damages = torch.zeros(n, width), torch.zeros(n, width, m_n), torch.zeros(n, width)
        numbers = torch.zeros(n, width, encode.NUMBERS)
        for b, t in enumerate(toks):
            for k, (card_id, up, pile, ms, cost, amounts, damage, nums) in enumerate(t["cards"]):
                cards[b, k], ups[b, k], piles[b, k, pile], costs[b, k] = index["cards"].get(card_id, 0), up, 1, cost
                damages[b, k], numbers[b, k] = damage, torch.tensor(nums)
                for j, m in enumerate(ms):
                    mods[b, k, j], mod_mask[b, k, j] = index["modifiers"].get(m, 0), 1
                mod_amounts[b, k, : len(amounts)] = torch.tensor(amounts)
        p_ids, p_amounts, p_mask, p_stacks, p_cards, p_card_mask, p_details = power_tensors(
            [t["powers"] for t in toks], index
        )

        e_n = max(1, max(len(t["enemies"]) for t in toks))
        q_n = max(1, max((len(e["powers"]) for t in toks for e in t["enemies"]), default=0))
        i_n = max(1, max((len(s) for t in toks for e in t["enemies"] for s in e["slots"]), default=0))
        enemies, enemy_mask, enemy_scalars = (
            torch.zeros(n, e_n, dtype=torch.long),
            torch.zeros(n, e_n),
            torch.zeros(n, e_n, 5),
        )
        e_powers = [torch.zeros(n, e_n, q_n, dtype=torch.long), torch.zeros(n, e_n, q_n), torch.zeros(n, e_n, q_n)]
        e_numbers = [torch.zeros(n, e_n, q_n), torch.zeros(n, e_n, q_n, dtype=torch.long), torch.zeros(n, e_n, q_n)]
        e_details = torch.zeros(n, e_n, q_n)
        intents = torch.zeros(n, e_n, encode.SLOTS, i_n, dtype=torch.long)
        intent_values, intent_mask = torch.zeros(n, e_n, encode.SLOTS, i_n, 2), torch.zeros(n, e_n, encode.SLOTS, i_n)
        for b, t in enumerate(toks):
            for k, e in enumerate(t["enemies"]):
                enemies[b, k], enemy_mask[b, k] = index["enemies"].get(e["id"], 0), 1
                enemy_scalars[b, k] = torch.tensor(e["scalars"])
                for j, (pid, amount, st, card, detail) in enumerate(e["powers"]):
                    e_powers[0][b, k, j], e_powers[1][b, k, j], e_powers[2][b, k, j] = (
                        index["powers"].get(pid, 0),
                        amount,
                        1,
                    )
                    e_numbers[0][b, k, j], e_details[b, k, j] = st, detail
                    if card:
                        e_numbers[1][b, k, j], e_numbers[2][b, k, j] = index["cards"].get(card, 0), 1
                for s, slot in enumerate(e["slots"][: encode.SLOTS]):
                    for j, (kind, damage, total) in enumerate(slot):
                        intents[b, k, s, j], intent_mask[b, k, s, j] = index["intents"].get(kind, 0), 1
                        intent_values[b, k, s, j] = torch.tensor([damage, total])

        bags = [
            x
            for table in encode.BAGS
            for x in padded([[index[table].get(i, 0) for i in t[table]] for t in toks], "cpu")
        ]
        orbs, orb_mask = padded([[index["orbs"].get(o, 0) for o in t["orbs"]] for t in toks], "cpu")
        orb_values = torch.zeros(n, orbs.shape[1], 2)
        for b, t in enumerate(toks):
            if t["orb_values"]:
                orb_values[b, : len(t["orb_values"])] = torch.tensor(t["orb_values"])
        a_n = max(1, max(len(t["allies"]) for t in toks))
        aq_n = max(1, max((len(a["powers"]) for t in toks for a in t["allies"]), default=0))
        allies, ally_scalars, ally_mask = (
            torch.zeros(n, a_n, dtype=torch.long),
            torch.zeros(n, a_n, 4),
            torch.zeros(n, a_n),
        )
        a_powers = [torch.zeros(n, a_n, aq_n, dtype=torch.long), torch.zeros(n, a_n, aq_n), torch.zeros(n, a_n, aq_n)]
        for b, t in enumerate(toks):
            for k, a in enumerate(t["allies"]):
                allies[b, k], ally_scalars[b, k], ally_mask[b, k] = (
                    index["allies"].get(a["id"], 0),
                    torch.tensor(a["scalars"]),
                    1,
                )
                for j, (pid, amount, _, _, _) in enumerate(a["powers"]):
                    a_powers[0][b, k, j], a_powers[1][b, k, j], a_powers[2][b, k, j] = (
                        index["powers"].get(pid, 0),
                        amount,
                        1,
                    )

        r_n = max(1, max(len(t["relics"]) for t in toks))
        relic_values, relic_status = torch.zeros(n, r_n, 3), torch.zeros(n, r_n, dtype=torch.long)
        for b, t in enumerate(toks):
            for k, (values, status) in enumerate(zip(t["relic_values"], t["relic_status"], strict=True)):
                relic_values[b, k], relic_status[b, k] = torch.tensor(values), index["relic_status"].get(status, 0)

        tensors = [
            cards, ups, piles, mods, mod_mask, p_ids, p_amounts, p_mask, torch.tensor([t["player"] for t in toks]),
            enemies, enemy_scalars, enemy_mask, *e_powers, intents, intent_values, intent_mask, *bags,
            p_stacks, p_cards, p_card_mask, *e_numbers, orbs, orb_mask, allies, ally_scalars, ally_mask, *a_powers,
            torch.tensor([t["extra"] for t in toks]), costs, mod_amounts, relic_values, relic_status, damages, orb_values,
            numbers, p_details, e_details,
        ]  # fmt: skip
        self.tensors = [x.to(device) for x in tensors]
        self.target = torch.tensor([s["class"] for s in samples], dtype=torch.long, device=device)
        self.potions = self.tensors[encode.INPUTS.index("potions")]
        self.potion_mask = self.tensors[encode.INPUTS.index("potion_mask")]
        self.potion_used = torch.zeros_like(self.potion_mask)
        for b, s in enumerate(samples):
            self.potion_used[b, : len(s["potion_used"])] = torch.tensor(s["potion_used"], dtype=torch.float, device=device)

    def inputs(self):
        """The net's inputs, in the order of encode.INPUTS."""
        return tuple(self.tensors)


def mlp(*sizes):
    layers = []
    for a, b in zip(sizes, sizes[1:], strict=False):
        layers += [nn.Linear(a, b), nn.ReLU()]
    return nn.Sequential(*layers)


class Net(nn.Module):
    DIM, HIDDEN, BODY = 64, 128, 512
    SUM_SCALE = 1 / 8  # each pile's sum, scaled to about a mean's size
    N_PLAYER, N_EXTRA = 8, 4
    CLOSED = -1e4  # the logit of an ending the state rules out: no weight in float32's softmax

    def __init__(self, sizes: dict[str, int]):
        super().__init__()
        d, h = self.DIM, self.HIDDEN
        for table in encode.TABLES:
            if table != "powers":
                setattr(self, table, nn.Embedding(sizes[table], d))
        self.card_up = nn.Embedding(sizes["cards"], d)
        self.card_damage = nn.Embedding(sizes["cards"], d)
        self.card_block = nn.Embedding(sizes["cards"], d)
        self.card_calc = nn.Embedding(sizes["cards"], d)
        self.card_replay = nn.Embedding(sizes["cards"], d)
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
        self.orb_at = nn.Embedding(encode.MAX_ORBS * sizes["orbs"], d)  # an orb's kind at its place in the queue
        for m in self.modules():
            if isinstance(m, nn.Embedding):
                nn.init.normal_(m.weight, std=0.1)
        self.pile = nn.Parameter(torch.randn(len(encode.PILES), d) * 0.1)
        self.slot = nn.Parameter(torch.randn(encode.SLOTS, d) * 0.1)
        self.cost = nn.Parameter(torch.randn(d) * 0.1)
        self.damage = nn.Parameter(torch.randn(d) * 0.1)
        self.stars = nn.Parameter(torch.randn(d) * 0.1)
        self.gathered = nn.Parameter(torch.randn(d) * 0.1)
        self.melted = nn.Parameter(torch.randn(d) * 0.1)
        self.card_phi = mlp(d, h, h)
        self.power_phi = mlp(d, h)
        self.enemy_scalars = nn.Linear(5, d)
        self.enemy_phi = mlp(d + h + encode.SLOTS * d, 2 * h, 2 * h)
        self.ally_scalars = nn.Linear(4, d)
        self.ally_phi = mlp(d + h, h)
        width = len(encode.PILES) * h + self.N_PLAYER + h + 2 * h + 4 * d + h + self.N_EXTRA
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
        being gone, as (sample, group, potion) (inputs as encode.INPUTS)."""
        z = self.encode(*inputs)
        potions = self.potion_head(z).view(z.shape[0], POTION_GROUPS, -1)
        return self.reachable(self.head(z), inputs[encode.INPUTS.index("player")]), potions

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
        places = torch.arange(orbs.shape[1], device=orbs.device).clamp_max(encode.MAX_ORBS - 1)[None, :]
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
