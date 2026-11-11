"""The data of the book's figures (docs/src/data/), from the champion: one play of the book's example seed in each
environment, the champion's reports and its two net files. A promotion reruns the two plays and this script.

The plays, with the champion files (the Silent, seed 0SSL7NS01MU6, the 13th seed of promotion-1-100):

    run --champion eval/champions/sl.json --env SL --character SILENT --seeds eval/seeds/promotion-1-100.txt \
        --start 12 --n 1 --records DIR/sl-records.jsonl --log DIR/sl-log.jsonl
    run --champion eval/champions/nosl.json --env NoSL --character SILENT --seeds eval/seeds/promotion-1-100.txt \
        --start 12 --n 1 --records DIR/nosl-records.jsonl --log DIR/nosl-log.jsonl
    uv run --project learn python docs/scripts/book-data.py DIR

V's values are the champion's V on the logged run views (learn/v, read by the encoder: learn/common.py's Encoder,
src/Sts2Ai.Encode built here), as the brain computes them: a map move's option is its afterstate (`entered`); any other option of the SL macro brain is played on the node's clone, and what the
chosen one is worth is the run where the node's search ends, at its next map decision. The panel's texts are made as
the mod's Describe and L10n make them (src/Sts2Ai.Mod/UI/), with the game's names of src/data/names/.
"""

import json
import re
import sys
from pathlib import Path

import onnx
import torch

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "learn"))

import common  # noqa: E402
from v import export  # noqa: E402
from v.data import sample  # noqa: E402
from v.net import Batch, Net, values  # noqa: E402

DATA = ROOT / "docs/src/data"
SL_CHAMPION = json.loads((ROOT / "eval/champions/sl.json").read_text())
NOSL_CHAMPION = json.loads((ROOT / "eval/champions/nosl.json").read_text())
SEED = "0SSL7NS01MU6"
LANGS = ["en", "zh-CN", "zh-TW"]
PLAY = {
    "SL": "run --champion eval/champions/sl.json --env SL --character SILENT --seeds eval/seeds/promotion-1-100.txt --start 12 --n 1 --records FILE --log FILE",
    "NoSL": "run --champion eval/champions/nosl.json --env NoSL --character SILENT --seeds eval/seeds/promotion-1-100.txt --start 12 --n 1 --records FILE --log FILE",
}

# The decisions the figures show, by decision_id in the plays' logs; each is checked where it matters. After a promotion
# the plays differ: pick them again with the same intent.
PLAN_FIGHT = 17  # PlanTimeline: the floor of the SL fight, the act 1 boss (one enemy, as the figure's bars expect)
PANEL_SL = 943  # PanelMock: the first decision of the act 3 boss fight in SL, two first actions kept, both won
PANEL_NOSL = 863  # PanelMock: a turn's first decision in the NoSL run's act 3 boss fight, after an end turn
PANEL_MAP = 341  # PanelMock and MapRoute: an SL map decision of two options, an enemy or a ? room
LOG_LINES = {"combat": 4, "card prompt": 1, "rest": 133}  # DecisionLog: one line of each kind


def lines(path: Path) -> list[dict]:
    return [json.loads(x) for x in path.read_text().splitlines() if x.strip()]


def write(path: Path, data, compact: bool = False) -> None:
    text = json.dumps(data, ensure_ascii=False, separators=(",", ":")) if compact else json.dumps(data, ensure_ascii=False, indent=1)
    path.write_text(text + "\n")
    print(f"wrote {path.relative_to(ROOT)}: {path.stat().st_size} bytes")


def champion_name(champion: dict) -> str:
    return champion["champion"]


# --- V ---------------------------------------------------------------------------------------------------------------

V_FILE = json.loads((ROOT / SL_CHAMPION["options"]["value"]).read_text())
_net = Net({t: len(ids) for t, ids in V_FILE["vocab"].items()}, V_FILE["floors"])
_net.load_state_dict(export.weights(V_FILE, _net))
_net.eval()
_index = {t: {x: k for k, x in enumerate(ids)} for t, ids in V_FILE["vocab"].items()}
_mean = torch.tensor(V_FILE["state"]["mean"], dtype=torch.float64)
_std = torch.tensor(V_FILE["state"]["std"], dtype=torch.float64)


_encoder = common.Encoder(common.checkout_encoder())


def V(views: list[dict]) -> list[float]:
    """The champion's V of each run view: the chance to win, as RunNet.Value gives it."""
    read = _encoder.runs([json.dumps({"run_view": v}) for v in views])
    with torch.no_grad():
        b = Batch([sample("", r["run"], V_FILE["floors"]) for r in read], _index, _mean, _std, V_FILE["floors"], "cpu")
        return [float(x) for x in values(_net, b)]


def entered(view: dict, action: str) -> dict:
    """The run view after the map move `map:COL,ROW`: the point added to the path and marked as being entered, as the
    brain's afterstate of a move is (ScreenAfterstates.Entered); the encoder reads the mark."""
    col, row = (int(x) for x in action.removeprefix("map:").split(","))
    m = view["map"] | {"visited": [*view["map"]["visited"], {"col": col, "row": row}], "entering": True}
    return view | {"map": m}


