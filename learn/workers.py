"""A trainer's work done in worker processes, the same as done in the trainer itself, bit for bit and in the same order.

Reading the logs (`each`): the trainers' readers turned one log after another into samples in Python (v10g1's 40,000 runs
took 15 min for V and 27 min for the combat net, the Python at a whole core and the encoder at 9 %). Each worker starts
its own encoder and reads whole logs; the trainer takes the logs' samples back in the list's order.

Building batches (`built`): a batch was built in Python while the GPU waited (on pushqkzh a combat batch of 256 took 148
ms, a training step 4.5 ms). The trainer still draws the order and cuts it into parts; a worker builds each part's batch
on the CPU, as the trainer's own process would, and the trainer moves it to its device. Since 2026-11-17 a batch's
tokens are looked up and padded in Rust (learn/batch): on one core of gcpsg a batch of 256 V 17 ms and combat 22 ms,
where the Python took 170 and 482.

Workers are forked from a fresh forkserver process, so the trainer's samples and CUDA state stay out of them, and run
one torch thread each. What goes to them and back goes by pickle: a function is a module's function or a
functools.partial of one.
"""

import collections
import itertools
import multiprocessing
import pickle

import torch

import common

AHEAD = 2  # tasks asked for ahead, per worker

_pool = None  # the batch builders, kept for the trainer's life
_encoder = None  # a reader's own encoder


def _start():
    torch.set_num_threads(1)


def _build(build, part):
    return pickle.dumps(build(part), protocol=pickle.HIGHEST_PROTOCOL)


def _start_reader(command):
    global _encoder
    _start()
    _encoder = common.Encoder(command)


def _read(read, item):
    return pickle.dumps(read(*item, encoder=_encoder), protocol=pickle.HIGHEST_PROTOCOL)


def _ordered(pool, task, fn, items, workers):
    """fn(item) of each item by the pool's task, in order, AHEAD a worker asked for ahead."""
    items = iter(items)
    ask = lambda item: pool.apply_async(task, (fn, item))  # noqa: E731
    pending = collections.deque(ask(item) for item in itertools.islice(items, AHEAD * workers))
    while pending:
        out = pickle.loads(pending.popleft().get())
        pending.extend(ask(item) for item in itertools.islice(items, 1))
        yield out


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
    build them ahead of the caller; with 0, this process builds them as they are asked for."""
    global _pool
    if not workers:
        for part in parts:
            yield moved(build(part), device)
        return
    if _pool is None:
        _pool = multiprocessing.get_context("forkserver").Pool(workers, initializer=_start)
    for batch in _ordered(_pool, _build, build, parts, workers):
        yield moved(batch, device)


def each(read, items, encoder, workers):
    """read(*item, encoder=...) for each item (a log to read, with what it needs), in order. WORKERS processes read them,
    each with an encoder of its own started as ENCODER was; with 0, this process reads them with ENCODER."""
    if not workers:
        for item in items:
            yield read(*item, encoder=encoder)
        return
    pool = multiprocessing.get_context("forkserver").Pool(
        workers, initializer=_start_reader, initargs=(encoder.command,)
    )
    try:
        yield from _ordered(pool, _read, read, items, workers)
        pool.close()
    finally:
        pool.terminate()
        pool.join()
