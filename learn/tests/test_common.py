import sys

import pytest

from common import LearnError, read_lines, start_sim, wait_sims


def test_read_lines_skips_blank_and_partial_lines(tmp_path):
    f = tmp_path / "a.jsonl"
    f.write_text('{"a": 1}\n\n{"b": 2}\n{"c": ')
    assert list(read_lines(str(f))) == [{"a": 1}, {"b": 2}]


def test_read_lines_reads_every_file_and_filters_by_needle(tmp_path):
    a, b = tmp_path / "a.jsonl", tmp_path / "b.jsonl"
    a.write_text('{"run_view": 1}\n{"x": 1}\n')
    b.write_text('{"run_view": 2}\n')
    assert list(read_lines(f"{a},{b}", '"run_view"')) == [{"run_view": 1}, {"run_view": 2}]


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
