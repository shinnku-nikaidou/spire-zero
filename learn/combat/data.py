"""The combat net's samples (docs: learning/combat-net §3): each round's start in the decision logs, the state a search leaf
stands in, with what V makes of how its combat ended.

A sample is the first combat decision of a round, with the public record of the enemies' moves before it (Harness's
history, rebuilt from the earlier rounds' observations as Dispatcher builds it). Its target is the fight's worth: V of the
run at the first macro decision after the fight, as a share of V at the last one before it, capped at WORTH_CAP; a death
is 0, and a fight that wins the run ends at V = 1. V reads the deck, the relics, the potions, the HP and the gold, so what
a fight spends is priced by what it means for the run. Every fifth run (by run id) is held out, for early stopping and the
report.

The endings are those of the policy that played, so each environment has its nets: only logs of the --env given are
read (SL's planner sees what NoSL's search must guess, and wins more from the same state).
"""

import json
import re
import zlib

from combat import encode
from common import LearnError, runs_by_log

WORTH_CAP = 4.0  # a run's chance can be next to nothing before a fight: 99 % of the wins lie below 3.4
KEEP = 1000  # one held-out round start in this many keeps its observation, for the net file's test states
HEAD = re.compile(r'\{"run":"(\w+)","decision_id":\d+,"env":"(\w+)","surface":"(\w+)"')


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def combats(path: str, env: str) -> dict[str, list[dict]]:
    """Per run, its combats in order: the observation of each round's start, the run's view before the combat and after
    it. A combat closes at the next macro decision, or when the round goes back, as Dispatcher's history does. A line of
    another env fails."""
    runs = {}
    with open(path) as f:
        for line in f:
            m = HEAD.match(line)
            if not m:
                continue
            run, line_env, surface = m.groups()
            if line_env != env:
                raise LearnError(f"{path} holds {line_env} decisions, not {env}: each environment has its combat nets")
            if surface == "card_choice":
                continue
            r = runs.setdefault(run, {"fights": [], "view": None, "open": False})
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if surface != "combat":
                r["open"] = False
                view = e.get("run_view")
                if view is not None:
                    r["view"] = view
                    if r["fights"] and r["fights"][-1]["after"] is None:
                        r["fights"][-1]["after"] = view
                continue
            obs = e["obs"]
            fight = r["fights"][-1] if r["open"] else None
            if fight is None or obs["round"] < fight["starts"][-1]["round"]:
                fight = {"starts": [], "before": r["view"], "after": None}
                r["fights"].append(fight)
                r["open"] = True
            if not fight["starts"] or fight["starts"][-1]["round"] != obs["round"]:
                fight["starts"].append(obs)
    return {run: r["fights"] for run, r in runs.items()}


def witness(history: dict, obs: dict):
    """As Dispatcher.Witness, on a round's first decision: each living enemy's intents, by uid."""
    for enemy in obs["enemies"]:
        if enemy.get("uid") is None or enemy.get("intents") is None or enemy["hp"] <= 0:
            continue
        history.setdefault(enemy["uid"], {"uid": enemy["uid"], "id": enemy.get("id"), "turns": []})["turns"].append(
            {"round": obs["round"], "intents": enemy["intents"]}
        )


def sample(run: str, obs: dict, history: list, worth: float) -> dict:
    """A round's start; only a few held-out ones keep the observation and the history they were made from."""
    s = {
        "run": run,
        "tokens": encode.tokens(obs, history),
        "worth": worth,
        "room": obs.get("room") or "<none>",
        "act": obs["act"],
        "encounter": obs.get("encounter") or "<none>",
        "round": obs["round"],
    }
    if held_out(run) and zlib.crc32(f"{run}:{obs['encounter']}:{obs['round']}:{obs['player']['hp']}".encode()) % KEEP == 0:
        s |= {"obs": obs, "history": history}
    return s


def read(logs: str, records: str, character: str, env: str, value, skip_seeds=frozenset()) -> tuple[list[dict], int]:
    """The round starts of the logs' combats as samples, and how many runs were skipped: those whose combats do not
    match their record's (a log cut short, a combat logged apart). `value` gives V of a list of run views. The runs of
    `skip_seeds` are left out. The run's last combat in a lost run counts as a death even with no `end` in its record."""
    samples, skipped = [], 0
    for i, (log, runs) in enumerate(runs_by_log(logs, records, character)):
        todo = []
        for run, fights in combats(log, env).items():
            r = runs.get(run)
            if r is None or r.get("failure") or r.get("seed") in skip_seeds:
                continue
            ends = r["combats"]
            if len(fights) != len(ends) or any(
                b["starts"][0]["encounter"] != f["encounter"] for b, f in zip(fights, ends, strict=True)
            ):
                skipped += 1
                continue
            for k, (fight, f) in enumerate(zip(fights, ends, strict=True)):
                last = k == len(ends) - 1
                end = f.get("end") or ({"died": True} if last and r.get("outcome") == "lost" else None)
                won_run = last and r.get("outcome") == "won"
                if end is None or fight["before"] is None or not (end["died"] or won_run or fight["after"]):
                    continue
                todo.append((run, fight, end["died"], won_run))
        before = value([t[1]["before"] for t in todo])
        after = value([t[1]["after"] for t in todo if not t[2] and not t[3]])
        next_after = iter(after)
        for (run, fight, died, won_run), v in zip(todo, before, strict=True):
            end_value = 0.0 if died else 1.0 if won_run else float(next(next_after))
            worth = min(WORTH_CAP, end_value / max(float(v), 1e-9))
            history = {}
            for obs in fight["starts"]:
                past = [{"uid": h["uid"], "id": h["id"], "turns": list(h["turns"])} for h in history.values()]
                samples.append(sample(run, obs, past, worth) | {"key": (i, run)})
                witness(history, obs)
    return samples, skipped
