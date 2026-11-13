#!/usr/bin/env python3
"""The translated functions rustc refuses, one `area::module::function` per line, from cargo's JSON messages.

gen.sh feeds them back to port, which writes those bodies as todo! and counts them in its report, so that a construct
the translator gets wrong (after a game update, say) costs one function and not the build.

    quarantine.py CHECK_JSON GEN_DIR
"""
import json
import re
import sys

check, gen = sys.argv[1], sys.argv[2].rstrip("/")
fn = re.compile(r"^pub fn ([A-Za-z0-9_]+)")
found = set()
for line in open(check):
    try:
        message = json.loads(line)
    except ValueError:
        continue
    if message.get("reason") != "compiler-message" or message["message"]["level"] != "error":
        continue
    for span in message["message"]["spans"]:
        if not span.get("is_primary"):
            continue
        path = span["file_name"]
        at = path.find("/gen/")
        parts = path[at + 5:].split("/") if at >= 0 else []
        if len(parts) != 2:  # only a class's module: gen/<area>/<class>.rs
            continue
        area, module = parts[0], parts[1][:-3]
        if module == "mod":
            continue
        lines = open(f"{gen}/{area}/{module}.rs").read().split("\n")
        for i in range(min(span["line_start"], len(lines)) - 1, -1, -1):
            m = fn.match(lines[i])
            if m:
                found.add(f"{area}::{module}::{m.group(1)}")
                break
for name in sorted(found):
    print(name)
