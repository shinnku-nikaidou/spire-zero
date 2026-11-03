import base64
import json
from pathlib import Path

import onnx
import pytest
import torch
from conftest import make_enemy, make_obs
from onnx.reference import ReferenceEvaluator
from typer.testing import CliRunner

import common
import game_ids
from combat import data, encode, export, train
from combat.net import Batch, Net, loss


def obs(round=1, hp=50, **over):
    o = make_obs(round=round, hp=hp, enemies=[make_enemy(1, hp=12), make_enemy(2, hp=0)], potions=("FIRE", None, None))
    o["hand"] = [{"id": "STRIKE", "up": 5, "cost": 1, "vars": {"Damage": 6}, "enchant": {"id": "SHARP", "amount": 2}}]
    o["draw"] = [{"id": "DEFEND", "cost": 1, "vars": {"Block": 5}}]
    o["player"]["powers"] = [{"id": "STRENGTH_POWER", "amount": 2}]
    o["relics"] = [{"id": "RING", "counter": 3}, {"id": "VASE", "used_up": True}]
    return o | over


def test_cards_are_ids_upgrades_piles_modifiers_costs_and_numbers():
    t = encode.tokens(obs(), [])
    assert t["cards"] == [
        ("STRIKE", encode.MAX_UP, 0, ["enchant:SHARP"], 1 / 3, [0.2], 0.3, [0, 0, 0, 0, 0]),
        ("DEFEND", 0, 1, [], 1 / 3, [], 0.0, [0.25, 0, 0, 0, 0]),
    ]
    assert t["relics"] == ["RING", "VASE:used"] and t["potions"] == ["FIRE"]
    assert t["relic_status"] == ["Normal", "Normal"]
    assert t["powers"] == [("STRENGTH_POWER", 0.2, 0.0, None, 0.0)]


def test_a_card_reads_its_keywords_tinker_spent_enchantment_and_gathered_numbers():
    o = obs()
    o["hand"] = [
        {"id": "MAD_SCIENCE", "cost": 1, "stars": 2, "keywords": ["Exhaust", "Retain"], "calc": 12, "replay": 1,
         "tinker": {"type": "Attack", "rider": "Violence"}, "affliction": {"id": "HEXED", "amount": 1}},
        {"id": "PUNCH_DAGGER", "cost": 1, "enchant": {"id": "MOMENTUM", "amount": 2, "extra": 4}, "spent": True,
         "this_turn": ["Sly"]},
        {"id": "BONE_SHARDS", "cost": 1, "vars": {"OstyDamage": 9}},
    ]  # fmt: skip
    mad, dagger, osty, _ = encode.tokens(o, [])["cards"]
    assert mad[3] == ["affliction:HEXED", "keyword:Exhaust", "keyword:Retain", "tinker:Attack", "rider:Violence"]
    assert mad[5] == [0.1] and mad[7] == pytest.approx([0, 0.6, 0.5, 0.4, 0])
    assert osty[6] == pytest.approx(0.45)
    assert dagger[3] == ["enchant:MOMENTUM", "keyword:Sly", "spent"] and dagger[7] == pytest.approx([0, 0, 0, 0, 0.2])
    tables = game_ids.combat_tables()
    assert all(m in tables["modifiers"] for m in mad[3] + dagger[3])


def test_a_power_reads_its_moved_number_and_the_card_it_stole_and_a_relic_its_effect_spent():
    o = obs()
    o["enemies"][0]["powers"] = [{"id": "SWIPE_POWER", "amount": 1, "stolen": {"id": "DEFEND"}, "detail": 10}]
    o["relics"] = [{"id": "CENTENNIAL_PUZZLE", "spent": True}, {"id": "RING", "status": "Active"}]
    t = encode.tokens(o, [])
    assert t["enemies"][0]["powers"] == [("SWIPE_POWER", 0.1, 0.0, "DEFEND", 0.5)]
    assert t["relic_status"] == ["Spent", "Active"] and "Spent" in game_ids.combat_tables()["relic_status"]


