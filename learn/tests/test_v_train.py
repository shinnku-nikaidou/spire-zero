import base64
import json
import math

import onnx
import pytest
import torch
from conftest import make_view
from onnx.reference import ReferenceEvaluator
from typer.testing import CliRunner

import bosslab
import common
from common import LearnError
from v import data, encode, export, train
from v.net import Batch, Net, centred, expected_floor, expected_progress, explored, values, win_chance


def record(run_id, floor, outcome="lost", combats=(), **config):
    return {
        "run_id": run_id,
        "character": "SILENT",
        "outcome": outcome,
        "floor": floor,
        "config": config,
        "combats": list(combats),
    }


def write_runs(tmp_path, runs=10, auto_win=False, gauntlet=False):
    """A log and its records: runs of three decisions each (floors 1–3), the run i reaching floor 4 + i % 4; in the
    gauntlet, each run dies in an elite fight on its last floor, having taken a tenth of its HP per floor reached."""
    log, recs = tmp_path / "log.jsonl", tmp_path / "rec.jsonl"
    with open(log, "w") as lf, open(recs, "w") as rf:
        for i in range(runs):
            run = f"{i:016x}"
            for d in range(3):
                view = make_view(floor=d + 1, hp=60 - 5 * d - i, gold=99 + 10 * i)
                lf.write(
                    json.dumps({"run": run, "decision_id": d, "brain": "l0 n2 explore 0.01", "run_view": view}) + "\n"
                )
            combats = [
                {"floor": f, "room": "Monster", "hp": 60, "max_hp": 70, "end": {"died": False, "hp": 55}}
                for f in (2, 3)
            ]
            config = {"auto_win": True} if auto_win else {"gauntlet": True} if gauntlet else {}
            if gauntlet:
                end = {"died": True, "hp": 0, "enemy_hp": 100 - 10 * (4 + i % 4), "enemy_max_hp": 100}
                combats.append({"floor": 4 + i % 4, "room": "Elite", "hp": 40, "max_hp": 70, "end": end})
            rf.write(json.dumps(record(run, 4 + i % 4, combats=combats, **config)) + "\n")
    lists = tmp_path / "smoke"
    (tmp_path / "smoke-logs.txt").write_text(f"{log}\n")
    (tmp_path / "smoke-recs.txt").write_text(f"{recs}\n")
    return str(lists)


def test_a_list_pair_gives_the_logs_and_records_in_the_same_order(tmp_path):
    (tmp_path / "x-logs.txt").write_text("/a/l1.jsonl\nb/l2.jsonl\n")
    (tmp_path / "x-recs.txt").write_text("/a/r1.jsonl\nb/r2.jsonl\n")
    logs, recs = common.lists(str(tmp_path / "x"))
    assert logs == f"/a/l1.jsonl,{common.ROOT / 'b/l2.jsonl'}" and recs.startswith("/a/r1.jsonl,")


def test_each_decision_is_a_sample_of_where_its_run_ended_and_each_run_weighs_one(tmp_path):
    samples, runs, views = data.read_runs(*common.lists(write_runs(tmp_path)), "SILENT", 64)
    assert len(samples) == 30 and len(runs) == 10 and len(views) == 30
    first = samples[0]
    assert (first["start"], first["end"], first["kind"], first["floor"]) == (1, 4, "died", 4)
    assert first["next"] == [5 / 70, 5 / 70] and samples[2]["next"] == []  # the combats after floors 1 and 3
    assert math.isclose(sum(s["weight"] for s in samples), 10)
    assert {data.held_out(s["run"]) for s in samples} == {True, False}
    # only the held-out samples keep their view, as JSON
    assert all(("view_json" in s) == data.held_out(s["run"]) and "view" not in s for s in samples)
    held = next(s for s in samples if data.held_out(s["run"]))
    assert json.loads(held["view_json"])["floor"] == held["state"][0]


def test_without_keep_views_no_views_are_kept(tmp_path):
    samples, _, views = data.read_runs(*common.lists(write_runs(tmp_path)), "SILENT", 64, keep_views=False)
    assert len(samples) == 30 and views == {}


def test_a_boss_gauntlet_is_refused(tmp_path):
    with pytest.raises(LearnError, match="auto-win"):
        data.read_runs(*common.lists(write_runs(tmp_path, auto_win=True)), "SILENT", 64)


def test_a_whole_runs_v_refuses_the_gauntlets_runs_and_the_gauntlets_v_whole_runs(tmp_path):
    (tmp_path / "a").mkdir()
    (tmp_path / "b").mkdir()
    with pytest.raises(LearnError, match="gauntlet"):
        data.read_runs(*common.lists(write_runs(tmp_path / "a", gauntlet=True)), "SILENT", 64)
    with pytest.raises(LearnError, match="gauntlet"):
        data.read_runs(*common.lists(write_runs(tmp_path / "b")), "SILENT", 64, gauntlet=True)


