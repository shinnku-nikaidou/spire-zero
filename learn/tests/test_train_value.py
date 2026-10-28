import math

import torch

import train_value as tv


def test_an_afterstate_is_the_view_with_its_changes_one_level_deep():
    view = {"hp": 50, "gold": 99, "map": {"boss": "B", "visited": [1]}, "deck": ["A"]}
    after = tv.merged(view, {"deck": ["A", "B"], "map": {"visited": [1, 2]}, "gold": None})
    assert after == {"hp": 50, "map": {"boss": "B", "visited": [1, 2]}, "deck": ["A", "B"]}
    assert view["map"]["visited"] == [1]


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
