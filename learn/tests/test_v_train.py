import base64
import json
import math

import onnx
import pytest
import torch
from conftest import encoder, encoder_command, make_view, read_run
from onnx.reference import ReferenceEvaluator
from typer.testing import CliRunner

import common
import game_ids
from v import data, export, potions, spec, train
from v.net import Batch, Net, values, win_chance


def record(run_id, floor, outcome="lost", combats=()):
    return {
        "run_id": run_id,
        "character": "SILENT",
        "outcome": outcome,
        "floor": floor,
        "config": {},
        "combats": list(combats),
    }


def write_runs(tmp_path, runs=10):
    """A log and its records: runs of three decisions each (floors 1–3), the run i reaching floor 4 + i % 4."""
    log, recs = tmp_path / "log.jsonl", tmp_path / "rec.jsonl"
    with open(log, "w") as lf, open(recs, "w") as rf:
        for i in range(runs):
            run = f"{i:016x}"
            for d in range(3):
                view = make_view(floor=d + 1, hp=60 - 5 * d - i, gold=99 + 10 * i)
                line = {"run": run, "decision_id": d, "env": "SL", "surface": "event", "brain": "l0", "run_view": view}
                lf.write(json.dumps(line, separators=(",", ":")) + "\n")
            combats = [
                {"floor": f, "room": "Monster", "hp": 60, "max_hp": 70, "end": {"died": False, "hp": 55}}
                for f in (2, 3)
            ]
            rf.write(json.dumps(record(run, 4 + i % 4, combats=combats)) + "\n")
    lists = tmp_path / "smoke"
    (tmp_path / "smoke-logs.txt").write_text(f"{log}\n")
    (tmp_path / "smoke-recs.txt").write_text(f"{recs}\n")
    return str(lists)


def test_each_decision_is_a_sample_of_where_its_run_ended_and_each_run_weighs_one(tmp_path):
    samples, runs = data.read_runs(*common.lists(write_runs(tmp_path)), 64, encoder())
    assert len(samples) == 30 and len(runs) == 10
    first = samples[0]
    assert (first["start"], first["end"], first["kind"], first["floor"]) == (1, 4, "died", 4)
    assert first["next"] == [5 / 70, 5 / 70] and samples[2]["next"] == []  # the combats after floors 1 and 3
    assert math.isclose(sum(s["weight"] for s in samples), 10)
    assert {data.held_out(s["run"]) for s in samples} == {True, False}
    # only the held-out samples keep their view, as JSON
    assert all(("view_json" in s) == data.held_out(s["run"]) and "view" not in s for s in samples)
    held = next(s for s in samples if data.held_out(s["run"]))
    assert json.loads(held["view_json"])["floor"] == held["state"][0]


def test_a_lost_runs_last_fight_with_no_end_costs_what_was_left():
    fight = {"floor": 5, "room": "Boss", "hp": 35, "max_hp": 70}
    assert data.next_combats(record("a", 5, combats=[fight]), 4) == [0.5]
    assert data.next_combats(record("a", 5, "failed", combats=[fight]), 4) == [0.0]


def test_the_hazards_are_labelled_up_to_the_floor_a_run_died_on_and_not_past_a_censored_one():
    died = data.sample("a", read_run(make_view(floor=2)), 8, 5, "died")
    censored = data.sample("b", read_run(make_view(floor=2)), 8, 5)
    unlabelled = data.sample("c", read_run(make_view(floor=2)), 8)
    b = Batch([died, censored, unlabelled], {t: {} for t in spec.TABLES}, torch.zeros(len(spec.STATE)), torch.ones(len(spec.STATE)), 8, "cpu")
    assert b.label_mask.tolist() == [[0, 0, 1, 1, 1, 1, 0, 0], [0, 0, 1, 1, 1, 0, 0, 0], [0] * 8]
    assert b.target[0].tolist() == [0, 0, 0, 0, 0, 1, 0, 0] and b.target[1].sum() == 0


def test_the_chance_to_win_is_the_chance_to_survive_every_floor_from_the_start():
    h = torch.tensor([[0.9, 0.5, 0.2, 0.0]])
    assert win_chance(h, torch.tensor([1])).tolist() == [pytest.approx(0.5 * 0.8)]


