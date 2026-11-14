#!/usr/bin/env python3
"""Where a running process spends its time, by sampling its stack with gdb (no other tool needed).

    engine/sample.py PID [SAMPLES] [ROOT] [> report]

Stops the process SAMPLES times (300 by default) and reads the stack of the thread that is working each time. Prints, for
every function below ROOT (the function that makes the calls; `spin` by default), the share of the samples it was
on the stack in and the share it was the innermost frame of, and the same by source file. Meant for `cargo test --release --test speed spin` built with line
tables (`CARGO_PROFILE_RELEASE_DEBUG=line-tables-only`), which keeps the functions rustc inlined visible.
"""
import collections
import re
import subprocess
import sys

pid = sys.argv[1]
samples = int(sys.argv[2]) if len(sys.argv) > 2 else 300
root = sys.argv[3] if len(sys.argv) > 3 else "spin"
FRAME = re.compile(r"^#\d+\s+(?:0x[0-9a-f]+ in )?(.+?) \((.*)\)(?: at (\S+?):\d+| from (\S+))?$")


WAITS = ("Parker::park", "futex_wait", "epoll_wait", "pthread_cond_wait", "nanosleep")


def stack():
    """The stack of the thread that is working: the deepest one that is not waiting."""
    out = subprocess.run(
        ["gdb", "-p", pid, "-batch", "-nx", "-ex", "set print frame-arguments none", "-ex", "thread apply all bt 200"],
        capture_output=True, text=True).stdout
    threads = []
    for line in out.split("\n"):
        if line.startswith("Thread "):
            threads.append([])
            continue
        m = FRAME.match(line.strip())
        if m and threads:
            name = re.sub(r"<.*>", "<..>", m.group(1))
            threads[-1].append((name, m.group(3) or m.group(4) or "?"))
    working = [t for t in threads if t and not any(w in f[0] for f in t for w in WAITS)]
    return max(working, key=len, default=[])


def short(path):
    return re.sub(r"^.*/(src/|rustc/[0-9a-f]+/library/|registry/src/[^/]+/)", "", path)


on, innermost, files, files_self = (collections.Counter() for _ in range(4))
taken = 0
for _ in range(samples):
    frames = stack()
    # Only what the test's own function calls: the frames above it are the test harness.
    cut = next((i for i, f in enumerate(frames) if f[0] == root), None)
    if cut is None:
        continue
    frames = [(f"{name}  [{short(path)}]", short(path)) for name, path in frames[:cut]]
    if not frames:
        continue
    taken += 1
    for name in {f[0] for f in frames}:
        on[name] += 1
    for path in {f[1] for f in frames}:
        files[path] += 1
    innermost[frames[0][0]] += 1
    files_self[frames[0][1]] += 1

print(f"{taken} samples of process {pid}, below `{root}`")
print("\nby function: % of samples on the stack, % innermost")
for name, n in on.most_common(90):
    print(f"  {100 * n / taken:5.1f} {100 * innermost[name] / taken:5.1f}  {name}")
print("\ninnermost function: %")
for name, n in innermost.most_common(50):
    print(f"  {100 * n / taken:5.1f}  {name}")
print("\nby source file: % on the stack, % innermost")
for path, n in files.most_common(50):
    print(f"  {100 * n / taken:5.1f} {100 * files_self[path] / taken:5.1f}  {path}")
