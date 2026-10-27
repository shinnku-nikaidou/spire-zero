import pytest

import weights
from common import LearnError


@pytest.fixture
def history(tmp_path, monkeypatch):
    path = tmp_path / "history"
    monkeypatch.setattr(weights, "HISTORY", path)
    return path


def test_the_default_is_the_name_in_the_history(history):
    assert weights.out_path("c1", None) == history / "c1.json"
    assert history.is_dir()


def test_a_file_in_the_history_is_never_replaced(history):
    weights.out_path("c1", None).write_text("{}")
    with pytest.raises(LearnError, match="another --name"):
        weights.out_path("c1", None)
    with pytest.raises(LearnError):
        weights.out_path("other", str(history / "c1.json"))


def test_an_explicit_path_elsewhere_may_be_replaced(history, tmp_path):
    out = tmp_path / "scratch" / "t.json"
    out.parent.mkdir()
    out.write_text("{}")
    assert weights.out_path("t", str(out)) == out
