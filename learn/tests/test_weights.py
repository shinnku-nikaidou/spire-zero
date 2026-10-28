import pytest

import weights
from common import LearnError


@pytest.fixture
def root(tmp_path, monkeypatch):
    path = tmp_path / "weights"
    monkeypatch.setattr(weights, "ROOT", path)
    return path


def test_the_default_is_the_name_in_the_characters_folder(root):
    assert weights.out_path("c1", None, "SILENT") == root / "silent" / "c1.json"
    assert (root / "silent").is_dir()


def test_a_file_in_a_characters_folder_is_never_replaced(root):
    weights.out_path("c1", None, "SILENT").write_text("{}")
    with pytest.raises(LearnError, match="another --name"):
        weights.out_path("c1", None, "SILENT")
    with pytest.raises(LearnError):
        weights.out_path("other", str(root / "silent" / "c1.json"), "IRONCLAD")
    assert weights.out_path("c1", None, "IRONCLAD") == root / "ironclad" / "c1.json"


def test_an_explicit_path_elsewhere_may_be_replaced(root, tmp_path):
    out = tmp_path / "scratch" / "t.json"
    out.parent.mkdir()
    out.write_text("{}")
    assert weights.out_path("t", str(out), "SILENT") == out
