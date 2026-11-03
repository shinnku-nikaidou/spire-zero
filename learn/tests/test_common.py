import pytest

import common
from common import LearnError, read_lines, runs_by_log, train_lists


def test_read_lines_skips_blank_and_partial_lines(tmp_path):
    f = tmp_path / "a.jsonl"
    f.write_text('{"a": 1}\n\n{"b": 2}\n{"c": ')
    assert list(read_lines(str(f))) == [{"a": 1}, {"b": 2}]


def test_read_lines_reads_every_file_and_filters_by_needle(tmp_path):
    a, b = tmp_path / "a.jsonl", tmp_path / "b.jsonl"
    a.write_text('{"run_view": 1}\n{"x": 1}\n')
    b.write_text('{"run_view": 2}\n')
    assert list(read_lines(f"{a},{b}", '"run_view"')) == [{"run_view": 1}, {"run_view": 2}]


@pytest.mark.parametrize("env", ["sl", "nosl"])
def test_train_lists_refuses_test_runs(tmp_path, env):
    (tmp_path / "x-logs.txt").write_text(f"/d/{env}/selfplay/g/log-a.jsonl\n/d/{env}/eval/gate/log-a.jsonl\n")
    (tmp_path / "x-recs.txt").write_text(f"/d/{env}/selfplay/g/rec-a.jsonl\n/d/{env}/eval/gate/rec-a.jsonl\n")
    with pytest.raises(LearnError, match="test runs"):
        train_lists(str(tmp_path / "x"))


def test_train_lists_takes_self_play_and_forks(tmp_path):
    (tmp_path / "x-logs.txt").write_text("/d/sl/selfplay/g/log-a.jsonl\n/d/sl/forks/f/log-a.jsonl\n")
    (tmp_path / "x-recs.txt").write_text("/d/sl/selfplay/g/rec-a.jsonl\n/d/sl/forks/f/rec-a.jsonl\n")
    assert train_lists(str(tmp_path / "x"))[1] == "/d/sl/selfplay/g/rec-a.jsonl,/d/sl/forks/f/rec-a.jsonl"


def test_read_lines_names_a_missing_file(tmp_path):
    with pytest.raises(LearnError, match="cannot read"):
        list(read_lines(str(tmp_path / "missing.jsonl")))


def test_a_list_pair_gives_the_logs_and_records_in_the_same_order(tmp_path):
    (tmp_path / "x-logs.txt").write_text("/a/l1.jsonl\nb/l2.jsonl\n")
    (tmp_path / "x-recs.txt").write_text("/a/r1.jsonl\nb/r2.jsonl\n")
    logs, recs = common.lists(str(tmp_path / "x"))
    assert logs == f"/a/l1.jsonl,{common.ROOT / 'b/l2.jsonl'}" and recs.startswith("/a/r1.jsonl,")


def test_runs_by_log_joins_each_log_with_its_own_records(tmp_path):
    # The same seed played by two policies: one run id, two runs, each with its own log.
    a, b = tmp_path / "rec-a.jsonl", tmp_path / "rec-b.jsonl"
    a.write_text('{"run_id": "ab", "character": "SILENT", "floor": 5}\n')
    b.write_text('{"run_id": "ab", "character": "SILENT", "floor": 9}\n')
    pairs = runs_by_log("log-a.jsonl,log-b.jsonl", f"{a},{b}")
    assert [(log, runs["ab"]["floor"]) for log, runs in pairs] == [("log-a.jsonl", 5), ("log-b.jsonl", 9)]


def test_runs_by_log_wants_one_record_file_per_log(tmp_path):
    a = tmp_path / "rec-a.jsonl"
    a.write_text("")
    with pytest.raises(LearnError, match="2 decision logs and 1 record files"):
        runs_by_log("log-a.jsonl,log-b.jsonl", str(a))


def test_runs_by_log_refuses_a_run_id_twice_in_one_file(tmp_path):
    a = tmp_path / "rec-a.jsonl"
    a.write_text('{"run_id": "ab", "character": "SILENT"}\n{"run_id": "ab", "character": "SILENT"}\n')
    with pytest.raises(LearnError, match="twice"):
        runs_by_log("log-a.jsonl", str(a))


def test_runs_by_log_reads_every_characters_runs(tmp_path):
    a = tmp_path / "rec-a.jsonl"
    a.write_text('{"run_id": "ab", "character": "IRONCLAD"}\n{"run_id": "cd", "character": "SILENT"}\n')
    [(_, runs)] = runs_by_log("log-a.jsonl", str(a))
    assert {r["character"] for r in runs.values()} == {"IRONCLAD", "SILENT"}
