"""Train V (docs: macro-brain/value §5) from self-play, and write its net file for `run --value`.

The samples are v/data.py's: the logged macro decisions of every character with where their runs ended and the next
combats' HP costs. The loss is the hazards' cross-entropy plus the second head's squared errors; early stopping on the
held-out runs weighs them the same way. A run is valued by its chance to win, the chance to survive every floor; the
report gives the Brier score of that chance on the held-out decisions against whether their runs were won
(`brier_held_out`, and `brier_held_out_floor_only` for the training runs' hazards whatever the state).

--init goes on from a net file instead of from scratch: its state scaling and weights, read back from its ONNX (the
second head, not in the file, starts anew). The report then also gives the file's own held-out loss and Brier score
(`init_held_out_loss`, `init_brier_held_out`), on the same held-out decisions.

The net's sizes and the training's settings are fixed below: an experiment changes them here, and only a result keeps
the change.

A run is read by the simulator's code (common.Encoder): --encoder is how to start it, the simulator's `encode` at
the commit that will play the net (`"~/sts2/sim-REV.sh encode"`), or the program src/Sts2Ai.Encode.

    uv run python -m v.train --list PREFIX --name NAME --encoder "SIM encode" [--init ../weights/all/CHAMPION.json]
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
from common import LearnError  # noqa: E402
from v import data, export, spec  # noqa: E402
from v.net import Batch, Net, aux_loss, hazard_loss, win_chance  # noqa: E402
from weights import out_path  # noqa: E402

script = common.app()

FLOORS = 64  # the hazard head's floors, from floor 0
EPOCHS, PATIENCE = 40, 5  # at most, and without a better held-out loss before stopping
BATCH = 256  # decisions per step
LR, WEIGHT_DECAY = 3e-5, 1e-4
AUX_WEIGHT = 3.0  # the second head's squared errors, per decision


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    encoder: Annotated[str, typer.Option(help="how to start the encoder that reads the runs: \"SIM encode\"")],
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/all/NAME.json)")] = None,
    init: Annotated[
        str | None,
        typer.Option(
            help="a net file to go on from: its state scaling and weights (the second head anew)"
        ),
    ] = None,
    seed: int = 0,
) -> None:
    out_file = out_path(name, out)
    start = export.read(init) if init else None
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    logs, records = common.train_lists(list_)
    reader = common.Encoder(encoder)
    if missing := [name for name in spec.STATE if name not in reader.hello["state"]]:
        raise LearnError(f"the encoder knows no {', '.join(missing)} of a run: is it this code's commit?")
    samples, runs = data.read_runs(logs, records, FLOORS, reader)
    reader.close()
    train = [s for s in samples if not data.held_out(s["run"])]
    test = [s for s in samples if data.held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")
    logger.info(
        "{} decisions of {} runs ({} held out)",
        len(samples),
        len({s["key"] for s in samples}),
        len({s["key"] for s in test}),
    )

    # Going on from a net keeps its state scaling; every net's tables are the game's.
    tables = game_ids.macro_tables()
    if start and start["vocab"] != tables:
        raise LearnError(f"{init} holds other tables than learn/game_ids.json's")
    if start and start["state"]["names"] != spec.STATE:
        raise LearnError(f"{init} reads the state {start['state']['names']}, not v/spec.py's {spec.STATE}")
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    if start:
        mean = torch.tensor(start["state"]["mean"], dtype=torch.float64)
        std = torch.tensor(start["state"]["std"], dtype=torch.float64)
    else:
        x = torch.tensor([s["state"] for s in train], dtype=torch.float64)
        w = torch.tensor([s["weight"] for s in train], dtype=torch.float64)
        mean = (w[:, None] * x).sum(0) / w.sum()
        std = ((w[:, None] * (x - mean) ** 2).sum(0) / w.sum()).sqrt()
        std = torch.where(std > 1e-9, std, torch.ones_like(std))

    def make(part):
        return Batch(part, index, mean, std, FLOORS, device)

    def batches(part, shuffle):
        order = random.sample(range(len(part)), len(part)) if shuffle else list(range(len(part)))
        for i in range(0, len(order), BATCH):
            yield make([part[k] for k in order[i : i + BATCH]])

    test_batches = list(batches(test, False))
    test_weight = sum(s["weight"] for s in test)

    # Baseline: per floor, the hazard the training runs show, whatever the state.
    alive, deaths = torch.zeros(FLOORS, dtype=torch.float64), torch.zeros(FLOORS, dtype=torch.float64)
    for b in batches(train, False):
        m, wt = b.label_mask.double().cpu(), b.weight.double().cpu()[:, None]
        alive, deaths = alive + (m * wt).sum(0), deaths + (b.target.double().cpu() * m * wt).sum(0)
    base_hazard = ((deaths + 0.5) / (alive + 1)).clamp(1e-4, 1 - 1e-4)
    base_logits = torch.log(base_hazard / (1 - base_hazard)).float().to(device)

    def evaluate(model):
        total, aux_total, wins = 0.0, 0.0, []
        with torch.no_grad():
            for b in test_batches:
                if model:
                    logits, aux = model.both(*b.inputs())
                    aux_total += float((aux_loss(aux, b) * b.weight).sum())
                else:
                    logits = base_logits.expand(len(b.weight), -1)
                total += float((hazard_loss(logits, b) * b.weight).sum())
                wins.append(win_chance(torch.sigmoid(logits), b.start))
        return total / test_weight, aux_total / test_weight, torch.cat(wins)

    model = Net({t: len(ids) for t, ids in tables.items()}, FLOORS)
    if start:
        model.load_state_dict(export.weights(start, model))
    else:
        # The net starts from the baseline and learns how the state moves each floor's hazard away from it.
        with torch.no_grad():
            model.head.bias.copy_(base_logits)
    model = model.to(device)
    if start:
        model.eval()
        init_loss, _, init_won_chance = evaluate(model)
        logger.info("the file's held-out loss {:.4f}", init_loss)
    opt = torch.optim.AdamW(model.parameters(), lr=LR, weight_decay=WEIGHT_DECAY)
    best, best_state, stale, epochs = math.inf, None, 0, 0
    for epoch in range(EPOCHS):
        epochs = epoch + 1
        model.train()
        for b in batches(train, True):
            logits, aux = model.both(*b.inputs())
            per_sample = hazard_loss(logits, b) + AUX_WEIGHT * aux_loss(aux, b)
            loss = (per_sample * b.weight).sum() / b.weight.sum()
            opt.zero_grad()
            loss.backward()
            opt.step()
        model.eval()
        held, held_aux, _ = evaluate(model)
        logger.info("epoch {}: held-out loss {:.4f}, next combats {:.4f}", epoch, held, held_aux)
        if held < best - 1e-4:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= PATIENCE:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: held-out loss against the baseline, and how well the chance to win predicts the runs won.
    held, held_aux, won_chance = evaluate(model)
    base_loss, _, base_won_chance = evaluate(None)
    weights = torch.tensor([s["weight"] for s in test], dtype=torch.float64, device=won_chance.device)
    won = torch.tensor([float(s["kind"] == "won") for s in test], dtype=torch.float64, device=won_chance.device)

    def brier(chance):
        return float((weights * (chance - won) ** 2).sum() / weights.sum())

    report = {
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "won": sum(1 for r in runs if r["outcome"] == "won"),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_loss_floor_only": base_loss,
        "held_out_next_combats": held_aux,
        "won_held_out_runs": len({s["key"] for s in test if s["kind"] == "won"}),
        "brier_held_out": brier(won_chance),
        "brier_held_out_floor_only": brier(base_won_chance),
    }
    if start:
        report |= {"init_held_out_loss": init_loss, "init_brier_held_out": brier(init_won_chance)}
    print(json.dumps(report))

    settings = {
        "floors": FLOORS, "epochs": EPOCHS, "patience": PATIENCE, "batch": BATCH, "lr": LR, "weight_decay": WEIGHT_DECAY,
        "aux_weight": AUX_WEIGHT, "dim": Net.DIM, "hidden": Net.HIDDEN, "body": Net.BODY,
    }  # fmt: skip
    trained = common.trained(list_, start, seed, epochs, settings, reader)
    net = export.net_file(name, FLOORS, tables, mean, std, model, test, report, seed, trained)
    export.write(net, out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
