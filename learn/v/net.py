"""Pure V's net (docs: learning §1): a set encoder over the deck, bags of ids for the rest, the raw state, then a body
and two heads. The main head gives, per floor, the chance to die on it if still alive there; V's value is the floor
expected to be reached, or (objective "win") the chance to survive every floor. The second head learns the next
combats' HP costs and the boss score, which shapes the body only: it is not in the net file. The boss gauntlet's V
(`shares`) has a third head: per floor, the share of the enemies' HP taken in the combat the run ends in there; its
value is the progress expected (`expected_progress`).

EncoderNet (plan v7 §3) has the same body and heads, its inputs encode.ENCODER_INPUTS: the build encoder (the cards,
relics and potions as one set through masked self-attention) and the route encoder (the map's points as a graph,
valued back from the boss) instead of the deck's set encoder and the relic, potion and map bags.
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


def route_inputs(samples, index, device) -> dict:
    """The route's tensors (encode.ROUTE): per sample, its nodes' type ids, floors ahead and quest marks, and each
    node's next nodes' indices; nodes and next lists padded to at least one, with their masks."""
    n = len(samples)
    width = max(1, max(len(s["tokens"]["route"]) for s in samples))
    fan = max(1, max((len(nx) for s in samples for *_, nx in s["tokens"]["route"]), default=1))
    ids, ahead = torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width, dtype=torch.long)
    quest, mask = torch.zeros(n, width), torch.zeros(n, width)
    nxt, next_mask = torch.zeros(n, width, fan, dtype=torch.long), torch.zeros(n, width, fan)
    for b, s in enumerate(samples):
        for k, (kind, floors, marked, to) in enumerate(s["tokens"]["route"]):
            ids[b, k], ahead[b, k], quest[b, k], mask[b, k] = index["route"].get(kind, 0), floors, float(marked), 1
            for j, t in enumerate(to):
                nxt[b, k, j], next_mask[b, k, j] = t, 1
    tensors = (ids, ahead, quest, mask, nxt, next_mask)
    return {name: x.to(device) for name, x in zip(encode.ROUTE, tensors, strict=True)}


class Batch:
    """Samples as tensors: the inputs a net reads (names: encode.INPUTS, or encode.ENCODER_INPUTS), the hazards'
    targets and the second head's, and the weights."""

    def __init__(self, samples, index, mean, std, floors, device, names=encode.INPUTS):
        self.names = names
        n, width = len(samples), max(1, max(len(s["tokens"]["cards"]) for s in samples))
        cards, ups, mask = torch.zeros(n, width, dtype=torch.long), torch.zeros(n, width), torch.zeros(n, width)
        mods, mod_mask = torch.zeros(n, width, 2, dtype=torch.long), torch.zeros(n, width, 2)
        for b, s in enumerate(samples):
            for k, (card_id, up, ms) in enumerate(s["tokens"]["cards"]):
                cards[b, k], ups[b, k], mask[b, k] = index["cards"].get(card_id, 0), up, 1
                for j, m in enumerate(ms[:2]):
                    mods[b, k, j], mod_mask[b, k, j] = index["modifiers"].get(m, 0), 1
        feed = {"cards": cards, "ups": ups, "card_mask": mask, "mods": mods, "mod_mask": mod_mask}
        feed = {name: x.to(device) for name, x in feed.items()}
        for t, bag_mask in encode.BAGS.items():
            if t in names:
                feed[t], feed[bag_mask] = padded(
                    [[index[t].get(i, 0) for i in s["tokens"][t]] for s in samples], device
                )
        if "route" in names:
            feed |= route_inputs(samples, index, device)
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
        # The gauntlet's: the share of the enemies' HP taken in the combat the run ended in (0 for other samples).
        self.share = torch.tensor([s.get("share", 0.0) for s in samples]).to(device)
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
        """The net's inputs, in the order of its names."""
        return tuple(self.feed[name] for name in self.names)


