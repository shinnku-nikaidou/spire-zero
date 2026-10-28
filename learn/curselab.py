"""Knowledge Demon's Curse of Knowledge picks, measured: the act 2 boss fights of logged runs played again with each
pick forced at every Curse of Knowledge prompt, the same seeds for every pick, the rest as the champion plays (the SL
planner at its default effort).

The move asks three times per fight (rounds 1, 5, 9), each time Disintegration (6, 7, 8 damage at the end of every
player turn, stacking) or a second card: Mind Rot (one card less drawn), then Sloth (at most 3 cards a turn), then
Waste Away (one energy less). p0 (`src/Sts2Ai.Brain/PromptBrain.cs`) takes Disintegration every time. A combo names
the pick at each prompt: D for Disintegration, M, S, W for the other card; DDD is p0's.

  tasks   from decision logs: each run's last run view before its Knowledge Demon fight (deck without enchantments,
          relics, potions, the HP it entered with), one `panel` line per (run, seed, combo) in OUT/tasks.txt, the
          runs in OUT/states.jsonl
  report  per combo: fights, win rate, the boss HP share taken, each against DDD on the same fights

Only the standard library: the fleet's hosts play the tasks with a plain python3 (`run_tasks.py`).

    python3 curselab.py tasks --logs 'g3/b1/log-*.jsonl' --out JOB/kd
    python3 curselab.py report JOB/kd
"""

import argparse
import glob
import json
import math
import os
import shlex

from bosslab import loadout

BOSS = "KNOWLEDGE_DEMON_BOSS"
ALTERNATIVES = ["MIND_ROT", "SLOTH", "WASTE_AWAY"]
COMBOS = ["DDD", "MDD", "DSD", "DDW", "MSD", "MDW", "DSW", "MSW"]


def prefer(combo: str) -> list[str]:
    """panel's --prompt-prefer for a combo: the second cards it takes; Disintegration is p0's pick at the rest."""
    return [card for card, pick in zip(ALTERNATIVES, combo, strict=True) if pick != "D"]


def states(logs: list[str]):
    """Each run that met the boss: its last run view before the fight, and whether the run won the fight."""
    for path in logs:
        runs: dict[str, list] = {}
        for line in open(path):
            try:
                e = json.loads(line)
            except json.JSONDecodeError:
                continue
            runs.setdefault(e["run"], []).append(e)
        for run, es in runs.items():
            fight = [i for i, e in enumerate(es) if (e.get("obs") or {}).get("encounter") == BOSS]
            if not fight:
                continue
            view = next((e["run_view"] for e in reversed(es[: fight[0]]) if "run_view" in e), None)
            if view is None:
                continue
            after = [e["run_view"] for e in es[fight[-1] + 1 :] if "run_view" in e]
            won = any(v["act"] > view["act"] or v["floor"] > view["floor"] + 1 for v in after)
            yield {"run": run, "log": os.path.basename(path), "won": won, "view": view}


def tasks(args) -> None:
    os.makedirs(args.out, exist_ok=True)
    logs = sorted(p for pattern in args.logs.split(",") for p in glob.glob(pattern))
    n = 0
    with open(os.path.join(args.out, "tasks.txt"), "w") as f, open(os.path.join(args.out, "states.jsonl"), "w") as s:
        for st in states(logs):
            n += 1
            view = st["view"]
            options = loadout(view)
            options[options.index("--hp") + 1] = str(view["hp"])
            seed = int(st["run"], 16) % 100000
            for k in range(args.seeds):
                for combo in COMBOS:
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
                        "1",
                        "--start",
                        str(k),
                        "--seed",
                        str(seed),
                        "--out",
                        f"res/{st['run']}-s{k}-{combo}.jsonl",
                    ]
                    if prefer(combo):
                        cmd += ["--prompt-prefer", ",".join(prefer(combo))]
                    f.write(shlex.join(cmd + options) + "\n")
            s.write(
                json.dumps(
                    {
                        "run": st["run"],
                        "log": st["log"],
                        "won": st["won"],
                        "floor": view["floor"],
                        "hp": view["hp"],
                        "max_hp": view["max_hp"],
                        "deck": len(view["deck"]),
                    }
                )
                + "\n"
            )
    print(f"{n} fights, {n * args.seeds * len(COMBOS)} task lines")


def taken(f: dict) -> float:
    """The boss HP share the fight took: 1 for a win."""
    return 1.0 if f["outcome"] == "Win" else 1 - f["enemy_hp"] / max(1, f["enemy_max_hp"])


