"""Train pure V (docs: learning §1–§2) from self-play, and write its net file for `run --value`.

The samples are v/data.py's: the logged macro decisions with where their runs ended and the next combats' HP costs,
and optionally boss sims (--boss-sims with the afterstates they were played from, --boss-afterstates) and door states
(--boss-states). The loss is the hazards' cross-entropy, plus the second head's squared errors, plus the boss sims'
fit of V's centred values and the door states' fit of the second head's boss score; early stopping on the held-out
runs weighs them the same way. --afterstates (the logged decisions' options) only feed the report's
`explore_temperature`: the `run --macro-explore` at which V would explore as often as the logs' brains did.

--gauntlet trains the boss gauntlet's V (plan v6) on the gauntlet's runs alone (`run --gauntlet`): the same net with a
third head, the share of the enemies' HP taken in the combat the run ends in, fitted at the floor it ended on; its value
is the progress expected (net file objective "progress").

The net's sizes and the training's settings are fixed below: an experiment changes them here, and only a result keeps
the change.

    uv run python -m v.train --list data/silent/lists/v-g0-g3 --name pure-1
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
from common import LearnError  # noqa: E402
from v import data, encode, export  # noqa: E402
from v.net import (  # noqa: E402
    BOSS_AT,
    Batch,
    Net,
    aux_loss,
    centred,
    expected_floor,
    expected_progress,
    explored,
    hazard_loss,
    pearson,
    share_loss,
)
from weights import out_path  # noqa: E402

script = common.app()

FLOORS = 64  # the hazard head's floors, from floor 0
MIN_COUNT = 3  # ids seen fewer times in the training runs read as unknown
EPOCHS, PATIENCE = 40, 5  # at most, and without a better held-out loss before stopping
BATCH, SIDE_BATCH = 256, 64  # decisions per step; boss sim decisions or doors per step
LR, WEIGHT_DECAY = 3e-5, 1e-4
AUX_WEIGHT = 3.0  # the second head's squared errors, per decision
BOSS_WEIGHT = 1.0  # the boss sims' fit, per decision, all its options together
DOOR_WEIGHT = 1.0  # the door states' fit, per door, all its HP levels together


def vocab(samples) -> dict[str, list[str]]:
    """Per table, the ids seen at least MIN_COUNT times in these samples; entry 0 reads every other id."""
    counts = {t: {} for t in encode.TABLES}
    for s in samples:
        tok = s["tokens"]
        for card_id, _, mods in tok["cards"]:
            counts["cards"][card_id] = counts["cards"].get(card_id, 0) + 1
            for m in mods:
                counts["modifiers"][m] = counts["modifiers"].get(m, 0) + 1
        for t in encode.BAGS:
            for i in tok[t]:
                counts[t][i] = counts[t].get(i, 0) + 1
    return {t: ["<unk>"] + sorted(i for i, n in c.items() if n >= MIN_COUNT) for t, c in counts.items()}


def stream(part, make):
    """Endless chunks of boss sim decisions or doors, shuffled anew on each pass."""
    while part:
        order = random.sample(part, len(part))
        for i in range(0, len(order), SIDE_BATCH):
            yield make(order[i : i + SIDE_BATCH])


def grouped(make, chunk):
    """Groups' samples as one batch, each sample's group, and how many groups."""
    group = [k for k, g in enumerate(chunk) for _ in g["samples"]]
    return make([s for g in chunk for s in g["samples"]]), torch.tensor(group), len(chunk)


def boss_report(model, groups, make) -> dict:
    """On held-out boss sims: how often V's best option is the sims' best (decisions with one best only), and the
    correlation of V's centred values with the targets."""
    agree = n = 0
    vs, ts = [], []
    with torch.no_grad():
        for g in groups:
            b = make(g["samples"])
            values = expected_floor(torch.sigmoid(model(*b.inputs())), b.start).cpu()
            vs.append(values - values.mean())
            ts.append(torch.tensor([s["boss_target"] for s in g["samples"]], dtype=torch.float64))
            scores = [s["boss_score"] for s in g["samples"]]
            if scores.count(max(scores)) == 1:
                n += 1
                agree += scores[int(values.argmax())] == max(scores)
    return {"boss_decisions": n, "boss_agree": agree / max(n, 1), "boss_corr": pearson(torch.cat(vs), torch.cat(ts))}


