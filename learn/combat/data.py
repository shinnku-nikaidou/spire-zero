"""The combat net's samples (docs: learning §2): each round's start in the decision logs, the state a search leaf
stands in, with how its combat ended in the run records.

A sample is the first combat decision of a round, with the public record of the enemies' moves before it (Harness's
history, rebuilt from the earlier rounds' observations as Dispatcher builds it). Its target is the combat's end from
there: a death, or the share of max HP still to be lost in one of BUCKETS buckets spanning [LOSS_MIN, 1) (below 0: a
combat that healed), and the potions still to be used after a win. Both are relative to the state, so the HP and
potions it has now count in full. Every fifth run (by run id) is held out, for early stopping and the report.

The endings are those of the policy that played, so each environment has its nets: only logs of the --env given are
read (SL's planner sees what NoSL's search must guess, and wins more from the same state).
"""

import json
import re

from combat import encode
from common import LearnError, runs_by_log

BUCKETS = 50  # buckets of the HP share lost from the state to a win
LOSS_MIN = -0.25  # the lowest bucket's bottom: a share healed
HEAD = re.compile(r'\{"run":"(\w+)","decision_id":\d+,"env":"(\w+)","surface":"(\w+)"')


def held_out(run_id: str) -> bool:
    return int(run_id, 16) % 5 == 0


def combats(path: str, env: str) -> dict[str, list[list[dict]]]:
    """Per run, its combats in order, each the list of its combat decisions. A combat closes at the next macro
    decision, or when the round goes back, as Dispatcher's history does. A line of another env fails."""
    runs = {}
    with open(path) as f:
        for line in f:
            m = HEAD.match(line)
            if not m:
                continue
            run, line_env, surface = m.groups()
            if line_env != env:
                raise LearnError(f"{path} holds {line_env} decisions, not {env}: each environment has its combat nets")
            blocks = runs.setdefault(run, [[]])
            if surface == "card_choice":
                continue
            if surface != "combat":
                if blocks[-1]:
                    blocks.append([])
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if blocks[-1] and e["obs"]["round"] < blocks[-1][-1]["obs"]["round"]:
                blocks.append([])
            blocks[-1].append(e)
    return {run: [b for b in blocks if b] for run, blocks in runs.items()}


def witness(history: dict, obs: dict):
    """As Dispatcher.Witness, on a round's first decision: each living enemy's intents, by uid."""
    for enemy in obs["enemies"]:
        if enemy.get("uid") is None or enemy.get("intents") is None or enemy["hp"] <= 0:
            continue
        history.setdefault(enemy["uid"], {"uid": enemy["uid"], "id": enemy.get("id"), "turns": []})["turns"].append(
            {"round": obs["round"], "intents": enemy["intents"]}
        )


def held_potions(obs: dict) -> int:
    return sum(1 for p in obs["potions"] if p is not None)


def share(hp: int, max_hp: int) -> float:
    return hp / max(1, max_hp)


def ending(obs: dict, end: dict) -> int:
    """The class of a combat's end from a state: 0 for a death, else 1 + the bucket of the HP share lost since."""
    if end["died"]:
        return 0
    lost = share(obs["player"]["hp"], obs["player"]["max_hp"]) - share(end["hp"], end["max_hp"])
    return 1 + max(0, min(BUCKETS - 1, int((lost - LOSS_MIN) / (1 - LOSS_MIN) * BUCKETS)))


def sample(run: str, obs: dict, history: list, end: dict) -> dict:
    return {
        "run": run,
        "obs": obs,
        "history": history,
        "tokens": encode.tokens(obs, history),
        "class": ending(obs, end),
        "used": held_potions(obs) - end["potions"],
    }


def read(logs: str, records: str, character: str, env: str) -> tuple[list[dict], int]:
    """The round starts of the logs' combats as samples, and how many runs were skipped: those whose combats do not
    match their record's (a log cut short, a combat logged apart)."""
    samples, skipped = [], 0
    for i, (log, runs) in enumerate(runs_by_log(logs, records, character)):
        for run, blocks in combats(log, env).items():
            r = runs.get(run)
            if r is None or r.get("failure"):
                continue
            fights = r["combats"]
            if len(blocks) != len(fights) or any(
                b[0]["obs"]["encounter"] != f["encounter"] for b, f in zip(blocks, fights, strict=True)
            ):
                skipped += 1
                continue
            for block, fight in zip(blocks, fights, strict=True):
                if "end" not in fight:
                    continue
                starts, last = [], -1
                for e in block:
                    if e["obs"]["round"] != last:
                        starts.append(e["obs"])
                        last = e["obs"]["round"]
                history = {}
                for obs in starts:
                    past = [{"uid": h["uid"], "id": h["id"], "turns": list(h["turns"])} for h in history.values()]
                    samples.append(sample(run, obs, past, fight["end"]) | {"key": (i, run)})
                    witness(history, obs)
    return samples, skipped
