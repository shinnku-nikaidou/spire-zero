import math

import torch

import train_value as tv


def test_an_afterstate_is_the_view_with_its_changes_one_level_deep():
    view = {"hp": 50, "gold": 99, "map": {"boss": "B", "visited": [1]}, "deck": ["A"]}
    after = tv.merged(view, {"deck": ["A", "B"], "map": {"visited": [1, 2]}, "gold": None})
    assert after == {"hp": 50, "map": {"boss": "B", "visited": [1, 2]}, "deck": ["A", "B"]}
    assert view["map"]["visited"] == [1]


def test_shares_of_the_training_runs_nest_and_disjoint_starts_split_them():
    runs = [f"{i * 7919:016x}" for i in range(5000)]
    train = [r for r in runs if not tv.held_out(r)]
    quarter = {r for r in train if tv.trained_on(r, 0.25, 0)}
    half = {r for r in train if tv.trained_on(r, 0.5, 0)}
    other = {r for r in train if tv.trained_on(r, 0.5, 0.5)}
    assert quarter < half and not half & other and half | other == set(train)
    assert 0.2 < len(quarter) / len(train) < 0.3


def test_the_exploration_temperature_comes_from_the_brain_name():
    assert tv.temperature("l0 explore 0.25") == 0.25
    assert tv.temperature("l0 n1 explore 2") == 2
    assert tv.temperature("l0") is None


def test_the_pull_matches_v0_by_the_option_and_a_net_by_its_own_temperature():
    assert tv.pull_temperature("l0 explore 0.25", 0.01) == 0.01
    assert tv.pull_temperature("l0 n2-b0.03 explore 0.006", 0.01) == 0.006


def test_explored_is_the_chance_of_a_draw_below_the_best_by_more_than_a_hundredth_of_the_temperature():
    values = torch.tensor([1.0, 0.0, 2.0, 2.0, 2.0 - 1e-4], dtype=torch.float64)
    group = torch.tensor([0, 0, 1, 1, 1])
    share = tv.explored(values, torch.ones_like(values), group, 2)
    assert math.isclose(float(share[0]), 1 / (1 + math.e))
    assert float(share[1]) == 0  # ties, and an option closer than 1 / 100, are as good as the best


def test_floors_within_are_the_floor_a_branch_died_on_or_the_one_after_the_horizon():
    floors, start, last = 20, torch.tensor([5, 5]), torch.tensor([17, 17])
    h = torch.zeros(2, floors)
    h[0, 9] = 1  # dies on floor 9
    h[1, 18] = 1  # past the horizon: lives through it
    assert tv.floors_within(h, start, last).tolist() == [9, 18]


def test_a_segment_log_softmax_normalises_each_group_alone():
    values = torch.tensor([1.0, 2.0, 0.5, 0.5, 3.0], dtype=torch.float64)
    group = torch.tensor([0, 0, 1, 1, 1])
    logp = tv.segment_log_softmax(values, group, 2)
    assert torch.allclose(logp[:2], torch.log_softmax(values[:2], 0))
    assert torch.allclose(logp[2:], torch.log_softmax(values[2:], 0))
    assert math.isclose(float(logp[2:].exp().sum()), 1.0)


def test_the_act_boss_label_is_the_last_boss_fight_of_the_act_or_unknown_when_truncated():
    boss = {"act": 0, "room": "Boss", "hp": 50, "max_hp": 80, "end": {"died": False, "hp": 40, "max_hp": 80}}
    other = {"act": 0, "room": "Monster", "hp": 80, "max_hp": 80, "end": {"died": False, "hp": 80, "max_hp": 80}}
    assert tv.act_boss({"outcome": "lost", "combats": [other, boss]}, 0) == [1.0, 0.5]
    assert tv.act_boss({"outcome": "lost", "combats": [other]}, 0) == [0.0, 0.0]
    died = boss | {"end": {"died": True}}
    assert tv.act_boss({"outcome": "truncated", "combats": [died]}, 0) == [0.0, 0.0]
    assert tv.act_boss({"outcome": "truncated", "combats": [other]}, 0) is None


def test_deck_attention_ignores_masked_cards():
    torch.manual_seed(0)
    block = tv.DeckAttention(8)
    x, mask = torch.randn(1, 3, 8), torch.tensor([[1.0, 1.0, 0.0]])
    y = x.clone()
    y[0, 2] = torch.randn(8)
    assert torch.allclose(block(x, mask)[0, :2], block(y, mask)[0, :2], atol=1e-6)


def test_a_boss_fight_scores_a_win_above_one_half_and_a_loss_by_the_boss_hp_taken():
    assert tv.boss_score({"won": True, "hp": 40}, 80) == 0.75
    assert tv.boss_score({"won": False, "enemy_hp": 300, "enemy_max_hp": 400}, 80) == 0.125


def test_a_boss_is_worth_the_floors_its_winners_went_on_to():
    win = {"act": 1, "room": "Boss", "hp": 50, "max_hp": 80, "end": {"died": False, "hp": 40, "max_hp": 80}}
    runs = [{"outcome": "lost", "floor": 40, "combats": [win]}, {"outcome": "lost", "floor": 36, "combats": [win]}]
    runs.append({"outcome": "lost", "floor": 33, "combats": [win | {"end": {"died": True}}]})
    assert tv.boss_worth(runs, 64) == {0: 0.0, 1: 5.0}


def test_centred_values_lose_their_groups_mean():
    v = tv.centred(torch.tensor([1.0, 3.0, 10.0]), torch.tensor([0, 0, 1]), 2)
    assert v.tolist() == [-1.0, 1.0, 0.0]


def test_the_net_reads_every_feature_the_logs_carry_or_the_chosen_ones():
    samples = [{"features": {"hp": 1, "damage": 2}}, {"features": {"floor": 3}}]
    assert tv.feature_names(samples, None) == ["damage", "floor", "hp"]
    assert tv.feature_names(samples, "hp,floor") == ["floor", "hp"]
    try:
        tv.feature_names(samples, "hp,gold")
    except tv.LearnError as e:
        assert "gold" in str(e)
    else:
        raise AssertionError("an unknown feature must fail")
