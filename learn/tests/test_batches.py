import functools

import torch
from conftest import make_enemy, make_obs, round_start

import batches
import game_ids
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
    here = list(batches.built(build, parts, "cpu", 0))
    there = list(batches.built(build, parts, "cpu", 2))
    assert len(here) == len(there) == len(parts)
    for part, a, b in zip(parts, here, there, strict=True):
        direct = Batch(part, index, "cpu")  # as the trainers built them before
        for other in (a, b):
            assert all(
                torch.equal(x, y) and x.dtype == y.dtype for x, y in zip(direct.inputs(), other.inputs(), strict=True)
            )
            assert torch.equal(direct.target, other.target) and torch.equal(direct.potion_used, other.potion_used)
