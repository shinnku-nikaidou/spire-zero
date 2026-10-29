"""Boss labels for card rewards (docs: learning §2): each option's deck fights its act's boss with the SL planner, a
few seeds each, the same seeds for every option of a decision, so the options share their luck.

  tasks    from decision logs and their afterstates (`afterstates`), one `panel` command line per (decision, option)
           in OUT/tasks.txt, and the decisions with their options in OUT/decisions.jsonl
  doors    the same files, but each option fights at the boss's door instead: the run's loadout before its boss
           fight, at the HP it entered with, with the decision's pick swapped for the option (the picked card out,
           the option's in, at the upgrade level the picked one had reached), for runs that reached that boss
  states   the runs' loadouts at a boss's door alone, at the HP they entered with and at shares of max HP, one decision
           line per door with an option per HP level, for `v.train --boss-states`
  play     plays the task lines whose output is missing, PROCS at a time at nice 19, each decision's options together
           and the decisions in a fixed shuffled order, so the finished decisions come early; a stopped play leaves no
           partial file, and another host can play the rest
  collect  joins the outputs back to the decisions, one line per decision with each option's fights, for
           `v.train --boss-sims`
  compare  on the decisions with every option played: how often the option V valued best is the fights' best

The loadout is the afterstate's deck, relics and potions; cards lose their enchantments (panel cannot give them) and
used-up or disabled relics are left out. HP starts at a fixed share of max HP per act (about the share the runs enter
that act's boss with), so the label measures the deck, not the HP the run happened to have.

Only the standard library, unlike the rest of learn/: the fleet's hosts play the tasks with a plain python3.

    python3 bosslab.py tasks --logs a.jsonl,b.jsonl --afterstates s.jsonl --acts 1 --out lab/a1
    python3 bosslab.py doors --logs a.jsonl --records ra.jsonl --afterstates s.jsonl --act 1 --out lab/d1
    python3 bosslab.py states --logs a.jsonl --records ra.jsonl --act 1 --out lab/s1
    python3 bosslab.py play SIM lab/a1/tasks.txt 8
    python3 bosslab.py collect --decisions lab/a1/decisions.jsonl --results lab/a1/res --out lab/a1/collect.jsonl
"""

import argparse
import json
import os
import random
import shlex
import subprocess
from concurrent.futures import ThreadPoolExecutor

ENTER_SHARE = {0: 0.96, 1: 0.81, 2: 0.6}
# The planner's budget for the labels: the low effort before c8d45b3, about 9 s a boss fight.
BUDGET = "--effort low --nodes 150 --beam 4 --easy-beam 0 --loss-plans 0 --replan 0"


def merged(view: dict, changes: dict) -> dict:
    """A run view with an afterstate's changes as `fork` and `afterstates` write them: the fields that differ, to one
    level inside an object field; a null removes a field. v/data.py reads afterstates with this one too."""
    out = dict(view)
    for k, v in changes.items():
        if v is None:
            out.pop(k, None)
        elif isinstance(v, dict) and isinstance(view.get(k), dict):
            out[k] = {kk: vv for kk, vv in {**view[k], **v}.items() if vv is not None}
        else:
            out[k] = v
    return out


def loadout(view: dict, hp: int | None = None) -> list[str]:
    """panel's options for the view's deck, relics, potions and its act's boss, at the act's HP share (or `hp`)."""
    deck = ",".join(c["id"] + ("+" if c.get("up") else "") for c in view["deck"])
    relics = [r["id"] for r in view.get("relics", []) if not r.get("used_up") and r.get("status") != "Disabled"]
    potions = [p for p in view.get("potions", []) if p]
    if hp is None:
        hp = max(1, round(ENTER_SHARE.get(view["act"], 0.8) * view["max_hp"]))
    args = [
        "--act",
        str(view["act"]),
        "--encounter",
        view["map"]["boss"],
        "--deck",
        deck,
        "--hp",
        str(hp),
        "--max-hp",
        str(view["max_hp"]),
    ]
    if relics:
        args += ["--relics", ",".join(relics)]
    if potions:
        args += ["--potions", ",".join(potions)]
    return args