def check_v() -> None:
    """V here must give the file's test values, as RunNet does on load."""
    for t, v in zip(V_FILE["tests"], V([t["run"] for t in V_FILE["tests"]]), strict=True):
        if abs(t["value"] - v) > 1e-6:
            raise SystemExit(f"V gives {v} for a test run the file values {t['value']}")


def points(view: dict) -> dict:
    return {(p["col"], p["row"]): p for p in view["map"]["points"]}


def point_type(view: dict, action: str) -> str:
    col, row = (int(x) for x in action.removeprefix("map:").split(","))
    return points(view)[(col, row)]["type"]


def moves(view: dict) -> list[str]:
    """The map moves from where the player is: the last visited point, or the start (the first by row, col) before any."""
    p = points(view)
    visited = view["map"]["visited"]
    at = (visited[-1]["col"], visited[-1]["row"]) if visited else min(p, key=lambda k: (k[1], k[0]))
    return [f"map:{n['col']},{n['row']}" for n in p[at]["next"]]


def best(options: list[str], vals: list[float]) -> int:
    """The first of the best, as MaxBy picks."""
    return max(range(len(options)), key=lambda k: (vals[k], -k))


def route(view: dict, action: str) -> list[str]:
    """MacroBrain.Plan: the route after a map move, each next point the best by V of its afterstate, up to the boss."""
    out = []
    while True:
        view = entered(view, action)
        options = moves(view)
        if not options:
            return out
        action = options[best(options, V([entered(view, a) for a in options]))]
        out.append(action)


def map_options(view: dict) -> list[tuple[str, float]]:
    options = moves(view)
    return list(zip(options, V([entered(view, a) for a in options]), strict=True))


# --- the plays -------------------------------------------------------------------------------------------------------


class Play:
    """One play of the example seed: its decision log, its run record, and V's values along it."""

    def __init__(self, env: str, logs: list[dict], record: dict, champion: dict):
        self.env, self.log, self.record, self.champion = env, logs, record, champion
        self.by_id = {e["decision_id"]: k for k, e in enumerate(logs)}
        if [e["decision_id"] for e in logs] != list(range(len(logs))):
            raise SystemExit(f"the {env} log is not one run's decisions in order")
        if record["seed"] != SEED or any(e["run"] != record["run_id"] for e in logs):
            raise SystemExit(f"the {env} log and record are not one run of {SEED}")

    def at(self, decision: int) -> dict:
        return self.log[self.by_id[decision]]

    def macro(self, e: dict) -> bool:
        """A decision the panel keeps V's chance of: outside combat (a card prompt there too)."""
        return e["surface"] != "combat" and not e["brain"] == self.prompts()

    def prompts(self) -> str:
        """The brain of the combat's card prompts, as the champion names it."""
        return re.search(r"combat prompts (\S+?),", champion_name(self.champion)).group(1)

    def view(self, k: int) -> dict:
        """The run view at log entry k: its own, or the next one's (a card prompt outside combat logs none)."""
        for e in self.log[k:]:
            if "run_view" in e:
                return e["run_view"]
        raise SystemExit(f"no run view after decision {k}")

    def worth(self, k: int) -> float:
        """What the macro brain valued the chosen option of log entry k at (the panel's chance to win after it)."""
        e = self.log[k]
        if e["surface"] == "map" and e["chosen"].startswith("map:"):
            return V([entered(e["run_view"], e["chosen"])])[0]
        if self.env == "NoSL":
            # L0 on the screen: these actions leave the run as it is (MacroBrain.After).
            if e["chosen"] not in ("leave", "proceed", "rewards_done"):
                raise SystemExit(f"NoSL decision {k}: {e['chosen']} is valued on a predicted afterstate this script does not make")
            return V([e["run_view"]])[0]
        for later in self.log[k + 1 :]:
            if later["surface"] == "combat":
                raise SystemExit(f"SL decision {k}: its node's search meets a combat")
            if later["surface"] == "map":
                return V([later["run_view"]])[0]
        raise SystemExit(f"SL decision {k}: the run ends in its node")

    def floor_of(self, k: int) -> int:
        """The floor of log entry k: a run view's floor; a combat's, the floor of the next run view."""
        return self.view(k)["floor"]


# --- the panel's words -----------------------------------------------------------------------------------------------

NAMES = {lang: json.loads((DATA / f"names/{lang}.json").read_text()) for lang in LANGS}


def l10n_table() -> dict[str, list[str]]:
    """The mod's own words (src/Sts2Ai.Mod/UI/L10n.cs): English -> French, Spanish, Simplified, Traditional, Japanese."""
    text = (ROOT / "src/Sts2Ai.Mod/UI/L10n.cs").read_text()
    s = r'"((?:[^"\\]|\\.)*)"'
    return {m[1]: re.findall(s, m[2]) for m in re.finditer(rf"\[{s}\] = \[((?:{s},?\s*)+)\]", text)}


L10N = l10n_table()


def T(lang: str, en: str, *args) -> str:
    line = en if lang == "en" else L10N[en][2 if lang == "zh-CN" else 3]
    for i, a in enumerate(args):
        line = line.replace(f"{{{i}}}", str(a))
    return line


