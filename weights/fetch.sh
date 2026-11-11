#!/usr/bin/env bash
# weights/fetch.sh [NAME...]: downloads net files into weights/all/ from the repository's release "weights" (GitHub's
# gh, logged in). With no NAME: the nets the champion files name (eval/champions/*.json). A file already here is kept:
# a name is used once and always means the same weights (learn/weights.py).
set -euo pipefail
cd "$(dirname "$0")/.."
names=("$@")
if [ ${#names[@]} -eq 0 ]; then
  mapfile -t names < <(grep -oh 'weights/all/[^"]*\.json' eval/champions/*.json | sed 's#weights/all/##; s#\.json$##' | sort -u)
fi
mkdir -p weights/all
for name in "${names[@]}"; do
  if [ -e "weights/all/$name.json" ]; then
    echo "weights/all/$name.json is here"
    continue
  fi
  gh release download weights --pattern "$name.json" --dir weights/all
  echo "weights/all/$name.json downloaded"
done