def card_rewards(logs: list[str], afterstates: list[str], acts: set[int]):
    """Each card reward of the acts with at least two options: its afterstates line and the logged run view."""
    views = {}
    for path in logs:
        for line in open(path):
            if '"card_reward"' not in line:
                continue
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            if e.get("surface") == "card_reward" and e["run_view"]["act"] in acts:
                views[(e["run"], e["decision_id"])] = e["run_view"]
    for path in afterstates:
        for line in open(path):
            a = json.loads(line)
            view = views.get((a["run"], a["decision_id"]))
            if a["surface"] == "card_reward" and view is not None and len(a["options"]) >= 2:
                yield a, view


def task_line(args, seed: int, key: str, view: dict, hp: int | None = None) -> str:
    """The `panel` command line of one option: its fights, with the decision's seed, into RESULTS/KEY.jsonl."""
    cmd = [
        "panel",
        "--player",
        "plan",
        "--env",
        "SL",
        *shlex.split(args.budget),
        "--combat-net",
        args.combat_net,
        "--n",
        str(args.seeds),
        "--seed",
        str(seed),
        "--out",
        f"{args.results}/{key}.jsonl",
        *loadout(view, hp),
    ]
    return shlex.join(cmd) + "\n"


def tasks(args) -> None:
    os.makedirs(args.out, exist_ok=True)
    picked = 0
    acts = {int(x) for x in args.acts.split(",")}
    with open(os.path.join(args.out, "tasks.txt"), "w") as f, open(os.path.join(args.out, "decisions.jsonl"), "w") as d:
        for a, view in card_rewards(args.logs.split(","), args.afterstates.split(","), acts):
            if int(a["run"], 16) % args.every != 0:
                continue
            picked += 1
            seed = int(a["run"], 16) % 100000 + a["decision_id"]
            options = []
            for k, o in enumerate(a["options"]):
                after = merged(view, o.get("after") or {})
                key = f"{a['run']}-{a['decision_id']}-{k}"
                f.write(task_line(args, seed, key, after))
                options.append(
                    {"k": k, "action": o["action"], "value": o["value"], "key": key, "deck": len(after["deck"])}
                )
            d.write(
                json.dumps(
                    {
                        "run": a["run"],
                        "decision_id": a["decision_id"],
                        "act": view["act"],
                        "floor": view["floor"],
                        "boss": view["map"]["boss"],
                        "chosen": a["chosen"],
                        "options": options,
                    }
                )
                + "\n"
            )
    print(f"{picked} card rewards")


def added(before: list[dict], after: list[dict]) -> list[dict]:
    """The cards of `after` that `before` lacks, by id and upgrade level."""
    left: dict[tuple, int] = {}
    for c in before:
        left[(c["id"], c.get("up") or 0)] = left.get((c["id"], c.get("up") or 0), 0) + 1
    out = []
    for c in after:
        k = (c["id"], c.get("up") or 0)
        if left.get(k):
            left[k] -= 1
        else:
            out.append(c)
    return out


def swapped(door: list[dict], picked: list[dict], option: list[dict]) -> list[dict] | None:
    """The door's deck with the picked cards out (a copy at the picked level first, else any copy) and the option's
    in, each at least at the level its picked counterpart had reached; None when a picked card has left the deck."""
    deck, levels = list(door), []
    for c in picked:
        same = [i for i, d in enumerate(deck) if d["id"] == c["id"]]
        if not same:
            return None
        i = next((i for i in same if (deck[i].get("up") or 0) == (c.get("up") or 0)), same[0])
        levels.append(deck.pop(i).get("up") or 0)
    for j, c in enumerate(option):
        up = max(c.get("up") or 0, levels[j] if j < len(levels) else 0)
        deck.append({k: v for k, v in c.items() if k != "up"} | ({"up": up} if up else {}))
    return deck


def boss_fights(records: str, act: int, max_runs: int | None) -> dict:
    """Each run's fight with the act's boss (its record's combat), for the runs that reached it; the first by run id
    with `max_runs`."""
    fights = {}
    for path in records.split(","):
        for line in open(path):
            r = json.loads(line)
            boss = next((c for c in r.get("combats", []) if c["act"] == act and c["room"] == "Boss"), None)
            if boss is not None and not r.get("failure"):
                fights[r["run_id"]] = boss
    runs = sorted(fights)[:max_runs] if max_runs else sorted(fights)
    return {run: fights[run] for run in runs}