def door_report(model, doors, make) -> dict:
    """On held-out doors, per act: the correlation of the sims' boss scores with the second head's, and with V's chance
    to live through the boss floor."""
    report = {}
    with torch.no_grad():
        for act, floor in data.BOSS_FLOORS.items():
            samples = [s for g in doors if g["act"] == act for s in g["samples"]]
            if len(samples) < 3:
                continue
            logits, aux = model.both(*make(samples).inputs())
            target = torch.tensor([s["boss_score"] for s in samples], dtype=torch.float64)
            head = torch.sigmoid(aux[:, BOSS_AT]).double().cpu()
            alive = 1 - torch.sigmoid(logits[:, floor]).double().cpu()
            report[str(act)] = {
                "samples": len(samples),
                "corr_head": pearson(head, target),
                "corr_v": pearson(alive, target),
            }
    return {"door_corr": report}


def explore_report(model, groups, make) -> dict:
    """The self-play temperature for V: the one at which V would explore the held-out logged decisions as often as
    the logs' brains did (the chance of drawing an option more than the temperature / 100 below the best), so that the
    next generation explores as this one did."""
    valued, logs_explored = [], 0.0
    with torch.no_grad():
        for i in range(0, len(groups), SIDE_BATCH):
            chunk = groups[i : i + SIDE_BATCH]
            b, group, n = grouped(make, chunk)
            values = expected_floor(torch.sigmoid(model(*b.inputs())), b.start).cpu()
            logged = torch.tensor([v for g in chunk for v in g["values"]], dtype=torch.float64)
            t = torch.tensor([g["temperature"] for g in chunk for _ in g["values"]], dtype=torch.float64)
            logs_explored += float(explored(logged, t, group, n).sum())
            valued.append((values, group, n))

    def share(t: float) -> float:
        return sum(float(explored(v, torch.full_like(v, t), g, n).sum()) for v, g, n in valued) / len(groups)

    # The share grows with the temperature: bisect on its log.
    target, lo, hi = logs_explored / len(groups), math.log(1e-6), math.log(10.0)
    for _ in range(40):
        mid = (lo + hi) / 2
        lo, hi = (mid, hi) if share(math.exp(mid)) < target else (lo, mid)
    return {
        "explore_decisions": len(groups),
        "explore_share_logs": target,
        "explore_temperature": math.exp((lo + hi) / 2),
    }


