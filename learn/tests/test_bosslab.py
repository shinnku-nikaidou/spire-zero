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


def test_the_door_swaps_the_pick_for_each_option_at_the_level_it_reached():
    door = [{"id": "STRIKE_SILENT"}, {"id": "NEUTRALIZE", "up": 1}, {"id": "NEUTRALIZE"}]
    assert bosslab.swapped(door, [{"id": "NEUTRALIZE", "up": 1}], [{"id": "DASH"}])[-1] == {"id": "DASH", "up": 1}
    assert bosslab.swapped(door, [{"id": "NEUTRALIZE"}], []) == door[:2]
    assert bosslab.swapped(door, [], [{"id": "DASH"}]) == door + [{"id": "DASH"}]
    assert bosslab.swapped(door, [{"id": "BACKFLIP"}], []) is None


def test_door_tasks_fight_the_runs_loadout_before_its_boss_at_the_hp_it_entered_with(tmp_path):
    run = "00000000000000ff"
    log, rec, after = tmp_path / "log.jsonl", tmp_path / "rec.jsonl", tmp_path / "after.jsonl"
    reward = view(floor=20)
    door = view(floor=32, deck=reward["deck"] + [{"id": "NEUTRALIZE", "up": 1}, {"id": "DASH"}])
    log.write_text(
        "\n".join(
            json.dumps(e)
            for e in [
                {"surface": "card_reward", "run": run, "decision_id": 3, "run_view": reward},
                {"surface": "map", "run": run, "decision_id": 9, "run_view": door},
                {"surface": "rewards", "run": run, "decision_id": 12, "run_view": view(floor=33)},
            ]
        )
        + "\n"
    )
    boss = {"act": 1, "floor": 33, "room": "Boss", "hp": 55, "max_hp": 75}
    rec.write_text(json.dumps({"run_id": run, "combats": [{**boss, "room": "Monster", "floor": 30}, boss]}) + "\n")
    options = [
        {"action": "card:0", "value": 20.1, "after": {"deck": reward["deck"] + [{"id": "NEUTRALIZE"}]}},
        {"action": "card:1", "value": 20.2, "after": {"deck": reward["deck"] + [{"id": "BACKFLIP"}]}},
        {"action": "card_alt:Skip", "value": 20.0, "after": {}},
    ]
    after.write_text(
        json.dumps({"surface": "card_reward", "run": run, "decision_id": 3, "chosen": "card:0", "options": options})
    )
    out = tmp_path / "lab"
    bosslab.doors(
        argparse.Namespace(
            logs=str(log),
            records=str(rec),
            afterstates=str(after),
            act=1,
            acts=None,
            max_runs=None,
            seeds=2,
            budget=bosslab.BUDGET,
            combat_net="c2.json",
            results="res",
            out=str(out),
        )
    )
    lines = [shlex.split(x) for x in (out / "tasks.txt").read_text().splitlines()]
    decks = [t[t.index("--deck") + 1] for t in lines]
    assert decks == [
        "STRIKE_SILENT,SURVIVOR+,DASH,NEUTRALIZE+",
        "STRIKE_SILENT,SURVIVOR+,DASH,BACKFLIP+",
        "STRIKE_SILENT,SURVIVOR+,DASH",
    ]
    assert all(t[t.index("--hp") + 1] == "55" and t[t.index("--max-hp") + 1] == "75" for t in lines)
    decision = json.loads((out / "decisions.jsonl").read_text())
    assert decision["act"] == 1 and decision["door_floor"] == 32 and len(decision["options"]) == 3


def test_a_won_fight_scores_above_any_lost_one():
    won = {"won": True, "hp": 0, "hp_start": 50, "enemy_hp": 0, "enemy_max_hp": 300}
    lost = {"won": False, "hp": 0, "hp_start": 50, "enemy_hp": 1, "enemy_max_hp": 300}
    assert bosslab.score(lost) < 0.5 <= bosslab.score(won)
    assert bosslab.score({**won, "hp": 50}) == 1