def words(f) -> dict:
    """A text in each of the site's languages."""
    return {lang: f(lang) for lang in LANGS}


def card(lang: str, c: dict) -> str:
    return NAMES[lang]["card"].get(c["id"], c["id"]) + ("+" if (c.get("up") or 0) > 0 else "")


def creature(lang: str, uid: int, obs: dict) -> str:
    living = [e for e in obs["enemies"] if e["hp"] > 0]
    for i, e in enumerate(living):
        if e["uid"] == uid:
            name = NAMES[lang]["monster"].get(e["id"], e["id"])
            return f"{name} #{i + 1}" if len(living) > 1 else name
    raise SystemExit(f"no living enemy {uid}")


def combat_action(lang: str, action: str, obs: dict) -> str:
    """Describe.Combat."""
    if action == "end_turn":
        return T(lang, "end turn")
    verb, *args = action.split(":")
    target = f" → {creature(lang, int(args[1]), obs)}" if len(args) > 1 else ""
    if verb == "play":
        uid = int(args[0].split("@")[0])
        held = next((c for c in obs["hand"] if c["uid"] == uid), None)
        return (card(lang, held) if held else f"card {uid}") + target
    if verb == "potion":
        return T(lang, "drink {0}", NAMES[lang]["potion"][obs["potions"][int(args[0])]]) + target
    raise SystemExit(f"cannot name {action}")


def gained(before: list[dict], after: list[dict]) -> list[dict]:
    """What a list of the run view holds after that it did not before, as a multiset."""
    left = [json.dumps(x, sort_keys=True) for x in before]
    out = []
    for x in after:
        key = json.dumps(x, sort_keys=True)
        if key in left:
            left.remove(key)
        else:
            out.append(x)
    return out


def macro_action(lang: str, play: Play, k: int) -> str:
    """Describe.Action outside combat, for the actions a run view and the next one tell apart."""
    e = play.log[k]
    view = e.get("run_view") or play.log[k - 1]["run_view"]  # a card prompt's: the one before, which opened it
    after = play.view(k + 1)
    added = lambda key: gained(view[key], after[key])  # noqa: E731
    if e["surface"] == "card_choice":
        # Describe.Pick names the prompt's options, the cards as they were: an upgrade or a removal takes one away.
        taken = gained(after["deck"], view["deck"]) or added("deck")
        if len(taken) != 1:
            raise SystemExit(f"{play.env} decision {k}: cannot tell the card prompt's pick from the run views")
        return T(lang, "pick {0}", card(lang, taken[0]))
    match e["surface"], e["chosen"]:
        case "map", a if a.startswith("map:"):
            return T(lang, "go to {0}", NAMES[lang]["map"][point_type(view, a)])
        case ("rest", "leave"):
            return T(lang, "leave")
        case "rest", a if a.startswith("rest:"):
            return NAMES[lang]["rest"][a.removeprefix("rest:")]
        case ("event", "proceed"):
            return T(lang, "proceed")
        case ("rewards", "rewards_done"):
            return T(lang, "proceed" if e["legal"] == 1 else "skip the rest, proceed")
        case ("shop", "leave"):
            return T(lang, "leave the shop")
        case ("rewards", _) if after["gold"] > view["gold"]:
            return T(lang, "take {0} gold", after["gold"] - view["gold"])
        case ("rewards", _) if added("relics"):
            return T(lang, "take {0}", NAMES[lang]["relic"][added("relics")[0]["id"]])
        case ("card_reward", _) if added("deck"):
            return T(lang, "take {0}", card(lang, added("deck")[0]))
        case ("treasure", _) if added("relics"):
            return NAMES[lang]["relic"][added("relics")[0]["id"]]
    raise SystemExit(f"{play.env} decision {k}: cannot name {e['surface']} {e['chosen']} from the run views")


def turn(first: str, plan: list[str]) -> tuple[list[str], int]:
    """Describe.Turn: the plan from the action to this turn's end, and how many turns it plans after that."""
    every = [first, *plan]
    if "end_turn" not in every:
        return every, 0
    end = every.index("end_turn")
    later = every[end + 1 :].count("end_turn") + (1 if len(every) > end + 1 and every[-1] != "end_turn" else 0)
    return every[: end + 1], later


def percent(p: float, whole: bool = False) -> str:
    return f"{p * 100:.0f}%" if whole else f"{p * 100:.1f}%"


