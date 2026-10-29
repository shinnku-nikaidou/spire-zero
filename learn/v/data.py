"""Pure V's samples (docs: learning §2): what was measured, never what someone judged.

Every macro decision of the decision logs (`run --log`) is a sample: its run view, and from the run records
(`run --records`, joined by run id) where the run ended: died on a floor, won, or stopped (censored). Its second head
learns the HP each of the run's next combats cost. Each run weighs the same however many decisions it has, and every
fifth run (by run id) is held out, for early stopping and the report.

Two kinds of simulated boss fights (`bosslab.py`) can be added, both measured by fighting:
  - boss sims, at a card reward: each option's afterstate fought its act's boss with the same seeds; only the options'
    differences within a decision count, as floors (twice what beating the boss is worth, per unit of score);
  - door states: a run's whole loadout at an act boss's door, fought at a few HP levels; the second head learns the
    scores, which shapes the body only.

The afterstates of logged decisions (`afterstates`) serve the report alone: the self-play temperature at which V would
explore as often as the logs did. A boss gauntlet pilot's runs (`run --auto-win`) are refused: their outcomes are not
real; their logs serve only as the views of their door states (--door-logs).

The boss gauntlet's V (plan v6, `run --gauntlet`: plain monster combats won at their start, elites and bosses fought)
reads the gauntlet's runs only, and a whole run's V refuses them. Its target is the run's progress (`progress`): where
the run ended, as a death on that floor, with the share of the enemies' HP taken in the combat it ended in as the third
head's label; a won run ends on its last floor with its last boss all taken.
"""

import bosslab
from common import LearnError, read_lines, runs_by_log
from v import encode

AUX = 6  # the next combats whose HP cost the second head learns
BOSS_FLOORS = {0: 17, 1: 33, 2: 48}  # the floor of each act's boss fight


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
        "boss_score": None,
        "weight": 1.0,
    }


def next_combats(record: dict, floor: int, fought_only: bool = False) -> list[float]:
    """The share of max HP each of the run's next combats after the floor cost, at most AUX of them (a death costs
    what was left); with fought_only, the gauntlet's fought combats only (elites and bosses)."""
    costs = []
    for c in record.get("combats", []):
        if c["floor"] <= floor or len(costs) == AUX or (fought_only and c["room"] == "Monster"):
            continue
        end = c.get("end") or {}
        lost = c["hp"] - (0 if end.get("died") else end.get("hp", c["hp"]))
        costs.append(min(max(lost / max(c["max_hp"], 1), 0.0), 1.0))
    return costs


def death_share(record: dict) -> float:
    """The share of the enemies' HP taken in the combat a lost run died in; 0 when it died outside a combat."""
    last = (record.get("combats") or [{}])[-1]
    end = last.get("end") or {}
    if not end.get("died") or last["floor"] != record["floor"] or not end.get("enemy_max_hp"):
        return 0.0
    return min(max(1 - end["enemy_hp"] / end["enemy_max_hp"], 0.0), 1.0)


def progress(record: dict) -> float:
    """The boss gauntlet's progress (plan v6 §2): the floor reached, plus the share of the enemies' HP taken in the
    combat a lost run died in; a won run took all of its last boss's (+1)."""
    return record["floor"] + (1.0 if record["outcome"] == "won" else death_share(record))


def read_runs(
    logs: str, records: str, character: str, floors: int, gauntlet: bool = False
) -> tuple[list[dict], list[dict], dict]:
    """The logged decisions as samples, the run records, and each decision's view and brain by (run, decision id).
    With gauntlet, the boss gauntlet's runs, each ended where it ended (won or lost) with its share (`progress`)."""
    samples, runs_seen, views = [], [], {}
    for i, (log, runs) in enumerate(runs_by_log(logs, records, character)):
        runs = {k: r for k, r in runs.items() if not r.get("failure")}
        if any(r["config"].get("auto_win") for r in runs.values()):
            raise LearnError(f"{log}: a boss gauntlet pilot's runs (run --auto-win) say nothing of how a run fares")
        if any(bool(r["config"].get("gauntlet")) != gauntlet for r in runs.values()):
            raise LearnError(
                f"{log}: the boss gauntlet's V reads the gauntlet's runs (run --gauntlet) alone, a whole run's V none of them"
            )
        runs_seen += runs.values()
        for e in read_lines(log, '"run_view"'):
            r = runs.get(e["run"])
            if r is None or "run_view" not in e:
                continue
            view = e["run_view"]
            views[(e["run"], e["decision_id"])] = (view, e["brain"])
            if gauntlet:
                ended = r["outcome"] in ("won", "lost")
                s = sample(e["run"], view, floors, r["floor"], "died" if ended else "censored")
                s["share"] = 1.0 if r["outcome"] == "won" else death_share(r)
            else:
                s = sample(
                    e["run"], view, floors, r["floor"], {"won": "won", "lost": "died"}.get(r["outcome"], "censored")
                )
            nxt = next_combats(r, view["floor"], fought_only=gauntlet)
            samples.append(s | {"key": (i, e["run"]), "floor": r["floor"], "next": nxt})
    per_run = {}
    for s in samples:
        per_run[s["key"]] = per_run.get(s["key"], 0) + 1
    for s in samples:
        s["weight"] = 1.0 / per_run[s["key"]]
    return samples, runs_seen, views