class Heads(nn.Module):
    """The heads over a net's body (`encoded`): the hazards, the second head, and the gauntlet's shares."""

    def forward(self, *inputs):
        """Per sample and floor, the logit of dying on that floor if alive there (inputs as INPUTS names them)."""
        return self.head(self.encoded(*inputs))

    def both(self, *inputs):
        """The hazards' logits and the second head's logits."""
        x = self.encoded(*inputs)
        return self.head(x), self.aux_head(x)

    def heads(self, *inputs):
        """The hazards' logits, the second head's, and the share head's (None without it)."""
        x = self.encoded(*inputs)
        return self.head(x), self.aux_head(x), None if self.share_head is None else self.share_head(x)

    def make_heads(self, width: int, floors: int, shares: bool) -> None:
        self.body = nn.Sequential(nn.Linear(width, self.BODY), nn.ReLU(), nn.Linear(self.BODY, self.BODY), nn.ReLU())
        self.head = nn.Linear(self.BODY, floors)
        self.aux_head = nn.Linear(self.BODY, N_AUX)
        self.share_head = nn.Linear(self.BODY, floors) if shares else None


class Net(Heads):
    INPUTS = encode.INPUTS
    DIM, HIDDEN, BODY = 32, 128, 256
    SUM_SCALE = 1 / 16  # the deck's sum next to its mean, scaled to about a mean's size

    def __init__(self, sizes: dict[str, int], floors: int, shares: bool = False):
        super().__init__()
        d, h = self.DIM, self.HIDDEN
        self.cards = nn.Embedding(sizes["cards"], d)
        self.card_up = nn.Embedding(sizes["cards"], d)
        self.modifiers = nn.Embedding(sizes["modifiers"], d)
        self.bags = nn.ModuleDict({t: nn.Embedding(sizes[t], d) for t in encode.BAGS})
        for e in (self.cards, self.card_up, self.modifiers, *self.bags.values()):
            nn.init.normal_(e.weight, std=0.1)
        self.phi = nn.Sequential(nn.Linear(d, h), nn.ReLU(), nn.Linear(h, h), nn.ReLU())
        self.make_heads(2 * h + len(encode.BAGS) * d + len(encode.STATE), floors, shares)

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


