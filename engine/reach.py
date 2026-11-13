#!/usr/bin/env python3
"""Which members that have no code yet can a combat reach?

    engine/reach.py [crate src dir]

Reads the engine's source as text: every generated function (gen/**, a `todo!("Class.Member")` body is a member that
is not translated or a stub) and every reference `g::path::name` in a function's body. The roots are the references
made outside generated functions: by the hand-written code (world.rs, manual/, the read side) and by the generated
tables (the model table, the saved properties). A dispatcher refers to every override, so the walk goes into all of
them: the list errs on the side of more.

Prints the members without code that the walk reaches, each with the chain of callers that reaches it.
"""
import collections
import pathlib
import re
import sys

src = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else pathlib.Path(__file__).parent / "crates/sts2ai-engine/src")
gen = src / "gen"

FN = re.compile(r"^(\s*)(?:pub )?fn ([a-z0-9_]+)\s*[<(]")
MOD = re.compile(r"^(\s*)pub mod ([a-z0-9_]+) \{")
REF = re.compile(r"\bg::((?:[a-z0-9_]+::)*[a-z0-9_]+)")
BARE = re.compile(r"\b([a-z0-9_]+__f)\b")
TODO = re.compile(r'^\s*todo!\("([^"]+)"\)\s*$')

bodies = {}  # path -> text of the body
missing = {}  # path -> the member's C# name
outside = []  # text outside every generated function: tables, statics


def read(path, prefix):
    mods, lines = [], path.read_text().split("\n")
    i = 0
    while i < len(lines):
        line = lines[i]
        m = MOD.match(line)
        if m:
            mods.append((len(m.group(1)), m.group(2)))
            i += 1
            continue
        if mods and line.startswith(" " * mods[-1][0] + "}") and line.strip() == "}":
            mods.pop()
            i += 1
            continue
        f = FN.match(line)
        if not f:
            outside.append(line)
            i += 1
            continue
        indent, name = f.group(1), f.group(2)
        # The body runs to the closing brace at the function's own indent (the code is rustfmt's).
        j = i
        while not (lines[j].rstrip().endswith("{") or lines[j].rstrip().endswith("{}")):
            j += 1
        k = j
        if not lines[j].rstrip().endswith("{}"):
            k = j + 1
            while lines[k] != indent + "}":
                k += 1
        full = "::".join(prefix + [x[1] for x in mods] + [name])
        text = "\n".join(lines[j + 1 : k])
        bodies[full] = text
        if k == j + 2 and (t := TODO.match(lines[j + 1])):
            missing[full] = t.group(1)
        i = k + 1


for path in sorted(gen.rglob("*.rs")):
    rel = path.relative_to(gen).with_suffix("").parts
    read(path, [p for p in rel if p != "mod"])


def refs(text, own_file):
    found = set()
    for m in REF.finditer(text):
        parts = m.group(1).split("::")
        # The longest prefix that is a function: what follows a function's name is not part of its path.
        for n in range(len(parts), 0, -1):
            if (p := "::".join(parts[:n])) in bodies:
                found.add(p)
                break
    if own_file == "fns":
        for m in BARE.finditer(text):
            if (p := "fns::" + m.group(1)) in bodies:
                found.add(p)
    return found


parent = {}
queue = collections.deque()


def reach(path, by):
    if path not in parent:
        parent[path] = by
        queue.append(path)


for path in sorted(src.rglob("*.rs")):
    if gen in path.parents:
        continue
    for p in refs(path.read_text(), ""):
        reach(p, str(path.relative_to(src)))
for p in refs("\n".join(outside), ""):
    reach(p, "a generated table")
while queue:
    at = queue.popleft()
    for p in refs(bodies[at], at.split("::")[0]):
        reach(p, at)


def chain(path):
    out = []
    while path in parent:
        path = parent[path]
        out.append(path)
    return out


reached = sorted((missing[p], p) for p in missing if p in parent)
print(f"{len(bodies)} generated functions, {len(parent)} reached; {len(missing)} without code, {len(reached)} of them reached")
for name, path in reached:
    kind = "stub" if path.startswith("stubs::") else "not translated"
    way = chain(path)
    print(f"{name}\t{kind}\t{' <- '.join(way[:4])}{' <- ..' if len(way) > 4 else ''}")
