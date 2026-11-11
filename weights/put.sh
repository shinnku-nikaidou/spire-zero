#!/usr/bin/env bash
# weights/put.sh NAME...: adds weights/all/NAME.json to the repository's release "weights" (GitHub's gh, logged in),
# where champion files, jobs on other machines' checkouts and the tests' CI take it from (weights/fetch.sh). The
# release refuses a name it already holds: a name is used once.
set -euo pipefail
cd "$(dirname "$0")/.."
[ $# -gt 0 ] || { sed -n '2,4p' "$0" >&2; exit 2; }
for name in "$@"; do
  [ -e "weights/all/$name.json" ] || { echo "no weights/all/$name.json" >&2; exit 1; }
  gh release upload weights "weights/all/$name.json"
  echo "$name.json is on the release"
done
