import base64
import json
import math

import onnx
import pytest
import torch
from conftest import combat_net_path, encoder, encoder_command, make_enemy, make_obs, make_view, read_run
from onnx.reference import ReferenceEvaluator
from typer.testing import CliRunner

import common
import game_ids
from f import export, train
from f.net import INPUTS, Batch, Net
from v import data, saves, spec
from v import export as v_export
from v.net import Net as V

TABLES = game_ids.macro_tables()
INDEX = {t: {i: k for k, i in enumerate(ids)} for t, ids in TABLES.items()}
SIZES = {t: len(ids) for t, ids in TABLES.items()}
MEAN = torch.tensor([10.0, 50, 70, 100, 5, 3, 0, 0, 0])
STD = torch.tensor([10.0, 20, 10, 80, 3, 1, 1, 1, 1])


def asked(view, env="SL") -> dict:
    """A decision of VIEW's as f reads it: V's sample, each potion it holds asked about (none known), its view kept."""
    held = [p for p in view["potions"] if p]
    return data.sample("a", read_run(view, env), 16) | {"saves": [(p, None) for p in held], "view": common.pack(view), "env": env}


def test_f_is_never_below_0_and_the_hp_and_max_hp_never_move_it():
    for seed in range(3):
        torch.manual_seed(seed)
        model = Net(SIZES).eval()
        for name, grid in (("hp", range(1, 71, 3)), ("max_hp", range(40, 120, 4))):
            b = Batch([asked(make_view(potions=["FIRE", None, "BLOCK"], **{name: x})) for x in grid], INDEX, MEAN, STD, "cpu")
            with torch.no_grad():
                f = model(*b.inputs())
            assert (f >= 0).all() and torch.allclose(f, f[0].expand_as(f)), name
        # what else the run holds does move it
        b = Batch([asked(make_view(potions=["FIRE", None, None], gold=g)) for g in (0, 500)], INDEX, MEAN, STD, "cpu")
        with torch.no_grad():
            f = model(*b.inputs())
        assert not torch.allclose(f[0], f[1])


def test_the_onnx_graph_gives_the_test_runs_f_as_torch_gave_it():
    torch.manual_seed(0)
    model = Net(SIZES).eval()
    held = [asked(make_view(floor=f, hp=30 + f, potions=["FIRE", None, "BLOCK"])) for f in (1, 2, 3)]
    net = export.net_file("t", TABLES, MEAN, STD, model, held, {}, 0, {})
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    assert [i.name for i in graph.graph.input] == INPUTS and [o.name for o in graph.graph.output] == ["saves"]
    assert len(net["tests"]) == 3 and all(len(t["saves"]) == 2 for t in net["tests"])
    for test in net["tests"]:
        b = Batch([asked(test["run"])], INDEX, MEAN, STD, "cpu")
        (saved,) = ReferenceEvaluator(graph).run(None, {n: x.numpy() for n, x in zip(INPUTS, b.inputs(), strict=True)})
        assert saved[0].tolist() == pytest.approx(test["saves"], rel=1e-5)


def v_file(tmp_path):
    """A V file of random weights, as v/export.py writes it."""
    torch.manual_seed(0)
    held = [data.sample("a", read_run(v), 16, 9, "died") | {"view": common.pack(v)} for v in (make_view(floor=f) for f in (1, 2))]
    net = v_export.net_file("v-test", 16, TABLES, MEAN, STD, V(SIZES, 16), held, {}, 0, {})
    path = tmp_path / "v-test.json"
    v_export.write(net, path)
    return net, path


def test_f_goes_on_from_a_vs_body_with_a_head_of_its_own_and_from_an_f_whole(tmp_path):
    vnet, _ = v_file(tmp_path)
    body = v_export.stored(vnet)
    torch.manual_seed(1)
    fresh = Net(SIZES)
    weights = export.weights(vnet, fresh)
    assert torch.equal(weights["body.0.weight"], body["body.0.weight"]) and torch.equal(weights["cards.weight"], body["cards.weight"])
    assert torch.equal(weights["head.0.weight"], fresh.state_dict()["head.0.weight"])
    fresh.load_state_dict(weights)
    held = [asked(make_view(floor=f, potions=["FIRE", None, None])) for f in (1, 2)]
    fnet = export.net_file("f-test", TABLES, MEAN, STD, fresh, held, {}, 0, {})
    torch.manual_seed(2)
    again = Net(SIZES)
    again.load_state_dict(export.weights(fnet, again))
    for test in fnet["tests"]:
        with torch.no_grad():
            f = again.eval()(*Batch([asked(test["run"])], INDEX, MEAN, STD, "cpu").inputs())
        assert f[0].tolist() == pytest.approx(test["saves"], rel=1e-5)


def record(run_id, outcome="lost"):
    return {"run_id": run_id, "character": "SILENT", "outcome": outcome, "floor": 3, "config": {}, "combats": []}


