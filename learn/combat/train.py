"""Train the combat net (docs: learning/combat-net §4), the combat search's leaf, from one environment's games, and write its
net file for `--combat-net`.

The samples are combat/data.py's: each round's start in the logs of --env. With --value the net's target is the fight's
worth by that V, the loss its squared error; without, the endings, the loss their cross-entropy plus POTION_WEIGHT times
the squared error of the potions used after a win. The net's sizes and the training's settings are fixed below: an
experiment changes them here, and only a result keeps the change.

    uv run python -m combat.train --list data/silent/lists/c7 --env SL --name c7-1 \\
        --skip-seeds ../eval/seeds/promotion-1-300.txt
"""

import json
import math
import random
from collections import defaultdict
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


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    env: Annotated[str, typer.Option(help="the environment whose games these are, SL or NoSL")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    value: Annotated[str | None, typer.Option(help="the net file of the V that prices the fights' ends: target worth")] = None,
    skip_seeds: Annotated[str | None, typer.Option(help="a seed file whose runs are not read: the gate's")] = None,
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
    samples, skipped = data.read(*common.lists(list_), character, env, run_value(value, device) if value else None, skip)
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

    def evaluate(model):
        """The held-out loss, and each held-out sample's prediction: its worth, or the leaf's value, the cross-entropy and
        the chance of death of its endings."""
        total, parts = 0.0, []
        with torch.no_grad():
            for b in test_batches:
                out = model(*b.inputs())
                total += float(loss(out, b).sum())
                parts.append(out if model.is_worth else (*out, b.target))
        if model.is_worth:
            return total / len(test), torch.cat(parts).double().cpu()
        logits = torch.cat([p[0] for p in parts]).double().cpu()
        potions = torch.cat([p[1] for p in parts]).double().cpu()
        target = torch.cat([p[2] for p in parts]).cpu()
        probs = torch.softmax(logits, 1)
        mids = data.LOSS_MIN + (torch.arange(1, data.BUCKETS + 1).double() - 0.5) * (1 - data.LOSS_MIN) / data.BUCKETS
        hp = torch.tensor([s["share"] for s in test]).double()[:, None]
        held = torch.tensor([s["held"] for s in test]).double()
        value = (probs[:, 1:] * (0.2 + 0.6 * (hp - mids))).sum(1) + (1 - probs[:, 0]) * 0.02 * (held - potions)
        return total / len(test), value, torch.nn.functional.cross_entropy(logits, target, reduction="none"), probs[:, 0]

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
        held = evaluate(model)[0]
        logger.info("epoch {}: held-out loss {:.4f}", epoch, held)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= PATIENCE:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: the held-out error against the training's prior and the rank within each fight, on the whole and by
    # act, room and encounter.
    held, *found = evaluate(model)
    if value:
        worths = found[0]
        target = torch.tensor([s["worth"] for s in test]).double()
        mean = sum(s["worth"] for s in train) / len(train)

        def part(ix):
            return {
                "samples": len(ix),
                "mse": float(((worths[ix] - target[ix]) ** 2).mean()),
                "mse_mean": float(((mean - target[ix]) ** 2).mean()),
                "rank_within": rank_within([test[k] for k in ix], worths[ix], "worth"),
                "worth": float(target[ix].mean()),
                "predicted": float(worths[ix].mean()),
            }
    else:
        valued, ces, deaths = found
        counts = torch.bincount(torch.tensor([s["class"] for s in train]), minlength=1 + data.BUCKETS).double() + 0.5
        prior = -torch.log(counts / counts.sum())[torch.tensor([s["class"] for s in test])]
        died = torch.tensor([s["class"] == 0 for s in test], dtype=torch.float64)

        def part(ix):
            return {
                "samples": len(ix),
                "ce": float(ces[ix].mean()),
                "ce_prior": float(prior[ix].mean()),
                "death": float(died[ix].mean()),
                "predicted_death": float(deaths[ix].mean()),
                "rank_within": rank_within([test[k] for k in ix], valued[ix], "price"),
            }

    def by(key):
        groups = defaultdict(list)
        for k, s in enumerate(test):
            groups[str(s[key])].append(k)
        return {g: part(ix) for g, ix in sorted(groups.items())}

    report = {
        "env": env,
        "value": value,
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "held_out_samples": len(test),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out": part(list(range(len(test)))),
        "by_act": by("act"),
        "by_room": by("room"),
        "by_encounter": by("encounter"),
    }
    print(json.dumps(report))

    kept = [s for s in test if "obs" in s]
    export.write(export.net_file(name, character, env, tables, model, kept, report, seed), out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