def answer(play: Play, decision: int, title: str, how: str) -> dict:
    """A brain's answer as the panel shows it (Describe.Recommendation, Assist's strip, sub and recent actions)."""
    k = play.by_id[decision]
    e = play.log[k]
    combat = e["surface"] == "combat"
    view = play.view(k)
    obs = e.get("obs")
    if combat:
        hp, max_hp = obs["player"]["hp"], obs["player"]["max_hp"]
        name = lambda lang, a: combat_action(lang, a, obs)  # noqa: E731
    else:
        hp, max_hp = view["hp"], view["max_hp"]
        name = lambda lang, a: T(lang, "go to {0}", NAMES[lang]["map"][point_type(view, a)])  # noqa: E731
    floor = play.floor_of(k)

    # The candidates: the log's for a combat search; for a map move, V of each next point's afterstate, the chosen first.
    if combat:
        candidates = e["candidates"]
    else:
        scored = map_options(view)
        if scored[best([a for a, _ in scored], [v for _, v in scored])][0] != e["chosen"]:
            raise SystemExit(f"decision {decision}: V's best map move is not the one the brain chose")
        ranked = sorted(scored, key=lambda o: (o[0] != e["chosen"], -o[1]))
        candidates = [{"action": a, "value": v, "ci": 0, "plan": route(view, a)} for a, v in ranked]
        if candidates[0]["plan"] != (e.get("plan") or []):
            raise SystemExit(f"decision {decision}: the route is not the one the brain logged")
    kind = ("fight" if any("win" in c for c in candidates) else "score") if combat else "run"
    options = [
        {
            "name": words(lambda lang, c=c: name(lang, c["action"])),
            "value": round(c["value"], 4),
            "ci": round(c["ci"], 4),
            "win": c.get("win"),
            "hp": c.get("hp"),
            "rooms": [point_type(view, a) for a in [c["action"], *c["plan"]]] if "plan" in c else None,
        }
        for c in candidates[:5]
    ] if len(candidates) > 1 or kind != "score" else []

    # The strip: V's chance from the last answer outside combat, and its change from the one before.
    macro = [j for j in range(k + (0 if combat else 1)) if play.macro(play.log[j])]
    win, before = play.worth(macro[-1]), play.worth(macro[-2])
    recent = [
        words(lambda lang, j=j: f"{T(lang, 'floor {0}', play.floor_of(j))} · {said(lang, play, j)}{shown_value(play, j)}")
        for j in range(k - 1, k - 5, -1)
    ]
    if combat:
        steps, later = turn(e["chosen"], e.get("plan") or [])
        then = [words(lambda lang, a=a: combat_action(lang, a, obs)) for a in steps[1:]]
        top = candidates[0]
        sub = words(lambda lang: T(lang, "this fight {0} · {1} HP left", percent(top["win"], True), round(top["hp"]))) if kind == "fight" else None
        later_words = words(lambda lang: T(lang, "{0} more turns planned", later)) if later else None
    else:
        then = [words(lambda lang, a=a: NAMES[lang]["map"][point_type(view, a)]) for a in e.get("plan") or []]
        sub = words(lambda lang: T(lang, "win chance {0}", percent(candidates[0]["value"])))
        later_words = None
    character = view["character"]
    return {
        "source": {
            "title": title,
            "how": how,
            "decision": decision,
            "seed": SEED,
            "champion": champion_name(play.champion),
            "env": play.env,
        },
        "status": words(lambda lang: f"{NAMES[lang]['character'][character]} · A{view['ascension']} · {T(lang, 'floor {0}', floor)} · {T(lang, 'HP {0}/{1}', hp, max_hp)}"),
        "floor": floor,
        "hp": hp,
        "max_hp": max_hp,
        "surface": "combat" if combat else "map",
        "next": words(lambda lang: name(lang, e["chosen"])),
        "route": not combat,
        "sub": sub,
        "then": then,
        "later": later_words,
        "kind": kind,
        "options": options,
        "brain": e["brain"],
        "seconds": round(e["elapsed_ms"] / 1000, 1),
        "win": win,
        "delta": round(win - before, 4),
        "recent": recent,
    }


def said(lang: str, play: Play, j: int) -> str:
    e = play.log[j]
    if e["surface"] == "combat":
        return combat_action(lang, e["chosen"], e["obs"])
    if not play.macro(e):
        raise SystemExit(f"{play.env} decision {j}: a combat's card prompt, whose options the log does not hold")
    return macro_action(lang, play, j)


def shown_value(play: Play, j: int) -> str:
    """Assist.Value: a macro answer's chance to win, a searched SL combat answer's chance to win the fight."""
    e = play.log[j]
    if play.macro(e):
        return " · " + percent(play.worth(j))
    if e.get("candidates") and "win" in e["candidates"][0]:
        return " · " + percent(e["candidates"][0]["win"], True)
    return ""


# --- the example run -------------------------------------------------------------------------------------------------

ROOMS = {"event": "Event", "rest": "RestSite", "shop": "Shop", "treasure": "Treasure"}


def per_floor(play: Play) -> dict[int, list[int]]:
    """Each floor's log entries: a run view's floor, a combat's or a card prompt's the floor of the next run view."""
    out: dict[int, list[int]] = {}
    for k in range(len(play.log)):
        out.setdefault(play.floor_of(k), []).append(k)
    return out