def test_a_gauntlet_run_ends_where_it_ended_with_the_share_of_the_enemies_hp_it_took():
    elite = {"floor": 9, "room": "Elite", "hp": 30, "max_hp": 70}
    died = record("a", 9, combats=[elite | {"end": {"died": True, "enemy_hp": 40, "enemy_max_hp": 100}}])
    event = record("b", 12, combats=[elite | {"end": {"died": False, "hp": 20, "enemy_hp": 0, "enemy_max_hp": 100}}])
    won = record("c", 48, "won", combats=[elite | {"end": {"died": False, "hp": 20}}])
    assert [data.death_share(r) for r in (died, event)] == [0.6, 0.0]
    assert [data.progress(r) for r in (died, event, won)] == [9.6, 12.0, 49.0]


def test_the_gauntlets_samples_end_where_their_run_ended_with_its_share_and_their_fought_next_combats(tmp_path):
    samples, _, views = data.read_runs(*common.lists(write_runs(tmp_path, gauntlet=True)), "SILENT", 64, gauntlet=True)
    assert views == {}
    first = samples[0]
    assert (first["end"], first["kind"], first["share"]) == (4, "died", pytest.approx(0.4))
    assert first["next"] == [40 / 70]  # the elite fight it died in, not the monsters won at their start


def test_a_boss_is_worth_the_floors_its_winners_went_on_to():
    win = {"act": 1, "room": "Boss", "hp": 50, "max_hp": 80, "end": {"died": False, "hp": 40, "max_hp": 80}}
    runs = [record("a", 40, combats=[win]), record("b", 36, combats=[win])]
    runs.append(record("c", 33, combats=[win | {"end": {"died": True}}]))
    assert data.boss_worth(runs, 64) == {0: 0.0, 1: 5.0, 2: 0.0}


def test_a_door_state_is_the_door_view_at_each_hp_level_with_its_mean_boss_score(tmp_path):
    views = {("r1", 9): (make_view(floor=32, hp=50, max_hp=80), "l0")}
    won = {"won": True, "hp": 40, "enemy_hp": 0, "enemy_max_hp": 100}
    lost = {"won": False, "hp": 0, "enemy_hp": 50, "enemy_max_hp": 100}
    line = {"run": "r1", "decision_id": 9, "act": 1, "max_hp": 80}
    line["options"] = [{"hp": 20, "fights": [lost, lost]}, {"hp": 80, "fights": [won, lost]}, {"hp": 60, "fights": []}]
    path = tmp_path / "states.jsonl"
    path.write_text(json.dumps(line) + "\n")
    [door] = data.read_boss_states(str(path), views, 64)
    assert [s["view"]["hp"] for s in door["samples"]] == [20, 80] and door["samples"][1]["state"][1] == 80
    assert [s["boss_score"] for s in door["samples"]] == [0.25, (0.75 + 0.25) / 2]
    assert [s["weight"] for s in door["samples"]] == [0.5, 0.5] and door["samples"][0]["end"] == 32


def test_a_boss_fight_scores_by_the_share_of_max_hp_left_or_of_the_boss_hp_taken():
    assert bosslab.score({"won": True, "hp": 40}, 80) == 0.75
    assert bosslab.score({"won": False, "enemy_hp": 300, "enemy_max_hp": 400}, 80) == 0.125


def test_the_exploration_temperature_comes_from_the_brain_name():
    assert data.temperature("l0 explore 0.25") == 0.25
    assert data.temperature("l0 n1 explore 2") == 2
    assert data.temperature("l0") is None


def test_the_hazards_are_labelled_up_to_the_floor_a_run_died_on_and_not_past_a_censored_one():
    died = data.sample("a", make_view(floor=2), 8, 5, "died")
    censored = data.sample("b", make_view(floor=2), 8, 5)
    unlabelled = data.sample("c", make_view(floor=2), 8)
    b = Batch([died, censored, unlabelled], {t: {} for t in encode.TABLES}, torch.zeros(6), torch.ones(6), 8, "cpu")
    assert b.label_mask.tolist() == [[0, 0, 1, 1, 1, 1, 0, 0], [0, 0, 1, 1, 1, 0, 0, 0], [0] * 8]
    assert b.target[0].tolist() == [0, 0, 0, 0, 0, 1, 0, 0] and b.target[1].sum() == 0


def test_the_expected_floor_adds_the_chance_to_survive_each_floor_from_the_start():
    h = torch.tensor([[0.0, 0.5, 1.0, 0.0]])
    assert expected_floor(h, torch.tensor([1])).tolist() == [1 + 0.5]


