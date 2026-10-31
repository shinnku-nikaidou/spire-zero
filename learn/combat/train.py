"""Train the combat net (docs: learning/combat-net §4), the combat search's leaf, from one environment's games, and write its
net file for `--combat-net`.

The samples are combat/data.py's: each round's start in the logs of --env. With --value the net's target is the fight's
worth by that V, the loss its squared error; without, the endings, the loss their cross-entropy plus POTION_WEIGHT times
each held potion's cross-entropy of being gone after a win. The net's sizes and the training's settings are fixed below:
an experiment changes them here, and only a result keeps the change. --test-list's games on the seeds of --skip-seeds,
never trained on, are only reported, as `medium`.

    uv run python -m combat.train --list data/silent/lists/c7 --env SL --name c7-2 \\
        --skip-seeds ../eval/seeds/promotion-1-300.txt --test-list data/silent/lists/medium
"""

import json
import math
import random
from collections import Counter, defaultdict
import warnings
from typing import Annotated

import typer
from loguru import logger

warnings.filterwarnings("ignore", message="Failed to initialize NumPy")
import torch  # noqa: E402

import common  # noqa: E402
from combat import data, encode, export  # noqa: E402
from combat.net import Batch, Net, loss  # noqa: E402
from common import LearnError  # noqa: E402
from v import data as v_data  # noqa: E402
from v import export as v_export  # noqa: E402
from v.net import Batch as VBatch  # noqa: E402
from v.net import EncoderNet, Net as VNet, win_chance  # noqa: E402
from weights import out_path  # noqa: E402

script = common.app()

MIN_COUNT = 3  # ids seen fewer times in the training runs read as unknown
EPOCHS, PATIENCE = 20, 3  # at most, and without a better held-out loss before stopping
BATCH = 256
LR, WEIGHT_DECAY = 1e-3, 1e-4
FLOORS = 64  # V's floors


def vocab(samples) -> dict[str, list[str]]:
    """Per table, the ids seen at least MIN_COUNT times in these samples; entry 0 reads every other id."""
    counts = {t: {} for t in encode.TABLES}

    def count(t, i):
        # the encounter says the room, the act and, per character's net, the character: they read as unknown
        if t != "context" or i.startswith("encounter:"):
            counts[t][i] = counts[t].get(i, 0) + 1

    def count_powers(ps):
        for pid, _, _, card in ps:
            count("powers", pid)
            if card:
                count("cards", card)

    for s in samples:
        tok = s["tokens"]
        for card_id, _, _, mods, _, _ in tok["cards"]:
            count("cards", card_id)
            for m in mods:
                count("modifiers", m)
        count_powers(tok["powers"])
        for e in tok["enemies"]:
            count("enemies", e["id"])
            count_powers(e["powers"])
            for slot in e["slots"]:
                for kind, _, _ in slot:
                    count("intents", kind)
        for t in (*encode.BAGS, "orbs", "relic_status"):
            for i in tok[t]:
                count(t, i)
        for a in tok["allies"]:
            count("allies", a["id"])
            count_powers(a["powers"])
    return {t: ["<unk>"] + sorted(i for i, n in c.items() if n >= MIN_COUNT) for t, c in counts.items()}