def test_the_share_of_decisions_where_v_rates_a_potion_below_the_empty_slot():
    held = [data.sample("a", read_run(make_view(potions=p)), 8) for p in (["FIRE", None, None], ["FIRE", "FIRE", "SWIFT"], [None] * 3)]

    def dislikes(potion):
        return lambda part: [-float(s["tokens"]["potions"].count(potion)) for s in part]

    # only the decisions that hold a potion are asked; one potion valued below the empty slot is enough
    assert potions.below_empty(dislikes("FIRE"), held) == {"decisions": 2, "share": 1.0}
    assert potions.below_empty(dislikes("SWIFT"), held) == {"decisions": 2, "share": 0.5}
    assert potions.below_empty(lambda part: [float(len(s["tokens"]["potions"])) for s in part], held)["share"] == 0
    assert potions.below_empty(dislikes("FIRE"), held[2:]) == {"decisions": 0, "share": None}


def held_out_sample(view):
    """A held-out decision that died on floor 9 of 16, with the view the net file keeps for its tests."""
    return data.sample("a", read_run(view), 16, 9, "died") | {"view_json": json.dumps(view)}


def test_the_onnx_graph_gives_the_test_runs_their_chance_to_win_as_torch_gave_it():
    samples = [held_out_sample(make_view(floor=f, hp=30 + f)) for f in (1, 2, 3)]
    tables = game_ids.macro_tables()
    torch.manual_seed(0)
    model = Net({t: len(ids) for t, ids in tables.items()}, 16)
    net = export.net_file("t", 16, tables, torch.zeros(len(spec.STATE)), torch.full((len(spec.STATE),), 10.0), model, samples, {}, 0, {})
    assert net["state"]["names"] == spec.STATE and len(net["tests"]) == 3
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    assert [i.name for i in graph.graph.input] == spec.INPUTS
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", read_run(test["run"]), 16)], index, torch.zeros(len(spec.STATE)), torch.full((len(spec.STATE),), 10.0), 16, "cpu")
        feed = {name: x.numpy() for name, x in zip(spec.INPUTS, b.inputs(), strict=True)}
        [hazards] = ReferenceEvaluator(graph).run(None, feed)
        assert math.isclose(float(win_chance(torch.tensor(hazards), b.start)[0]), test["value"], rel_tol=1e-5)


def test_a_net_files_weights_read_back_give_its_test_runs_their_values_and_keep_the_second_head_anew():
    samples = [held_out_sample(make_view(floor=f, hp=30 + f)) for f in (1, 2, 3)]
    tables = game_ids.macro_tables()
    sizes = {t: len(ids) for t, ids in tables.items()}
    torch.manual_seed(0)
    model = Net(sizes, 16)
    net = export.net_file("t", 16, tables, torch.zeros(len(spec.STATE)), torch.full((len(spec.STATE),), 10.0), model, samples, {}, 0, {})
    torch.manual_seed(1)
    fresh = Net(sizes, 16)
    weights = export.weights(net, fresh)
    assert torch.equal(weights["aux_head.weight"], fresh.state_dict()["aux_head.weight"])
    fresh.load_state_dict(weights)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", read_run(test["run"]), 16)], index, torch.zeros(len(spec.STATE)), torch.full((len(spec.STATE),), 10.0), 16, "cpu")
        with torch.no_grad():
            assert math.isclose(float(values(fresh.eval(), b)[0]), test["value"], rel_tol=1e-5)


def test_weights_the_export_merged_as_equal_read_back_apart():
    samples = [held_out_sample(make_view(floor=f, hp=30 + f)) for f in (1, 2, 3)]
    tables = game_ids.macro_tables()
    sizes = {t: len(ids) for t, ids in tables.items()}
    model = Net(sizes, 16)
    with torch.no_grad():  # as v9-0's: the damage and block rows all zero, so the ONNX keeps one of them
        model.card_damage.weight.zero_()
        model.card_block.weight.zero_()
    net = export.net_file("t", 16, tables, torch.zeros(len(spec.STATE)), torch.full((len(spec.STATE),), 10.0), model, samples, {}, 0, {})
    weights = export.weights(net, Net(sizes, 16))
    assert torch.equal(weights["card_block.weight"], torch.zeros(sizes["cards"], Net.DIM))
    assert torch.equal(weights["cards.weight"], model.cards.weight)


