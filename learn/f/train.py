"""Train f from self-play (plans/2026-11-21-1655-fight-score-f.md), and write its net file for `run --f-net`.

The samples are V's (v/data.py): the logged macro decisions that hold a potion, each potion held with what it saved
later (v/saves.py), by the combat net of --combat-net, the champion's in the environment the runs were played in: in
the fight where the run drank it, the HP and max HP that net says the player carries out of the fight with it, less
without it. A potion that left the belt outside a fight, or that the run still held at its end, saved 0; one the record
stops short of is not known and left out. The loss is f's squared error in units of 100 HP, every run weighed the same;
early stopping is on the held-out runs' loss (v/data.py's split).

--init is required: f goes on from a net file, a V's (its tables, state scaling and body, f's head anew; v/net.py's Body)
or an f's. The report gives the held-out root mean squared error in HP (`rmse_held_out`) against that of each potion
id's mean on the training decisions whatever the run (`rmse_id_mean`) and the --init net's (`init_rmse_held_out`), and
per potion id the held-out potions, their mean saving and f's mean (`by_potion`).

A run is read by the simulator's code (common.Encoder): --encoder is how to start it, the simulator's `encode` at
the commit that will play the net (`"~/sts2/sim-REV.sh encode"`), or the program src/Sts2Ai.Encode.

    uv run python -m f.train --list PREFIX --name NAME --encoder "SIM encode" --combat-net C.json --init V.json
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
from common import LearnError  # noqa: E402
from f import export  # noqa: E402
from f.net import Batch, Net, loss  # noqa: E402
from v import data, spec  # noqa: E402
from weights import out_path  # noqa: E402
from workers import built  # noqa: E402

script = common.app()

FLOORS = 64  # V's floors, which the samples are read with (their reach, which f does not read)
EPOCHS, PATIENCE = 40, 3  # at most, and without a better held-out loss before stopping
BATCH = 256  # decisions per step
LR, HEAD_LR, WEIGHT_DECAY = 3e-5, 1e-3, 1e-4  # the body's, the head's (new from a V)


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    encoder: Annotated[str, typer.Option(help="how to start the encoder that reads the runs: \"SIM encode\"")],
    combat_net: Annotated[str, typer.Option(help="the combat net file that says what each potion saved")],
    init: Annotated[str, typer.Option(help="the net file to go on from: a V's body, or an f")],
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/all/NAME.json)")] = None,
    seed: int = 0,
    workers: Annotated[int, typer.Option(help="processes that read the logs, threads that build the batches (0: this one)")] = 8,
    max_epochs: Annotated[int, typer.Option(help="epochs at most (patience may stop it sooner)")] = EPOCHS,
) -> None:
    out_file = out_path(name, out)
    start = export.read(init)
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    tables = game_ids.macro_tables()
    if start["vocab"] != tables:
        raise LearnError(f"{init} holds other tables than learn/game_ids.json's")
    if start["state"]["names"] != spec.STATE:
        raise LearnError(f"{init} reads the state {start['state']['names']}, not v/spec.py's {spec.STATE}")
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    mean = torch.tensor(start["state"]["mean"], dtype=torch.float64)
    std = torch.tensor(start["state"]["std"], dtype=torch.float64)

    logs, records = common.train_lists(list_)
    reader = common.Encoder(encoder)
    if missing := [n for n in spec.STATE if n not in reader.hello["state"]]:
        raise LearnError(f"the encoder knows no {', '.join(missing)} of a run: is it this code's commit?")
    samples, _ = data.read_runs(logs, records, FLOORS, reader, workers, combat_net)
    reader.close()
    runs = len({s["key"] for s in samples})
    samples = [s for s in samples if "event" not in s and s["saves"]]
    train = [s for s in samples if not data.held_out(s["run"])]
    test = [s for s in samples if data.held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out decisions holding a potion: too few runs")

    def known(part):
        return [(p, v) for s in part for p, v in s["saves"] if v is not None]

    logger.info(
        "{} decisions holding a potion of {} runs ({} held out), {} potions whose saving is known ({} held out)",
        len(samples), runs, len(test), len(known(samples)), len(known(test)),
    )  # fmt: skip

    build = functools.partial(Batch, index=index, mean=mean, std=std, device="cpu")

    def batches(part, shuffle):
        order = random.sample(range(len(part)), len(part)) if shuffle else list(range(len(part)))
        parts = ([part[k] for k in order[i : i + BATCH]] for i in range(0, len(order), BATCH))
        yield from built(build, parts, device, workers)

    test_weight = sum(s["weight"] for s in test)

    def evaluate(model):
        """The held-out loss, f's root mean squared error in HP, and per potion id its potions, mean saving and f."""
        total, sq, n, by = 0.0, 0.0, 0, {}
        with torch.no_grad():
            for b, part in zip(batches(test, False), (test[i : i + BATCH] for i in range(0, len(test), BATCH)), strict=True):
                f = model(*b.inputs())
                total += float((loss(f, b) * b.weight).sum())
                sq += float(((f - b.saved) ** 2 * b.known).sum())
                n += int(b.known.sum())
                for k, s in enumerate(part):
                    for j, (p, v) in enumerate(s["saves"]):
                        if v is not None:
                            row = by.setdefault(p, [0, 0.0, 0.0])
                            row[0], row[1], row[2] = row[0] + 1, row[1] + v, row[2] + float(f[k, j])
        return total / test_weight, math.sqrt(sq / max(1, n)), by

    model = Net({t: len(ids) for t, ids in tables.items()})
    model.load_state_dict(export.weights(start, model))
    model = model.to(device)
    model.eval()
    init_loss, init_rmse, _ = evaluate(model)
    logger.info("held-out loss {:.4f} ({:.2f} HP) from {}", init_loss, init_rmse, start["name"])
    heads = [p for n, p in model.named_parameters() if n.startswith("head.")]
    body = [p for n, p in model.named_parameters() if not n.startswith("head.")]
    opt = torch.optim.AdamW([{"params": body}, {"params": heads, "lr": HEAD_LR}], lr=LR, weight_decay=WEIGHT_DECAY)
    best, best_state, stale, epochs = math.inf, None, 0, 0
    for epoch in range(max_epochs):
        epochs = epoch + 1
        model.train()
        for b in batches(train, True):
            per_sample = loss(model(*b.inputs()), b)
            step = (per_sample * b.weight).sum() / b.weight.sum()
            opt.zero_grad()
            step.backward()
            opt.step()
        model.eval()
        held, rmse, _ = evaluate(model)
        logger.info("epoch {}: held-out loss {:.4f} ({:.2f} HP)", epoch, held, rmse)
        # each epoch's weights beside the net file, so a training cut short keeps them
        torch.save({k: v.cpu() for k, v in model.state_dict().items()}, out_file.with_name(f"{out_file.stem}.e{epoch}.pt"))
        if held < best - 1e-5:
            best, best_state, stale = held, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= PATIENCE:
                break
    model.load_state_dict(best_state)
    model.eval()

    held, rmse, by = evaluate(model)
    by_id = {}
    for p, v in known(train):
        by_id.setdefault(p, []).append(v)
    means = {p: sum(v) / len(v) for p, v in by_id.items()}
    overall = sum(v for _, v in known(train)) / max(1, len(known(train)))
    id_sq = [(means.get(p, overall) - v) ** 2 for p, v in known(test)]
    report = {
        "runs": runs,
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "potions": len(known(samples)),
        "held_out_potions": len(known(test)),
        "held_out_loss": held,
        "rmse_held_out": rmse,
        "rmse_id_mean": math.sqrt(sum(id_sq) / max(1, len(id_sq))),
        "init_held_out_loss": init_loss,
        "init_rmse_held_out": init_rmse,
        "by_potion": {p: {"potions": c, "saved": s / c, "f": f / c} for p, (c, s, f) in sorted(by.items(), key=lambda x: -x[1][0])},
    }
    print(json.dumps(report))

    settings = {
        "epochs": max_epochs, "patience": PATIENCE, "batch": BATCH, "lr": LR, "head_lr": HEAD_LR, "weight_decay": WEIGHT_DECAY,
        "dim": Net.DIM, "hidden": Net.HIDDEN, "body": Net.BODY, "combat_net": export.read(combat_net)["name"],
    }  # fmt: skip
    trained = common.trained(list_, start, seed, epochs, settings, reader)
    net = export.net_file(name, tables, mean, std, model, test, report, seed, trained)
    export.write(net, out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