def example_run(play: Play) -> dict:
    rec = play.record
    fights = {c["floor"]: c for c in rec["combats"]}
    floors, acts, act = [], [], -1
    for floor, ks in sorted(per_floor(play).items()):
        views = [play.log[k]["run_view"] for k in ks if "run_view" in play.log[k]]
        first = fights[floor]["act"] if floor in fights else views[0]["act"]
        row = {"floor": floor, "act": first, "macro": len(views), "other": len(ks) - len(views)}
        if first != act:
            act = first
            m = next(v for v in views if v["act"] == act)["map"]
            acts.append({"id": next(v for v in views if v["act"] == act)["act_id"], "ancient": m["ancient"], "boss": m["boss"]})
            row["room"] = "Ancient"
        elif floor in fights:
            row["room"] = fights[floor]["room"]
        else:
            row["room"] = next(ROOMS[play.log[k]["surface"]] for k in ks if play.log[k]["surface"] in ROOMS)
        if floor in fights:
            c = fights[floor]
            row |= {"encounter": c["encounter"], "hp": c["hp"], "end_hp": c["end"]["hp"], "max_hp": c["max_hp"]}
        else:
            row |= {"hp": views[0]["hp"], "max_hp": views[0]["max_hp"]}
        floors.append(row)
    if sum(f["macro"] + f["other"] for f in floors) != len(play.log) or len(floors) != rec["floor"]:
        raise SystemExit("the example run's floors do not add up to its log")
    return {
        "source": {
            "command": PLAY["SL"],
            "seed": SEED,
            "outcome": rec["outcome"],
            "floor": rec["floor"],
            "decisions": len(play.log),
            "surfaces": rec["surfaces"],
            "how": "combats (floor, room, encounter, HP at start and end) from the run's record; decisions per floor from "
            "its decision log: those with a run view are outside combat, the others are a combat's decisions and card "
            "prompts, each on the floor of the next run view; a floor without a combat is the room its decisions were "
            "on (an event may have been a ? point), and each act's first floor is its Ancient",
        },
        "acts": acts,
        "floors": floors,
    }


def example_nodes(play: Play) -> dict:
    """The first three nodes: each node's steps and where the game saves (docs: environments/information §4)."""
    rec = play.record
    fights = {c["floor"]: c for c in rec["combats"]}
    floors = per_floor(play)
    nodes = []
    for floor in (1, 2, 3):
        ks = floors[floor]
        if floor == 1:
            point = "Ancient"
        else:
            move = floors[floor - 1][-1]
            point = point_type(play.log[move]["run_view"], play.log[move]["chosen"])
        steps = [{"kind": "arrive", "save": "run_start" if floor == 1 else "arrival"}]
        if point == "Unknown":
            steps.append({"kind": "roll", "becomes": play.log[ks[0]]["surface"]})
        fight = [k for k in ks if play.log[k]["surface"] == "combat" or (play.log[k]["surface"] == "card_choice" and not play.macro(play.log[k]))]
        for k in ks:
            e = play.log[k]
            if k in fight:
                if k == fight[0]:
                    c = fights[floor]
                    steps.append({"kind": "combat", "encounter": c["encounter"], "hp": c["hp"], "max_hp": c["max_hp"], "end_hp": c["end"]["hp"], "decisions": [fight[0], fight[-1]]})
                    steps.append({"kind": "save", "save": "combat_won"})
                continue
            if e["surface"] == "card_choice":
                steps.append({"kind": "prompt", "decisions": [k, k]})
                continue
            if point == "Ancient" and e["surface"] == "event" and e["chosen"] == "proceed":
                steps.append({"kind": "save", "save": "ancient"})
            steps.append({"kind": "decision", "surface": e["surface"], "action": e["chosen"], "decision": k})
        nodes.append({"floor": floor, "point": point, "steps": steps})
    return {
        "source": {
            "command": PLAY["SL"],
            "seed": SEED,
            "how": "the decisions of floors 1-3 from the run's decision log, the floor-2 combat from its record; the game's "
            "save points from the game's code (docs: environments/information §4)",
        },
        "nodes": nodes,
    }


# --- V along the run -------------------------------------------------------------------------------------------------


def series(play: Play) -> list[dict]:
    """Every decision with a run view, and V's value of it: the chance to win as V reads the state there."""
    ks = [k for k, e in enumerate(play.log) if "run_view" in e]
    vals = V([play.log[k]["run_view"] for k in ks])
    out = []
    for k, v in zip(ks, vals, strict=True):
        e, view = play.log[k], play.log[k]["run_view"]
        out.append({"d": k, "s": e["surface"], "floor": view["floor"], "act": view["act"], "hp": view["hp"], "max_hp": view["max_hp"], "chosen": e["chosen"], "n": e["legal"], "v": round(v, 4)})
    return out


