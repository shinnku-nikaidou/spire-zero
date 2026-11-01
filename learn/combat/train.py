"""Train the combat net (docs: learning/combat-net §4), the combat search's leaf, from one environment's games, and write its
net file for `--combat-net`.

The samples are combat/data.py's: each round's start in the logs of --env. The loss is the endings' cross-entropy plus
POTION_WEIGHT times each held potion's cross-entropy of being gone after a win. The net's sizes and the training's settings
are fixed below: an experiment changes them here, and only a result keeps the change. --test-list's games on the seeds of
--skip-seeds, never trained on, are only reported, as `medium`.

    uv run python -m combat.train --list data/silent/lists/c7 --env SL --name c7-2 \\
        --skip-seeds ../eval/seeds/promotion-1-300.txt --test-list data/silent/lists/medium
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
import game_ids  # noqa: E402
from combat import data, export, report  # noqa: E402
from combat.net import Batch, Net, loss  # noqa: E402
from common import LearnError  # noqa: E402
from weights import out_path  # noqa: E402

script = common.app()

EPOCHS, PATIENCE = 20, 3  # at most, and without a better held-out loss before stopping
BATCH = 256
LR, WEIGHT_DECAY = 1e-3, 1e-4


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    env: Annotated[str, typer.Option(help="the environment whose games these are, SL or NoSL")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
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
    samples, skipped = data.read(*common.lists(list_), character, env, skip)
    train = [s for s in samples if not data.held_out(s["run"])]
    test = [s for s in samples if data.held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")
    logger.info(
        "{} round starts from {} runs; {} runs skipped, their combats not matching the record",
        len(samples), len({s["key"] for s in samples}), skipped,
    )  # fmt: skip

    tables = game_ids.combat_tables()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}

    def batches(part, shuffle):
        order = random.sample(range(len(part)), len(part)) if shuffle else list(range(len(part)))
        for i in range(0, len(order), BATCH):
            yield Batch([part[k] for k in order[i : i + BATCH]], index, device)

    test_batches = list(batches(test, False))

    def held_out_loss(model):
        with torch.no_grad():
            return sum(float(loss(model(*b.inputs()), b).sum()) for b in test_batches) / len(test)

    model = Net({t: len(ids) for t, ids in tables.items()}).to(device)
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

    fit = {
        "env": env,
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "held_out_samples": len(test),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        **report.summary(model, test, test_batches, train),
    }
    if test_list:
        medium, _ = data.read(*common.lists(test_list), character, env, only_seeds=skip)
        fit["medium"] = report.summary(model, medium, list(batches(medium, False)), train)
    print(json.dumps(fit))

    kept = [s for s in test if "obs" in s]
    export.write(export.net_file(name, character, env, tables, model, kept, fit, seed), out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
