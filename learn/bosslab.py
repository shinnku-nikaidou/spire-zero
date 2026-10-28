"""Boss labels for card rewards (docs: learning §2): each option's deck fights its act's boss with the SL planner, a
few seeds each, the same seeds for every option of a decision, so the options share their luck.

  tasks    from decision logs and their afterstates (`afterstates`), one `panel` command line per (decision, option)
           in OUT/tasks.txt, and the decisions with their options in OUT/decisions.jsonl
  play     plays the task lines whose output is missing, PROCS at a time at nice 19, each decision's options together
           and the decisions in a fixed shuffled order, so the finished decisions come early; a stopped play leaves no
           partial file, and another host can play the rest
  collect  joins the outputs back to the decisions, one line per decision with each option's fights, for
           `train_value.py --boss-sims`
  compare  on the decisions with every option played: how often the option V valued best is the fights' best

The loadout is the afterstate's deck, relics and potions; cards lose their enchantments (panel cannot give them) and
used-up or disabled relics are left out. HP starts at a fixed share of max HP per act (about the share the runs enter
that act's boss with), so the label measures the deck, not the HP the run happened to have.

Only the standard library, unlike the rest of learn/: the fleet's hosts play the tasks with a plain python3.

    python3 bosslab.py tasks --logs a.jsonl,b.jsonl --afterstates s.jsonl --acts 1 --out lab/a1
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
    """The view with an afterstate's changes, one level deep (as train_value.py's)."""
    out = dict(view)
    for k, v in changes.items():
        if v is None:
            out.pop(k, None)
        elif isinstance(v, dict) and isinstance(out.get(k), dict):
            out[k] = {**out[k], **v}
        else:
            out[k] = v
    return out


def loadout(view: dict) -> list[str]:
    """panel's options for the view's deck, relics, potions and its act's boss, at the act's HP share."""
    deck = ",".join(c["id"] + ("+" if c.get("up") else "") for c in view["deck"])
    relics = [r["id"] for r in view.get("relics", []) if not r.get("used_up") and r.get("status") != "Disabled"]
    potions = [p for p in view.get("potions", []) if p]
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
                    *loadout(after),
                ]
                f.write(shlex.join(cmd) + "\n")
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


def out_of(task: list[str], root: str | None) -> str:
    path = task[task.index("--out") + 1]
    return os.path.join(root, os.path.basename(path)) if root else path


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


def score(f: dict) -> float:
    """A fight as one number in [0, 1], as train_value.py's boss score: a win 0.5 to 1 by the HP left, a loss 0 to
    0.5 by the boss's HP taken."""
    if f["won"]:
        return 0.5 + 0.5 * f["hp"] / max(1, f["hp_start"])
    return 0.5 * (1 - f["enemy_hp"] / max(1, f["enemy_max_hp"]))


def compare(args) -> None:
    n = agree_v = agree_chosen = self_agree = 0
    regret_v = regret_chosen = spread = 0.0
    for line in open(args.decisions):
        d = json.loads(line)
        rows = [(o, fights_of(args.results, o["key"])[0]) for o in d["options"]]
        if any(len(f) < 2 for _, f in rows):
            continue
        n += 1
        mean = [sum(map(score, f)) / len(f) for _, f in rows]
        best = max(range(len(rows)), key=lambda i: mean[i])
        v = max(range(len(rows)), key=lambda i: rows[i][0]["value"])
        chosen = next(i for i, (o, _) in enumerate(rows) if o["action"] == d["chosen"])
        first, second = (max(range(len(rows)), key=lambda i, s=s: score(rows[i][1][s])) for s in (0, 1))
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
    {"tasks": tasks, "play": play, "collect": collect, "compare": compare}[a.cmd](a)
