import argparse
import json
import shlex

import bosslab


def view(**changes):
    v = {
        "act": 1,
        "floor": 20,
        "max_hp": 70,
        "map": {"boss": "KAISER_CRAB_BOSS"},
        "deck": [{"id": "STRIKE_SILENT"}, {"id": "SURVIVOR", "up": True}],
        "relics": [{"id": "RING_OF_THE_SNAKE"}, {"id": "OLD_COIN", "used_up": True}, {"id": "X", "status": "Disabled"}],
        "potions": ["FIRE_POTION", None],
    }
    return {**v, **changes}


def test_the_loadout_keeps_the_live_relics_and_starts_at_the_acts_hp_share():
    args = bosslab.loadout(view())
    opts = dict(zip(args[::2], args[1::2], strict=True))
    assert opts["--deck"] == "STRIKE_SILENT,SURVIVOR+"
    assert opts["--relics"] == "RING_OF_THE_SNAKE" and opts["--potions"] == "FIRE_POTION"
    assert opts["--hp"] == str(round(0.81 * 70)) and opts["--encounter"] == "KAISER_CRAB_BOSS"


def test_every_option_of_a_decision_fights_with_the_same_seeds(tmp_path):
    log, after = tmp_path / "log.jsonl", tmp_path / "after.jsonl"
    log.write_text(
        json.dumps({"surface": "card_reward", "run": "00000000000000ff", "decision_id": 3, "run_view": view()}) + "\n"
    )
    options = [
        {"action": "card_0", "value": 20.1, "after": {"deck": view()["deck"] + [{"id": "NEUTRALIZE"}]}},
        {"action": "card_alt_0", "value": 20.0, "after": {}},
    ]
    after.write_text(
        json.dumps(
            {
                "surface": "card_reward",
                "run": "00000000000000ff",
                "decision_id": 3,
                "chosen": "card_0",
                "options": options,
            }
        )
        + "\n"
    )
    out = tmp_path / "lab"
    bosslab.tasks(
        argparse.Namespace(
            logs=str(log),
            afterstates=str(after),
            acts="1",
            every=1,
            seeds=2,
            budget=bosslab.BUDGET,
            combat_net="c2.json",
            results="res",
            out=str(out),
        )
    )
    lines = [shlex.split(x) for x in (out / "tasks.txt").read_text().splitlines()]
    assert [t[t.index("--seed") + 1] for t in lines] == ["258", "258"]
    assert lines[0][lines[0].index("--deck") + 1].endswith(",NEUTRALIZE") and "--beam" in lines[1]
    decision = json.loads((out / "decisions.jsonl").read_text())
    assert [o["key"] for o in decision["options"]] == ["00000000000000ff-3-0", "00000000000000ff-3-1"]


def test_a_won_fight_scores_above_any_lost_one():
    won = {"won": True, "hp": 0, "hp_start": 50, "enemy_hp": 0, "enemy_max_hp": 300}
    lost = {"won": False, "hp": 0, "hp_start": 50, "enemy_hp": 1, "enemy_max_hp": 300}
    assert bosslab.score(lost) < 0.5 <= bosslab.score(won)
    assert bosslab.score({**won, "hp": 50}) == 1