def mean_se(xs: list[float]) -> tuple[float, float]:
    if not xs:
        return math.nan, math.nan
    m = sum(xs) / len(xs)
    if len(xs) < 2:
        return m, math.nan
    return m, math.sqrt(sum((x - m) ** 2 for x in xs) / (len(xs) - 1) / len(xs))


def report(args) -> None:
    fights: dict[tuple[str, int, str], dict] = {}
    failed = 0
    for path in glob.glob(os.path.join(args.job, "res", "*.jsonl")):
        run, k, combo = os.path.basename(path)[: -len(".jsonl")].rsplit("-", 2)
        for line in open(path):
            f = json.loads(line)
            if f["outcome"] in ("Win", "Loss"):
                fights[(run, int(k[1:]), combo)] = f
            else:
                failed += 1
    runs = sorted({r for r, _, _ in fights})
    print(f"{len(fights)} fights over {len(runs)} runs ({failed} failed or truncated)")
    # Paired against DDD on the same (run, seed), averaged per run first: a run's seeds share its deck.
    print("combo  fights  win rate        boss HP taken    vs DDD: win          HP taken         curses (mean stacks)")
    for combo in COMBOS:
        mine = [(r, k, f) for (r, k, c), f in fights.items() if c == combo]
        if not mine:
            continue
        wins = [1.0 if f["outcome"] == "Win" else 0.0 for _, _, f in mine]
        share = [taken(f) for _, _, f in mine]
        per_run_win: dict[str, list[float]] = {}
        per_run_share: dict[str, list[float]] = {}
        for r, k, f in mine:
            base = fights.get((r, k, "DDD"))
            if base is None:
                continue
            per_run_win.setdefault(r, []).append((f["outcome"] == "Win") - (base["outcome"] == "Win"))
            per_run_share.setdefault(r, []).append(taken(f) - taken(base))
        dw = mean_se([sum(v) / len(v) for v in per_run_win.values()])
        ds = mean_se([sum(v) / len(v) for v in per_run_share.values()])
        stacks = {}
        for _, _, f in mine:
            for p, a in (f.get("player_powers") or {}).items():
                if p.removesuffix("_POWER") in ["DISINTEGRATION", *ALTERNATIVES]:
                    stacks[p.removesuffix("_POWER")] = stacks.get(p.removesuffix("_POWER"), 0) + a
        w, ws = mean_se(wins)
        s, ss = mean_se(share)
        print(
            f"{combo}   {len(mine):6d}  {w:6.1%} ± {ws:5.1%}  {s:6.1%} ± {ss:5.1%}  {dw[0]:+6.1%} ± {dw[1]:5.1%}  "
            f"{ds[0]:+6.1%} ± {ds[1]:5.1%}  "
            + " ".join(f"{c[0]}{a / len(mine):.1f}" for c, a in sorted(stacks.items()))
        )
    # The best combo per (run, seed), known only after the fact: what a pick that saw the outcomes could reach.
    pairs = {(r, k) for r, k, _ in fights}
    full = [(r, k) for r, k in pairs if all((r, k, c) in fights for c in COMBOS)]
    if full:
        best_win = sum(any(fights[(r, k, c)]["outcome"] == "Win" for c in COMBOS) for r, k in full) / len(full)
        best_share = sum(max(taken(fights[(r, k, c)]) for c in COMBOS) for r, k in full) / len(full)
        print(
            f"best combo per fight, after the fact ({len(full)} fights with all combos): win {best_win:.1%}, "
            f"boss HP taken {best_share:.1%}"
        )
    reached = [
        len([p for p in (f.get("player_powers") or {}) if p.removesuffix("_POWER") in ALTERNATIVES])
        for (_, _, c), f in fights.items()
        if c == "MSW"
    ]
    if reached:
        print("MSW fights by curse prompts reached (0-3): " + " ".join(f"{i}:{reached.count(i)}" for i in range(4)))


if __name__ == "__main__":
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    t = sub.add_parser("tasks", help="panel command lines for the logged runs' Knowledge Demon fights")
    t.add_argument("--logs", required=True, help="decision logs, glob patterns, comma-separated")
    t.add_argument("--seeds", type=int, default=3, help="fights per (run, combo)")
    t.add_argument("--budget", default="", help="the planner's budget options (default: its default effort)")
    t.add_argument("--combat-net", default="weights/silent/c2.json")
    t.add_argument("--out", required=True)
    r = sub.add_parser("report", help="each combo's fights against DDD's")
    r.add_argument("job", help="the folder with res/")
    a = p.parse_args()
    {"tasks": tasks, "report": report}[a.cmd](a)
