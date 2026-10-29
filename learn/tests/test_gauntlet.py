import json

import pytest
from conftest import make_view
from typer.testing import CliRunner

import gauntlet


def run(run_id, seed, floor, outcome, combats):
    return {
        "run_id": run_id,
        "seed": seed,
        "outcome": outcome,
        "floor": floor,
        "config": {"gauntlet": True},
        "combats": combats,
    }


def fight(act, floor, room, died, enemy_hp=0):
    end = {"died": died, "hp": 0 if died else 30, "enemy_hp": enemy_hp, "enemy_max_hp": 100}
    return {"act": act, "floor": floor, "room": room, "hp": 50, "max_hp": 70, "end": end}


RUNS = [
    run("a", "S1", 17, "lost", [fight(0, 6, "Monster", False), fight(0, 17, "Boss", True, 25)]),
    run("b", "S2", 9, "lost", [fight(0, 9, "Elite", True, 50)]),
    run("c", "S3", 48, "won", [fight(0, 17, "Boss", False), fight(1, 33, "Boss", False), fight(2, 48, "Boss", False)]),
]


def test_the_report_counts_progress_bosses_elites_and_deaths():
    r = gauntlet.runs_report(RUNS)
    assert r["runs"] == 3 and r["won"] == 1
    assert r["progress"] == pytest.approx((17.75 + 9.5 + 49) / 3)
    assert r["boss"]["0"] == {"reached": 2, "beaten": 1, "rate": 0.5} and r["boss"]["2"]["rate"] == 1.0
    assert r["elites_per_run"] == pytest.approx(1 / 3) and r["elite_deaths"] == 1
    assert r["died_in"] == {"Boss": 1, "Elite": 1}


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
    r = gauntlet.logs_report(str(log), {"a"})
    assert r["rest"] == {"40%-70%": {"HEAL": 1}, "90%-100%": {"SMITH": 1}}
    assert r["shop"] == {"potion": 1, "leave": 1}


def test_against_the_champion_it_pairs_by_seed_and_promotes_on_a_lower_bound_above_zero(tmp_path):
    # The champion died outside combat 5 floors earlier on S1 and S2, and never finished S3.
    worse = [r | {"floor": r["floor"] - 5, "combats": []} for r in RUNS[:2]]
    recs, champ = tmp_path / "r.jsonl", tmp_path / "c.jsonl"
    recs.write_text("".join(json.dumps(r) + "\n" for r in RUNS))
    champ.write_text("".join(json.dumps(r) + "\n" for r in worse))
    result = CliRunner().invoke(gauntlet.script, ["--records", str(recs), "--against", str(champ)])
    assert result.exit_code == 0, result.output
    pairs = json.loads(result.output)["against"]
    assert pairs["pairs"] == 2 and pairs["diff"] == pytest.approx((5.75 + 5.5) / 2) and pairs["promote"]
