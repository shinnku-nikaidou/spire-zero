"""Train V from self-play, and write its net file for `run --value`.

The samples are v/data.py's: the logged macro decisions of every character with where their runs ended and the next
combats' HP costs. The loss is the hazards' cross-entropy plus the second head's squared errors; early stopping on the
held-out runs weighs them the same way. A run is valued by its chance to win, the chance to survive every floor; the
report gives the Brier score of that chance on the held-out decisions against whether their runs were won
(`brier_held_out`, and `brier_held_out_floor_only` for the training runs' hazards whatever the state), and of the
held-out decisions that hold a potion, the share V values higher with one potion gone (`potion_below_empty`,
v/potions.py).

--init goes on from a net file instead of from scratch: its state scaling and weights, read back from its ONNX (the
second head, not in the file, starts anew). The report then also gives the file's own held-out loss and Brier score
(`init_held_out_loss`, `init_brier_held_out`), on the same held-out decisions.

--bootstrap FA.json,FB.json --horizon N trains on multi-step targets (v/bootstrap.py): a training decision keeps what
happened over the next N floors and from there on is labelled with the hazards of a frozen V that never saw its run,
FA trained on fold A of the training runs alone and FB on fold B (`--fold A`, `--fold B`); the held-out decisions keep
their real outcomes, so the report and early stopping are on the same labels as without.

Event options (v/data.py) train with the rest; an SL line's that left the room is labelled by the frozen V's, so only
with --bootstrap. Their held-out samples are not in the held-out loss, which stays on real outcomes: the report gives
theirs apart (`held_out_event_loss`, labelled by both frozen V's, neither of which saw those runs), with how many of
each kind there were (`event_samples`), and the held-out loss of SL's and NoSL's decisions apart
(`held_out_loss_by_env`).

The net's sizes and the training's settings are fixed below: an experiment changes them here, and only a result keeps
the change.

A run is read by the simulator's code (common.Encoder): --encoder is how to start it, the simulator's `encode` at
the commit that will play the net (`"~/sts2/sim-REV.sh encode"`), or the program src/Sts2Ai.Encode.

    uv run python -m v.train --list PREFIX --name NAME --encoder "SIM encode" [--init ../weights/all/CHAMPION.json]
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
from v import bootstrap, data, export, potions, spec  # noqa: E402
from v.net import Batch, Net, aux_loss, hazard_loss, win_chance  # noqa: E402
from weights import out_path  # noqa: E402
from workers import built  # noqa: E402

script = common.app()

FLOORS = 64  # the hazard head's floors, from floor 0
EPOCHS, PATIENCE = 40, 3  # at most, and without a better held-out loss before stopping
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
    workers: Annotated[int, typer.Option(help="processes that read the logs, threads that build the batches (0: this one)")] = 8,
    fold: Annotated[str | None, typer.Option(help="train on one fold of the training runs alone: A or B")] = None,
    frozen: Annotated[
        str | None,
        typer.Option("--bootstrap", help="the frozen V's of folds A and B, FA.json,FB.json, for multi-step targets"),
    ] = None,
    horizon: Annotated[int, typer.Option(help="with --bootstrap: the floors labelled with what happened")] = 0,
    max_epochs: Annotated[int, typer.Option(help="epochs at most (patience may stop it sooner)")] = EPOCHS,
) -> None:
    if fold is not None and fold not in bootstrap.FOLDS:
        raise LearnError(f"--fold is one of {', '.join(bootstrap.FOLDS)}, not {fold}")
    if (frozen is None) != (horizon == 0) or horizon < 0:
        raise LearnError("--bootstrap and --horizon (at least 1) go together")
    out_file = out_path(name, out)
    start = export.read(init) if init else None
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    logs, records = common.train_lists(list_)
    reader = common.Encoder(encoder)
    if missing := [name for name in spec.STATE if name not in reader.hello["state"]]:
        raise LearnError(f"the encoder knows no {', '.join(missing)} of a run: is it this code's commit?")
    samples, runs = data.read_runs(logs, records, FLOORS, reader, workers)
    reader.close()
    if not frozen and (left := sum(1 for s in samples if "later" in s)):
        logger.info("{} event options whose lines left the room are left out: their labels need --bootstrap", left)
        samples = [s for s in samples if "later" not in s]
    event_kinds = {k: sum(1 for s in samples if s.get("event") == k) for k in ("taken", "left", "ended")}
    train = [s for s in samples if not data.held_out(s["run"]) and fold in (None, bootstrap.fold(s["run"]))]
    test = [s for s in samples if data.held_out(s["run"]) and "event" not in s]
    test_events = [s for s in samples if data.held_out(s["run"]) and "event" in s]
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

    build = functools.partial(Batch, index=index, mean=mean, std=std, floors=FLOORS, device="cpu")

    def batches(part, shuffle):
        order = random.sample(range(len(part)), len(part)) if shuffle else list(range(len(part)))
        parts = ([part[k] for k in order[i : i + BATCH]] for i in range(0, len(order), BATCH))
        yield from built(build, parts, device, workers)

    # The held-out batches are built again at each look rather than kept: on a list of 125,000 runs they outgrow a 16 G
    # GPU. Built in order, with no draw, so each look sees the same batches.
    test_weight = sum(s["weight"] for s in test)

    # Baseline: per floor, the hazard the training runs show, whatever the state.
    alive, deaths = torch.zeros(FLOORS, dtype=torch.float64), torch.zeros(FLOORS, dtype=torch.float64)
    for b in batches(train, False):
        m, wt = b.label_mask.double().cpu(), b.weight.double().cpu()[:, None]
        alive, deaths = alive + (m * wt).sum(0), deaths + (b.target.double().cpu() * m * wt).sum(0)
    base_hazard = ((deaths + 0.5) / (alive + 1)).clamp(1e-4, 1 - 1e-4)
    base_logits = torch.log(base_hazard / (1 - base_hazard)).float().to(device)

    # Multi-step targets, after the baseline, which stays on what happened.
    boot = None
    if frozen:
        with torch.random.fork_rng(devices=[]):  # the frozen V's leave the training's own draws as they were
            nets = bootstrap.frozen([export.read(p) for p in frozen.split(",")], tables, FLOORS, device)
        got = bootstrap.attach(train, horizon, nets, index, FLOORS, BATCH, device, workers)
        boot = {"nets": {f: nets[f][0]["name"] for f in sorted(nets)}, "horizon": horizon, "share": got / len(train)}
        logger.info("{} of {} training decisions labelled {} floors on by {}", got, len(train), horizon, boot["nets"])
        lines = bootstrap.attach_events(train + test_events, nets, index, FLOORS, BATCH, device, workers)
        logger.info("{} event options labelled where their lines left the room", lines)
    event_batches = list(batches(test_events, False))
    event_weight = sum(s["weight"] for s in test_events)

    def event_loss(model):
        """The held-out event options' loss, by their own labels."""
        if not event_batches:
            return None
        with torch.no_grad():
            return sum(float((hazard_loss(model(*b.inputs()), b) * b.weight).sum()) for b in event_batches) / event_weight

    def by_env(model):
        """The held-out loss of SL's decisions and of NoSL's apart."""
        out = {}
        for env in ("SL", "NoSL"):
            mine = [s for s in test if s.get("env", "SL") == env]
            if mine:
                with torch.no_grad():
                    total = sum(float((hazard_loss(model(*b.inputs()), b) * b.weight).sum()) for b in batches(mine, False))
                out[env] = total / sum(s["weight"] for s in mine)
        return out

    def evaluate(model):
        total, aux_total, wins = 0.0, 0.0, []
        with torch.no_grad():
            for b in batches(test, False):
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
    for epoch in range(max_epochs):
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
        logger.info("epoch {}: held-out loss {:.4f}, next combats {:.4f}, event options {}", epoch, held, held_aux, event_loss(model))
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
        # how often V would still throw a potion away: under potions.LIMIT, MacroBrain.Offered goes
        "potion_below_empty": potions.below_empty(potions.by(model, make), test)["share"],
        "held_out_loss_by_env": by_env(model),
        "event_samples": event_kinds,
        "held_out_event_loss": event_loss(model),
    }
    if start:
        report |= {"init_held_out_loss": init_loss, "init_brier_held_out": brier(init_won_chance)}
    if boot:
        report["bootstrapped_share"] = boot["share"]  # of the training decisions
    print(json.dumps(report))

    settings = {
        "floors": FLOORS, "epochs": max_epochs, "patience": PATIENCE, "batch": BATCH, "lr": LR, "weight_decay": WEIGHT_DECAY,
        "aux_weight": AUX_WEIGHT, "dim": Net.DIM, "hidden": Net.HIDDEN, "body": Net.BODY,
    }  # fmt: skip
    trained = common.trained(list_, start, seed, epochs, settings, reader)
    if fold:
        trained["fold"] = fold  # a frozen V of v/bootstrap.py: it never saw the other fold's runs
    if boot:
        trained["bootstrap"] = {"nets": boot["nets"], "horizon": horizon}
    net = export.net_file(name, FLOORS, tables, mean, std, model, test + test_events, report, seed, trained)
    export.write(net, out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
