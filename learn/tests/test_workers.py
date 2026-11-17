import functools
from pathlib import Path

import torch
from conftest import encoder, make_enemy, make_obs, round_start

import common
import game_ids
import workers
from combat import data
from combat.net import Batch


def test_batches_built_in_workers_are_those_built_here_in_the_same_order():
    samples = []
    for hp in range(5, 70, 4):
        o = make_obs(hp=hp, enemies=[make_enemy(1, hp=hp % 13 + 1)] * (1 + hp % 3), potions=("FIRE", None, None))
        end = {"died": hp < 20, "hp": hp // 2, "max_hp": 70, "potions": 0}
        samples.append(data.sample(f"{hp:x}", round_start(o), end, {"potions": []}))
    tables = game_ids.combat_tables()
    index = {t: {i: k for k, i in enumerate(ids)} for t, ids in tables.items()}
    build = functools.partial(Batch, index=index, device="cpu")
    parts = [samples[i : i + 3] for i in range(0, len(samples), 3)]
    here = list(workers.built(build, parts, "cpu", 0))
    there = list(workers.built(build, parts, "cpu", 2))
    assert len(here) == len(there) == len(parts)
    for part, a, b in zip(parts, here, there, strict=True):
        direct = Batch(part, index, "cpu")  # as the trainers built them before
        for other in (a, b):
            assert all(
                torch.equal(x, y) and x.dtype == y.dtype for x, y in zip(direct.inputs(), other.inputs(), strict=True)
            )
            assert torch.equal(direct.target, other.target) and torch.equal(direct.potion_used, other.potion_used)


def lists_of(tmp_path, write, n):
    """N logs, each with its records, written by WRITE in a folder of its own, as one list pair."""
    logs, recs = [], []
    for k in range(n):
        prefix = write(tmp_path / f"part{k}")
        logs += Path(f"{prefix}-logs.txt").read_text().split()
        recs += Path(f"{prefix}-recs.txt").read_text().split()
    (tmp_path / "all-logs.txt").write_text("\n".join(logs) + "\n")
    (tmp_path / "all-recs.txt").write_text("\n".join(recs) + "\n")
    return common.lists(str(tmp_path / "all"))


def test_logs_read_in_workers_give_the_samples_read_here_in_the_same_order(tmp_path):
    import test_combat
    import test_v_train

    from v import data as v_data

    def mkdir_then(write):
        def go(path):
            path.mkdir(parents=True)
            return write(path)

        return go

    combat_lists = lists_of(tmp_path / "c", mkdir_then(lambda p: test_combat.write_runs(p, runs=4)), 5)
    here, skipped_here = data.read(*combat_lists, encoder(), 0)
    there, skipped_there = data.read(*combat_lists, encoder(), 2)
    assert len(here) == 5 * 4 * 3 and here == there and skipped_here == skipped_there
    assert [s["key"][0] for s in here] == sorted(s["key"][0] for s in here)  # the list's order

    v_lists = lists_of(tmp_path / "v", mkdir_then(lambda p: test_v_train.write_runs(p, runs=4)), 5)
    here, runs_here = v_data.read_runs(*v_lists, 64, encoder(), 0)
    there, runs_there = v_data.read_runs(*v_lists, 64, encoder(), 2)
    assert len(here) == 5 * 4 * 3 and here == there and runs_here == runs_there