def test_the_player_and_each_enemy_are_their_raw_state_scaled():
    t = encode.tokens(obs(round=4), [])
    assert t["player"] == pytest.approx([50 / 70, 0.5, 0.7, 0, 1, 1, 0, 0.4])
    [enemy, dead] = t["enemies"]  # one lying dead is read: it may revive
    assert enemy["id"] == "M" and enemy["scalars"] == pytest.approx([12 / 20, 0.12, 0.2, 0, 0])
    assert enemy["slots"] == [[("Attack", 0.6, 0.3)]]
    assert dead["scalars"][:2] == [0, 0]
    assert "character:SILENT" in t["context"] and "character:SILENT" in game_ids.combat_tables()["context"]


def test_an_enemy_reads_its_last_rounds_intents_most_recent_first():
    history = [{"uid": 1, "id": "M", "turns": [{"round": r, "intents": [{"type": f"T{r}"}]} for r in range(1, 6)]}]
    enemy = encode.tokens(obs(round=6), history)["enemies"][0]
    assert [s[0][0] if s else None for s in enemy["slots"]] == ["Attack", "T5", "T4", "T3"]
    assert enemy["scalars"][4] == 0.5  # five rounds shown


def test_an_ending_is_a_death_or_the_bucket_of_the_hp_share_lost_since():
    o = obs(hp=70)
    assert data.ending(o, {"died": True}) == 0
    assert data.ending(o, {"died": False, "hp": 70, "max_hp": 70}) == 1 + int(0.25 / 1.25 * data.BUCKETS)
    assert data.ending(o, {"died": False, "hp": 0, "max_hp": 70}) == data.BUCKETS


def test_a_potion_used_is_one_gone_from_the_view_after_the_fight():
    o = obs(potions=("FIRE", "FAIRY", "FIRE"))
    assert data.potions_used(o, {"potions": ["FIRE", "FAIRY", None]}) == [0, 0, 1]  # one copy of two
    assert data.potions_used(o, {"potions": [None, None, "FIRE"]}) == [0, 1, 1]  # a Fairy goes with no action
    assert data.potions_used(obs(), {"potions": ["FIRE", "GAINED", None]}) == [0]  # gained later: not this state's
    assert data.potions_used(obs(), {"potions": [None, None, None]}) == [1]  # drunk after the last round's start


def test_a_win_cannot_end_in_a_bucket_the_hp_held_rules_out():
    samples = [
        data.sample("a", obs(hp=hp), [], {"died": False, "hp": end, "max_hp": 70, "potions": 0}, {"potions": []})
        for hp, end in ((70, 70), (70, 1), (7, 1), (7, 70), (1, 1), (1, 70), (35, 34))
    ]
    tables = game_ids.combat_tables()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    b = Batch(samples, index, "cpu")
    net = Net({t: len(i) for t, i in tables.items()})
    logits, potions = net(*b.inputs())
    assert potions.shape[1] == len(tables["potions"])
    closed = logits < Net.CLOSED / 2
    assert not closed.gather(1, b.target[:, None]).any()  # the truth stays open
    assert not closed[:, 0].any()  # a death always can be
    low = data.LOSS_MIN + torch.arange(data.BUCKETS) * data.WIDTH
    assert closed[2, 1:].tolist() == (low >= 7 / 70).tolist()  # at 10 % HP nothing above 10 % can be lost
    assert closed[0, 1:].tolist() == (low + data.WIDTH <= -1e-6).tolist()  # at full HP nothing can heal
    assert torch.softmax(logits, 1)[closed].max() == 0


def test_a_cards_numbers_and_modifiers_and_the_orbs_numbers_and_order_move_the_output():
    tables = game_ids.combat_tables()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    torch.manual_seed(0)
    net = Net({t: len(i) for t, i in tables.items()}).eval()

    def outcome(hand_damage=6, orbs=(), **card):
        o = obs()
        o["hand"][0]["vars"]["Damage"] = hand_damage
        o["hand"][0] |= card
        o["player"]["orbs"] = [{"id": i, "passive": p, "evoke": e} for i, p, e in orbs]
        s = data.sample("a", o, [], {"died": True, "potions": 0}, None)
        with torch.no_grad():
            return net(*Batch([s], index, "cpu").inputs())[0]

    dark, frost = ("DARK_ORB", 6, 6), ("FROST_ORB", 2, 5)
    assert not torch.equal(outcome(), outcome(hand_damage=30))
    for field, value in (("calc", 20), ("replay", 1), ("stars", 3), ("keywords", ["Retain"]), ("this_turn", ["Sly"]), ("spent", True)):
        assert not torch.equal(outcome(), outcome(**{field: value})), field
    assert not torch.equal(outcome(orbs=[dark, frost]), outcome(orbs=[("DARK_ORB", 6, 18), frost]))
    assert not torch.equal(outcome(orbs=[dark, frost]), outcome(orbs=[("DARK_ORB", 9, 6), frost]))
    assert not torch.equal(outcome(orbs=[dark, frost]), outcome(orbs=[frost, dark]))
    assert not torch.equal(outcome(orbs=[dark] * 12), outcome(orbs=[dark] * 11))


