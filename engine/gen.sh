#!/usr/bin/env bash
# The decompiled game -> crates/sts2ai-engine/src/gen (the translated classes, the state, the schema). See DESIGN.md.
#
#   engine/gen.sh [--check]
#
# Reads the decompiled source and the game's assemblies of the version below (tmp/, made from the user's own copy of the
# game), runs port, formats what it wrote. With --check, cargo check runs after it; a function rustc refuses is put on
# the quarantine list (its body becomes a todo!, the report counts it) and port runs again, until the crate compiles.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(dirname "$here")"
version="${STS2_VERSION:-v0.113.0}"
decompiled="${STS2_DECOMPILED:-$repo/tmp/decompiled/sts2-$version}"
bin="${STS2_BIN_DIR:-$repo/tmp/gamebin/$version}"
out="$here/crates/sts2ai-engine/src/gen"
quarantine="$repo/tmp/engine/quarantine.txt"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH"

[ -d "$decompiled" ] || { echo "gen.sh: no decompiled game at $decompiled" >&2; exit 1; }
[ -d "$bin" ] || { echo "gen.sh: no game assemblies at $bin" >&2; exit 1; }
mkdir -p "$(dirname "$quarantine")"
[ "${1:-}" = "--fresh" ] && { rm -f "$quarantine"; shift; }
touch "$quarantine"

dotnet build "$repo/src/Sts2Ai.Port" -c Release -v q --nologo >"$repo/tmp/engine/port-build.log" 2>&1 || { grep -E "error" "$repo/tmp/engine/port-build.log" | head -20 >&2; echo "gen.sh: port does not build" >&2; exit 1; }
port() {
    dotnet "$repo/src/Sts2Ai.Port/bin/Release/net9.0/port.dll" --decompiled "$decompiled" --bin "$bin" \
        --config "$here/port.toml" --out "$out" --quarantine "$quarantine"
    (cd "$here" && find "$out" -name '*.rs' -print0 | xargs -0 rustfmt --edition 2024 --config max_width=140 2>/dev/null) || true
}
port
[ "${1:-}" = "--check" ] || exit 0

for round in 1 2 3 4 5 6 7 8; do
    if (cd "$here" && cargo check --message-format=json 2>/dev/null) > "$repo/tmp/engine/check.json"; then
        echo "gen.sh: cargo check passes ($(grep -c . "$quarantine") functions quarantined)"
        exit 0
    fi
    before="$(grep -c . "$quarantine" || true)"
    python3 "$here/quarantine.py" "$repo/tmp/engine/check.json" "$out" >> "$quarantine"
    sort -u -o "$quarantine" "$quarantine"
    after="$(grep -c . "$quarantine" || true)"
    echo "gen.sh: round $round: $after functions quarantined" >&2
    [ "$after" = "$before" ] && { echo "gen.sh: cargo check fails outside the translated functions" >&2; exit 1; }
    port
done
exit 1