@script.command(help=__doc__)
def main(
    list_: Annotated[str, typer.Option("--list", help="the list pair PREFIX-logs.txt and PREFIX-recs.txt")],
    name: Annotated[str, typer.Option(help="the net's name, as the champion will show it")],
    out: Annotated[str | None, typer.Option(help="where the file goes (default: weights/CHARACTER/NAME.json)")] = None,
    character: Annotated[str, typer.Option(help="the character whose runs these are")] = "SILENT",
    boss_sims: Annotated[str | None, typer.Option(help="boss sims (`bosslab.py collect`), comma-separated")] = None,
    boss_afterstates: Annotated[
        str | None, typer.Option(help="the afterstates (`afterstates`) the boss sims were played from")
    ] = None,
    boss_states: Annotated[
        str | None, typer.Option(help="door states (`bosslab.py states`, collected), comma-separated")
    ] = None,
    door_logs: Annotated[
        str | None, typer.Option(help="boss gauntlet logs (`run --auto-win`) whose door states --boss-states holds")
    ] = None,
    afterstates: Annotated[
        str | None, typer.Option(help="the logged decisions' options (`afterstates`), for explore_temperature")
    ] = None,
    gauntlet: Annotated[bool, typer.Option(help="the boss gauntlet's V, on its runs (run --gauntlet) alone")] = False,
    seed: int = 0,
) -> None:
    out_file = out_path(name, out, character)
    if bool(boss_sims) != bool(boss_afterstates):
        raise LearnError("--boss-sims and --boss-afterstates go together")
    if gauntlet and (boss_sims or boss_states or door_logs or afterstates):
        raise LearnError("--gauntlet trains on the gauntlet's runs alone: no boss sims, door states or afterstates")
    torch.manual_seed(seed)
    random.seed(seed)
    device = "cuda" if torch.cuda.is_available() else "cpu"

    logs, records = common.lists(list_)
    samples, runs, views = data.read_runs(logs, records, character, FLOORS, gauntlet)
    train = [s for s in samples if not data.held_out(s["run"])]
    test = [s for s in samples if data.held_out(s["run"])]
    if not train or not test:
        raise LearnError(f"{len(train)} train and {len(test)} held-out samples: too few runs")
    worth = data.boss_worth(runs, FLOORS)
    bosses = data.read_boss_sims(boss_sims, boss_afterstates, views, FLOORS, worth) if boss_sims else []
    door_views = views | data.read_views(door_logs) if door_logs else views
    doors = data.read_boss_states(boss_states, door_views, FLOORS) if boss_states else []
    options = data.read_options(afterstates, views, FLOORS) if afterstates else []
    train_bosses, test_bosses = (
        [g for g in bosses if not data.held_out(g["run"])],
        [g for g in bosses if data.held_out(g["run"])],
    )
    train_doors, test_doors = (
        [g for g in doors if not data.held_out(g["run"])],
        [g for g in doors if data.held_out(g["run"])],
    )
    test_options = [g for g in options if data.held_out(g["run"])]
    logger.info(
        "{} decisions of {} runs ({} held out), {} boss sim decisions, {} doors, {} logged decisions with options",
        len(samples),
        len({s["key"] for s in samples}),
        len({s["key"] for s in test}),
        len(bosses),
        len(doors),
        len(options),
    )

    tables = vocab(train)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
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

    # The boss sims are fitted in units of their targets' mean square, the doors in units of their scores' variance,
    # so the weights do not depend on the labels' scale.
    boss_scale = max(
        1e-4,
        sum(s["boss_target"] ** 2 * s["weight"] for g in train_bosses for s in g["samples"])
        / max(1, len(train_bosses)),
    )
    door_samples = [s for g in train_doors for s in g["samples"]]
    door_mean = sum(s["boss_score"] * s["weight"] for s in door_samples) / max(1, len(train_doors))
    door_scale = max(
        1e-4, sum((s["boss_score"] - door_mean) ** 2 * s["weight"] for s in door_samples) / max(1, len(train_doors))
    )

    def boss_loss(model, chunk):
        """Per decision: V's centred values against the targets, plus the second head's boss score."""
        b, group, n = chunk
        logits, aux = model.both(*b.inputs())
        values = centred(expected_floor(torch.sigmoid(logits), b.start), group.to(device), n)
        fit = ((values - b.boss_target) ** 2 * b.weight).sum() / boss_scale
        return (fit + AUX_WEIGHT * (aux_loss(aux, b) * b.weight).sum()) / n

    def door_loss(model, chunk):
        """Per door: the second head's boss scores against the sims'."""
        b, _, n = chunk
        _, aux = model.both(*b.inputs())
        return (aux_loss(aux, b) * b.weight).sum() / door_scale / n

    def held_side(model, groups, loss):
        with torch.no_grad():
            return sum(float(loss(model, grouped(make, [g]))) for g in groups) / max(1, len(groups))

    # Baseline: per floor, the hazard the training runs show, whatever the state.
    alive, deaths = torch.zeros(FLOORS, dtype=torch.float64), torch.zeros(FLOORS, dtype=torch.float64)
    for b in batches(train, False):
        m, wt = b.label_mask.double().cpu(), b.weight.double().cpu()[:, None]
        alive, deaths = alive + (m * wt).sum(0), deaths + (b.target.double().cpu() * m * wt).sum(0)
    base_hazard = ((deaths + 0.5) / (alive + 1)).clamp(1e-4, 1 - 1e-4)
    base_logits = torch.log(base_hazard / (1 - base_hazard)).float().to(device)
    # The gauntlet's baseline share: the mean share taken where the training runs ended.
    ended = [s for s in train if s["kind"] == "died"]
    base_share = min(max(sum(s["share"] * s["weight"] for s in ended) / max(1e-9, sum(s["weight"] for s in ended)), 1e-4), 1 - 1e-4)
    base_share_logit = math.log(base_share / (1 - base_share)) if gauntlet else 0.0

    def evaluate(model):
        total, aux_total, rows = 0.0, 0.0, []
        with torch.no_grad():
            for b in test_batches:
                if model:
                    logits, aux, shares = model.heads(*b.inputs())
                    aux_total += float((aux_loss(aux, b) * b.weight).sum())
                else:
                    logits = base_logits.expand(len(b.weight), -1)
                    shares = torch.full_like(logits, base_share_logit) if gauntlet else None
                total += float((hazard_loss(logits, b) * b.weight).sum())
                if shares is None:
                    rows.append(expected_floor(torch.sigmoid(logits), b.start))
                else:
                    total += float((share_loss(shares, b) * b.weight).sum())
                    rows.append(expected_progress(torch.sigmoid(logits), torch.sigmoid(shares), b.start))
        return total / test_weight, torch.cat(rows), aux_total / test_weight

    model = Net({t: len(ids) for t, ids in tables.items()}, FLOORS, shares=gauntlet).to(device)
    # The net starts from the baseline and learns how the state moves each floor's hazard away from it.
    with torch.no_grad():
        model.head.bias.copy_(base_logits)
        if gauntlet:
            model.share_head.bias.fill_(base_share_logit)
    opt = torch.optim.AdamW(model.parameters(), lr=LR, weight_decay=WEIGHT_DECAY)
    boss_stream = stream(train_bosses, lambda c: grouped(make, c))
    door_stream = stream(train_doors, lambda c: grouped(make, c))
    best, best_state, stale = math.inf, None, 0
    for epoch in range(EPOCHS):
        model.train()
        for b in batches(train, True):
            logits, aux, shares = model.heads(*b.inputs())
            per_sample = hazard_loss(logits, b) + AUX_WEIGHT * aux_loss(aux, b)
            if shares is not None:
                per_sample = per_sample + share_loss(shares, b)
            loss = (per_sample * b.weight).sum() / b.weight.sum()
            if train_bosses:
                loss = loss + BOSS_WEIGHT * boss_loss(model, next(boss_stream))
            if train_doors:
                loss = loss + DOOR_WEIGHT * door_loss(model, next(door_stream))
            opt.zero_grad()
            loss.backward()
            opt.step()
        model.eval()
        held, _, held_aux = evaluate(model)
        held_boss = held_side(model, test_bosses, boss_loss) if test_bosses else 0.0
        held_door = held_side(model, test_doors, door_loss) if test_doors else 0.0
        total = held + BOSS_WEIGHT * held_boss + DOOR_WEIGHT * held_door
        logger.info(
            "epoch {}: held-out loss {:.4f}, next combats {:.4f}, boss sims {:.4f}, doors {:.4f}",
            epoch,
            held,
            held_aux,
            held_boss,
            held_door,
        )
        if total < best - 1e-4:
            best, best_state, stale = total, {k: v.clone() for k, v in model.state_dict().items()}, 0
        else:
            stale += 1
            if stale >= PATIENCE:
                break
    model.load_state_dict(best_state)
    model.eval()

    # The report: held-out loss against the baseline, and how well the expected floor predicts the floor reached.
    held, predicted, held_aux = evaluate(model)
    base_loss, base_predicted, _ = evaluate(None)
    # The gauntlet's actual is its progress: the floor, plus the share taken where the run ended.
    actual = torch.tensor(
        [float(s["floor"]) + (s["share"] if gauntlet and s["kind"] == "died" else 0.0) for s in test],
        dtype=torch.float64,
        device=predicted.device,
    )
    weights = torch.tensor([s["weight"] for s in test], dtype=torch.float64, device=predicted.device)

    def r2(pred):
        m = (weights * actual).sum() / weights.sum()
        return float(1 - (weights * (actual - pred) ** 2).sum() / (weights * (actual - m) ** 2).sum())

    report = {
        "runs": len({s["key"] for s in samples}),
        "held_out_runs": len({s["key"] for s in test}),
        "samples": len(samples),
        "won": sum(1 for r in runs if r["outcome"] == "won"),
        "vocab": {t: len(ids) for t, ids in tables.items()},
        "held_out_loss": held,
        "held_out_loss_floor_only": base_loss,
        "held_out_next_combats": held_aux,
        "r2_held_out": r2(predicted),
        "r2_held_out_floor_only": r2(base_predicted),
    }
    if bosses:
        report |= {"boss_sims": len(bosses), "held_out_boss_sims": len(test_bosses), "boss_worth": worth}
        report |= {"held_out_boss_loss": held_side(model, test_bosses, boss_loss)}
        report |= boss_report(model, test_bosses, make)
    if doors:
        report |= {"doors": len(doors), "held_out_doors": len(test_doors)}
        report |= {"held_out_door_loss": held_side(model, test_doors, door_loss)}
        report |= door_report(model, test_doors, make)
    if test_options:
        report |= explore_report(model, test_options, make)
    print(json.dumps(report))

    export.write(export.net_file(name, character, FLOORS, tables, mean, std, model, test, report, seed), out_file)
    logger.info("wrote {}", out_file)


if __name__ == "__main__":
    common.run(script)