def run_value(path: str, device: str):
    """V of a list of run views, as RunNet.Value gives it for a net of objective win."""
    file = v_export.read(path)
    kind = EncoderNet if v_export.encoders(file) else VNet
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in file["vocab"].items()}
    mean = torch.tensor(file["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(file["state"]["std"], dtype=torch.float64)
    model = kind({t: len(ids) for t, ids in file["vocab"].items()}, FLOORS)
    model.load_state_dict(v_export.weights(file, model))
    model = model.to(device).eval()

    def value(views):
        out = []
        with torch.no_grad():
            for i in range(0, len(views), 512):
                part = [v_data.sample("", view, FLOORS) for view in views[i : i + 512]]
                b = VBatch(part, index, mean, std, FLOORS, device, kind.INPUTS)
                out += win_chance(torch.sigmoid(model(*b.inputs())), b.start).tolist()
        return out

    return value


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


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    env: Annotated[str, typer.Option(help="the environment whose games these are, SL or NoSL")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    value: Annotated[str | None, typer.Option(help="the net file of the V that prices the fights' ends: target worth")] = None,
    skip_seeds: Annotated[str | None, typer.Option(help="a seed file whose runs are not read: the gate's")] = None,
    test_list: Annotated[str | None, typer.Option(help="a list pair whose games on --skip-seeds' seeds are only reported, as `medium`")] = None,
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/CHARACTER/NAME.json)")] = None,
    character: Annotated[str, typer.Option(help="the character whose runs these are")] = "SILENT",
    seed: int = 0,
) -> None:
    if env not in ("SL", "NoSL"):
        raise LearnError(f"--env is SL or NoSL, not {env}")
    out_file = out_path(name, out, character)
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    skip = set()
    if skip_seeds:
        with open(skip_seeds) as f:
            skip = {x.strip() for x in f if x.strip() and not x.startswith("#")}
    run_v = run_value(value, device) if value else None
    samples, skipped = data.read(*common.lists(list_), character, env, run_v, skip)
    train = [s for s in samples if not data.held_out(s["run"])]
    test = [s for s in samples if data.held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")
    logger.info(
        "{} round starts from {} runs; {} runs skipped, their combats not matching the record",
        len(samples), len({s["key"] for s in samples}), skipped,
    )  # fmt: skip

    tables = vocab(train)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}

    def batches(part, shuffle):
        order = random.sample(range(len(part)), len(part)) if shuffle else list(range(len(part)))
        for i in range(0, len(order), BATCH):
            yield Batch([part[k] for k in order[i : i + BATCH]], index, device)

    test_batches = list(batches(test, False))

    def held_out_loss(model):
        with torch.no_grad():
            return sum(float(loss(model(*b.inputs()), b).sum()) for b in test_batches) / len(test)

    def predict(model, part, part_batches):
        """Each sample's prediction: its worth; or the leaf's value, the cross-entropy, the chance of death, the HP share
        expected lost after a win, and its held potions' chances of being gone."""
        rows = []
        with torch.no_grad():
            for b in part_batches:
                out = model(*b.inputs())
                rows.append(out if model.is_worth else (out[0], out[1].gather(1, b.potions).sigmoid()))
        if model.is_worth:
            return torch.cat(rows).double().cpu()
        logits = torch.cat([r[0] for r in rows]).double().cpu()
        wide = [c for r in rows for c in r[1].cpu().tolist()]
        chances = [c[: len(s["potion_used"])] for c, s in zip(wide, part, strict=True)]
        target = torch.tensor([s["class"] for s in part])
        probs = torch.softmax(logits, 1)
        mids = data.LOSS_MIN + (torch.arange(1, data.BUCKETS + 1).double() - 0.5) * data.WIDTH
        hp = torch.tensor([s["share"] for s in part]).double()[:, None]
        kept = torch.tensor([sum(1 - x for x in c) for c in chances]).double()
        value = (probs[:, 1:] * (0.2 + 0.6 * (hp - mids))).sum(1) + (1 - probs[:, 0]) * 0.02 * kept
        expected = (probs[:, 1:] * mids).sum(1) / (1 - probs[:, 0]).clamp_min(1e-9)
        ces = torch.nn.functional.cross_entropy(logits, target, reduction="none")
        return value, ces, probs[:, 0], expected, chances

    model = Net({t: len(ids) for t, ids in tables.items()}, worth=value is not None).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=LR, weight_decay=WEIGHT_DECAY)
    best, best_state, stale = math.inf, None, 0
    for epoch in range(EPOCHS):
        model.train()
        for b in batches(train, True):
            step = loss(model(*b.inputs()), b).mean()
            opt.zero_grad()
            step.backward()
            opt.step()
        model.eval()
        held = held_out_loss(model)
        logger.info("epoch {}: held-out loss {:.4f}", epoch, held)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= PATIENCE:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: the error against the training's prior and the rank within each fight, on the whole and by act, room and
    # encounter. Beside a net's ranks stand those of the HP share held now: the end's price follows it (0.82 on v7g1).
    mean = sum(s["worth"] for s in train) / len(train) if value else None
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

    def summary(part, part_batches):
        found = predict(model, part, part_batches)
        if value:
            target = torch.tensor([s["worth"] for s in part]).double()

            def of(ix):
                return {
                    "samples": len(ix),
                    "mse": float(((found[ix] - target[ix]) ** 2).mean()),
                    "mse_mean": float(((mean - target[ix]) ** 2).mean()),
                    "rank_within": rank_within([part[k] for k in ix], found[ix], "worth"),
                    "worth": float(target[ix].mean()),
                    "predicted": float(found[ix].mean()),
                }
        else:
            valued, ces, deaths, expected, chances = found
            prior = -torch.log(counts / counts.sum())[torch.tensor([s["class"] for s in part])]
            died = torch.tensor([s["class"] == 0 for s in part], dtype=torch.float64)
            shares = torch.tensor([s["share"] for s in part]).double()
            rows_of = [
                [
                    (p, s["room"], c, u)
                    for p, c, u in zip(s["tokens"]["potions"], chances[k], s["potion_used"], strict=True)
                ]
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
                    "rank_within": rank_within(some, valued[ix], "price"),
                    "rank_within_share": rank_within(some, shares[ix], "price"),
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

        out = {
            "held_out": of(list(range(len(part)))),
            "by_act": by(lambda s: s["act"]),
            "by_room": by(lambda s: s["room"]),
            "by_encounter": by(lambda s: s["encounter"]),
        }
        if not value:
            out["by_hp"] = by(lambda s: "below 30 %" if s["share"] < 0.3 else "the rest")
            ids = defaultdict(list)
            for rows in rows_of:
                for r in rows:
                    ids[r[0]].append(r)
            out["by_potion"] = {p: potion_report(rows) for p, rows in sorted(ids.items())}
        return out

    report = {
        "env": env,
        "value": value,
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "held_out_samples": len(test),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        **summary(test, test_batches),
    }
    if test_list:
        medium, _ = data.read(*common.lists(test_list), character, env, run_v, only_seeds=skip)
        report["medium"] = summary(medium, list(batches(medium, False)))
    print(json.dumps(report))

    kept = [s for s in test if "obs" in s]
    export.write(export.net_file(name, character, env, tables, model, kept, report, seed), out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
