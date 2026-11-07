#!/bin/bash
# on.sh HOST [ARG...] < SCRIPT: runs the bash script on stdin on HOST, as our user there and from its home, with ARGs
# as $1... HOST is local, a host listed in the repository's .env as STS2_ON_HOST="TARGET [nyctl option...]" (its shell
# goes through nyctl, tmp/nyctl), or else an ssh alias. Through nyctl the script runs as root -> runuser in its own scope
# in the user's slice: there a host's box applies, and a restart of the nyctl agent (whose cgroup a plain runuser would
# stay in) does not kill what the script leaves running. HOST hub is the hub's root, from /root. TIMEOUT (s, default
# 300) bounds a nyctl call. Exits with the script's code.
set -eu
[ $# -ge 1 ] || { sed -n 2,7p "$0"; exit 2; }
R=$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)
. "$R/.env"
h=$1; shift
[ "$h" = local ] && { cd; exec bash -s -- "$@"; }
v=STS2_ON_$h
[ -n "${!v:-}" ] || exec ssh -o BatchMode=yes "$h" "bash -s -- $(printf '%q ' "$@")"
read -ra t <<< "${!v}"
f=$(mktemp); trap 'rm -f "$f"' EXIT
if [ "$h" = hub ]; then
  { echo "cd /root && bash -s -- $(printf '%q ' "$@") <<'__on_sh__'"; cat; echo __on_sh__; } > "$f"
else
  { echo "systemd-run --scope -q --slice=user-\$(id -u shinnku).slice -- runuser -u shinnku -- \\"
    echo "  bash -c 'cd && exec bash -s -- \"\$@\"' on.sh $(printf '%q ' "$@") <<'__on_sh__'"
    cat
    echo __on_sh__
  } > "$f"
fi
"$R/tmp/nyctl/bin/nyctl" exec "${t[@]}" --timeout "${TIMEOUT:-300}" --script "$f"
