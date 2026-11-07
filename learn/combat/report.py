"""What a trained combat net's report says (docs: learning/combat-net §4), held-out or on the gate's seeds: the error against
the training's prior and the rank within each fight, on the whole and by act, room, encounter, HP and potion. Beside a
net's ranks stand those of the HP share held now.
"""

import math
from collections import Counter, defaultdict

import torch

from combat import data
from combat.net import won_group


def ranks(x: torch.Tensor) -> torch.Tensor:
    """Average ranks, ties shared."""
    _, inverse, counts = torch.unique(x, return_inverse=True, return_counts=True)
    ends = counts.cumsum(0).double()
    return (ends - (counts.double() - 1) / 2)[inverse]


def rank_within(samples, predicted: torch.Tensor, key: str) -> float:
    """The rank correlation of the prediction with what the fight came to (a sample's `key`) within each (encounter,
    round) of 10 or more, by their sizes: what a beam ranks is the states of one fight."""
    groups = defaultdict(list)
    for k, s in enumerate(samples):
        groups[(s["encounter"], s["round"])].append(k)
    total, weight = 0.0, 0
    target = torch.tensor([s[key] for s in samples]).double()
    for ix in groups.values():
        a, b = predicted[ix], target[ix]
        if len(ix) < 10 or a.std() == 0 or b.std() == 0:
            continue
        total += len(ix) * float(torch.corrcoef(torch.stack([ranks(a), ranks(b)]))[0, 1])
        weight += len(ix)
    return total / max(1, weight)


def auc_within(samples, score: torch.Tensor) -> float:
    """The chance that a death's round start scores above a survivor's, within each (encounter, round) with 3 or more of
    each, by their deaths."""
    groups = defaultdict(list)
    for k, s in enumerate(samples):
        groups[(s["encounter"], s["round"])].append(k)
    died = torch.tensor([s["class"] == 0 for s in samples])
    total, weight = 0.0, 0
    for ix in groups.values():
        d = died[ix]
        deaths, others = int(d.sum()), int((~d).sum())
        if deaths >= 3 and others >= 3:
            total += (float(ranks(score[ix])[d].sum()) - deaths * (deaths + 1) / 2) / others
            weight += deaths
    return total / max(1, weight)


def potion_rates(samples):
    """What a held potion's chance of being gone would be by its id, and by its id and room, from these samples' wins."""
    used, held = Counter(), Counter()
    for s in samples:
        if s["class"] > 0:
            for p, u in zip(s["tokens"]["potions"], s["potion_used"], strict=True):
                for key in (None, p, (p, s["room"])):
                    used[key] += u
                    held[key] += 1

    def rate(key):
        k = key if held[key] else None
        return used[k] / max(1, held[k])

    return rate


def log_loss(chance: float, used: int) -> float:
    c = min(max(chance, 1e-6), 1 - 1e-6)
    return -math.log(c if used else 1 - c)


def predict(model, part, part_batches):
    """Each sample's prediction: the cross-entropy, the chance of death, the HP share expected lost after a win, and its
    held potions' chances of being gone, by the potion head of the group of buckets the sample's win ended in."""
    rows = []
    with torch.no_grad():
        for b in part_batches:
            logits, potions = model(*b.inputs())
            rows.append((logits, won_group(potions, b.target).gather(1, b.potions).sigmoid()))
    logits = torch.cat([r[0] for r in rows]).double().cpu()
    wide = [c for r in rows for c in r[1].cpu().tolist()]
    chances = [c[: len(s["potion_used"])] for c, s in zip(wide, part, strict=True)]
    target = torch.tensor([s["class"] for s in part])
    probs = torch.softmax(logits, 1)
    mids = data.LOSS_MIN + (torch.arange(1, data.BUCKETS + 1).double() - 0.5) * data.WIDTH
    expected = (probs[:, 1:] * mids).sum(1) / (1 - probs[:, 0]).clamp_min(1e-9)
    ces = torch.nn.functional.cross_entropy(logits, target, reduction="none")
    return ces, probs[:, 0], expected, chances


def summary(model, part, part_batches, train) -> dict:
    """The net on `part`, against the prior and potion rates of the samples it trained on."""
    counts = torch.bincount(torch.tensor([s["class"] for s in train]), minlength=1 + data.BUCKETS).double() + 0.5
    rate = potion_rates(train)

    def potion_report(rows):
        """The chances of (id, room, chance, used) against the rates of the id and of the id and room."""
        n = max(1, len(rows))
        return {
            "rows": len(rows),
            "used": sum(r[3] for r in rows) / n,
            "predicted": sum(r[2] for r in rows) / n,
            "log_loss": sum(log_loss(r[2], r[3]) for r in rows) / n,
            "log_loss_id": sum(log_loss(rate(r[0]), r[3]) for r in rows) / n,
            "log_loss_id_room": sum(log_loss(rate((r[0], r[1])), r[3]) for r in rows) / n,
        }

    ces, deaths, expected, chances = predict(model, part, part_batches)
    prior = -torch.log(counts / counts.sum())[torch.tensor([s["class"] for s in part])]
    died = torch.tensor([s["class"] == 0 for s in part], dtype=torch.float64)
    shares = torch.tensor([s["share"] for s in part]).double()
    rows_of = [
        [(p, s["room"], c, u) for p, c, u in zip(s["tokens"]["potions"], chances[k], s["potion_used"], strict=True)]
        if s["class"] > 0
        else []
        for k, s in enumerate(part)
    ]

    def of(ix):
        some, wins = [part[k] for k in ix], [k for k in ix if part[k]["class"] > 0]
        won = [part[k] for k in wins]
        return {
            "samples": len(ix),
            "ce": float(ces[ix].mean()),
            "ce_prior": float(prior[ix].mean()),
            "death": float(died[ix].mean()),
            "predicted_death": float(deaths[ix].mean()),
            "rank_lost": rank_within(won, expected[wins], "lost"),
            "rank_lost_share": rank_within(won, shares[wins], "lost"),
            "auc_death": auc_within(some, deaths[ix]),
            "auc_death_share": auc_within(some, -shares[ix]),
            "potions": potion_report([r for k in ix for r in rows_of[k]]),
        }

    def by(key):
        groups = defaultdict(list)
        for k, s in enumerate(part):
            groups[str(key(s))].append(k)
        return {g: of(ix) for g, ix in sorted(groups.items())}

    ids = defaultdict(list)
    for rows in rows_of:
        for r in rows:
            ids[r[0]].append(r)
    return {
        "held_out": of(list(range(len(part)))),
        "by_act": by(lambda s: s["act"]),
        "by_room": by(lambda s: s["room"]),
        "by_encounter": by(lambda s: s["encounter"]),
        "by_hp": by(lambda s: "below 30 %" if s["share"] < 0.3 else "the rest"),
        "by_potion": {p: potion_report(rows) for p, rows in sorted(ids.items())},
    }
