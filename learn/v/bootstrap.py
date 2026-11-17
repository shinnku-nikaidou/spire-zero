"""Multi-step targets for V: a training decision labelled with what happened over the next floors, and from the run's
state some floors on with where a frozen V says the run ends from there (plans/2026-11-15-1809-v-multistep-targets.md).

A run's outcome is one label that all its ~150 decisions share. Here a decision on floor f keeps what happened up to
s', its run's first logged decision on floor f + HORIZON or later, and from s' on is labelled with the hazards q a
frozen V gives at s': Batch makes them the targets from s' on, each floor weighed by the frozen V's chance that the run
is still alive there. That is today's loss averaged over the frozen V's distribution of where the run ends from s', so
hazards of 0 and 1 at the run's real end give today's label back. A decision whose run has no decision HORIZON floors
on (it ended before) keeps today's label; held-out decisions always do, so every score still reads real outcomes.

The frozen V must not have trained on the run it labels: V learns its training runs' outcomes within one pass, so its
value at a later state of such a run would hand the outcome back. So the training runs are cut in two folds by run id
(the held-out runs, id % 5 == 0, are in neither), a V is trained on each fold alone (`v.train --fold`), and each fold's
runs are labelled by the other fold's V.

An SL run's event option played out as NoSL plays it (v/data.py) is labelled the same way from where its line left the
room (`later`), not from its run's later decisions, which SL played: by the other fold's V, and for a held-out run by
the mean of both, neither of which saw it (`attach_events`).
"""

import array
import bisect
import functools

import torch

from common import LearnError
from v import export
from v.net import Batch, Net
from workers import built

FOLDS = {"A": (1, 2), "B": (3, 4)}  # a training run's id % 5


def fold(run_id: str) -> str | None:
    """The fold of a training run; None for a held-out one."""
    rest = int(run_id, 16) % 5
    return next((f for f, rests in FOLDS.items() if rest in rests), None)


def later(samples: list[dict], horizon: int) -> list[int | None]:
    """For each sample, the index in samples of its s': the first decision of its run (the decision's own view, not a
    map move's afterstate) on a floor HORIZON or more past the sample's; None if the run has none."""
    by_run = {}
    for k, s in enumerate(samples):
        by_run.setdefault(s["key"], []).append(k)
    out = [None] * len(samples)
    for key, ks in by_run.items():
        own = sorted((samples[k]["decision"], k) for k in ks if samples[k]["own"])
        starts = [samples[k]["start"] for _, k in own]
        if starts != sorted(starts):
            raise LearnError(f"run {key[1]} of log {key[0]} goes down a floor between its decisions")
        for k in ks:
            if samples[k].get("event") in ("left", "ended"):  # an SL line's own end, not its run's (attach_events)
                continue
            j = bisect.bisect_left(starts, samples[k]["start"] + horizon)
            out[k] = own[j][1] if j < len(own) else None
    return out


def frozen(files: list[dict], tables: dict, floors: int, device: str) -> dict[str, tuple[dict, Net]]:
    """The frozen V's by the fold each trained on, one for each fold, as models on DEVICE; refused unless each was
    trained on one fold (`trained.fold`), the two on different folds, with the game's tables and FLOORS floors."""
    nets = {}
    for f in files:
        which = f.get("trained", {}).get("fold")
        if which not in FOLDS:
            raise LearnError(f"{f['name']} was not trained on one fold (v.train --fold), so it may know any run")
        if which in nets:
            raise LearnError(f"{f['name']} and {nets[which][0]['name']} were both trained on fold {which}")
        if f["vocab"] != tables or f["floors"] != floors:
            raise LearnError(f"{f['name']} holds other tables or floors than this training")
        model = Net({t: len(ids) for t, ids in tables.items()}, floors)
        model.load_state_dict(export.weights(f, model))
        nets[which] = (f, model.to(device).eval())
    if set(nets) != set(FOLDS):
        raise LearnError(f"a frozen V for each fold is needed, got {sorted(nets)}")
    return nets


def attach(train: list[dict], horizon: int, nets: dict, index: dict, floors: int, batch: int, device: str,
           workers: int) -> int:  # fmt: skip
    """Gives each training sample with an s' its `boot`: the floor of s' and the hazards there of the V that did not
    train on its run (each on its own state scaling), which Batch turns into its targets. Returns how many got one."""
    points = later(train, horizon)
    need = sorted({j for j in points if j is not None})
    hazards = {}
    for which, (f, model) in nets.items():
        mine = [j for j in need if fold(train[j]["run"]) not in (which, None)]
        mean = torch.tensor(f["state"]["mean"], dtype=torch.float64)
        std = torch.tensor(f["state"]["std"], dtype=torch.float64)
        build = functools.partial(Batch, index=index, mean=mean, std=std, floors=floors, device="cpu")
        cuts = [mine[i : i + batch] for i in range(0, len(mine), batch)]
        parts = ([train[j] for j in cut] for cut in cuts)
        with torch.no_grad():
            for cut, b in zip(cuts, built(build, parts, device, workers), strict=True):
                for j, row in zip(cut, torch.sigmoid(model(*b.inputs())).float().cpu(), strict=True):
                    # an array, not a tensor: the batch builders' parts go by multiprocessing's pickler, which would
                    # send each tensor through shared memory
                    hazards[j] = array.array("f", row.tolist())
    if missing := [j for j in need if j not in hazards]:
        raise LearnError(f"{len(missing)} states of runs in no fold (run {train[missing[0]]['run']}): held-out runs?")
    for s, j in zip(train, points, strict=True):
        if j is not None:
            s["boot"] = (train[j]["start"], hazards[j])
    return len(points) - points.count(None)


def attach_events(samples: list[dict], nets: dict, index: dict, floors: int, batch: int, device: str, workers: int) -> int:
    """Gives each event option's sample whose line left the room (`later`, v/data.py) its `boot`: the floor where the
    line ended and the hazards there of the frozen V that did not train on its run, the mean of both for a held-out
    run. Returns how many got one."""
    mine = [s for s in samples if "later" in s]
    if not mine:
        return 0
    states = [{"tokens": s["later"]["tokens"], "state": s["later"]["state"], "start": s["later"]["start"],
               "end": s["later"]["start"], "kind": "censored", "weight": 1.0, "next": []} for s in mine]  # fmt: skip
    rows = {}
    for which, (f, model) in nets.items():
        mean = torch.tensor(f["state"]["mean"], dtype=torch.float64)
        std = torch.tensor(f["state"]["std"], dtype=torch.float64)
        build = functools.partial(Batch, index=index, mean=mean, std=std, floors=floors, device="cpu")
        cuts = [list(range(i, min(i + batch, len(states)))) for i in range(0, len(states), batch)]
        with torch.no_grad():
            got = []
            for b in built(build, ([states[k] for k in cut] for cut in cuts), device, workers):
                got.append(torch.sigmoid(model(*b.inputs())).float().cpu())
        rows[which] = torch.cat(got)
    for k, s in enumerate(mine):
        which = fold(s["run"])
        h = rows[other(which)][k] if which is not None else torch.stack([r[k] for r in rows.values()]).mean(0)
        s["boot"] = (s["later"]["start"], array.array("f", h.tolist()))
    return len(mine)


def other(which: str) -> str:
    """The fold whose V did not train on fold WHICH's runs."""
    return next(f for f in FOLDS if f != which)
