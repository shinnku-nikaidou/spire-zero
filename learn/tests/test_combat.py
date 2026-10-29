import base64
import json
import math

import onnx
import pytest
import torch
from conftest import make_enemy, make_obs
from onnx.reference import ReferenceEvaluator
from typer.testing import CliRunner

import common
from combat import data, encode, export, train
from combat.net import Batch, Net


def obs(round=1, hp=50, **over):
    o = make_obs(round=round, hp=hp, enemies=[make_enemy(1, hp=12), make_enemy(2, hp=0)], potions=("FIRE", None, None))
    o["hand"] = [{"id": "STRIKE", "up": 5, "cost": 1, "vars": {"Damage": 6}, "enchant": {"id": "SHARP", "amount": 2}}]
    o["draw"] = [{"id": "DEFEND", "cost": 1, "vars": {"Block": 5}}]
    o["player"]["powers"] = [{"id": "STRENGTH_POWER", "amount": 2}]
    o["relics"] = [{"id": "RING", "counter": 3}, {"id": "VASE", "used_up": True}]
    return o | over


def test_cards_are_ids_upgrades_piles_and_modifiers_without_their_numbers():
    t = encode.tokens(obs(), [])
    assert t["cards"] == [("STRIKE", encode.MAX_UP, 0, ["enchant:SHARP"]), ("DEFEND", 0, 1, [])]
    assert t["relics"] == ["RING", "VASE:used"] and t["potions"] == ["FIRE"]
    assert t["powers"] == [("STRENGTH_POWER", 0.2, 0.0, None)]


def test_the_player_and_each_living_enemy_are_their_raw_state_scaled():
    t = encode.tokens(obs(round=4), [])
    assert t["player"] == pytest.approx([50 / 70, 0.5, 0.7, 0, 1, 1, 0, 0.4])
    [enemy] = t["enemies"]  # the dead one is left out
    assert enemy["id"] == "M" and enemy["scalars"] == pytest.approx([12 / 20, 0.12, 0.2, 0, 0])
    assert enemy["slots"] == [[("Attack", 0.6, 0.3)]]


def test_an_enemy_reads_its_last_rounds_intents_most_recent_first():
    history = [{"uid": 1, "id": "M", "turns": [{"round": r, "intents": [{"type": f"T{r}"}]} for r in range(1, 6)]}]
    [enemy] = encode.tokens(obs(round=6), history)["enemies"]
    assert [s[0][0] if s else None for s in enemy["slots"]] == ["Attack", "T5", "T4", "T3"]
    assert enemy["scalars"][4] == 0.5  # five rounds shown


def test_an_ending_is_a_death_or_the_bucket_of_the_hp_share_lost_since():
    o = obs(hp=70)
    assert data.ending(o, {"died": True}) == 0
    assert data.ending(o, {"died": False, "hp": 70, "max_hp": 70}) == 1 + int(0.25 / 1.25 * data.BUCKETS)
    assert data.ending(o, {"died": False, "hp": 0, "max_hp": 70}) == data.BUCKETS


def write_runs(tmp_path, env="SL", runs=10):
    """A log and its records: runs of one combat of three rounds, each ending at 40 HP with one potion left."""
    log, recs = tmp_path / "log.jsonl", tmp_path / "rec.jsonl"
    with open(log, "w") as lf, open(recs, "w") as rf:
        for i in range(runs):
            run = f"{i:016x}"
            for d, r in enumerate((1, 1, 2, 3)):
                line = {"run": run, "decision_id": d, "env": env, "surface": "combat", "obs": obs(round=r, hp=60 - r)}
                lf.write(json.dumps(line, separators=(",", ":")) + "\n")
            lf.write(
                json.dumps({"run": run, "decision_id": 4, "env": env, "surface": "map"}, separators=(",", ":")) + "\n"
            )
            end = {"died": i % 3 == 0, "hp": 40, "max_hp": 70, "potions": 0}
            rf.write(
                json.dumps({"run_id": run, "character": "SILENT", "combats": [{"encounter": "E", "end": end}]}) + "\n"
            )
    (tmp_path / "s-logs.txt").write_text(f"{log}\n")
    (tmp_path / "s-recs.txt").write_text(f"{recs}\n")
    return str(tmp_path / "s")


def test_each_round_start_is_a_sample_with_the_history_before_it(tmp_path):
    samples, skipped = data.read(*common.lists(write_runs(tmp_path)), "SILENT", "SL")
    assert skipped == 0 and len(samples) == 30  # rounds 1, 2 and 3 of each run's combat
    first, third = samples[0], samples[2]
    assert first["history"] == [] and [t["round"] for t in third["history"][0]["turns"]] == [1, 2]
    assert first["class"] == 0 and samples[3]["class"] > 0 and first["used"] == 1


def test_another_environments_logs_are_refused(tmp_path):
    with pytest.raises(common.LearnError, match="NoSL decisions, not SL"):
        data.read(*common.lists(write_runs(tmp_path, env="NoSL")), "SILENT", "SL")


def test_the_onnx_graph_gives_the_test_states_the_endings_torch_gave_them():
    history = [{"uid": 1, "id": "M", "turns": [{"round": 1, "intents": [{"type": "Buff"}]}]}]
    samples = [data.sample("a", obs(round=r, hp=40 + r), history, {"died": False, "hp": 30, "max_hp": 70, "potions": 0})
               for r in (2, 3)]  # fmt: skip
    tables = train.vocab(samples * 3)
    torch.manual_seed(0)
    net = export.net_file("t", "SILENT", "SL", tables, Net({t: len(i) for t, i in tables.items()}), samples, {}, 0)
    assert net["env"] == "SL" and net["lags"] == encode.LAGS and len(net["tests"]) == 2
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    assert [i.name for i in graph.graph.input] == encode.INPUTS
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", test["obs"], test["history"], {"died": True, "potions": 0})], index, "cpu")
        feed = {name: x.numpy() for name, x in zip(encode.INPUTS, b.inputs(), strict=True)}
        outcome, used = ReferenceEvaluator(graph).run(None, feed)
        assert outcome[0].tolist() == pytest.approx(test["outcome"], abs=1e-5)
        assert math.isclose(float(used[0]), test["potions_used"], abs_tol=1e-5)


def test_training_writes_a_net_file_of_its_environment(tmp_path):
    out = tmp_path / "net.json"
    args = ["--list", write_runs(tmp_path), "--env", "SL", "--name", "t", "--out", str(out)]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    net = json.loads(out.read_text())
    assert net["env"] == "SL" and net["fit"]["runs"] == 10 and net["fit"]["held_out_runs"] == 2 and net["tests"]