def rest_options(play: Play, decision: int) -> list[dict]:
    """A rest site's two options as L0 on the clone values them: resting heals 30 % of max HP (HealRestSiteOption), and
    smithing is worth the best upgrade, which the run then has at its next map decision."""
    k = play.by_id[decision]
    e, view = play.log[k], play.log[k]["run_view"]
    if e["chosen"] != "rest:SMITH" or e["legal"] != 2:
        raise SystemExit(f"decision {decision} is not a rest site that smithed with two options")
    healed = min(view["max_hp"], view["hp"] + view["max_hp"] * 3 // 10)
    upgraded = gained(view["deck"], play.view(k + 1)["deck"])
    return [
        {"action": "rest:SMITH", "value": round(play.worth(k), 4), "after": {"deck": upgraded}},
        {"action": "rest:HEAL", "value": round(V([view | {"hp": healed}])[0], 4), "after": {"hp": healed}},
    ]


def log_line(play: Play, decision: int) -> dict:
    """A decision log line as the figure abbreviates it: without its observation, history and run view."""
    return {k: v for k, v in play.at(decision).items() if k not in ("obs", "history", "run_view")}


def macro_examples(play: Play) -> dict:
    k = play.by_id[PANEL_MAP]
    view = play.log[k]["run_view"]
    rest = LOG_LINES["rest"]
    return {
        "source": {
            "command": PLAY["SL"],
            "seed": SEED,
            "outcome": play.record["outcome"],
            "floor": play.record["floor"],
            "values": "the champion's V (eval/champions/sl.json's value): series.v of each run view; a map move's option "
            "of its afterstate; the chosen option of any other decision of the run where its node's search ends, the "
            "next map decision's view; a rest site's other option of its afterstate",
        },
        "decisions": {
            "rest": {"decision_id": rest, "surface": "rest", "chosen": play.at(rest)["chosen"], "run_view": play.at(rest)["run_view"], "options": rest_options(play, rest)},
            "map": {"decision_id": PANEL_MAP, "surface": "map", "chosen": play.log[k]["chosen"], "run_view": view, "options": [{"action": a, "value": round(v, 4), "route": route(view, a)} for a, v in map_options(view)]},
        },
        "log_lines": {name: log_line(play, d) for name, d in LOG_LINES.items()},
        "series": series(play),
    }


def learning_td(play: Play) -> dict:
    return {
        "source": {
            "command": PLAY["SL"],
            "seed": SEED,
            "values": "v: the champion's V (eval/champions/sl.json's value) of the run view at each decision that has one; "
            "outcome: the run's, 1 for a win",
        },
        "outcome": 1 if play.record["outcome"] == "won" else 0,
        "floor": play.record["floor"],
        "series": [{x: s[x] for x in ("d", "s", "floor", "act", "hp", "max_hp", "v")} for s in series(play)],
    }


# --- the panel -------------------------------------------------------------------------------------------------------


def panel_examples(sl: Play, nosl: Play) -> dict:
    k = sl.by_id[PANEL_MAP]
    e, view = sl.log[k], sl.log[k]["run_view"]
    later = [x["chosen"] for x in sl.log[k + 1 :] if x["surface"] == "map" and x["run_view"]["act"] == view["act"]]
    m = view["map"]
    return {
        "source": f"sl_combat, nosl_combat, map_options: three decisions of the book's example seed ({SEED}, the Silent "
        "at A0), one from each play of eval/champions/sl.json and eval/champions/nosl.json; map: the map of map_options' "
        "decision. Texts made as Describe and L10n make them.",
        "sl_combat": answer(sl, PANEL_SL, "SL combat", "the first decision of the act 3 boss fight: the SL planner plans the fight"),
        "nosl_combat": answer(nosl, PANEL_NOSL, "NoSL combat", "a turn's first decision in the act 3 boss fight: PIMC over 8 worlds"),
        "map_options": answer(sl, PANEL_MAP, "map", "a map decision: L0 values each next point with V"),
        "map": {
            "source": {
                "command": PLAY["SL"],
                "seed": SEED,
                "decision": PANEL_MAP,
                "floor": view["floor"],
                "hp": view["hp"],
                "max_hp": view["max_hp"],
                "act": view["act"],
                "act_id": view["act_id"],
                "route": "the route the brain planned after its pick, as the decision log holds it (MacroBrain.Plan)",
                "travelled": later,
            },
            "brain": e["brain"],
            "points": m["points"],
            "visited": m["visited"],
            "boss": m["boss"],
            "ancient": m["ancient"],
            "chosen": e["chosen"],
            "options": [{"action": a, "value": round(v, 4)} for a, v in map_options(view)],
            "route": e.get("plan") or [],
        },
    }


# --- combat examples -------------------------------------------------------------------------------------------------


def slim(obs: dict) -> dict:
    """A combat observation as PlanTimeline reads it."""
    p = obs["player"]
    return {
        "round": obs["round"],
        "encounter": obs["encounter"],
        "room": obs["room"],
        "act": obs["act"],
        "player": {k: p[k] for k in ("hp", "max_hp", "block", "energy")} | ({"powers": p["powers"]} if p["powers"] else {}),
        "hand": [{k: c[k] for k in ("id", "cost", "up", "uid") if k in c} for c in obs["hand"]],
        "draw": len(obs["draw"]),
        "discard": len(obs["discard"]),
        "exhaust": len(obs["exhaust"]),
        "potions": obs["potions"],
        "enemies": [{k: x[k] for k in ("uid", "id", "hp", "max_hp", "block", "intents", "powers") if k in x} for x in obs["enemies"]],
    }


def combat_examples(play: Play) -> dict:
    old = json.loads((DATA / "combat-examples.json").read_text())
    fight = next(c for c in play.record["combats"] if c["floor"] == PLAN_FIGHT)
    ks = [k for k, e in enumerate(play.log) if e["surface"] == "combat" and play.floor_of(k) == PLAN_FIGHT]
    first = play.log[ks[0]]
    if any(len(play.log[k]["obs"]["enemies"]) != 1 for k in ks):
        raise SystemExit(f"the fight on floor {PLAN_FIGHT} is not against one enemy")
    nosl = old["nosl"]
    nosl["source"] = {
        "seed": nosl["source"]["seed"],
        "floor": nosl["source"]["floor"],
        "outcome": nosl["source"]["outcome"],
        "how": "one combat decision of a NoSL run of PIMC on that seed, as its decision log holds it",
    }
    return {
        "sl": {
            "source": {
                "command": PLAY["SL"],
                "seed": SEED,
                "how": f"the combat decisions of the fight on floor {PLAN_FIGHT}, as the decision log holds them; a step "
                "is searched when the planner answered it with candidates",
            },
            "fight": {
                "floor": PLAN_FIGHT,
                "encounter": fight["encounter"],
                "start": {"hp": fight["hp"], "max_hp": fight["max_hp"]},
                "end": fight["end"],
                "first": {k: first[k] for k in ("plan", "chosen", "candidates", "elapsed_ms", "budget", "legal")},
                "steps": [
                    {"chosen": play.log[k]["chosen"], "searched": "candidates" in play.log[k], "elapsed_ms": round(play.log[k]["elapsed_ms"], 1), "obs": slim(play.log[k]["obs"])}
                    for k in ks
                ],
            },
        },
        "nosl": nosl,
    }


# --- reports ---------------------------------------------------------------------------------------------------------


def report(character: str) -> tuple[str, dict]:
    path = SL_CHAMPION["reports"][character]
    return path, json.loads((ROOT / path).read_text())


def surface_counts(play: Play) -> dict:
    _, r = report("SILENT")
    runs = r["runs"]
    names = sorted({s for x in runs for s in x["surfaces"]}, key=lambda s: -sum(x["surfaces"].get(s, 0) for x in runs))
    prompts = re.search(r"combat prompts (\S+?),", r["config"]["champion"]).group(1)
    rec = play.record
    return {
        "source": {
            "report": "the champion's Silent report (eval/champions/sl.json's reports.SILENT)",
            "what": "each run's surfaces counts, summed over the report's runs; the example is the SL play of seed "
            f"{SEED} ({PLAY['SL']}); card prompts outside combat are the card prompts less those of the combat "
            "prompts' brain",
        },
        "runs": len(runs),
        "won": sum(x["outcome"] == "won" for x in runs),
        "surfaces": [
            {
                "surface": s,
                "decisions": sum(x["surfaces"].get(s, 0) for x in runs),
                "runs": sum(1 for x in runs if x["surfaces"].get(s, 0) > 0),
                "example": rec["surfaces"].get(s, 0),
            }
            for s in names
        ],
        "card_choice_macro": {
            "all": sum(x["surfaces"].get("card_choice", 0) - x["brains"].get(prompts, 0) for x in runs),
            "example": rec["surfaces"].get("card_choice", 0) - rec["brains"].get(play.prompts(), 0),
        },
        "example": {"seed": SEED, "outcome": rec["outcome"], "floor": rec["floor"]},
    }


def champion_wins() -> dict:
    """Per character, the runs of the champion's report and how many it won, best first (the first page's cards)."""
    rows = []
    for character in SL_CHAMPION["reports"]:
        _, r = report(character)
        (ascension,) = {x["ascension"] for x in r["runs"]}
        won = sum(x["outcome"] == "won" for x in r["runs"])
        rows.append({"character": character, "ascension": ascension, "runs": len(r["runs"]), "won": won})
    return {
        "source": {
            "what": "per character: the runs of the champion's report and how many it won",
            "report": "the champion's reports (eval/champions/sl.json's reports)",
        },
        "env": SL_CHAMPION["env"],
        "characters": sorted(rows, key=lambda row: -row["won"] / row["runs"]),
    }


def eval_reports() -> dict:
    """Where the champion's Necrobinder runs that were lost ended (learn/gate.py's rule)."""
    _, r = report("NECROBINDER")
    runs = r["runs"]
    rows: dict[int, dict] = {}
    no_end = 0
    for x in runs:
        if x["outcome"] != "lost":
            continue
        last = (x["combats"] or [{}])[-1]
        here = last.get("floor") == x["floor"]
        no_end += here and not last.get("end")
        where = last["room"] if here and last.get("end", {}).get("died") else "outside"
        row = rows.setdefault(x["floor"], {"floor": x["floor"], "Boss": 0, "Elite": 0, "Monster": 0, "outside": 0})
        row[where] += 1
    bosses = [[c for x in runs for c in x["combats"] if c["act"] == act and c["room"] == "Boss"] for act in range(3)]
    return {
        "source": {
            "what": "one report: per floor, where its lost runs died",
            "report": "the champion's Necrobinder report (eval/champions/sl.json's reports.NECROBINDER)",
        },
        "report": {
            "env": r["config"]["env"],
            "seedSet": r["seed_set"],
            "runs": len(runs),
            "won": sum(x["outcome"] == "won" for x in runs),
            "bossesReached": [len(b) for b in bosses],
            "bossesBeaten": [sum(1 for c in b if c.get("end") and not c["end"]["died"]) for b in bosses],
            "deaths": {"byFloor": [rows[f] for f in sorted(rows)], "noEnd": no_end},
        },
    }


# --- the net files ---------------------------------------------------------------------------------------------------

DTYPES = {1: "float", 7: "int64", 11: "double", 9: "bool"}


def tensor(t) -> dict:
    dims = [d.dim_param or d.dim_value for d in t.type.tensor_type.shape.dim]
    return {"name": t.name, "dtype": DTYPES[t.type.tensor_type.elem_type], "shape": dims}


def graph(b64: str) -> dict:
    import base64
    from collections import Counter

    model = onnx.load_from_string(base64.b64decode(b64))
    g = model.graph
    shapes = [{"name": i.name, "shape": list(i.dims)} for i in g.initializer]
    count = 0
    for i in g.initializer:
        n = 1
        for d in i.dims:
            n *= d
        count += n
    return {
        "opset": model.opset_import[0].version,
        "producer": f"{model.producer_name} {model.producer_version}",
        "inputs": [tensor(t) for t in g.input],
        "outputs": [tensor(t) for t in g.output],
        "initializers": shapes,
        "params": count,
        "ops": dict(Counter(n.op_type for n in g.node).most_common()),
    }


def shape_of(v) -> object:
    """A test entry's field as the figure names it."""
    if isinstance(v, dict):
        return sorted(v)
    if isinstance(v, list):
        return f"array of {len(v)}" if v and all(isinstance(x, int | float) for x in v) else "array"
    return type(v).__name__


def anatomy(path: str) -> dict:
    """A net file's fields: names, types, sizes and shapes, and the small fields' values; no weights."""
    f = json.loads((ROOT / path).read_text())
    fields = []
    for key, v in f.items():
        field = {"key": key, "type": {dict: "object", list: "array"}.get(type(v), type(v).__name__)}
        if key == "vocab":
            field["tables"] = {t: {"size": len(ids), "first": ids[1:4]} for t, ids in v.items()}
        elif key == "state":
            field |= {"names": v["names"], "mean": v["mean"], "std": v["std"]}
        elif key == "fit":
            field["values"] = v
        elif key == "onnx":
            field |= {"size": len(v), "onnx": graph(v)}
        elif key == "tests":
            field |= {"size": len(v), "entry": {k: shape_of(x) for k, x in v[0].items()}, "values": [t["value"] for t in v] if "value" in v[0] else None}
        else:
            field["value"] = v
        fields.append(field)
    return {"file": path, "bytes": (ROOT / path).stat().st_size, "fields": fields}


def learning_netfiles() -> dict:
    o = SL_CHAMPION["options"]
    return {
        "source": {
            "files": "the champion's two net files: eval/champions/sl.json's value (v) and combat-net (c)",
            "note": "structure and shapes only, no weights",
        },
        "v": anatomy(o["value"]),
        "c": anatomy(o["combat-net"]),
    }


# --- the kept files' sources -----------------------------------------------------------------------------------------


def keep_sources() -> None:
    """Two data files that do not come from the champion: only their sources are rewritten."""
    path = DATA / "book/round-start.json"
    data = json.loads(path.read_text())
    data["source"] = {
        "what": "tests[0].obs of a combat net file: a held-out start of a round from NoSL self-play, as the trainer read it",
        "fields": "the observation as Protocol serializes it; the draw pile is sorted by card key, as every observation sends it",
    }
    write(path, data)
    path = DATA / "book/eval-pairs.json"
    data = json.loads(path.read_text())
    data["source"] = {
        "what": "per seed, in the seed set order: [floor of a, floor of b]; aWon and bWon are the indices of the seeds each "
        "side won",
        "reports": "pairs of committed reports on the same seeds: SL whole runs on promotion-1-300, NoSL whole runs on "
        "promotion-1; the nets are named A to E",
    }
    write(path, data, compact=True)


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    folder = Path(sys.argv[1])
    check_v()
    sl = Play("SL", lines(folder / "sl-log.jsonl"), lines(folder / "sl-records.jsonl")[0], SL_CHAMPION)
    nosl = Play("NoSL", lines(folder / "nosl-log.jsonl"), lines(folder / "nosl-records.jsonl")[0], NOSL_CHAMPION)
    write(DATA / "book/example-run.json", example_run(sl))
    write(DATA / "book/example-nodes.json", example_nodes(sl))
    write(DATA / "macro-examples.json", macro_examples(sl))
    write(DATA / "book/learning-td.json", learning_td(sl))
    write(DATA / "book/panel-examples.json", panel_examples(sl, nosl))
    write(DATA / "combat-examples.json", combat_examples(sl))
    write(DATA / "book/surface-counts.json", surface_counts(sl))
    write(DATA / "book/eval-reports.json", eval_reports())
    write(DATA / "book/champion-wins.json", champion_wins())
    write(DATA / "book/learning-netfiles.json", learning_netfiles())
    keep_sources()


if __name__ == "__main__":
    main()
