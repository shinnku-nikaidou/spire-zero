import base64
import json
from pathlib import Path

import onnx
import pytest
import torch
from conftest import encoder, encoder_command, make_enemy, make_obs, round_start
from onnx.reference import ReferenceEvaluator
from typer.testing import CliRunner

import common
import game_ids
from combat import data, export, spec, train
from combat.net import Batch, Net, loss, won_group


def obs(round=1, hp=50, **over):
    o = make_obs(round=round, hp=hp, enemies=[make_enemy(1, hp=12), make_enemy(2, hp=0)], potions=("FIRE", None, None))
    o["hand"] = [{"id": "STRIKE", "up": 5, "cost": 1, "vars": {"Damage": 6}, "enchant": {"id": "SHARP", "amount": 2}}]
    o["draw"] = [{"id": "DEFEND", "cost": 1, "vars": {"Block": 5}}]
    o["player"]["powers"] = [{"id": "STRENGTH_POWER", "amount": 2}]
    o["relics"] = [{"id": "RING", "counter": 3}, {"id": "VASE", "used_up": True}]
    return o | over


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
        data.sample("a", round_start(obs(hp=hp)), {"died": False, "hp": end, "max_hp": 70, "potions": 0}, {"potions": []})
        for hp, end in ((70, 70), (70, 1), (7, 1), (7, 70), (1, 1), (1, 70), (35, 34))
    ]
    tables = game_ids.combat_tables()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    b = Batch(samples, index, "cpu")
    net = Net({t: len(i) for t, i in tables.items()})
    logits, potions = net(*b.inputs())
    assert potions.shape[1:] == (data.POTION_GROUPS, len(tables["potions"]))
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
        s = data.sample("a", round_start(o), {"died": True, "potions": 0}, None)
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
    won = data.sample("a", round_start(obs()), end, {"potions": []})
    dead = data.sample("a", round_start(obs()), {"died": True, "potions": 0}, None)
    plain = data.sample("a", round_start(obs(potions=(None, None, None))), end, {"potions": []})
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


def test_a_wins_potion_chances_are_those_of_its_group_of_buckets():
    potions = torch.arange(2 * data.POTION_GROUPS * 3).float().view(2, data.POTION_GROUPS, 3)
    got = won_group(potions, torch.tensor([1, data.BUCKETS]))
    assert got.tolist() == [potions[0, 0].tolist(), potions[1, data.POTION_GROUPS - 1].tolist()]
    per = data.BUCKETS // data.POTION_GROUPS
    assert [data.potion_group(c) for c in (1, per, per + 1, data.BUCKETS)] == [0, 0, 1, data.POTION_GROUPS - 1]


def shown(round):
    """The record of enemy moves as a decision of this round logs it: what the living enemy showed each round so far."""
    intents = [{"type": "Attack", "damage": 6, "hits": 1}]
    return [{"uid": 1, "id": "M", "turns": [{"round": r, "intents": intents} for r in range(1, round + 1)]}]


def write_runs(tmp_path, env="SL", runs=10):
    """A log and its records: runs of one combat of three rounds, each ending at 40 HP with its potion gone; ENV "both"
    plays every other run in NoSL."""
    log, recs = tmp_path / "log.jsonl", tmp_path / "rec.jsonl"
    with open(log, "w") as lf, open(recs, "w") as rf:
        for i in range(runs):
            run = f"{i:016x}"
            for d, r in enumerate((1, 1, 2, 3)):
                line = {"run": run, "decision_id": d, "env": env_of(env, i), "surface": "combat", "obs": obs(round=r, hp=60 - r), "history": shown(r)}
                lf.write(json.dumps(line, separators=(",", ":")) + "\n")
            line = {"run": run, "decision_id": 4, "env": env_of(env, i), "surface": "map", "run_view": {"potions": [None, None, None]}}
            lf.write(json.dumps(line, separators=(",", ":")) + "\n")
            end = {"died": i % 3 == 0, "hp": 40, "max_hp": 70, "potions": 0}
            rf.write(
                json.dumps({"run_id": run, "seed": f"S{i}", "character": "SILENT", "combats": [{"encounter": "E", "end": end}]}) + "\n"
            )
    (tmp_path / "s-logs.txt").write_text(f"{log}\n")
    (tmp_path / "s-recs.txt").write_text(f"{recs}\n")
    return str(tmp_path / "s")


def env_of(env: str, i: int) -> str:
    return ("SL", "NoSL")[i % 2] if env == "both" else env


def test_each_round_start_is_a_sample_with_the_history_before_it(tmp_path):
    samples, skipped = data.read(*common.lists(write_runs(tmp_path)), encoder())
    assert skipped == 0 and len(samples) == 30  # rounds 1, 2 and 3 of each run's combat
    first, third = samples[0], samples[2]
    enemy = [common.unpack(s["tokens"])["enemies"][0] for s in (first, third)]
    assert enemy[0]["scalars"][4] == 0 and enemy[1]["scalars"][4] == 0.2
    assert first["class"] == 0 and samples[3]["class"] > 0 and samples[3]["potion_used"] == [1]
    assert first["potion_used"] == [0] and first["lost"] is None and samples[3]["lost"] == pytest.approx((59 - 40) / 70)


def test_both_environments_train_one_net_each_state_read_in_its_own(tmp_path):
    samples, skipped = data.read(*common.lists(write_runs(tmp_path, env="both")), encoder())
    assert skipped == 0 and len(samples) == 30
    for s in samples:
        context = common.unpack(s["tokens"])["context"]
        assert ("env:NoSL" in context) == (s["env"] == "NoSL")
    assert {s["env"] for s in samples} == {"SL", "NoSL"}


