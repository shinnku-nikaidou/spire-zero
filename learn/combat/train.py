"""Train the combat net (docs: learning §1–§2), the combat search's leaf, from one environment's games, and write its
net file for `--combat-net`.

The samples are combat/data.py's: each round's start in the logs of --env, with how its combat ended. The loss is the
endings' cross-entropy plus POTION_WEIGHT times the squared error of the potions used after a win. The net's sizes and
the training's settings are fixed below: an experiment changes them here, and only a result keeps the change.

    uv run python -m combat.train --list data/silent/lists/sl-combat --env SL --name sl-c1
"""

import json
import math
import random
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
from weights import out_path  # noqa: E402

script = common.app()

MIN_COUNT = 3  # ids seen fewer times in the training runs read as unknown
EPOCHS, PATIENCE = 60, 6  # at most, and without a better held-out loss before stopping
BATCH = 256
LR, WEIGHT_DECAY = 1e-3, 1e-4
POTION_WEIGHT = 0.1


def vocab(samples) -> dict[str, list[str]]:
    """Per table, the ids seen at least MIN_COUNT times in these samples; entry 0 reads every other id."""
    counts = {t: {} for t in encode.TABLES}

    def count(t, i):
        counts[t][i] = counts[t].get(i, 0) + 1

    def count_powers(ps):
        for pid, _, _, card in ps:
            count("powers", pid)
            if card:
                count("cards", card)

    for s in samples:
        tok = s["tokens"]
        for card_id, _, _, mods in tok["cards"]:
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
        for t in (*encode.BAGS, "orbs"):
            for i in tok[t]:
                count(t, i)
        for a in tok["allies"]:
            count("allies", a["id"])
            count_powers(a["powers"])
    return {t: ["<unk>"] + sorted(i for i, n in c.items() if n >= MIN_COUNT) for t, c in counts.items()}


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    env: Annotated[str, typer.Option(help="the environment whose games these are, SL or NoSL")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
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

    samples, skipped = data.read(*common.lists(list_), character, env)
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
        """The held-out loss, and each held-out sample's cross-entropy and chance of death."""
        total, ces, deaths = 0.0, [], []
        with torch.no_grad():
            for b in test_batches:
                logits, potions = model(*b.inputs())
                total += float(loss(logits, potions, b, POTION_WEIGHT).sum())
                ces.append(torch.nn.functional.cross_entropy(logits, b.target, reduction="none"))
                deaths.append(torch.softmax(logits, 1)[:, 0])
        return total / len(test), torch.cat(ces).double().cpu(), torch.cat(deaths).double().cpu()

    model = Net({t: len(ids) for t, ids in tables.items()}).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=LR, weight_decay=WEIGHT_DECAY)
    best, best_state, stale = math.inf, None, 0
    for epoch in range(EPOCHS):
        model.train()
        for b in batches(train, True):
            logits, potions = model(*b.inputs())
            step = loss(logits, potions, b, POTION_WEIGHT).mean()
            opt.zero_grad()
            step.backward()
            opt.step()
        model.eval()
        held, _, _ = evaluate(model)
        logger.info("epoch {}: held-out loss {:.4f}", epoch, held)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= PATIENCE:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: the held-out cross-entropy against a prior that knows only the training endings, by room too (a
    # monster's move order should matter most in the long fights of elites and bosses), and how well the chance of
    # death the net gives tells the deaths.
    held, ces, deaths = evaluate(model)
    rooms = [s["obs"].get("room") or "<none>" for s in test]
    counts = torch.bincount(torch.tensor([s["class"] for s in train]), minlength=1 + data.BUCKETS).double() + 0.5
    prior = counts / counts.sum()
    died = torch.tensor([s["class"] == 0 for s in test], dtype=torch.float64)
    report = {
        "env": env,
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "held_out_samples": len(test),
        "deaths": int(sum(1 for s in samples if s["class"] == 0)),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_ce": float(ces.mean()),
        "held_out_ce_prior": -float(sum(math.log(prior[s["class"]]) for s in test)) / len(test),
        "held_out_ce_by_room": {
            r: float(ces[[k for k, x in enumerate(rooms) if x == r]].mean()) for r in sorted(set(rooms))
        },
        "held_out_samples_by_room": {r: rooms.count(r) for r in sorted(set(rooms))},
        "death_corr": float(torch.corrcoef(torch.stack([deaths, died]))[0, 1]),
    }
    print(json.dumps(report))

    export.write(export.net_file(name, character, env, tables, model, test, report, seed), out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