class Attention(nn.Module):
    """A masked self-attention block over a set (pre-norm): attention among the set's present elements, then a
    feed-forward layer, each added back. Its layer norms have no scale and shift of their own (the next Linear has
    them): the ONNX export merges weights of equal values, which would leave their names out of the file."""

    def __init__(self, d: int, heads: int, ff: int):
        super().__init__()
        self.d, self.heads, self.scale = d, heads, 1 / math.sqrt(d // heads)
        self.norm1, self.norm2 = nn.LayerNorm(d, elementwise_affine=False), nn.LayerNorm(d, elementwise_affine=False)
        self.qkv, self.out = nn.Linear(d, 3 * d), nn.Linear(d, d)
        self.ff = nn.Sequential(nn.Linear(d, ff), nn.ReLU(), nn.Linear(ff, d))

    def forward(self, x, mask):
        b, n = x.shape[0], x.shape[1]
        qkv = self.qkv(self.norm1(x)).reshape(b, n, 3, self.heads, self.d // self.heads).permute(2, 0, 3, 1, 4)
        q, k, v = qkv.unbind(0)
        # A padded element is never attended to (a large negative, not -inf: a set of padding alone stays finite).
        scores = q @ k.transpose(-1, -2) * self.scale + (mask[:, None, None, :] - 1) * 1e9
        y = (scores.softmax(-1) @ v).transpose(1, 2).reshape(b, n, self.d)
        x = x + self.out(y)
        return x + self.ff(self.norm2(x))


class EncoderNet(Heads):
    """V with the build and route encoders (plan v7 §3). The build: each card (its id, upgrade level and modifiers, as
    Net reads it), relic and potion is one element of a set, with its kind's vector added; LAYERS masked self-attention
    blocks let them act on each other; then their mean and sum, as Net's deck. The route: each node (encode.route: the
    player's point, then the reachable ones) starts from its type, floors ahead and quest mark; for ROUNDS rounds,
    as many as an act's rows, each node is made anew from that start and the max and mean of the nodes it leads to, so
    the boss's reaches the player's point; then the player's node and the mean of all. The context bag and the state
    are Net's."""

    INPUTS = encode.ENCODER_INPUTS
    DIM, HEADS, LAYERS, FF = 64, 4, 2, 128  # the build encoder
    ROUTE, ROUNDS = 32, encode.MAX_AHEAD  # the route encoder's width and rounds
    BODY = Net.BODY
    SUM_SCALE = Net.SUM_SCALE

    def __init__(self, sizes: dict[str, int], floors: int, shares: bool = False):
        super().__init__()
        d, r = self.DIM, self.ROUTE
        self.cards, self.card_up = nn.Embedding(sizes["cards"], d), nn.Embedding(sizes["cards"], d)
        self.modifiers = nn.Embedding(sizes["modifiers"], d)
        self.relics, self.potions = nn.Embedding(sizes["relics"], d), nn.Embedding(sizes["potions"], d)
        self.context = nn.Embedding(sizes["context"], d)
        self.point, self.ahead = nn.Embedding(sizes["route"], r), nn.Embedding(encode.MAX_AHEAD + 1, r)
        for e in (
            self.cards,
            self.card_up,
            self.modifiers,
            self.relics,
            self.potions,
            self.context,
            self.point,
            self.ahead,
        ):
            nn.init.normal_(e.weight, std=0.1)
        # each kind's vector, and the quest mark's (random, not zeros: the export merges weights of equal values)
        self.card_kind, self.relic_kind, self.potion_kind = (nn.Parameter(0.1 * torch.randn(d)) for _ in range(3))
        self.quest = nn.Parameter(0.1 * torch.randn(r))
        self.layers = nn.ModuleList(Attention(d, self.HEADS, self.FF) for _ in range(self.LAYERS))
        self.norm = nn.LayerNorm(d, elementwise_affine=False)
        self.update = nn.Sequential(nn.Linear(3 * r, r), nn.ReLU())
        self.make_heads(2 * d + d + 2 * r + len(encode.STATE), floors, shares)

    def built(self, cards, ups, card_mask, mods, mod_mask, relics, relic_mask, potions, potion_mask):
        """The build encoder: the mean and the scaled sum of the set's elements after attention."""
        c = (
            self.cards(cards)
            + ups[..., None] * self.card_up(cards)
            + (self.modifiers(mods) * mod_mask[..., None]).sum(2)
            + self.card_kind
        )
        x = torch.cat([c, self.relics(relics) + self.relic_kind, self.potions(potions) + self.potion_kind], 1)
        mask = torch.cat([card_mask, relic_mask, potion_mask], 1)
        for layer in self.layers:
            x = layer(x, mask)
        x = self.norm(x) * mask[..., None]
        total = x.sum(1)
        return torch.cat([total / mask.sum(1, keepdim=True).clamp_min(1), total * self.SUM_SCALE], 1)

    def routed(self, route, ahead, quest, mask, nxt, next_mask):
        """The route encoder: the player's node and the mean of all nodes, after ROUNDS rounds back from the boss."""
        b, n, fan = nxt.shape
        start = (self.point(route) + self.ahead(ahead) + quest[..., None] * self.quest) * mask[..., None]
        present = next_mask[..., None]
        count = next_mask.sum(2, keepdim=True)
        leads = (count > 0).to(start.dtype)
        h = start
        for _ in range(self.ROUNDS):
            nexts = torch.gather(h, 1, nxt.reshape(b, n * fan, 1).expand(-1, -1, h.shape[2])).reshape(b, n, fan, -1)
            top = (nexts * present + (present - 1) * 1e9).amax(2) * leads  # 0 for a node that leads nowhere
            mean = (nexts * present).sum(2) / count.clamp_min(1)
            h = self.update(torch.cat([start, top, mean], 2)) * mask[..., None]
        return torch.cat([h[:, 0], h.sum(1) / mask.sum(1, keepdim=True).clamp_min(1)], 1)

    def encoded(self, cards, ups, card_mask, mods, mod_mask, relics, relic_mask, potions, potion_mask, *rest):
        """The body's output, which the heads read."""
        context, context_mask, *route, state = rest
        parts = [
            self.built(cards, ups, card_mask, mods, mod_mask, relics, relic_mask, potions, potion_mask),
            (self.context(context) * context_mask[..., None]).sum(1),
            self.routed(*route),
            state,
        ]
        return self.body(torch.cat(parts, 1))


class Hazards(nn.Module):
    """The net as ONNX holds it: the hazards themselves, then for a net with a share head its shares (RunNet.Value)."""

    def __init__(self, net):
        super().__init__()
        self.net = net

    def forward(self, *inputs):
        hazards, _, shares = self.net.heads(*inputs)
        return torch.sigmoid(hazards) if shares is None else torch.sigmoid(torch.cat([hazards, shares], 1))


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


def expected_progress(hazards: torch.Tensor, shares: torch.Tensor, start: torch.Tensor):
    """As RunNet.Value for "progress": the floor expected, plus the chance to end on each floor from the start on times
    the share of the enemies' HP taken there."""
    h, s = hazards.double(), shares.double()
    g = torch.arange(h.shape[1], device=h.device)
    from_start = g >= start[:, None]
    survive = torch.where(from_start, 1 - h, torch.ones_like(h)).cumprod(1)
    before = torch.cat([torch.ones_like(survive[:, :1]), survive[:, :-1]], 1)
    return expected_floor(hazards, start) + (before * h * s * from_start).sum(1)


def values(model, b: Batch, objective: str = "floor"):
    """V's value of a batch, as RunNet.Value gives it: the floor expected, the chance to win (objective "win"), or with
    a share head the progress expected."""
    hazards, _, shares = model.heads(*b.inputs())
    if shares is not None:
        return expected_progress(torch.sigmoid(hazards), torch.sigmoid(shares), b.start)
    return (win_chance if objective == "win" else expected_floor)(torch.sigmoid(hazards), b.start)


def share_loss(logits, b: Batch):
    """Per sample, the cross-entropy of the share head at the floor the run ended on (none for a censored run)."""
    per_floor = nn.functional.binary_cross_entropy_with_logits(
        logits, b.share[:, None].expand_as(logits), reduction="none"
    )
    return (per_floor * b.target * b.label_mask).sum(1)


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
    """Per group, the chance a softmax at the temperature (one per option) draws an option more than temperature / 100
    below the group's best: what self-play counts as explored (Softmax.Explored)."""
    best = torch.full((groups,), -math.inf, dtype=values.dtype, device=values.device)
    best = best.scatter_reduce(0, group, values, reduce="amax")
    p = segment_log_softmax(values / temperature, group, groups).exp()
    below = best[group] - values > temperature / 100
    return torch.zeros(groups, dtype=p.dtype, device=p.device).index_add(0, group, p * below)


def centred(values: torch.Tensor, group: torch.Tensor, groups: int) -> torch.Tensor:
    """Values less their group's mean."""
    total = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, values)
    count = torch.zeros(groups, dtype=values.dtype, device=values.device).index_add(0, group, torch.ones_like(values))
    return values - (total / count)[group]


def pearson(x: torch.Tensor, y: torch.Tensor) -> float:
    x, y = x - x.mean(), y - y.mean()
    return float((x * y).sum() / ((x * x).sum() * (y * y).sum()).sqrt().clamp_min(1e-12))
