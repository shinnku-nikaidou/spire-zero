#!/usr/bin/env python3
"""Where the speed test's `spin` spent its time, from the stacks it sampled of itself.

    engine/sample.py STACKS [ROOT] [> report]

STACKS is the file `spin` wrote (STS2AI_SPEED_STACKS, see crates/sts2ai-engine/tests/speed.rs): the program's mappings
and one line of addresses a sample. Prints, for every function below ROOT (the function that makes the calls; `spin`
by default), the share of the samples it was on the stack in and the share it was the innermost frame of, and the same
by source file. Addresses are named with addr2line; built with line tables (`CARGO_PROFILE_RELEASE_DEBUG=
line-tables-only`) the functions rustc inlined are told apart too.
"""
import collections
import re
import subprocess
import sys

path = sys.argv[1]
root = sys.argv[2] if len(sys.argv) > 2 else "spin"

maps, samples = [], []
for line in open(path):
    part = line.split()
    if not part:
        continue
    if part[0] == "map":
        low, high = (int(x, 16) for x in part[1].split("-"))
        maps.append((low, high, int(part[3], 16), part[6]))
    else:
        # The first two frames are the handler and the kernel's way into it. The third is where the thread was; the
        # ones after are return addresses, whose call is one byte back at the least.
        frames = [int(x, 16) for x in part[2:]]
        samples.append([a if i == 0 else a - 1 for i, a in enumerate(frames)])

# An address in a file: the file, and the address as the file has it (a program loaded anywhere starts at 0).
bases = {}
for low, high, offset, file in maps:
    bases[file] = min(bases.get(file, low - offset), low - offset)


def where(address):
    for low, high, offset, file in maps:
        if low <= address < high:
            return file, address - bases[file]
    return None, address


def short(source):
    return re.sub(r"^.*/(src/|rustc/[0-9a-f]+/library/|registry/src/[^/]+/)", "", source)


def clean(name):
    name = re.sub(r"::h[0-9a-f]{16}$", "", name)
    return re.sub(r"<.*>", "<..>", name)


# Every address once, through addr2line: the functions at it, the innermost inlined one first.
by_file = collections.defaultdict(set)
for sample in samples:
    for address in sample:
        file, inside = where(address)
        if file:
            by_file[file].add(inside)
named = {}
for file, addresses in by_file.items():
    ordered = sorted(addresses)
    out = subprocess.run(["addr2line", "-f", "-C", "-i", "-a", "-e", file], input="\n".join(hex(a) for a in ordered),
                         capture_output=True, text=True).stdout.split("\n")
    at, current = 0, None
    while at < len(out):
        line = out[at]
        if line.startswith("0x"):
            current = (file, int(line, 16))
            named[current] = []
            at += 1
        elif current is not None and at + 1 < len(out):
            source = out[at + 1].rsplit(":", 1)[0]
            library = file.rsplit("/", 1)[-1]
            named[current].append((clean(line) if line != "??" else f"?? in {library}", short(source) if source != "??" else library))
            at += 2
        else:
            at += 1

on, innermost, files, files_self = (collections.Counter() for _ in range(4))
taken = 0
for sample in samples:
    frames = []
    for address in sample:
        file, inside = where(address)
        frames.extend(named.get((file, inside), [("?", "?")]))
    # Only what the test's own function calls: the frames above it are the test harness.
    cut = next((i for i, f in enumerate(frames) if f[0] == root or f[0].endswith("::" + root)), None)
    if cut is None:
        continue
    frames = [(f"{name}  [{source}]", source) for name, source in frames[:cut]]
    if not frames:
        continue
    taken += 1
    for name in {f[0] for f in frames}:
        on[name] += 1
    for source in {f[1] for f in frames}:
        files[source] += 1
    innermost[frames[0][0]] += 1
    files_self[frames[0][1]] += 1

print(f"{taken} samples of {len(samples)} below `{root}`, from {path}")
print("\nby function: % of samples on the stack, % innermost")
for name, n in on.most_common(120):
    print(f"  {100 * n / taken:5.1f} {100 * innermost[name] / taken:5.1f}  {name}")
print("\ninnermost function: %")
for name, n in innermost.most_common(60):
    print(f"  {100 * n / taken:5.1f}  {name}")
print("\nby source file: % on the stack, % innermost")
for source, n in files.most_common(50):
    print(f"  {100 * n / taken:5.1f} {100 * files_self[source] / taken:5.1f}  {source}")