def at_doors(logs: str, fights: dict, act: int, reward_acts: set[int]) -> tuple[dict, dict]:
    """Each run's door: its last logged decision of the act before the boss fight, as (decision id, run view); and
    the run views of its card rewards in `reward_acts`."""
    door, views = {}, {}
    for path in logs.split(","):
        for line in open(path):
            if '"run_view"' not in line:
                continue
            e = json.loads(line)
            run, v = e["run"], e.get("run_view")
            if run not in fights or v is None:
                continue
            if v["act"] == act and v["floor"] < fights[run]["floor"]:
                if run not in door or e["decision_id"] > door[run][0]:
                    door[run] = (e["decision_id"], v)
            if e["surface"] == "card_reward" and v["act"] in reward_acts:
                views[(run, e["decision_id"])] = v
    return door, views


def doors(args) -> None:
    os.makedirs(args.out, exist_ok=True)
    act = args.act
    fights = boss_fights(args.records, act, args.max_runs)
    door, views = at_doors(args.logs, fights, act, {int(x) for x in args.acts.split(",")} if args.acts else {act})
    picked = dropped = 0
    with open(os.path.join(args.out, "tasks.txt"), "w") as f, open(os.path.join(args.out, "decisions.jsonl"), "w") as d:
        for path in args.afterstates.split(","):
            for line in open(path):
                a = json.loads(line)
                view = views.get((a["run"], a["decision_id"]))
                if a["surface"] != "card_reward" or view is None or a["run"] not in door or len(a["options"]) < 2:
                    continue
                door_view, boss = door[a["run"]][1], fights[a["run"]]
                adds = {
                    o["action"]: added(view["deck"], merged(view, o.get("after") or {})["deck"]) for o in a["options"]
                }
                if a["chosen"] not in adds:
                    continue
                decks = {k: swapped(door_view["deck"], adds[a["chosen"]], c) for k, c in adds.items()}
                if any(deck is None for deck in decks.values()):
                    dropped += 1
                    continue
                picked += 1
                seed = int(a["run"], 16) % 100000 + a["decision_id"]
                options = []
                for k, o in enumerate(a["options"]):
                    key = f"{a['run']}-{a['decision_id']}-{k}"
                    after = {**door_view, "deck": decks[o["action"]], "max_hp": boss["max_hp"]}
                    f.write(task_line(args, seed, key, after, max(1, boss["hp"])))
                    options.append(
                        {"k": k, "action": o["action"], "value": o["value"], "key": key, "deck": len(after["deck"])}
                    )
                d.write(
                    json.dumps(
                        {
                            "run": a["run"],
                            "decision_id": a["decision_id"],
                            "act": act,
                            "floor": view["floor"],
                            "decision_act": view["act"],
                            "door_floor": door_view["floor"],
                            "boss": door_view["map"]["boss"],
                            "hp": boss["hp"],
                            "chosen": a["chosen"],
                            "options": options,
                        }
                    )
                    + "\n"
                )
    print(f"{len(door)} runs at the door, {picked} card rewards, {dropped} left out (the pick left the deck)")


def out_of(task: list[str], root: str | None) -> str:
    path = task[task.index("--out") + 1]
    return os.path.join(root, os.path.basename(path)) if root else path


def states(args) -> None:
    os.makedirs(args.out, exist_ok=True)
    fights = boss_fights(args.records, args.act, args.max_runs)
    door, _ = at_doors(args.logs, fights, args.act, set())
    shares = [float(x) for x in args.hp_shares.split(",")]
    with open(os.path.join(args.out, "tasks.txt"), "w") as f, open(os.path.join(args.out, "decisions.jsonl"), "w") as d:
        for run in sorted(door):
            did, view = door[run]
            boss = fights[run]
            levels = sorted({max(1, boss["hp"])} | {max(1, round(x * boss["max_hp"])) for x in shares})
            seed = int(run, 16) % 100000 + did
            options = []
            for k, hp in enumerate(levels):
                key = f"{run}-{did}-{k}"
                f.write(task_line(args, seed, key, view | {"max_hp": boss["max_hp"]}, hp))
                options.append({"k": k, "hp": hp, "key": key, "deck": len(view["deck"]), "entered": hp == boss["hp"]})
            line = {
                "run": run,
                "decision_id": did,
                "act": args.act,
                "floor": view["floor"],
                "boss": view["map"]["boss"],
            }
            d.write(json.dumps(line | {"max_hp": boss["max_hp"], "hp": boss["hp"], "options": options}) + "\n")
    print(f"{len(door)} runs at the act {args.act} boss's door")


