"""Training batches built in worker processes while the trainer steps. The trainers' time went to building batches in
Python (on pushqkzh a combat batch of 256 took 148 ms, a training step on its GPU 4.5 ms). The trainer still draws the
order and cuts it into parts; a worker builds each part's batch on the CPU, as the trainer's own process would, and the
trainer moves it to its device, so the batches are the same bit for bit and come in the same order.
"""

import collections
import itertools
import multiprocessing
import pickle

import torch

AHEAD = 2  # batches asked for ahead, per worker

_pool = None


def _start():
    torch.set_num_threads(1)


def _build(build, part):
    return pickle.dumps(build(part), protocol=pickle.HIGHEST_PROTOCOL)


def moved(batch, device):
    """BATCH with its tensors (the attributes that are tensors, or lists or dicts of them) on DEVICE."""
    for k, v in vars(batch).items():
        if isinstance(v, torch.Tensor):
            setattr(batch, k, v.to(device))
        elif isinstance(v, list):
            setattr(batch, k, [x.to(device) for x in v])
        elif isinstance(v, dict):
            setattr(batch, k, {n: x.to(device) for n, x in v.items()})
    return batch


def built(build, parts, device, workers):
    """build(part), a batch built on the CPU, for each part (a list of samples) in order, on DEVICE. WORKERS processes
    build them, AHEAD each ahead of the caller; with 0, this process builds them as they are asked for. `build` and the
    samples go to the workers by pickle: `build` is a module's function or a functools.partial of one."""
    global _pool
    if not workers:
        for part in parts:
            yield moved(build(part), device)
        return
    if _pool is None:
        # a fresh process forks the workers: the trainer's samples and its CUDA state stay out of them
        _pool = multiprocessing.get_context("forkserver").Pool(workers, initializer=_start)
    parts = iter(parts)
    ask = lambda part: _pool.apply_async(_build, (build, part))  # noqa: E731
    pending = collections.deque(ask(part) for part in itertools.islice(parts, AHEAD * workers))
    while pending:
        batch = pickle.loads(pending.popleft().get())
        pending.extend(ask(part) for part in itertools.islice(parts, 1))
        yield moved(batch, device)
