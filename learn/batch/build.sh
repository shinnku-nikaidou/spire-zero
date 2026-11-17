#!/usr/bin/env bash
# Builds the trainers' batch builder (sts2_batch, learn/batch/src/lib.rs) and puts it beside learn's modules, as
# learn/sts2_batch.abi3.so, where `import sts2_batch` finds it. Any Python 3.12 or later loads it (abi3); PYTHON names
# the one the build reads its settings from (default: learn/.venv's, else python3).
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
cargo=$(command -v cargo || echo "$HOME/.cargo/bin/cargo")
py=${PYTHON:-$here/../.venv/bin/python}
[ -x "$py" ] || py=$(command -v python3)
PYO3_PYTHON=$py "$cargo" build --release --manifest-path "$here/Cargo.toml"
cp "$here/target/release/libsts2_batch.so" "$here/../sts2_batch.abi3.so"