def test_training_goes_on_from_a_net_file_with_its_tables_and_state_scaling(tmp_path):
    first, second = tmp_path / "first.json", tmp_path / "second.json"
    lists = write_runs(tmp_path)
    for out, more in ((first, []), (second, ["--init", str(first)])):
        args = ["--list", lists, "--name", out.stem, "--out", str(out), "--encoder", encoder_command(), *more]
        result = CliRunner().invoke(train.script, args)
        assert result.exit_code == 0, result.output
    a, b = json.loads(first.read_text()), json.loads(second.read_text())
    assert b["vocab"] == a["vocab"] and b["state"] == a["state"]
    # each file says what made it: the commit, the list, the net it went on from, the seed, the epochs and the settings
    assert (a["trained"]["list"], a["trained"]["init"], a["trained"]["seed"]) == (lists, None, 0)
    assert b["trained"]["init"] == "first" and a["trained"]["rev"] == common.revision()
    assert a["trained"]["encoder"] == encoder().hello["commit"]
    assert 1 <= a["trained"]["epochs"] <= a["trained"]["settings"]["epochs"] == train.EPOCHS
    assert a["trained"]["settings"]["lr"] == train.LR and a["trained"]["settings"]["body"] == Net.BODY
    # the report gives the file's own held-out numbers, on the same held-out decisions
    assert "init_held_out_loss" not in a["fit"]
    assert math.isclose(b["fit"]["init_held_out_loss"], a["fit"]["held_out_loss"], rel_tol=1e-4)
    assert math.isclose(b["fit"]["init_brier_held_out"], a["fit"]["brier_held_out"], rel_tol=1e-4)


def test_training_writes_a_net_file_with_its_report(tmp_path):
    out = tmp_path / "net.json"
    args = ["--list", write_runs(tmp_path), "--name", "t", "--out", str(out), "--encoder", encoder_command()]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    net = json.loads(out.read_text())
    assert net["fit"]["runs"] == 10 and net["fit"]["held_out_runs"] == 2
    assert net["fit"]["won_held_out_runs"] == 0 and 0 <= net["fit"]["brier_held_out"] <= 1
    assert set(net["vocab"]) == set(spec.TABLES) and net["tests"]
    assert all(isinstance(t["run"], dict) and "floor" in t["run"] for t in net["tests"])  # views, not JSON


def test_a_map_move_gives_a_sample_entering_the_point(tmp_path):
    lists = write_runs(tmp_path)
    logs = common.lists(lists)[0].split(",")
    for log in logs:
        rows = [json.loads(line) for line in open(log)]
        rows = [r | {"surface": "map", "chosen": "map:1,1"} for r in rows]
        with open(log, "w") as f:
            f.writelines(json.dumps(r, separators=(",", ":")) + "\n" for r in rows)
    samples, _ = data.read_runs(*common.lists(lists), 64, encoder())
    assert len(samples) == 60 and samples[1]["tokens"]["map"][0] == "Elite+0:quest"
    assert math.isclose(sum(s["weight"] for s in samples), 10)
    assert "Elite+0:quest" in game_ids.macro_tables()["map"]


def test_training_refuses_to_go_on_from_a_net_of_other_tables(tmp_path):
    first = tmp_path / "first.json"
    lists = write_runs(tmp_path)
    args = ["--list", lists, "--name", "first", "--out", str(first), "--encoder", encoder_command()]
    assert CliRunner().invoke(train.script, args).exit_code == 0
    net = json.loads(first.read_text())
    first.write_text(json.dumps(net | {"vocab": net["vocab"] | {"cards": net["vocab"]["cards"][:-1]}}))
    args = ["--list", lists, "--name", "second", "--out", str(tmp_path / "second.json"), "--init", str(first), "--encoder", encoder_command()]
    assert CliRunner().invoke(train.script, args).exit_code != 0
