import json

import pytest
from conftest import make_view
from typer.testing import CliRunner

import gate


def run(run_id, seed, floor, outcome, combats):
    return {
        "run_id": run_id,
        "seed": seed,
        "outcome": outcome,
        "act": 0 if floor <= 17 else 1 if floor <= 33 else 2,
        "floor": floor,
        "config": {},
        "combats": combats,
    }


def fight(act, floor, room, died):
    end = {"died": died, "hp": 0 if died else 30}
    return {"act": act, "floor": floor, "room": room, "hp": 50, "max_hp": 70, "end": end}


RUNS = [
    run("a", "S1", 17, "lost", [fight(0, 6, "Monster", False), fight(0, 17, "Boss", True)]),
    run("b", "S2", 9, "lost", [fight(0, 9, "Elite", True)]),
    run("c", "S3", 48, "won", [fight(0, 17, "Boss", False), fight(1, 33, "Boss", False), fight(2, 48, "Boss", False)]),
]


def test_the_report_counts_bosses_elites_and_deaths():
    r = gate.runs_report(RUNS)
    assert r["runs"] == 3 and r["won"] == 1
    assert r["boss"]["0"] == {"reached": 2, "beaten": 1, "rate": 0.5} and r["boss"]["2"]["rate"] == 1.0
    assert r["elites_per_run"] == pytest.approx(1 / 3) and r["elite_deaths"] == 1
    assert r["died_in"] == {"0": {"Boss": 1, "Elite": 1}} and r["floor"] == pytest.approx((17 + 9 + 48) / 3)


def test_rest_sites_by_hp_and_shop_purchases_by_what_the_run_gained(tmp_path):
    log = tmp_path / "log.jsonl"
    lines = [
        {"run": "a", "surface": "rest", "chosen": "rest:HEAL", "run_view": make_view(hp=30, max_hp=70)},
        {"run": "a", "surface": "rest", "chosen": "rest:SMITH", "run_view": make_view(hp=69, max_hp=70)},
        {"run": "a", "surface": "shop", "chosen": "buy:3", "run_view": make_view()},
        {"run": "a", "surface": "shop", "chosen": "leave", "run_view": make_view(potions=["FIRE", "X", None])},
        {"run": "z", "surface": "rest", "chosen": "rest:HEAL", "run_view": make_view()},  # no record: left out
    ]
    log.write_text("".join(json.dumps(x) + "\n" for x in lines))
    r = gate.logs_report(str(log), {"a"})
    assert r["rest"] == {"40%-70%": {"HEAL": 1}, "90%-100%": {"SMITH": 1}}
    assert r["shop"] == {"potion": 1, "leave": 1}


def test_the_map_counts_a_choice_between_a_monster_and_a_rest_site_by_hp(tmp_path):
    def at(hp, chosen, *types):
        view = make_view(hp=hp, max_hp=70)
        nxt = [{"col": c, "row": 1, "type": t, "next": []} for c, t in enumerate(types)]
        here = {"col": 0, "row": 0, "type": "Ancient", "next": [{"col": p["col"], "row": 1} for p in nxt]}
        view["map"] = {"points": [here, *nxt], "visited": [{"col": 0, "row": 0}]}
        return {"run": "a", "surface": "map", "chosen": f"map:{chosen},1", "run_view": view}

    log = tmp_path / "log.jsonl"
    lines = [at(30, 1, "Monster", "RestSite"), at(65, 0, "Monster", "RestSite", "Elite"), at(65, 0, "Monster", "Shop")]
    log.write_text("".join(json.dumps(x) + "\n" for x in lines))
    r = gate.logs_report(str(log), {"a"})
    assert r["monster_or_rest"] == {"40%-70%": {"RestSite": 1}, "90%-100%": {"Monster": 1}}


def test_a_run_that_won_plain_monster_fights_at_their_start_is_never_judged(tmp_path):
    recs = tmp_path / "r.jsonl"
    recs.write_text(json.dumps(run("a", "S1", 30, "lost", []) | {"config": {"auto_win": [0, 1]}}) + "\n")
    result = CliRunner().invoke(gate.script, ["--records", str(recs)])
    assert isinstance(result.exception, gate.LearnError) and "--auto-win" in str(result.exception)


def test_runs_of_two_game_modes_are_never_read_together(tmp_path):
    recs = tmp_path / "r.jsonl"
    recs.write_text(json.dumps(run("a", "S1", 30, "lost", [])) + "\n" + json.dumps(run("b", "S2", 30, "lost", []) | {"config": {"mode": "Dread"}}) + "\n")
    with pytest.raises(gate.LearnError, match="one game mode"):
        gate.read_records(str(recs))