def test_the_potion_loss_reads_held_potions_after_wins_only():
    end = {"died": False, "hp": 40, "max_hp": 70, "potions": 0}
    won = data.sample("a", obs(), [], end, {"potions": []})
    dead = data.sample("a", obs(), [], {"died": True, "potions": 0}, None)
    plain = data.sample("a", obs(potions=(None, None, None)), [], end, {"potions": []})
    tables = game_ids.combat_tables()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    b = Batch([won, dead, plain], index, "cpu")
    assert b.potion_used.tolist() == [[1], [0], [0]] and b.potion_mask.tolist() == [[1], [1], [0]]
    net = Net({t: len(i) for t, i in tables.items()})
    out = net(*b.inputs())
    per = loss(out, b)
    ces = torch.nn.functional.cross_entropy(out[0], b.target, reduction="none")
    assert per[0] > ces[0]
    assert per[1:].tolist() == pytest.approx(ces[1:].tolist())


def write_runs(tmp_path, env="SL", runs=10):
    """A log and its records: runs of one combat of three rounds, each ending at 40 HP with its potion gone."""
    log, recs = tmp_path / "log.jsonl", tmp_path / "rec.jsonl"
    with open(log, "w") as lf, open(recs, "w") as rf:
        for i in range(runs):
            run = f"{i:016x}"
            for d, r in enumerate((1, 1, 2, 3)):
                line = {"run": run, "decision_id": d, "env": env, "surface": "combat", "obs": obs(round=r, hp=60 - r)}
                lf.write(json.dumps(line, separators=(",", ":")) + "\n")
            line = {"run": run, "decision_id": 4, "env": env, "surface": "map", "run_view": {"potions": [None, None, None]}}
            lf.write(json.dumps(line, separators=(",", ":")) + "\n")
            end = {"died": i % 3 == 0, "hp": 40, "max_hp": 70, "potions": 0}
            rf.write(
                json.dumps({"run_id": run, "seed": f"S{i}", "character": "SILENT", "combats": [{"encounter": "E", "end": end}]}) + "\n"
            )
    (tmp_path / "s-logs.txt").write_text(f"{log}\n")
    (tmp_path / "s-recs.txt").write_text(f"{recs}\n")
    return str(tmp_path / "s")


def test_each_round_start_is_a_sample_with_the_history_before_it(tmp_path):
    samples, skipped = data.read(*common.lists(write_runs(tmp_path)), "SILENT", "SL")
    assert skipped == 0 and len(samples) == 30  # rounds 1, 2 and 3 of each run's combat
    first, third = samples[0], samples[2]
    assert first["tokens"]["enemies"][0]["scalars"][4] == 0 and third["tokens"]["enemies"][0]["scalars"][4] == 0.2
    assert first["class"] == 0 and samples[3]["class"] > 0 and samples[3]["potion_used"] == [1]
    assert first["potion_used"] == [0] and first["lost"] is None and samples[3]["lost"] == pytest.approx((59 - 40) / 70)


def test_another_environments_logs_are_refused(tmp_path):
    with pytest.raises(common.LearnError, match="NoSL decisions, not SL"):
        data.read(*common.lists(write_runs(tmp_path, env="NoSL")), "SILENT", "SL")


