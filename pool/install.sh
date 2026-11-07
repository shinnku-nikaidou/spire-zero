#!/bin/bash
# install.sh: puts the sim on one of the user's own machines, under ~/sts2 as every host of the pool has it. It is served
# by the hub's private download (dist.py) with @URL@, @PIN@ and @MYKEY@ filled in by dist-admin.py; dist-admin.py
# command prints the one line to paste, which sets STS2_KEY. It installs what is missing when it can (root and apt), lets
# this repository's machine in by ssh, plays one short run as a test and prints what to tell the session.
set -eu

main() {
  : "${STS2_KEY:?set STS2_KEY: dist-admin.py command prints the whole line}"
  [ "$(uname -m)" = x86_64 ] && [ "$(uname -s)" = Linux ] || { echo "install: needs Linux x86_64" >&2; exit 1; }

  missing=
  for tool in curl tar zstd rsync ssh ssh-keygen setsid; do command -v "$tool" > /dev/null || missing="$missing $tool"; done
  icu=$(ldconfig -p 2> /dev/null | grep -c libicuuc || true)
  if [ -n "$missing" ] || [ "$icu" = 0 ]; then
    if [ "$(id -u)" = 0 ] && command -v apt-get > /dev/null; then
      apt-get update -qq < /dev/null
      apt-get install -y -qq curl tar zstd rsync openssh-client util-linux \
        "$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -1)" < /dev/null
    elif [ -n "$missing" ]; then
      echo "install: missing:$missing; install them and run this again" >&2
      exit 1
    fi
  fi

  get() { curl -fsS --max-time 1800 -k --pinnedpubkey 'sha256//@PIN@' -H "x-api-key: $STS2_KEY" "@URL@/$1"; }
  rev=$(get latest)
  s=$HOME/sts2
  mkdir -p "$s"
  chmod 700 "$s"
  if [ -x "$s/sim-$rev.sh" ]; then
    echo "install: $rev is here already"
  else
    echo "install: downloading $rev"
    rm -rf "$s/.part"
    mkdir "$s/.part"
    get "sts2-$rev.tar.zst" | zstd -dqc | tar -x -C "$s/.part"
    # the launcher last: a copy cut off halfway never looks finished
    (cd "$s/.part" && for d in dotnet gamebin builds; do
      mkdir -p "$s/$d"
      cp -a "$d/." "$s/$d/"
    done)
    mkdir -p "$s/home"
    mv "$s/.part/sim-$rev.sh" "$s/sim-$rev.sh"
    rm -rf "$s/.part"
  fi

  mkdir -p "$HOME/.ssh"
  chmod 700 "$HOME/.ssh"
  touch "$HOME/.ssh/authorized_keys"
  chmod 600 "$HOME/.ssh/authorized_keys"
  grep -qF '@MYKEY@' "$HOME/.ssh/authorized_keys" || echo '@MYKEY@' >> "$HOME/.ssh/authorized_keys"

  echo "install: testing with one short run"
  t=$(mktemp -d)
  if (cd "$t" && "$s/sim-$rev.sh" run --env SL --policy play --seed 99 --n 1 --max-decisions 1000 --records "$t/r.jsonl" > "$t/out.txt" 2>&1 < /dev/null) &&
    [ "$(wc -l < "$t/r.jsonl")" = 1 ]; then
    echo "install: the test run played"
  else
    echo "install: the test run failed:" >&2
    tail -5 "$t/out.txt" >&2
    rm -rf "$t"
    exit 1
  fi
  rm -rf "$t"

  echo
  echo "Done. Tell the session this machine's address and user ($(id -un)); it sees itself as $(hostname -I | cut -d' ' -f1)"
  echo "$(nproc) threads, $(awk '/MemTotal/ { printf "%d", $2 / 1048576 }' /proc/meminfo) G of memory"
}

main
