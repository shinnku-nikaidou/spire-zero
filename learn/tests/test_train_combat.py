import json
from types import SimpleNamespace

import pytest
import torch
from typer.testing import CliRunner

import train_combat as tc

ATTACK = {"type": "Attack", "damage": 6, "hits": 1}
BUFF = {"type": "Buff"}
DEFEND = {"type": "Defend"}


def test_outcome_classes_a_death_then_the_hp_share_lost_from_the_state(obs):
    start = obs(hp=70, max_hp=70)
    assert tc.outcome(start, {"died": True, "hp": 0, "max_hp": 70}, 50, -0.25) == 0
    # No loss sits in the bucket of 0, which is 0.25 / 1.25 of the way up.
    assert tc.outcome(start, {"died": False, "hp": 70, "max_hp": 70}, 50, -0.25) == 1 + 10
    assert tc.outcome(start, {"died": False, "hp": 1, "max_hp": 70}, 50, -0.25) == 50
    # A heal beyond the lowest bucket is clamped into it.
    assert tc.outcome(obs(hp=10, max_hp=70), {"died": False, "hp": 70, "max_hp": 70}, 50, -0.25) == 1


def test_values_count_the_hp_held_now_in_full():
    probs = torch.zeros(2, 51)
    probs[:, 11] = 1  # the same predicted loss for both states
    used = torch.zeros(2)
    b = SimpleNamespace(share=torch.tensor([50 / 70, 49 / 70]), held=torch.tensor([1.0, 1.0]))
    v = tc.values(probs, used, b, 50, -0.25)
    assert float(v[0] - v[1]) == pytest.approx(tc.PER_HP / 70, rel=1e-5)


def test_values_count_potions_held_less_those_used():
    probs = torch.zeros(1, 51)
    probs[0, 11] = 1
    b = SimpleNamespace(share=torch.tensor([1.0]), held=torch.tensor([2.0]))
    v0 = tc.values(probs, torch.tensor([0.0]), b, 50, -0.25)
    v1 = tc.values(probs, torch.tensor([1.0]), b, 50, -0.25)
    assert float(v0 - v1) == pytest.approx(tc.PER_POTION, rel=1e-5)


def test_witness_records_each_living_enemy_once_per_round(obs, enemy):
    history = {}
    tc.witness(history, obs(round=1, enemies=[enemy(1), enemy(2)]))
    tc.witness(history, obs(round=2, enemies=[enemy(1, intents=[BUFF]), enemy(2, hp=0)]))
    assert [t["round"] for t in history[1]["turns"]] == [1, 2]
    assert [t["round"] for t in history[2]["turns"]] == [1]
    assert history[1]["turns"][1]["intents"] == [BUFF]


def test_tokens_slots_are_the_intents_now_then_the_last_rounds_most_recent_first(obs, enemy):
    past = [{"uid": 1, "id": "M", "turns": [{"round": 1, "intents": [ATTACK]}, {"round": 2, "intents": [BUFF]}]}]
    now = obs(round=3, enemies=[enemy(1, intents=[DEFEND])])
    kinds = lambda t: [[k for k, _, _ in slot] for slot in t["enemies"][0]["slots"]]  # noqa: E731
    assert kinds(tc.tokens(now, past, 3, True)) == [["Defend"], ["Buff"], ["Attack"]]
    assert kinds(tc.tokens(now, past, 1, True)) == [["Defend"], ["Buff"]]
    assert kinds(tc.tokens(now, past, 0, True)) == [["Defend"]]


def test_tokens_hide_the_round_and_rounds_shown_when_asked(obs, enemy):
    past = [{"uid": 1, "id": "M", "turns": [{"round": 1, "intents": [ATTACK]}]}]
    now = obs(round=2, enemies=[enemy(1)])
    shown, hidden = tc.tokens(now, past, 3, True), tc.tokens(now, past, 3, False)
    assert shown["player"][7] == pytest.approx(0.2) and hidden["player"][7] == 0
    assert shown["enemies"][0]["scalars"][4] == pytest.approx(0.1) and hidden["enemies"][0]["scalars"][4] == 0


def test_tokens_cap_huge_numbers(obs, enemy):
    giant = enemy(1, hp=999_999_999)
    giant["max_hp"] = 999_999_999
    scalars = tc.tokens(obs(enemies=[giant]), [], 3, True)["enemies"][0]["scalars"]
    assert max(scalars) == tc.CAP


def test_intent_token_reads_missing_hits_as_one():
    assert tc.intent_token({"type": "Attack", "damage": 10}) == ("Attack", 1.0, 0.5)
    assert tc.intent_token({"type": "Attack", "damage": 10, "hits": 0}) == ("Attack", 1.0, 0.0)
    assert tc.intent_token({"type": "Buff"}) == ("Buff", 0.0, 0.0)


def test_next_intents_follow_each_enemy_by_uid(obs, enemy):
    now = obs(round=1, enemies=[enemy(1), enemy(2), enemy(3, hp=0)])
    nxt = obs(round=2, enemies=[enemy(2, intents=[BUFF]), enemy(1, hp=0)])
    assert tc.next_intents(now, nxt) == [None, [BUFF]]
    assert tc.next_intents(now, None) == [None, None]


def test_combats_split_at_macro_decisions_and_when_the_round_goes_back(tmp_path, obs):
    def line(surface, round=None):
        entry = {"run": "abc", "decision_id": 0, "env": "NoSL", "surface": surface}
        if round is not None:
            entry["obs"] = obs(round=round)
        return json.dumps(entry, separators=(",", ":"))

    log = tmp_path / "log.jsonl"
    log.write_text(
        "\n".join(
            [
                line("combat", 1),
                line("card_choice"),
                line("combat", 2),
                line("rewards"),
                line("combat", 1),
                line("combat", 3),
                line("combat", 1),
            ]
        )
        + "\n"
    )
    blocks = tc.combats(str(log))["abc"]
    assert [[e["obs"]["round"] for e in b] for b in blocks] == [[1, 2], [1, 3], [1]]


def test_the_command_line_checks_its_options():
    result = CliRunner().invoke(tc.script, ["--logs", "x", "--records", "y", "--name", "n", "--fold", "7"])
    assert result.exit_code != 0
    assert "--fold" in result.output
