import array
import base64
import json
import math

import onnx
import pytest
import torch
from conftest import OPTION, encoder, encoder_command, make_view, read_run
from onnx.reference import ReferenceEvaluator
from typer.testing import CliRunner

import carry
import common
import game_ids
from v import bootstrap, data, export, potions, spec, train
from v.net import Batch, Net, boot_targets, values, win_chance


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
    # only the held-out samples keep their view, packed as their tokens are
    assert all(("view" in s) == data.held_out(s["run"]) for s in samples)
    held = next(s for s in samples if data.held_out(s["run"]))
    assert common.unpack(held["view"])["floor"] == held["state"][0]


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
        return lambda part: [-float(common.unpack(s["tokens"])["potions"].count(potion)) for s in part]

    # only the decisions that hold a potion are asked; one potion valued below the empty slot is enough
    assert potions.below_empty(dislikes("FIRE"), held) == {"decisions": 2, "share": 1.0}
    assert potions.below_empty(dislikes("SWIFT"), held) == {"decisions": 2, "share": 0.5}

    def more_is_better(part):
        return [float(len(common.unpack(s["tokens"])["potions"])) for s in part]

    assert potions.below_empty(more_is_better, held)["share"] == 0
    assert potions.below_empty(dislikes("FIRE"), held[2:]) == {"decisions": 0, "share": None}


def held_out_sample(view):
    """A held-out decision that died on floor 9 of 16, with the view the net file keeps for its tests."""
    return data.sample("a", read_run(view), 16, 9, "died") | {"view": common.pack(view)}


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
    assert len(samples) == 60 and common.unpack(samples[1]["tokens"])["map"][0] == "Elite+0:quest"
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


def map_moves(lists):
    """The logs' decisions made map moves, so each gives its afterstate as a second sample."""
    for log in common.lists(lists)[0].split(","):
        rows = [json.loads(line) for line in open(log)]
        with open(log, "w") as f:
            f.writelines(json.dumps(r | {"surface": "map", "chosen": "map:1,1"}, separators=(",", ":")) + "\n" for r in rows)


def test_a_decisions_later_state_is_its_runs_first_own_decision_that_many_floors_on(tmp_path):
    lists = write_runs(tmp_path)
    map_moves(lists)
    samples, _ = data.read_runs(*common.lists(lists), 64, encoder())
    # each run: floor 1's decision and its afterstate, then floor 2's, then floor 3's
    run = [k for k, s in enumerate(samples) if s["run"] == f"{0:016x}"]
    assert [samples[k]["own"] for k in run] == [True, False] * 3
    assert [samples[k]["decision"] for k in run] == [0, 0, 1, 1, 2, 2]
    one, two = bootstrap.later(samples, 1), bootstrap.later(samples, 2)
    assert [one[k] for k in run] == [run[2], run[2], run[4], run[4], None, None]
    assert [two[k] for k in run] == [run[4], run[4], None, None, None, None]


def test_the_folds_cut_the_training_runs_by_id_and_leave_the_held_out_ones_out():
    assert [bootstrap.fold(f"{i:016x}") for i in range(10)] == [None, "A", "A", "B", "B"] * 2


def test_hazards_of_0_and_1_at_the_runs_real_end_give_todays_labels():
    died = data.sample("a", read_run(make_view(floor=2)), 8, 5, "died")
    won = data.sample("b", read_run(make_view(floor=2)), 8, 7, "won")
    args = ({t: {} for t in spec.TABLES}, torch.zeros(len(spec.STATE)), torch.ones(len(spec.STATE)), 8, "cpu")
    plain = Batch([died, won], *args)
    dies_on_5, lives = array.array("f", [0, 0, 0, 0, 0, 1, 0, 0]), array.array("f", [0] * 8)
    booted = Batch([died | {"boot": (4, dies_on_5)}, won | {"boot": (3, lives)}], *args)
    assert torch.equal(booted.target, plain.target) and torch.equal(booted.label_mask, plain.label_mask)


def test_a_bootstrapped_floor_weighs_the_frozen_vs_chance_that_the_run_is_alive_there():
    hazards = torch.tensor([[0.0, 0.0, 0.0, 0.5, 0.2, 0.0]])
    target, weight = boot_targets(torch.tensor([[1]]), torch.tensor([[3]]), hazards, 6)
    assert target[0].tolist() == pytest.approx([0, 0, 0, 0.5, 0.2, 0])
    assert weight[0].tolist() == pytest.approx([0, 1, 1, 1, 0.5, 0.4])