def test_the_onnx_graph_gives_the_test_states_the_endings_torch_gave_them():
    history = [{"uid": 1, "id": "M", "turns": [{"round": 1, "intents": [{"type": "Buff"}]}]}]
    end = {"died": False, "hp": 30, "max_hp": 70, "potions": 0}
    samples = [data.sample("a", start, end, {"potions": []}) | {"obs": start["obs"], "history": start["history"]}
               for start in (round_start(obs(round=r, hp=40 + r), history) for r in (2, 3))]  # fmt: skip
    tables = game_ids.combat_tables()
    torch.manual_seed(0)
    net = export.net_file("t", "SL", tables, Net({t: len(i) for t, i in tables.items()}), samples, {}, 0, {})
    assert net["env"] == "SL" and net["lags"] == spec.LAGS and len(net["tests"]) == 2
    graph = onnx.load_from_string(base64.b64decode(net["onnx"]))
    assert [i.name for i in graph.graph.input] == spec.INPUTS
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"]:
        b = Batch([data.sample("a", round_start(test["obs"], test["history"]), {"died": True, "potions": 0}, None)], index, "cpu")
        feed = {name: x.numpy() for name, x in zip(spec.INPUTS, b.inputs(), strict=True)}
        outcome, used = ReferenceEvaluator(graph).run(None, feed)
        assert outcome[0].tolist() == pytest.approx(test["outcome"], abs=1e-5)
        assert used[0].flatten().tolist() == pytest.approx(test["potion_used"], abs=1e-5)
        assert 0 <= min(test["potion_used"]) and max(test["potion_used"]) <= 1


def test_training_writes_a_net_file_of_both_environments(tmp_path, monkeypatch):
    monkeypatch.setattr(data, "KEEP", 1)
    out = tmp_path / "net.json"
    runs = write_runs(tmp_path, env="both", runs=14)
    # --workers 0: the patched KEEP holds in this process only, not in a worker's fresh import
    args = ["--list", runs, "--name", "t", "--out", str(out), "--test-list", runs, "--encoder", encoder_command()]
    args += ["--workers", "0"]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    net = json.loads(out.read_text())
    assert net["env"] == "SL+NoSL" and set(net["fit"]["held_out_loss_by_env"]) == {"SL", "NoSL"}
    assert {t.get("env", "SL") for t in net["tests"]} == {"SL", "NoSL"}
    assert net["fit"]["runs"] == 14 and net["fit"]["held_out_runs"] == 3 and net["tests"]
    made = net["trained"]
    assert (made["list"], made["init"], made["seed"]) == (runs, None, 0) and made["rev"] == common.revision()
    assert 1 <= made["epochs"] <= made["settings"]["epochs"] and made["settings"]["lr"] == train.LR
    assert made["settings"]["test_list"] == runs and made["settings"]["dim"] == Net.DIM
    assert set(net["fit"]["held_out"]) >= {"rank_lost", "rank_lost_share", "auc_death", "auc_death_share", "potions"}
    assert net["fit"]["by_hp"] and net["fit"]["by_potion"]
    assert net["fit"]["medium"]["held_out"]["samples"] == 42  # the 14 runs' three rounds


def test_the_champions_weights_read_back_give_its_test_states_their_endings():
    """The SL champion's combat net, which weights/fetch.sh brings into a checkout."""
    repo = Path(__file__).parents[2]
    champion = json.loads((repo / "eval" / "champions" / "sl.json").read_text())
    net = export.read(str(repo / champion["options"]["combat-net"]))
    tables = net["vocab"]
    model = Net({t: len(i) for t, i in tables.items()}).eval()
    model.load_state_dict(export.weights(net, model))
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    for test in net["tests"][:3]:
        b = Batch([data.sample("a", round_start(test["obs"], test["history"]), {"died": True, "potions": 0}, None)], index, "cpu")
        with torch.no_grad():
            outcome = torch.softmax(model(*b.inputs())[0], 1)[0]
        assert outcome.tolist() == pytest.approx(test["outcome"], abs=1e-5)


def test_training_goes_on_from_a_net_file_and_keeps_it_unless_an_epoch_beats_it(tmp_path, monkeypatch):
    monkeypatch.setattr(data, "KEEP", 1)
    first, second = tmp_path / "first.json", tmp_path / "second.json"
    runs = write_runs(tmp_path, runs=14)
    args = ["--list", runs, "--name", "first", "--out", str(first), "--encoder", encoder_command(), "--workers", "0"]
    assert CliRunner().invoke(train.script, args).exit_code == 0
    args = ["--list", runs, "--name", "second", "--out", str(second), "--init", str(first), "--encoder", encoder_command(), "--workers", "0",
            "--max-epochs", "0"]
    result = CliRunner().invoke(train.script, args)
    assert result.exit_code == 0, result.output
    a, b = json.loads(first.read_text()), json.loads(second.read_text())
    assert a["vocab"] == b["vocab"]
    assert b["tests"][0]["obs"] == a["tests"][0]["obs"]  # the same held-out states, picked by the same seed
    assert b["tests"][0]["outcome"] == pytest.approx(a["tests"][0]["outcome"], abs=1e-5)


def test_a_combat_the_run_died_in_before_its_first_decision_leaves_the_run_in(tmp_path):
    prefix = write_runs(tmp_path, runs=3)
    recs = tmp_path / "rec.jsonl"
    entered = {"encounter": "BOSS", "hp": 0, "end": {"died": True, "hp": 0, "max_hp": 70, "potions": 0}}
    lines = [json.loads(line) for line in recs.read_text().splitlines()]
    recs.write_text("".join(json.dumps(r | {"combats": r["combats"] + [entered]}) + "\n" for r in lines))
    samples, skipped = data.read(*common.lists(prefix), encoder())
    assert skipped == 0 and len(samples) == 9
