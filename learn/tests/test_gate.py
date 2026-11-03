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


def test_whole_runs_pair_on_wins_and_floors_and_promote_on_the_wins(tmp_path):
    # The challenger wins three of four seeds the champion lost; the champion never finished S5.
    mine = [run(f"r{i}", f"S{i}", 48, "won", []) for i in range(3)]
    mine += [run("r3", "S3", 30, "lost", []), run("r5", "S5", 48, "won", [])]
    theirs = [run(f"c{i}", f"S{i}", 20, "lost", []) for i in range(4)]
    recs, champ = tmp_path / "r.jsonl", tmp_path / "c.jsonl"
    recs.write_text("".join(json.dumps(r) + "\n" for r in mine))
    champ.write_text("".join(json.dumps(r) + "\n" for r in theirs))
    result = CliRunner().invoke(gate.script, ["--records", str(recs), "--against", str(champ)])
    assert result.exit_code == 0, result.output
    report = json.loads(result.output)
    assert report["won"] == 4
    pairs = report["against"]
    assert (pairs["pairs"], pairs["won"], pairs["champion_won"]) == (4, 3, 0)
    assert pairs["wins_diff"] == 0.75 and pairs["floor_diff"] == pytest.approx((28 * 3 + 10) / 4)
    assert pairs["wins_lower_95"] == pytest.approx(0.75 - 1.645 * 0.25) and pairs["promote"]