def test_a_training_on_multi_step_targets_takes_a_v_of_each_fold_and_says_so(tmp_path):
    lists = write_runs(tmp_path)
    nets = {}
    for f in bootstrap.FOLDS:
        nets[f] = tmp_path / f"f{f}.json"
        args = ["--list", lists, "--name", f"f{f}", "--out", str(nets[f]), "--encoder", encoder_command(), "--fold", f]
        result = CliRunner().invoke(train.script, args)
        assert result.exit_code == 0, result.output
        assert json.loads(nets[f].read_text())["trained"]["fold"] == f

    def multi_step(frozen):
        out = tmp_path / "td.json"
        args = ["--list", lists, "--name", "td", "--out", str(out), "--encoder", encoder_command()]
        return out, CliRunner().invoke(train.script, [*args, "--bootstrap", frozen, "--horizon", "1"])

    out, result = multi_step(f"{nets['A']},{nets['B']}")
    assert result.exit_code == 0, result.output
    td = json.loads(out.read_text())
    assert td["trained"]["bootstrap"] == {"nets": {"A": "fA", "B": "fB"}, "horizon": 1} and "fold" not in td["trained"]
    # each training run's decisions are on floors 1-3: the first two have a decision a floor on
    assert td["fit"]["bootstrapped_share"] == pytest.approx(2 / 3)
    _, twice = multi_step(f"{nets['A']},{nets['A']}")
    assert isinstance(twice.exception, common.LearnError) and "both trained on fold A" in str(twice.exception)
    plain = tmp_path / "plain.json"
    args = ["--list", lists, "--name", "plain", "--out", str(plain), "--encoder", encoder_command()]
    assert CliRunner().invoke(train.script, args).exit_code == 0
    _, unfolded = multi_step(f"{nets['A']},{plain}")
    assert isinstance(unfolded.exception, common.LearnError) and "not trained on one fold" in str(unfolded.exception)


def test_an_event_option_pending_reads_everything_its_button_shows_in_noSL():
    view = make_view(floor=3)
    plain, pending = read_run(view), read_run(view | {"pending": OPTION}, "NoSL")
    assert "pending" not in plain["tokens"] and "env:NoSL" not in plain["tokens"]["context"]
    p = pending["tokens"]["pending"]
    assert "env:NoSL" in pending["tokens"]["context"] and p["key"] == OPTION["key"]
    assert [c[0] for c in p["cards"]] == ["STRIKE", "GUILTY"] and p["cards"][0][1] == 1 and p["cards_owned"] == [1, 0]
    assert p["relics"] == ["RING", "VASE"] and p["relic_values"][0] == [0.3, 1, 0] and p["relics_owned"] == [1, 0]
    assert p["potions"] == ["FIRE_POTION"] and p["potions_owned"] == [1]
    assert p["numbers"] == ["WELLSPRING.BatheCurses"] and p["number_values"] == [0.05]


def test_the_onnx_graph_reads_a_pending_event_option_and_the_environment_as_torch_does():
    plain = [held_out_sample(make_view(floor=f, hp=30 + f)) for f in (1, 2)]
    pending = [
        data.sample("a", read_run(view, "NoSL"), 16, 9, "died") | {"view": common.pack(view), "env": "NoSL", "pending": True}
        for view in (make_view(floor=f) | {"pending": OPTION} for f in (2, 3))
    ]
    tables = game_ids.macro_tables()
    torch.manual_seed(0)
    model = Net({t: len(ids) for t, ids in tables.items()}, 16)
    mean, std = torch.zeros(len(spec.STATE)), torch.full((len(spec.STATE),), 10.0)
    net = export.net_file("t", 16, tables, mean, std, model, plain + pending, {}, 0, {})
    assert [t.get("env") for t in net["tests"]] == [None, None, "NoSL", "NoSL"]
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    assert [i.name for i in graph.graph.input] == spec.INPUTS
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    values_of = []
    for test in net["tests"]:
        b = Batch([data.sample("a", read_run(test["run"], test.get("env", "SL")), 16)], index, mean, std, 16, "cpu")
        feed = {name: x.numpy() for name, x in zip(spec.INPUTS, b.inputs(), strict=True)}
        [hazards] = ReferenceEvaluator(graph).run(None, feed)
        values_of.append(float(win_chance(torch.tensor(hazards), b.start)[0]))
        assert math.isclose(values_of[-1], test["value"], rel_tol=1e-5)
    # a fresh net reads them: the option moves the value
    without = Batch([data.sample("a", read_run(make_view(floor=2), "NoSL"), 16)], index, mean, std, 16, "cpu")
    assert not math.isclose(float(values(model, without)[0]), values_of[2], rel_tol=1e-6)


