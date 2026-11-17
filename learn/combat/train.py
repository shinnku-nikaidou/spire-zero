"""Train the combat net, the combat search's leaf, from SL's and NoSL's games, and write its
net file for `--combat-net`.

The samples are combat/data.py's: each round's start in the logs, every character's and each environment's, a NoSL
state carrying its mark (env:NoSL), so that one net serves both; the report gives each environment's held-out loss
apart (`held_out_loss_by_env`). The loss is the
endings' cross-entropy plus POTION_WEIGHT times each held potion's cross-entropy of being gone after a win. The net's
sizes and the training's settings are fixed below: an experiment changes them here, and only a result keeps the change.
--list refuses test runs (eval jobs); --test-list's games are only reported, as `medium`. --init goes on from a net file instead of from scratch, at the lower
LR_INIT, and keeps the file's weights unless a later epoch beats them on the held-out loss.

A state is read by the simulator's code (common.Encoder): --encoder is how to start it, the simulator's `encode` at
the commit that will play the net (`"~/sts2/sim-REV.sh encode"`), or the program src/Sts2Ai.Encode.

    uv run python -m combat.train --list PREFIX --name NAME --encoder "SIM encode" [--test-list PREFIX]
        [--init ../weights/all/CHAMPION.json]
"""

import functools
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
from combat import data, export, report, spec  # noqa: E402
from combat.net import POTION_WEIGHT, Batch, Net, loss  # noqa: E402
from common import LearnError  # noqa: E402
from weights import out_path  # noqa: E402
from workers import built  # noqa: E402

script = common.app()

EPOCHS, PATIENCE = 20, 3  # at most, and without a better held-out loss before stopping
BATCH = 256
LR, LR_INIT, WEIGHT_DECAY = 1e-3, 3e-4, 1e-4
ENVS = ("SL", "NoSL")  # the environments, in the order a net file names them


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    encoder: Annotated[str, typer.Option(help="how to start the encoder that reads the states: \"SIM encode\"")],
    test_list: Annotated[str | None, typer.Option(help="a list pair whose games are only reported, as `medium`")] = None,
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/all/NAME.json)")] = None,
    init: Annotated[str | None, typer.Option(help="a net file to go on from: its weights, read back from its ONNX")] = None,
    seed: int = 0,
    workers: Annotated[int, typer.Option(help="processes that read the logs, threads that build the batches (0: this one)")] = 8,
) -> None:
    out_file = out_path(name, out)
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    reader = common.Encoder(encoder)
    if reader.hello["lags"] != spec.LAGS:
        raise LearnError(f"the encoder reads {reader.hello['lags']} earlier rounds of intents, the net {spec.LAGS}")
    samples, skipped = data.read(*common.train_lists(list_), reader, workers)
    train = [s for s in samples if not data.held_out(s["run"])]
    test = [s for s in samples if data.held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")
    logger.info(
        "{} round starts from {} runs; {} runs skipped, their combats not matching the record",
        len(samples), len({s["key"] for s in samples}), skipped,
    )  # fmt: skip

    tables = game_ids.combat_tables()
    start = export.read(init) if init else None
    if start and start["vocab"] != tables:
        raise LearnError(f"{init} holds other tables than learn/game_ids.json's")
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}

    build = functools.partial(Batch, index=index, device="cpu")

    def batches(part, shuffle):
        order = random.sample(range(len(part)), len(part)) if shuffle else list(range(len(part)))
        parts = ([part[k] for k in order[i : i + BATCH]] for i in range(0, len(order), BATCH))
        yield from built(build, parts, device, workers)

    # The held-out batches are built again at each look rather than kept (V's trainer says why).

    def held_out_loss_of(model, part_batches, n):
        with torch.no_grad():
            return sum(float(loss(model(*b.inputs()), b).sum()) for b in part_batches) / n

    def held_out_loss(model):
        return held_out_loss_of(model, batches(test, False), len(test))

    model = Net({t: len(ids) for t, ids in tables.items()}).to(device)
    best, best_state, stale, epochs = math.inf, None, 0, 0
    if start:
        model.load_state_dict(export.weights(start, model))
        model.eval()
        best, best_state = held_out_loss(model), {k: v.clone() for k, v in model.state_dict().items()}
        logger.info("the file's held-out loss {:.4f}", best)
    opt = torch.optim.AdamW(model.parameters(), lr=LR_INIT if start else LR, weight_decay=WEIGHT_DECAY)
    for epoch in range(EPOCHS):
        epochs = epoch + 1
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

    envs = [e for e in ENVS if any(s["env"] == e for s in samples)]
    env = "+".join(envs)

    def by_env(part):
        """The held-out loss of each environment's samples apart."""
        out = {}
        for e in envs:
            mine = [s for s in part if s["env"] == e]
            if mine:
                out[e] = held_out_loss_of(model, list(batches(mine, False)), len(mine))
        return out

    fit = {
        "env": env,
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "held_out_samples": len(test),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        **report.summary(model, test, batches(test, False), train),
        "held_out_loss_by_env": by_env(test),
    }
    if test_list:
        medium, _ = data.read(*common.lists(test_list), reader, workers)
        fit["medium"] = report.summary(model, medium, list(batches(medium, False)), train)
    reader.close()
    print(json.dumps(fit))

    kept = [s for s in test if "obs" in s]
    settings = {
        "epochs": EPOCHS, "patience": PATIENCE, "batch": BATCH, "lr": LR_INIT if start else LR, "weight_decay": WEIGHT_DECAY,
        "potion_weight": POTION_WEIGHT, "dim": Net.DIM, "hidden": Net.HIDDEN, "body": Net.BODY, "test_list": test_list,
    }  # fmt: skip
    trained = common.trained(list_, start, seed, epochs, settings, reader)
    export.write(export.net_file(name, env, tables, model, kept, fit, seed, trained), out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