def test_the_chance_to_win_is_the_chance_to_survive_every_floor_from_the_start():
    h = torch.tensor([[0.9, 0.5, 0.2, 0.0]])
    assert win_chance(h, torch.tensor([1])).tolist() == [pytest.approx(0.5 * 0.8)]


def test_explored_is_the_chance_of_a_draw_below_the_best_by_more_than_a_hundredth_of_the_temperature():
    values = torch.tensor([1.0, 0.0, 2.0, 2.0, 2.0 - 1e-4], dtype=torch.float64)
    share = explored(values, torch.ones_like(values), torch.tensor([0, 0, 1, 1, 1]), 2)
    assert math.isclose(float(share[0]), 1 / (1 + math.e))
    assert float(share[1]) == 0  # ties, and an option closer than 1 / 100, are as good as the best


def test_centred_values_lose_their_groups_mean():
    assert centred(torch.tensor([1.0, 3.0, 10.0]), torch.tensor([0, 0, 1]), 2).tolist() == [-1.0, 1.0, 0.0]


def test_the_onnx_graph_gives_the_test_runs_the_values_torch_gave_them():
    samples = [data.sample("a", make_view(floor=f, hp=30 + f), 16, 9, "died") for f in (1, 2, 3)]
    tables = train.vocab(samples * 3)
    torch.manual_seed(0)
    model = Net({t: len(ids) for t, ids in tables.items()}, 16)
    net = export.net_file("t", "SILENT", 16, tables, torch.zeros(6), torch.full((6,), 10.0), model, samples, {}, 0)
    assert net["state"]["names"] == encode.STATE and len(net["tests"]) == 3
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    assert [i.name for i in graph.graph.input] == encode.INPUTS
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", test["run"], 16)], index, torch.zeros(6), torch.full((6,), 10.0), 16, "cpu")
        feed = {name: x.numpy() for name, x in zip(encode.INPUTS, b.inputs(), strict=True)}
        [hazards] = ReferenceEvaluator(graph).run(None, feed)
        value = float(expected_floor(torch.tensor(hazards), b.start)[0])
        assert math.isclose(value, test["value"], rel_tol=1e-5)


def test_a_win_nets_test_runs_are_valued_at_their_chance_to_win():
    samples = [data.sample("a", make_view(floor=f, hp=30 + f), 16, 9, "died") for f in (1, 2, 3)]
    tables = train.vocab(samples * 3)
    torch.manual_seed(0)
    model = Net({t: len(ids) for t, ids in tables.items()}, 16)
    net = export.net_file(
        "t", "SILENT", 16, tables, torch.zeros(6), torch.full((6,), 10.0), model, samples, {}, 0, "win"
    )
    assert net["objective"] == "win"
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", test["run"], 16)], index, torch.zeros(6), torch.full((6,), 10.0), 16, "cpu")
        feed = {name: x.numpy() for name, x in zip(encode.INPUTS, b.inputs(), strict=True)}
        [hazards] = ReferenceEvaluator(graph).run(None, feed)
        assert math.isclose(float(win_chance(torch.tensor(hazards), b.start)[0]), test["value"], rel_tol=1e-5)


def test_the_expected_progress_adds_the_chance_to_end_on_each_floor_times_its_share():
    h, s = torch.tensor([[0.0, 0.5, 1.0, 0.0]]), torch.tensor([[0.9, 0.2, 0.6, 0.9]])
    # From floor 1: ends on 1 (half the time, share 0.2) or on 2 (share 0.6).
    assert math.isclose(float(expected_progress(h, s, torch.tensor([1]))[0]), 1.5 + 0.5 * 0.2 + 0.5 * 0.6, rel_tol=1e-6)


def test_a_gauntlet_nets_onnx_graph_gives_the_hazards_then_the_shares_and_the_test_runs_their_progress():
    samples = [data.sample("a", make_view(floor=f, hp=30 + f), 16, 9, "died") | {"share": 0.5} for f in (1, 2, 3)]
    tables = train.vocab(samples * 3)
    torch.manual_seed(0)
    model = Net({t: len(ids) for t, ids in tables.items()}, 16, shares=True)
    net = export.net_file("t", "SILENT", 16, tables, torch.zeros(6), torch.full((6,), 10.0), model, samples, {}, 0)
    assert net["objective"] == "progress"
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", test["run"], 16)], index, torch.zeros(6), torch.full((6,), 10.0), 16, "cpu")
        feed = {name: x.numpy() for name, x in zip(encode.INPUTS, b.inputs(), strict=True)}
        [out] = ReferenceEvaluator(graph).run(None, feed)
        assert out.shape == (1, 32)
        out = torch.tensor(out)
        value = float(expected_progress(out[:, :16], out[:, 16:], b.start)[0])
        assert math.isclose(value, test["value"], rel_tol=1e-5)


