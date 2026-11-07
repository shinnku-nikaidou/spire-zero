"""The combat net's samples (docs: learning/combat-net §3): each round's start in the decision logs, the state a search leaf
stands in, with how its combat ended.

A sample is the first combat decision of a round, with the public record of the enemies' moves before it (Harness's
history, rebuilt from the earlier rounds' observations as Dispatcher builds it). Its targets: a death, or the share of max
HP still to be lost in one of BUCKETS buckets spanning [LOSS_MIN, 1) (below 0: a combat that healed), and after a win, per
potion held, whether it is gone; relative to the state, so the HP and potions it has now count in full.

Every fifth run (by run id) is held out, for early stopping and the report.

The endings are those of the policy that played, so only logs of the --env given are read.
"""

import json
import re
import zlib
from collections import Counter

from combat import encode
from common import LearnError, runs_by_log

BUCKETS = 50  # buckets of the HP share lost from the state to a win
LOSS_MIN = -0.25  # the lowest bucket's bottom: a share healed
WIDTH = (1 - LOSS_MIN) / BUCKETS
POTION_GROUPS = 5  # groups of BUCKETS / POTION_GROUPS buckets in a row, each with its own chances of the potions going


def potion_group(cls: int) -> int:
    """The group of buckets a win's ending class (1 to BUCKETS) falls in, whose potion chances go with it."""
    return (cls - 1) // (BUCKETS // POTION_GROUPS)
KEEP = 1000  # one held-out round start in this many keeps its observation, for the net file's test states
HEAD = re.compile(r'\{"run":"(\w+)","decision_id":\d+,"env":"(\w+)","surface":"(\w+)"')


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def combats(path: str, env: str) -> dict[str, list[dict]]:
    """Per run, its combats in order: the observation of each round's start and the run's view after the combat. A combat
    closes at the next macro decision, or when the round goes back, as Dispatcher's history does. A line of another env
    fails."""
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
            r = runs.setdefault(run, {"fights": [], "open": False})
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if surface != "combat":
                r["open"] = False
                view = e.get("run_view")
                if view is not None and r["fights"] and r["fights"][-1]["after"] is None:
                    r["fights"][-1]["after"] = view
                continue
            obs = e["obs"]
            fight = r["fights"][-1] if r["open"] else None
            if fight is None or obs["round"] < fight["starts"][-1]["round"]:
                fight = {"starts": [], "after": None}
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


def potions_used(obs: dict, after: dict) -> list[int]:
    """Per potion held at this state, in slot order, whether it is gone from the first run view after the fight, each
    copy left there keeping one. A fight's actions miss a Fairy spent with none, and the last round's start a potion
    drunk after it; a potion gained later is no potion of this state."""
    left = Counter(after["potions"])
    used = []
    for p in obs["potions"]:
        if p is not None:
            used.append(0 if left[p] > 0 else 1)
            left[p] -= 1
    return used


def share(hp: int, max_hp: int) -> float:
    return hp / max(1, max_hp)


def ending(obs: dict, end: dict) -> int:
    """The class of a combat's end from a state: 0 for a death, else 1 + the bucket of the HP share lost since."""
    if end["died"]:
        return 0
    lost = share(obs["player"]["hp"], obs["player"]["max_hp"]) - share(end["hp"], end["max_hp"])
    return 1 + max(0, min(BUCKETS - 1, int((lost - LOSS_MIN) / (1 - LOSS_MIN) * BUCKETS)))


def sample(run: str, obs: dict, history: list, end: dict, after: dict | None) -> dict:
    """A round's start; only a few held-out ones keep the observation and the history they were made from."""
    now = share(obs["player"]["hp"], obs["player"]["max_hp"])
    s = {
        "run": run,
        "tokens": encode.tokens(obs, history),
        "class": ending(obs, end),
        "potion_used": [0 for p in obs["potions"] if p is not None] if end["died"] else potions_used(obs, after),
        "lost": None if end["died"] else now - share(end["hp"], end["max_hp"]),
        "share": now,
        "room": obs.get("room") or "<none>",
        "act": obs["act"],
        "encounter": obs.get("encounter") or "<none>",
        "round": obs["round"],
    }
    if held_out(run) and zlib.crc32(f"{run}:{obs['encounter']}:{obs['round']}:{obs['player']['hp']}".encode()) % KEEP == 0:
        s |= {"obs": obs, "history": history}
    return s


def read(logs: str, records: str, env: str) -> tuple[list[dict], int]:
    """The round starts of the logs' combats as samples, and how many runs were skipped: those whose combats do not
    match their record's (a log cut short, a combat logged apart). The run's last combat in a lost run counts as a death
    even with no `end` in its record; a win with no run view after it is left out."""
    samples, skipped = [], 0
    for i, (log, runs) in enumerate(runs_by_log(logs, records)):
        for run, fights in combats(log, env).items():
            r = runs.get(run)
            if r is None or r.get("failure"):
                continue
            # a combat the run died in before its first decision (entered at 0 HP) has no round start in the log
            ends = [c for c in r["combats"] if c.get("hp") != 0]
            if len(fights) != len(ends) or any(
                b["starts"][0]["encounter"] != f["encounter"] for b, f in zip(fights, ends, strict=True)
            ):
                skipped += 1
                continue
            for k, (fight, f) in enumerate(zip(fights, ends, strict=True)):
                end = f.get("end") or ({"died": True} if k == len(ends) - 1 and r.get("outcome") == "lost" else None)
                if end is None or (not end["died"] and fight["after"] is None):
                    continue
                history = {}
                for obs in fight["starts"]:
                    past = [{"uid": h["uid"], "id": h["id"], "turns": list(h["turns"])} for h in history.values()]
                    samples.append(sample(run, obs, past, end, fight["after"]) | {"key": (i, run)})
                    witness(history, obs)
    return samples, skipped