def test_the_onnx_graph_gives_the_test_states_the_endings_torch_gave_them():
    history = [{"uid": 1, "id": "M", "turns": [{"round": 1, "intents": [{"type": "Buff"}]}]}]
    end = {"died": False, "hp": 30, "max_hp": 70, "potions": 0}
    samples = [data.sample("a", obs(round=r, hp=40 + r), history, end, {"potions": []})
               | {"obs": obs(round=r, hp=40 + r), "history": history} for r in (2, 3)]  # fmt: skip
    tables = game_ids.combat_tables()
    torch.manual_seed(0)
    net = export.net_file("t", "SILENT", "SL", tables, Net({t: len(i) for t, i in tables.items()}), samples, {}, 0)
    assert net["env"] == "SL" and net["lags"] == encode.LAGS and len(net["tests"]) == 2
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    assert [i.name for i in graph.graph.input] == encode.INPUTS
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", test["obs"], test["history"], {"died": True, "potions": 0}, None)], index, "cpu")
        feed = {name: x.numpy() for name, x in zip(encode.INPUTS, b.inputs(), strict=True)}
        outcome, used = ReferenceEvaluator(graph).run(None, feed)
        assert outcome[0].tolist() == pytest.approx(test["outcome"], abs=1e-5)
        assert used[0].tolist() == pytest.approx(test["potion_used"], abs=1e-5)
        assert 0 <= min(test["potion_used"]) and max(test["potion_used"]) <= 1


def test_training_writes_a_net_file_of_its_environment(tmp_path, monkeypatch):
    monkeypatch.setattr(data, "KEEP", 1)
    out = tmp_path / "net.json"
    runs = write_runs(tmp_path, runs=14)
    args = ["--list", runs, "--env", "SL", "--name", "t", "--out", str(out), "--test-list", runs]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    net = json.loads(out.read_text())
    assert net["env"] == "SL" and net["fit"]["runs"] == 14 and net["fit"]["held_out_runs"] == 3 and net["tests"]
    assert set(net["fit"]["held_out"]) >= {"rank_lost", "rank_lost_share", "auc_death", "auc_death_share", "potions"}
    assert net["fit"]["by_hp"] and net["fit"]["by_potion"]
    assert net["fit"]["medium"]["held_out"]["samples"] == 42  # the 14 runs' three rounds


def test_c9_0s_weights_read_back_give_its_test_states_their_endings():
    net = export.read(str(Path(__file__).parents[2] / "weights" / "all" / "c9-0.json"))
    tables = net["vocab"]
    model = Net({t: len(i) for t, i in tables.items()}).eval()
    model.load_state_dict(export.weights(net, model))
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"][:3]:
        b = Batch([data.sample("a", test["obs"], test["history"], {"died": True, "potions": 0}, None)], index, "cpu")
        with torch.no_grad():
            outcome = torch.softmax(model(*b.inputs())[0], 1)[0]
        assert outcome.tolist() == pytest.approx(test["outcome"], abs=1e-5)


def test_training_goes_on_from_a_net_file_and_keeps_it_unless_an_epoch_beats_it(tmp_path, monkeypatch):
    monkeypatch.setattr(data, "KEEP", 1)
    first, second = tmp_path / "first.json", tmp_path / "second.json"
    runs = write_runs(tmp_path, runs=14)
    args = ["--list", runs, "--env", "SL", "--name", "first", "--out", str(first)]
    assert CliRunner().invoke(train.script, args).exit_code == 0
    monkeypatch.setattr(train, "EPOCHS", 0)
    args = ["--list", runs, "--env", "SL", "--name", "second", "--out", str(second), "--init", str(first)]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    a, b = json.loads(first.read_text()), json.loads(second.read_text())
    assert a["vocab"] == b["vocab"]
    assert b["tests"][0]["outcome"] == pytest.approx(a["tests"][0]["outcome"], abs=1e-5) or a["tests"][0]["obs"] != b["tests"][0]["obs"]


def test_a_combat_the_run_died_in_before_its_first_decision_leaves_the_run_in(tmp_path):
    prefix = write_runs(tmp_path, runs=3)
    recs = tmp_path / "rec.jsonl"
    entered = {"encounter": "BOSS", "hp": 0, "end": {"died": True, "hp": 0, "max_hp": 70, "potions": 0}}
    lines = [json.loads(line) for line in recs.read_text().splitlines()]
    recs.write_text("".join(json.dumps(r | {"combats": r["combats"] + [entered]}) + "\n" for r in lines))
    samples, skipped = data.read(*common.lists(prefix), "SILENT", "SL")
    assert skipped == 0 and len(samples) == 9
