"""V's samples (docs: macro-brain/value §3): what was measured, never what someone judged.

Every macro decision of the decision logs (`run --log`) is a sample: its run as V reads it, and from the run records
(`run --records`, joined by run id) where the run ended: died on a floor, won, or stopped (censored). Its second head
learns the HP each of the run's next combats cost. Each run weighs the same however many decisions it has, and every
fifth run (by run id) is held out, for early stopping and the report. Every character's runs are read: one V serves
them all. A run's map moves give a second sample each, the chosen point's afterstate, with the run's own outcome: what
V reads when it values a map option, which no logged view shows.

A run is read by the simulator's code, not here (common.Encoder): its tokens and its numbers, and a map move's
afterstate, are what RunNet feeds V in play.
"""

import json

from common import HEAD, LearnError, runs_by_log
from v import spec

AUX = 6  # the next combats whose HP cost the second head learns


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def sample(run: str, read: dict, floors: int, reached: int | None = None, kind: str = "censored") -> dict:
    """A run as V reads it (`read`: its tokens and its numbers by name, the encoder's) as a sample: died on floor
    reached, won, or lived through the floor before reached (censored); with no reach, it has no hazard label."""
    state = [float(read["state"][name]) for name in spec.STATE]
    start = min(max(int(read["state"]["floor"]), 0), floors - 1)
    end = start if reached is None else max(start, min(reached, floors - 1))
    return {
        "run": run,
        "tokens": read["tokens"],
        "state": state,
        "start": start,
        "end": end,
        "kind": kind,
        "next": [],
        "weight": 1.0,
    }


def next_combats(record: dict, floor: int) -> list[float]:
    """The share of max HP each of the run's next combats after the floor cost, at most AUX of them (a death costs
    what was left)."""
    costs = []
    combats = record.get("combats", [])
    for c in combats:
        if c["floor"] <= floor or len(costs) == AUX:
            continue
        # a death inside the player's own action (a card in hand at the turn's end, a play) leaves the fight no end
        end = c.get("end") or ({"died": True} if c is combats[-1] and record.get("outcome") == "lost" else {})
        lost = c["hp"] - (0 if end.get("died") else end.get("hp", c["hp"]))
        costs.append(min(max(lost / max(c["max_hp"], 1), 0.0), 1.0))
    return costs


def macro_lines(log: str, runs: dict) -> list[tuple[str, str]]:
    """The lines of a log that are macro decisions of the runs given, each with its run id; a line still being written
    is left out."""
    try:
        f = open(log)
    except OSError as e:
        raise LearnError(f"cannot read {log}: {e.strerror}") from e
    with f:
        heads = ((HEAD.match(line), line) for line in f)
        return [(m[1], line) for m, line in heads if m and m[1] in runs and '"run_view"' in line and line.endswith("\n")]


def read_runs(logs: str, records: str, floors: int, encoder) -> tuple[list[dict], list[dict]]:
    """The logged decisions as samples, and the run records.

    A sample keeps its tokens and state, not its view: as Python objects a view is ~54 KB, ten times those (2026-10-30:
    plan v6 generation 3's 2.16M decisions took ~117 GB a training, now 21 GB); a held-out decision keeps its view as
    JSON, for the net file's test runs."""
    samples, runs_seen = [], []
    for i, (log, runs) in enumerate(runs_by_log(logs, records)):
        runs = {k: r for k, r in runs.items() if not r.get("failure")}
        runs_seen += runs.values()
        lines = macro_lines(log, runs)
        for (run, line), read in zip(lines, encoder.runs([line for _, line in lines], log), strict=True):
            if read is None:
                continue
            r = runs[run]
            kind = {"won": "won", "lost": "died"}.get(r["outcome"], "censored")
            s = sample(run, read["run"], floors, r["floor"], kind)
            nxt = next_combats(r, int(read["run"]["state"]["floor"]))
            if held_out(run):
                s["view_json"] = json.dumps(json.loads(line)["run_view"])
            more = [sample(run, read["entered"], floors, r["floor"], kind)] if "entered" in read else []
            for x in [s, *more]:
                samples.append(x | {"key": (i, run), "floor": r["floor"], "next": nxt})
    per_run = {}
    for s in samples:
        per_run[s["key"]] = per_run.get(s["key"], 0) + 1
    for s in samples:
        s["weight"] = 1.0 / per_run[s["key"]]
    return samples, runs_seen
