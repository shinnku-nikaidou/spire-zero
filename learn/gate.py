"""A V's SL report and gate: what the runs reached, and, against the champion's runs on the same seeds, the paired
difference and the verdict.

From the run records: the runs, the runs won, their mean floor, each act's boss reached and beaten, the elite fights
per run and the deaths in them, and per act where the lost runs died (the room of the combat, or outside combat). From
the decision logs (--logs): the rest sites' choices by the HP share they were made at, the map's choices between a
monster and a rest site by the same shares, and what the shops sold (a purchase is told by what the next decision's run
gained: a relic, a card, a potion). A time-boxed job finished only some runs; a log's runs without a record are left
out.

--against pairs the runs with the champion's by seed, on the seeds both finished: the challenger less the champion,
the mean, its standard error and the 95 % one-sided lower bound, of the wins (1 or 0) and the floors; promote when the
bound of the wins is above 0.

    uv run python gate.py --records R1,R2 --logs L1,L2 [--against C1,C2]
"""

import json
import math
from typing import Annotated

import typer

import common
from common import LearnError, read_lines

script = common.app()

HP_BANDS = [(0.0, 0.4), (0.4, 0.7), (0.7, 0.9), (0.9, 1.01)]  # shares of max HP, the rest sites' report


def mean_se(xs: list[float]) -> tuple[float, float]:
    n = len(xs)
    if n == 0:
        return math.nan, math.nan
    m = sum(xs) / n
    return m, (math.sqrt(sum((x - m) ** 2 for x in xs) / (n - 1) / n) if n > 1 else math.nan)


def read_records(paths: str) -> list[dict]:
    """The finished runs."""
    return [r for r in read_lines(paths) if not r.get("failure")]


def runs_report(runs: list[dict]) -> dict:
    """The records' numbers."""
    floor, floor_se = mean_se([float(r["floor"]) for r in runs])
    acts = {}
    for act in range(3):
        bosses = [c for r in runs for c in r["combats"] if c["act"] == act and c["room"] == "Boss"]
        beaten = sum(1 for c in bosses if c.get("end") and not c["end"]["died"])
        acts[str(act)] = {"reached": len(bosses), "beaten": beaten, "rate": beaten / len(bosses) if bosses else None}
    elites = [c for r in runs for c in r["combats"] if c["room"] == "Elite"]
    died_in = {}
    for r in runs:
        last = (r["combats"] or [{}])[-1]
        if r["outcome"] == "lost":
            where = (
                last["room"] if last.get("end", {}).get("died") and last["floor"] == r["floor"] else "outside combat"
            )
            act = died_in.setdefault(str(r["act"]), {})
            act[where] = act.get(where, 0) + 1
    return {
        "runs": len(runs),
        "floor": floor,
        "floor_se": floor_se,
        "won": sum(1 for r in runs if r["outcome"] == "won"),
        "boss": acts,
        "elites_per_run": len(elites) / max(1, len(runs)),
        "elite_deaths": sum(1 for c in elites if c.get("end", {}).get("died")),
        "died_in": dict(sorted(died_in.items())),
    }


def band(view: dict) -> str:
    share = view["hp"] / max(1, view["max_hp"])
    lo, hi = next(b for b in HP_BANDS if b[0] <= share < b[1])
    return f"{lo:.0%}-{min(hi, 1):.0%}"


def bought(before: dict, after: dict) -> str:
    """What a shop purchase gave, from the run views before it and at the next decision."""
    if len(after["relics"]) > len(before["relics"]):
        return "relic"
    if len(after["deck"]) > len(before["deck"]):
        return "card"
    if sum(p is not None for p in after["potions"]) > sum(p is not None for p in before["potions"]):
        return "potion"
    return "other"


def options(view: dict) -> dict[str, str]:
    """A map decision's options, "col,row" to the point's type: where the last point visited leads."""
    points = {(p["col"], p["row"]): p for p in view["map"]["points"]}
    visited = view["map"]["visited"]
    here = points.get((visited[-1]["col"], visited[-1]["row"])) if visited else None
    nxt = [points.get((n["col"], n["row"])) for n in here["next"]] if here else []
    return {f"{p['col']},{p['row']}": p["type"] for p in nxt if p}


def logs_report(paths: str, runs: set[str]) -> dict:
    """The rest sites', the map's (a monster or a rest site) and the shops' choices of the logged runs that have a
    record."""
    rest, monster_or_rest, shop, last = {}, {}, {}, {}
    for e in read_lines(paths, '"run_view"'):
        if e["run"] not in runs or "run_view" not in e:
            continue
        view = e["run_view"]
        if e["run"] in last:
            prev = last.pop(e["run"])
            kind = bought(prev, view)
            shop[kind] = shop.get(kind, 0) + 1
        if e["surface"] == "rest" and e["chosen"].startswith("rest:"):
            choices = rest.setdefault(band(view), {})
            choices[e["chosen"][5:]] = choices.get(e["chosen"][5:], 0) + 1
        elif e["surface"] == "map" and e["chosen"].startswith("map:"):
            types = options(view)
            if {"Monster", "RestSite"} <= set(types.values()):
                taken = types.get(e["chosen"][4:], "other")
                choices = monster_or_rest.setdefault(band(view), {})
                choices[taken] = choices.get(taken, 0) + 1
        elif e["surface"] == "shop":
            if e["chosen"].startswith("buy:"):
                last[e["run"]] = view
            elif e["chosen"] in ("remove", "leave"):
                shop[e["chosen"]] = shop.get(e["chosen"], 0) + 1
    return {"rest": dict(sorted(rest.items())), "monster_or_rest": dict(sorted(monster_or_rest.items())), "shop": shop}


def paired(runs: list[dict], champion: list[dict]) -> dict:
    """The challenger less the champion on the seeds both finished: the wins and the floors; the verdict on the wins."""
    theirs = {r["seed"]: r for r in champion}
    both = [(r, theirs[r["seed"]]) for r in runs if r["seed"] in theirs]

    def diff(value) -> tuple[float, float, float]:
        m, se = mean_se([value(a) - value(b) for a, b in both])
        return m, se, m - 1.645 * se

    m, se, lower = diff(lambda r: float(r["outcome"] == "won"))
    floor, floor_se, _ = diff(lambda r: float(r["floor"]))
    return {
        "pairs": len(both),
        "won": sum(1 for a, _ in both if a["outcome"] == "won"),
        "champion_won": sum(1 for _, b in both if b["outcome"] == "won"),
        "wins_diff": m,
        "wins_diff_se": se,
        "wins_lower_95": lower,
        "floor_diff": floor,
        "floor_diff_se": floor_se,
        "promote": bool(lower > 0),
    }


@script.command(help=__doc__)
def main(
    records: Annotated[str, typer.Option(help="the V's run records, comma-separated")],
    logs: Annotated[str | None, typer.Option(help="their decision logs, for the rest sites, map and shops")] = None,
    against: Annotated[
        str | None, typer.Option(help="the champion's run records, to pair with")
    ] = None,
) -> None:
    runs = read_records(records)
    if not runs:
        raise LearnError(f"no finished run in {records}")
    report = runs_report(runs)
    if logs:
        report |= logs_report(logs, {r["run_id"] for r in runs})
    if against:
        report["against"] = paired(runs, read_records(against))
    print(json.dumps(report))


if __name__ == "__main__":
    common.run(script)
