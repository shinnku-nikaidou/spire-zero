"""V's samples (docs: macro-brain/value §3): what was measured, never what someone judged.

Every macro decision of the decision logs (`run --log`) is a sample: its run view, and from the run records
(`run --records`, joined by run id) where the run ended: died on a floor, won, or stopped (censored). Its second head
learns the HP each of the run's next combats cost. Each run weighs the same however many decisions it has, and every
fifth run (by run id) is held out, for early stopping and the report. Every character's runs are read: one V serves
them all. A run's map moves give a second sample each, the chosen point's afterstate (encode.entered), with the run's
own outcome: what V reads when it values a map option, which no logged view shows.
"""

import json

from common import read_lines, runs_by_log
from v import encode

AUX = 6  # the next combats whose HP cost the second head learns


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def sample(run: str, view: dict, floors: int, reached: int | None = None, kind: str = "censored") -> dict:
    """A run view as a sample: died on floor reached, won, or lived through the floor before reached (censored); with
    no reach, it has no hazard label."""
    start = min(max(view["floor"], 0), floors - 1)
    end = start if reached is None else max(start, min(reached, floors - 1))
    return {
        "run": run,
        "view": view,
        "tokens": encode.tokens(view),
        "state": encode.state(view),
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


def read_runs(logs: str, records: str, floors: int) -> tuple[list[dict], list[dict]]:
    """The logged decisions as samples, and the run records.

    A sample keeps its tokens and state, not its view: as Python objects a view is ~54 KB, ten times those (2026-10-30:
    plan v6 generation 3's 2.16M decisions took ~117 GB a training, now 21 GB); a held-out one keeps it as JSON, for
    the net file's test runs."""
    samples, runs_seen = [], []
    for i, (log, runs) in enumerate(runs_by_log(logs, records)):
        runs = {k: r for k, r in runs.items() if not r.get("failure")}
        runs_seen += runs.values()
        for e in read_lines(log, '"run_view"'):
            r = runs.get(e["run"])
            if r is None or "run_view" not in e:
                continue
            view = e["run_view"]
            kind = {"won": "won", "lost": "died"}.get(r["outcome"], "censored")
            s = sample(e["run"], view, floors, r["floor"], kind)
            nxt = next_combats(r, view["floor"])
            more = []
            if e.get("surface") == "map" and e["chosen"].startswith("map:"):
                more.append(sample(e["run"], encode.entered(view, e["chosen"]), floors, r["floor"], kind))
            for x in [s, *more]:
                if held_out(e["run"]):
                    x["view_json"] = json.dumps(x["view"])
                del x["view"]
                samples.append(x | {"key": (i, e["run"]), "floor": r["floor"], "next": nxt})
    per_run = {}
    for s in samples:
        per_run[s["key"]] = per_run.get(s["key"], 0) + 1
    for s in samples:
        s["weight"] = 1.0 / per_run[s["key"]]
    return samples, runs_seen