def potion_rows(run):
    """A run of one character: a decision holding FIRE and BLOCK, a fight where FIRE is drunk, a decision, a decision
    with BLOCK gone (thrown away), then a fight that killed the run holding nothing."""
    rows = []

    def macro(d, potions):
        rows.append({"run": run, "decision_id": d, "env": "SL", "surface": "rewards", "brain": "l0",
                     "run_view": make_view(floor=1 + d // 2, potions=potions)})  # fmt: skip

    def fight(d, potions, rnd=1):
        o = make_obs(round=rnd, hp=40, enemies=[make_enemy(1)], potions=potions)
        rows.append({"run": run, "decision_id": d, "env": "SL", "surface": "combat", "brain": "l0", "obs": o, "history": []})

    macro(0, ["FIRE", None, "BLOCK"])
    fight(1, ["FIRE", None, "BLOCK"])
    fight(2, [None, None, "BLOCK"], rnd=2)
    macro(3, [None, None, "BLOCK"])
    macro(4, [None, None, None])
    fight(5, [None, None, None])
    return rows


def write_potion_log(tmp_path):
    log = tmp_path / "log.jsonl"
    run = "0" * 15 + "1"
    rows = potion_rows(run)
    with open(log, "w") as f:
        f.writelines(json.dumps(r, separators=(",", ":")) + "\n" for r in rows)
    return str(log), run, rows


def test_a_potion_saved_what_the_combat_net_says_it_did_in_the_fight_where_it_was_drunk(tmp_path):
    log, run, rows = write_potion_log(tmp_path)
    got = saves.labels(log, {run: {"outcome": "lost"}}, encoder(), combat_net_path())
    model, index = saves.combat_net(combat_net_path())
    first = json.dumps(rows[1])
    without = json.dumps(rows[1] | {"obs": rows[1]["obs"] | {"potions": [None, None, "BLOCK"]}})
    tokens = [r["tokens"] for r in encoder().combats([first, without])]
    a, b = saves.carried(model, index, tokens)
    assert got[(run, 0)] == [("FIRE", pytest.approx(a - b)), ("BLOCK", 0.0)]  # BLOCK went outside a fight
    assert got[(run, 3)] == [("BLOCK", 0.0)] and (run, 4) not in got
    open_ = saves.labels(log, {run: {"outcome": None}}, encoder(), combat_net_path())
    assert open_[(run, 0)][1] == ("BLOCK", 0.0)  # it went: the record's end does not matter


def test_a_potion_still_held_where_its_record_stops_short_is_not_known(tmp_path):
    log, run, rows = write_potion_log(tmp_path)
    with open(log, "w") as f:  # the run's record stops after the first decision
        f.write(json.dumps(rows[0], separators=(",", ":")) + "\n")
    assert saves.labels(log, {run: {"outcome": None}}, encoder(), combat_net_path())[(run, 0)] == [("FIRE", None), ("BLOCK", None)]
    assert saves.labels(log, {run: {"outcome": "won"}}, encoder(), combat_net_path())[(run, 0)] == [("FIRE", 0.0), ("BLOCK", 0.0)]


def test_training_writes_an_f_file_with_its_report_from_a_vs_body(tmp_path):
    _, init = v_file(tmp_path)
    log, recs = tmp_path / "runs.jsonl", tmp_path / "rec.jsonl"
    with open(log, "w") as lf, open(recs, "w") as rf:
        for i in range(10):  # runs 0 and 5 held out (v/data.py)
            run = f"{i:016x}"
            lf.writelines(json.dumps(r, separators=(",", ":")) + "\n" for r in potion_rows(run))
            rf.write(json.dumps(record(run)) + "\n")
    (tmp_path / "pot-logs.txt").write_text(f"{log}\n")
    (tmp_path / "pot-recs.txt").write_text(f"{recs}\n")
    out = tmp_path / "f.json"
    args = ["--list", str(tmp_path / "pot"), "--name", "f-t", "--out", str(out), "--encoder", encoder_command(),
            "--combat-net", combat_net_path(), "--init", str(init), "--workers", "0"]  # fmt: skip
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    net = json.loads(out.read_text())
    fit = net["fit"]
    # each run: decision 0 holds FIRE and BLOCK, decision 3 BLOCK; FIRE's saving is the combat net's, BLOCK's 0
    assert (fit["runs"], fit["held_out_runs"], fit["samples"], fit["potions"], fit["held_out_potions"]) == (10, 2, 20, 30, 6)
    assert set(fit["by_potion"]) == {"FIRE", "BLOCK"} and fit["by_potion"]["BLOCK"]["saved"] == 0.0
    assert math.isfinite(fit["rmse_held_out"]) and math.isfinite(fit["init_rmse_held_out"])
    assert net["trained"]["init"] == "v-test" and net["trained"]["settings"]["combat_net"] == "c-test"
    assert net["tests"] and all(len(t["saves"]) >= 1 for t in net["tests"])