def read_views(logs: str) -> dict:
    """Each logged decision's view and brain by (run, decision id), from logs whose runs are no samples: a boss
    gauntlet's, whose door states (--boss-states) need their views."""
    return {
        (e["run"], e["decision_id"]): (e["run_view"], e["brain"])
        for e in read_lines(logs, '"run_view"')
        if "run_view" in e
    }


def beat_boss(record: dict, act: int) -> bool:
    bosses = [c for c in record.get("combats", []) if c.get("act") == act and c.get("room") == "Boss"]
    return bool(bosses) and bool(bosses[-1].get("end")) and not bosses[-1]["end"].get("died")


def boss_worth(records: list[dict], floors: int) -> dict[int, float]:
    """Per act, what beating its boss is worth in floors: the mean floor reached by the runs that beat it, less the
    boss floor (where the runs that lost to it died)."""
    worth = {}
    for act, boss in BOSS_FLOORS.items():
        reached = [min(r["floor"], floors - 1) for r in records if beat_boss(r, act)]
        worth[act] = max(sum(reached) / len(reached) - boss, 0.0) if reached else 0.0
    return worth


def read_boss_sims(paths: str, afterstates: str, views: dict, floors: int, worth: dict) -> list[dict]:
    """The boss sims (`bosslab.py collect`): per decision, each option's afterstate (the logged view with the option's
    changes from `afterstates`) as a sample with no hazard label, its fights' mean boss score, and its target: the
    score less the decision's mean, times 2 * worth[act] floors per unit."""
    sims = [d for d in read_lines(paths) if (d["run"], d["decision_id"]) in views and d["act"] in BOSS_FLOORS]
    sims = [d for d in sims if sum(1 for o in d["options"] if o["fights"]) >= 2]
    wanted = {(d["run"], d["decision_id"]) for d in sims}
    options = {}
    for e in read_lines(afterstates):
        if (e["run"], e["decision_id"]) in wanted:
            options[(e["run"], e["decision_id"])] = {o["action"]: o for o in e["options"]}
    groups = []
    for d in sims:
        view = views[(d["run"], d["decision_id"])][0]
        after = options.get((d["run"], d["decision_id"]), {})
        group = []
        for o in d["options"]:
            if not o["fights"] or o["action"] not in after:
                continue
            a = bosslab.merged(view, after[o["action"]].get("after") or {})
            scores = [bosslab.score(f, a["max_hp"]) for f in o["fights"]]
            group.append(sample(d["run"], a, floors) | {"boss_score": sum(scores) / len(scores)})
        if len(group) >= 2:
            mean = sum(x["boss_score"] for x in group) / len(group)
            for x in group:
                x["weight"] = 1.0 / len(group)
                x["boss_target"] = 2 * worth[d["act"]] * (x["boss_score"] - mean)
            groups.append({"run": d["run"], "samples": group})
    if not groups:
        raise LearnError("no decision of --boss-sims has two options with fights and a logged view")
    return groups


def read_boss_states(paths: str, views: dict, floors: int) -> list[dict]:
    """The door states (`bosslab.py states`, collected): per door, the logged view at each HP level fought, as samples
    with no hazard label and their fights' mean boss score; the levels of a door share its weight."""
    doors = []
    for d in read_lines(paths):
        if (d["run"], d["decision_id"]) not in views or d["act"] not in BOSS_FLOORS:
            continue
        view = views[(d["run"], d["decision_id"])][0]
        group = []
        for o in d["options"]:
            if not o["fights"]:
                continue
            scores = [bosslab.score(f, d["max_hp"]) for f in o["fights"]]
            at = view | {"hp": o["hp"], "max_hp": d["max_hp"]}
            group.append(sample(d["run"], at, floors) | {"boss_score": sum(scores) / len(scores)})
        for x in group:
            x["weight"] = 1.0 / len(group)
        if group:
            doors.append({"run": d["run"], "act": d["act"], "samples": group})
    if not doors:
        raise LearnError("no door of --boss-states has fights and a logged view")
    return doors


def temperature(brain: str) -> float | None:
    """The macro exploration temperature a log's brain name carries ("l0 explore 0.25"), or None when it did not explore."""
    parts = brain.split()
    return float(parts[parts.index("explore") + 1]) if "explore" in parts else None


def read_options(paths: str, views: dict, floors: int) -> list[dict]:
    """The logged decisions with two options or more whose brain explored: each option's afterstate as a sample, the
    values the logging V gave them, and its exploration temperature (in its own units)."""
    groups = []
    for e in read_lines(paths):
        key = (e["run"], e["decision_id"])
        if len(e["options"]) < 2 or key not in views:
            continue
        view, brain = views[key]
        t = temperature(brain)
        if t is None:
            continue
        groups.append(
            {
                "run": e["run"],
                "samples": [sample(e["run"], bosslab.merged(view, o.get("after") or {}), floors) for o in e["options"]],
                "values": [o["value"] for o in e["options"]],
                "temperature": t,
            }
        )
    if not groups:
        raise LearnError("no decision of --afterstates has two options and a logged view of an exploring brain")
    return groups
