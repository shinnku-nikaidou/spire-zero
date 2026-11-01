import sys

import pytest

from common import LearnError, read_lines, runs_by_log, start_sim, train_lists, wait_sims


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


def test_wait_sims_reports_the_failed_one_with_its_log(tmp_path):
    ok = start_sim([sys.executable, "-c", "print('fine')"], tmp_path / "ok.log")
    bad = start_sim([sys.executable, "-c", "import sys; print('boom'); sys.exit(3)"], tmp_path / "bad.log")
    with pytest.raises(LearnError, match="1 of 2 sim processes failed") as e:
        wait_sims([(ok, tmp_path / "ok.log"), (bad, tmp_path / "bad.log")])
    assert "boom" in str(e.value)
    assert ok.returncode == 0


def test_wait_sims_passes_when_all_succeed(tmp_path):
    procs = [
        (start_sim([sys.executable, "-c", "pass"], tmp_path / f"{i}.log"), tmp_path / f"{i}.log") for i in range(3)
    ]
    wait_sims(procs)


def test_runs_by_log_joins_each_log_with_its_own_records(tmp_path):
    # The same seed played by two policies: one run id, two runs, each with its own log.
    a, b = tmp_path / "rec-a.jsonl", tmp_path / "rec-b.jsonl"
    a.write_text('{"run_id": "ab", "character": "SILENT", "floor": 5}\n')
    b.write_text('{"run_id": "ab", "character": "SILENT", "floor": 9}\n{"seed": "no id"}\n')
    pairs = runs_by_log("log-a.jsonl,log-b.jsonl", f"{a},{b}", "SILENT")
    assert [(log, runs["ab"]["floor"]) for log, runs in pairs] == [("log-a.jsonl", 5), ("log-b.jsonl", 9)]


def test_runs_by_log_wants_one_record_file_per_log(tmp_path):
    a = tmp_path / "rec-a.jsonl"
    a.write_text("")
    with pytest.raises(LearnError, match="2 decision logs and 1 record files"):
        runs_by_log("log-a.jsonl,log-b.jsonl", str(a), "SILENT")


def test_runs_by_log_refuses_a_run_id_twice_in_one_file(tmp_path):
    a = tmp_path / "rec-a.jsonl"
    a.write_text('{"run_id": "ab", "character": "SILENT"}\n{"run_id": "ab", "character": "SILENT"}\n')
    with pytest.raises(LearnError, match="twice"):
        runs_by_log("log-a.jsonl", str(a), "SILENT")


def test_runs_by_log_refuses_another_characters_run(tmp_path):
    a = tmp_path / "rec-a.jsonl"
    a.write_text('{"run_id": "ab", "character": "IRONCLAD"}\n')
    with pytest.raises(LearnError, match="IRONCLAD, not SILENT"):
        runs_by_log("log-a.jsonl", str(a), "SILENT")
