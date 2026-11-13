import pytest
import torch

import carry
from common import LearnError

WAS = {"cards": ["<unk>", "A", "C"]}
NOW = {"cards": ["<unk>", "A", "B", "C"]}
ROWS = {"cards.weight": ("cards", 1), "card_grown.weight": ("cards", 1), "head.weight": ("cards", 2)}


def test_an_id_the_old_tables_lack_starts_from_the_old_nets_row_for_an_unknown_id():
    old = {"cards.weight": torch.tensor([[9.0], [1.0], [3.0]])}
    out = carry.carried(old, {"cards.weight": torch.zeros(4, 1)}, ROWS, WAS, NOW)
    assert out["cards.weight"].flatten().tolist() == [9, 1, 9, 3]


def test_a_weight_that_stacks_a_tables_rows_is_carried_block_by_block():
    old = {"head.weight": torch.tensor([[9.0], [1.0], [3.0], [8.0], [2.0], [4.0]])}
    out = carry.carried(old, {"head.weight": torch.zeros(8, 1)}, ROWS, WAS, NOW)
    assert out["head.weight"].flatten().tolist() == [9, 1, 9, 3, 8, 2, 8, 4]


def test_a_new_weight_and_a_layers_new_columns_are_zero_and_the_rest_is_copied():
    old = {"body.0.weight": torch.ones(2, 3), "body.0.bias": torch.tensor([5.0, 6.0])}
    own = {
        "body.0.weight": torch.full((2, 5), 7.0),
        "body.0.bias": torch.zeros(2),
        "card_grown.weight": torch.ones(4, 1),
    }
    out = carry.carried(old, own, ROWS, WAS, NOW)
    assert out["body.0.weight"].tolist() == [[1, 1, 1, 0, 0]] * 2
    assert out["body.0.bias"].tolist() == [5, 6]
    assert out["card_grown.weight"].abs().sum() == 0


def test_a_weight_kept_is_the_nets_own():
    own = {"aux_head.weight": torch.full((1, 2), 3.0)}
    assert carry.carried({}, own, ROWS, WAS, NOW, ["aux_head."])["aux_head.weight"].tolist() == [[3, 3]]


def test_a_weight_the_old_net_lacks_or_holds_in_another_shape_is_refused():
    with pytest.raises(LearnError, match="no weight"):
        carry.carried({}, {"phi.0.weight": torch.zeros(2, 2)}, ROWS, WAS, NOW)
    with pytest.raises(LearnError, match="in the old net"):
        carry.carried({"phi.0.weight": torch.zeros(2, 3)}, {"phi.0.weight": torch.zeros(2, 4)}, ROWS, WAS, NOW)