def test_training_the_gauntlets_v_writes_a_progress_net(tmp_path):
    out = tmp_path / "net.json"
    args = ["--list", write_runs(tmp_path, gauntlet=True), "--name", "t", "--out", str(out), "--gauntlet"]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    net = json.loads(out.read_text())
    assert net["objective"] == "progress" and net["fit"]["gauntlet"] and net["fit"]["runs"] == 10


def test_a_net_files_weights_read_back_give_its_test_runs_their_values_and_keep_the_second_head_anew():
    samples = [data.sample("a", make_view(floor=f, hp=30 + f), 16, 9, "died") | {"share": 0.5} for f in (1, 2, 3)]
    tables = train.vocab(samples * 3)
    sizes = {t: len(ids) for t, ids in tables.items()}
    torch.manual_seed(0)
    model = Net(sizes, 16, shares=True)
    net = export.net_file("t", "SILENT", 16, tables, torch.zeros(6), torch.full((6,), 10.0), model, samples, {}, 0)
    torch.manual_seed(1)
    fresh = Net(sizes, 16, shares=True)
    weights = export.weights(net, fresh)
    assert torch.equal(weights["aux_head.weight"], fresh.state_dict()["aux_head.weight"])
    fresh.load_state_dict(weights)
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", test["run"], 16)], index, torch.zeros(6), torch.full((6,), 10.0), 16, "cpu")
        with torch.no_grad():
            assert math.isclose(float(values(fresh.eval(), b)[0]), test["value"], rel_tol=1e-5)


def test_training_goes_on_from_a_net_file_with_its_tables_and_state_scaling(tmp_path):
    first, second = tmp_path / "first.json", tmp_path / "second.json"
    lists = write_runs(tmp_path, gauntlet=True)
    for out, more in ((first, []), (second, ["--init", str(first)])):
        args = ["--list", lists, "--name", out.stem, "--out", str(out), "--gauntlet", *more]
        result = CliRunner().invoke(train.script, args)
        assert result.exit_code == 0, result.output
    a, b = json.loads(first.read_text()), json.loads(second.read_text())
    assert b["vocab"] == a["vocab"] and b["state"] == a["state"]


def test_a_whole_runs_v_goes_on_from_the_gauntlets_and_the_gauntlets_from_no_whole_runs(tmp_path):
    (tmp_path / "g").mkdir()
    (tmp_path / "w").mkdir()
    gauntlet, whole = tmp_path / "gauntlet.json", tmp_path / "whole.json"
    args = ["--list", write_runs(tmp_path / "g", gauntlet=True), "--name", "g", "--out", str(gauntlet), "--gauntlet"]
    assert CliRunner().invoke(train.script, args).exit_code == 0
    lists = write_runs(tmp_path / "w")
    args = ["--list", lists, "--name", "w", "--out", str(whole), "--init", str(gauntlet), "--objective", "win"]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    a, b = json.loads(gauntlet.read_text()), json.loads(whole.read_text())
    assert b["objective"] == "win" and b["vocab"] == a["vocab"] and b["state"] == a["state"]
    args = ["--list", write_runs(tmp_path / "g", gauntlet=True), "--name", "t", "--gauntlet", "--init", str(whole)]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code != 0 and "win net" in str(result.exception)


def test_the_gauntlets_v_takes_no_objective(tmp_path):
    args = ["--list", write_runs(tmp_path, gauntlet=True), "--name", "t", "--gauntlet", "--objective", "win"]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code != 0 and "progress" in str(result.exception)


def test_training_writes_a_net_file_with_its_report(tmp_path):
    out = tmp_path / "net.json"
    result = CliRunner().invoke(train.script, ["--list", write_runs(tmp_path), "--name", "t", "--out", str(out)])
    assert result.exit_code == 0, result.output
    net = json.loads(out.read_text())
    assert net["objective"] == "floor" and net["fit"]["runs"] == 10 and net["fit"]["held_out_runs"] == 2
    assert net["fit"]["won_held_out_runs"] == 0 and 0 <= net["fit"]["brier_held_out"] <= 1
    assert set(net["vocab"]) == set(encode.TABLES) and net["tests"]
    assert all(isinstance(t["run"], dict) and "floor" in t["run"] for t in net["tests"])  # views, not JSON


def test_a_gauntlets_logs_give_its_door_states_their_views_and_no_samples(tmp_path):
    log = tmp_path / "gauntlet.jsonl"
    log.write_text(json.dumps({"run": "g1", "decision_id": 4, "brain": "l0", "run_view": make_view(floor=16)}) + "\n")
    views = data.read_views(str(log))
    assert list(views) == [("g1", 4)] and views[("g1", 4)][0]["floor"] == 16