def play(args) -> None:
    lines = [shlex.split(line) for line in open(args.tasks) if line.strip()]
    groups: dict[str, list] = {}
    for t in lines:
        groups.setdefault(os.path.basename(out_of(t, None)).rsplit("-", 1)[0], []).append(t)
    order = sorted(groups)
    random.Random(7).shuffle(order)
    todo = [t for g in order for t in groups[g] if not os.path.exists(out_of(t, args.root))]
    print(f"{len(todo)} of {len(lines)} tasks to play", flush=True)

    def one(task: list[str]) -> int:
        out = out_of(task, args.root)
        task = list(task)
        task[task.index("--out") + 1] = out + ".part"
        r = subprocess.run(["nice", "-n", "19", args.sim, *task], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if r.returncode == 0:
            os.replace(out + ".part", out)
        elif os.path.exists(out + ".part"):
            os.remove(out + ".part")
        return r.returncode

    with ThreadPoolExecutor(args.procs) as pool:
        codes = list(pool.map(one, todo))
    print(f"done: {codes.count(0)} played, {len(codes) - codes.count(0)} failed", flush=True)


def fights_of(results: str, key: str) -> tuple[list[dict], int]:
    """An option's ended fights, and how many failed."""
    path = os.path.join(results, key + ".jsonl")
    lines = [json.loads(x) for x in open(path)] if os.path.exists(path) else []
    ended = [
        {
            "won": f["outcome"] == "Win",
            "hp": f["hp"],
            "hp_start": f["hp_start"],
            "enemy_hp": f["enemy_hp"],
            "enemy_max_hp": f["enemy_max_hp"],
            "rounds": f["rounds"],
        }
        for f in lines
        if f["outcome"] in ("Win", "Loss")
    ]
    return ended, len(lines) - len(ended)


def collect(args) -> None:
    with open(args.out, "w") as out:
        for line in open(args.decisions):
            d = json.loads(line)
            for o in d["options"]:
                o["fights"], o["failed"] = fights_of(args.results, o["key"])
            out.write(json.dumps(d) + "\n")


def score(f: dict, max_hp: int) -> float:
    """A fight as one number in [0, 1], V's boss score (v/data.py): a win 0.5 plus half the share of max_hp left, a
    loss half the share of the boss's HP taken."""
    if f["won"]:
        return 0.5 + 0.5 * min(max(f["hp"] / max(max_hp, 1), 0.0), 1.0)
    return 0.5 * (1 - min(max(f["enemy_hp"] / max(f["enemy_max_hp"], 1), 0.0), 1.0))


def compare(args) -> None:
    n = agree_v = agree_chosen = self_agree = 0
    regret_v = regret_chosen = spread = 0.0
    for line in open(args.decisions):
        d = json.loads(line)
        rows = [(o, fights_of(args.results, o["key"])[0]) for o in d["options"]]
        if any(len(f) < 2 for _, f in rows):
            continue
        n += 1
        # Every option of a decision starts at the same HP, so scoring by it ranks them as by max HP.
        mean = [sum(score(x, x["hp_start"]) for x in f) / len(f) for _, f in rows]
        best = max(range(len(rows)), key=lambda i: mean[i])
        v = max(range(len(rows)), key=lambda i: rows[i][0]["value"])
        chosen = next(i for i, (o, _) in enumerate(rows) if o["action"] == d["chosen"])
        first, second = (
            max(range(len(rows)), key=lambda i, s=s: score(rows[i][1][s], rows[i][1][s]["hp_start"])) for s in (0, 1)
        )
        agree_v += best == v
        agree_chosen += best == chosen
        self_agree += first == second
        regret_v += mean[best] - mean[v]
        regret_chosen += mean[best] - mean[chosen]
        spread += mean[best] - min(mean)
    if not n:
        print("no decision has every option played")
        return
    print(f"{n} card rewards with every option played")
    print(
        f"the fights' best = V's best: {agree_v / n:.1%}; = the run's pick: {agree_chosen / n:.1%}; "
        f"seed 1's best = seed 2's best: {self_agree / n:.1%}"
    )
    print(
        f"mean score (0 to 1), best option minus V's best: {regret_v / n:.3f}; minus the run's pick: "
        f"{regret_chosen / n:.3f}; best minus worst: {spread / n:.3f}"
    )


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    t = sub.add_parser("tasks", help="panel command lines for the card rewards of the logs")
    t.add_argument("--logs", required=True, help="decision logs, comma-separated")
    t.add_argument("--afterstates", required=True, help="their afterstates, comma-separated")
    t.add_argument("--acts", default="1", help="act indices, comma-separated (0 is the first act)")
    t.add_argument("--every", type=int, default=1, help="keep the runs whose id is divisible by this")
    t.add_argument("--seeds", type=int, default=2, help="fights per option")
    t.add_argument("--budget", default=BUDGET, help="the planner's budget options")
    t.add_argument("--combat-net", default="weights/silent/c2.json")
    t.add_argument("--results", default="res", help="where the panel outputs go, as the playing host sees it")
    t.add_argument("--out", required=True)
    o = sub.add_parser("doors", help="panel command lines for the card rewards of the runs at a boss's door")
    o.add_argument("--logs", required=True, help="decision logs, comma-separated")
    o.add_argument("--records", required=True, help="their run records, comma-separated")
    o.add_argument("--afterstates", required=True, help="their afterstates, comma-separated")
    o.add_argument("--act", type=int, default=1, help="the act index of the boss (0 is the first act)")
    o.add_argument("--acts", help="the act indices of the card rewards, comma-separated (default: the boss's)")
    o.add_argument("--max-runs", type=int, help="the first runs by run id only")
    o.add_argument("--seeds", type=int, default=2, help="fights per option")
    o.add_argument("--budget", default=BUDGET, help="the planner's budget options")
    o.add_argument("--combat-net", default="weights/silent/c2.json")
    o.add_argument("--results", default="res", help="where the panel outputs go, as the playing host sees it")
    o.add_argument("--out", required=True)
    s = sub.add_parser("states", help="panel command lines for the runs' loadouts at a boss's door, at HP levels")
    s.add_argument("--logs", required=True, help="decision logs, comma-separated")
    s.add_argument("--records", required=True, help="their run records, comma-separated")
    s.add_argument("--act", type=int, default=1, help="the act index of the boss (0 is the first act)")
    s.add_argument("--hp-shares", default="0.4,0.7,1", help="shares of max HP to fight at, besides the HP entered with")
    s.add_argument("--max-runs", type=int, help="the first runs by run id only")
    s.add_argument("--seeds", type=int, default=2, help="fights per HP level")
    s.add_argument("--budget", default=BUDGET, help="the planner's budget options")
    s.add_argument("--combat-net", default="weights/silent/c2.json")
    s.add_argument("--results", default="res", help="where the panel outputs go, as the playing host sees it")
    s.add_argument("--out", required=True)
    g = sub.add_parser("play", help="play the task lines whose output is missing")
    g.add_argument("sim", help="the sim's launcher (tmp/builds/REV/sim.sh)")
    g.add_argument("tasks")
    g.add_argument("procs", type=int)
    g.add_argument("--root", help="put the outputs here, by their file names, instead of the lines' --out")
    c = sub.add_parser("collect", help="join the outputs back to the decisions")
    c.add_argument("--decisions", required=True)
    c.add_argument("--results", required=True)
    c.add_argument("--out", required=True)
    m = sub.add_parser("compare", help="V's best against the fights' best")
    m.add_argument("decisions")
    m.add_argument("results")
    a = p.parse_args()
    {"tasks": tasks, "doors": doors, "states": states, "play": play, "collect": collect, "compare": compare}[a.cmd](a)