def test_a_carried_v_values_a_run_alike_in_both_environments_and_with_an_option_pending():
    """What carry.py does to a V of before the environment and the event options: their rows and columns at zero."""
    tables = game_ids.macro_tables()
    torch.manual_seed(1)
    model = Net({t: len(ids) for t, ids in tables.items()}, 16)
    own = model.state_dict()
    was = {t: [i for i in ids if not i.startswith("env:")] for t, ids in tables.items() if t not in ("pending", "event_vars")}
    width = own["body.0.weight"].shape[1] - (3 * Net.DIM + Net.DIM + Net.HIDDEN)
    old = {k: v for k, v in own.items() if k not in carry.FRESH and k not in ("pending.weight", "event_vars.weight")}
    old["body.0.weight"] = own["body.0.weight"][:, :width]
    old["bags.context.weight"] = own["bags.context.weight"][[tables["context"].index(i) for i in was["context"]]]
    model.load_state_dict(carry.carried(old, own, carry.V_ROWS, was, tables, ["aux_head."]))
    model.eval()
    assert model.event_vars.weight.abs().sum() > 0 and model.pending.weight[0].abs().sum() == 0
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    mean, std = torch.zeros(len(spec.STATE)), torch.full((len(spec.STATE),), 10.0)
    view = make_view(floor=4)
    runs = [read_run(view), read_run(view, "NoSL"), read_run(view | {"pending": OPTION}, "NoSL")]
    b = Batch([data.sample("a", r, 16) for r in runs], index, mean, std, 16, "cpu")
    with torch.no_grad():
        got = values(model, b).tolist()
    assert got[0] == pytest.approx(got[1], abs=1e-7) and got[0] == pytest.approx(got[2], abs=1e-7)


def write_event_runs(tmp_path, runs=10):
    """write_runs's runs with an event at their second decision: odd runs in NoSL self-play, three options played out,
    one leaving the room (the one taken), one dying there and one starting a fight; even runs in SL, with a line as the
    builds of 2026-11-17 logged there, which teaches nothing since each environment has its own nets."""
    lists = write_runs(tmp_path, runs)
    log = common.lists(lists)[0].split(",")[0]
    rows = [json.loads(line) for line in open(log)]
    for r in rows:
        i = int(r["run"], 16)
        if r["decision_id"] != 1:
            r["env"] = ("SL", "NoSL")[i % 2]
            continue
        later = r["run_view"] | {"floor": r["run_view"]["floor"], "hp": 20}
        if i % 2:
            r |= {"env": "NoSL", "event": [
                {"option": OPTION, "end": later, "ending": "left", "taken": True},
                {"option": OPTION | {"index": 1}, "end": r["run_view"] | {"hp": 0}, "ending": "died"},
                {"option": OPTION | {"index": 2}, "ending": "fight"},
            ]}
        else:
            r["event"] = [{"option": OPTION, "end": later, "ending": "left"}]
    with open(log, "w") as f:
        f.writelines(json.dumps(r, separators=(",", ":")) + "\n" for r in rows)
    return lists


def test_event_options_give_samples_in_noSL_apart_from_their_runs_decisions(tmp_path):
    samples, _ = data.read_runs(*common.lists(write_event_runs(tmp_path)), 64, encoder())
    events = [s for s in samples if "event" in s]
    kinds = sorted(s["event"] for s in events)
    assert kinds == ["ended"] * 5 + ["left"] * 5 + ["taken"] * 5
    for s in events:
        tok = common.unpack(s["tokens"])
        assert s["env"] == "NoSL" and "env:NoSL" in tok["context"] and tok["pending"]["key"] == OPTION["key"]
    left = next(s for s in events if s["event"] == "left")
    assert left["end"] == left["start"] and left["later"]["start"] == left["start"]  # labelled from where it left
    ended = next(s for s in events if s["event"] == "ended")
    assert ended["kind"] == "died" and ended["end"] == ended["start"]
    # the runs' own decisions weigh one a run, each event option one decision of its run more
    assert math.isclose(sum(s["weight"] for s in samples if "event" not in s), 10)
    assert math.isclose(sum(s["weight"] for s in events), 15 / 3)
    assert {s["env"] for s in samples if "event" not in s} == {"SL", "NoSL"}
    assert all(("view" in s) == data.held_out(s["run"]) for s in events)


def test_a_line_of_an_event_is_labelled_from_where_it_left_the_room_by_the_v_that_never_saw_its_run(tmp_path):
    lists = write_event_runs(tmp_path)
    nets = {}
    for f in bootstrap.FOLDS:
        nets[f] = tmp_path / f"f{f}.json"
        args = ["--list", lists, "--name", f"f{f}", "--out", str(nets[f]), "--encoder", encoder_command(), "--fold", f]
        result = CliRunner().invoke(train.script, args)
        assert result.exit_code == 0, result.output
    out = tmp_path / "td.json"
    args = ["--list", lists, "--name", "td", "--out", str(out), "--encoder", encoder_command()]
    result = CliRunner().invoke(train.script, [*args, "--bootstrap", f"{nets['A']},{nets['B']}", "--horizon", "1"])
    assert result.exit_code == 0, result.output
    fit = json.loads(out.read_text())["fit"]
    assert fit["event_samples"] == {"taken": 5, "left": 5, "ended": 5}
    assert fit["held_out_event_loss"] is not None and set(fit["held_out_loss_by_env"]) == {"SL", "NoSL"}
    plain = tmp_path / "plain.json"
    result = CliRunner().invoke(train.script, ["--list", lists, "--name", "plain", "--out", str(plain), "--encoder", encoder_command()])
    assert result.exit_code == 0, result.output
    assert json.loads(plain.read_text())["fit"]["event_samples"] == {"taken": 5, "left": 0, "ended": 5}
