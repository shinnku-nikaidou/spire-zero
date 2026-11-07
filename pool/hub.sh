#!/bin/bash
# hub.sh: the data channel between the hosts (pool/README.md): rsync over ssh to the hub (STS2_HUB in the repository's
# .env), root's /root/sts2-store. Every host has its own key ~/.ssh/sts2_hub, which the hub lets do nothing but rsync
# inside the store (rrsync), and checks the hub by its host key (HostKeyAlias sts2-store). Shell on the hub is on.sh hub.
# This machine moves no job data: only small files (manifests, summaries, weights) come here.
#   hub.sh put HOST SRC [DEST]   on HOST, rsync SRC (a path from its home) into the hub's DEST (default: SRC's name)
#   hub.sh get HOST SRC [DEST]   on HOST, rsync the hub's SRC into DEST (a path from its home; default: SRC's name)
#   hub.sh ssh HOST              prints the ssh command rsync uses on HOST (for scripts that run there)
#   hub.sh addr HOST             prints the hub's rsync address as HOST reaches it (root@IP)
#   hub.sh url                   prints the dealer's URL, where workers ask for chunks (deal.py)
#   hub.sh dist                  prints the file service's URL, where workers send chunks and take builds (dist.py)
#   hub.sh serve                 installs deal.py on the hub as the service sts2-deal and restarts it
#   hub.sh ls [PATH] | df | rm PATH...   look at or empty the hub
#   hub.sh key HOST...           give HOST its hub key and authorize it on the hub (once per host and hub)
#   hub.sh init                  once, on the hub: the store folder
# HOST is a host of the fleet or local. Paths follow rsync's rules (a trailing / copies a directory's contents); put and get
# take extra rsync options in OPTS (e.g. OPTS=--remove-source-files).
set -eu
[ $# -ge 1 ] || { sed -n 2,17p "$0"; exit 2; }
P=$(dirname "$(readlink -f "$0")")
. "$P/../.env"
ON=$P/on.sh
STORE=/root/sts2-store
PORT=$STS2_HUB_DEAL_PORT
cmd=$1; shift
# the hub as HOST reaches it, "ADDRESS PORT": straight for every host
route() { echo "$STS2_HUB" 22; }
# the ssh that rsync uses on HOST; ssh expands the ~
sshcmd() {
  set -- $(route "$1")
  echo "ssh -F /dev/null -p $2 -i ~/.ssh/sts2_hub -o IdentitiesOnly=yes -o HostKeyAlias=sts2-store \
-o UserKnownHostsFile=~/.ssh/sts2_store_known_hosts -o StrictHostKeyChecking=yes -o BatchMode=yes \
-o ConnectTimeout=20 -o ServerAliveInterval=60 -o ServerAliveCountMax=5"
}
addr() { set -- $(route "$1"); echo "root@$1"; }
# copies can be long: a nyctl call waits up to TIMEOUT (default here 3 h)
copy() {  # HOST SRC DEST, both already in rsync's form (the hub's side as root@IP:PATH)
  TIMEOUT=${TIMEOUT:-10800} "$ON" "$1" "$(sshcmd "$1")" "${OPTS:-}" "$2" "$3" <<'EOS'
rsync -a -z --partial --mkpath $2 -e "$1" "$3" "$4"
EOS
}
case $cmd in
  put) copy "$1" "$2" "$(addr "$1"):${3:-$(basename "$2")}" ;;
  get) copy "$1" "$(addr "$1"):$2" "${3:-$(basename "$2")}" ;;
  ssh) sshcmd "$1" ;;
  addr) addr "$1" ;;
  url) echo "http://$(route x | cut -d' ' -f1):$PORT" ;;
  dist) echo "https://$STS2_HUB:$STS2_HUB_DIST_PORT" ;;
  serve) { echo "cat > /root/sts2-deal.py <<'__deal__'"; cat "$P/deal.py"; echo __deal__
    cat <<EOS
cat > /etc/systemd/system/sts2-deal.service <<'__unit__'
[Unit]
Description=sts2: the work pool's dealer (pool/deal.py)
After=network.target
[Service]
ExecStart=/usr/bin/python3 /root/sts2-deal.py $STORE $PORT
Restart=always
RestartSec=2
Nice=19
[Install]
WantedBy=multi-user.target
__unit__
systemctl daemon-reload
systemctl enable -q sts2-deal
systemctl restart sts2-deal
sleep 1
systemctl is-active sts2-deal
EOS
    } | "$ON" hub ;;
  ls) "$ON" hub "$STORE" "${1:-.}" <<'EOS'
cd "$1" && du -ah --max-depth=2 -- "$2" | sort -k2
EOS
    ;;
  df) "$ON" hub "$STORE" <<'EOS'
df -h "$1" | tail -1; du -sh "$1"
EOS
    ;;
  rm) "$ON" hub "$STORE" "$@" <<'EOS'
cd "$1"; shift
for p; do case /$p/ in */../* | //*) echo "hub rm: not under the hub: $p" >&2; exit 2 ;; esac; done
rm -rf -- "$@"; df -h . | tail -1
EOS
    ;;
  key)
    hostkey=$(echo 'cut -d" " -f1,2 /etc/ssh/ssh_host_ed25519_key.pub' | "$ON" hub | tail -1)
    for h; do
      pub=$("$ON" "$h" "$h" "$hostkey" <<'EOS'
umask 077; mkdir -p ~/.ssh
[ -f ~/.ssh/sts2_hub ] || ssh-keygen -q -t ed25519 -N '' -C "sts2-hub@$1" -f ~/.ssh/sts2_hub
echo "sts2-store $2" > ~/.ssh/sts2_store_known_hosts
cat ~/.ssh/sts2_hub.pub
EOS
      )
      pub=$(echo "$pub" | grep '^ssh-ed25519 ' | tail -1)
      "$ON" hub "$STORE" "$pub" <<'EOS'
umask 077; touch .ssh/authorized_keys
grep -qF "$2" .ssh/authorized_keys || echo "restrict,command=\"/usr/bin/rrsync -no-lock $1\" $2" >> .ssh/authorized_keys
EOS
      echo "hub key: $h" >&2
    done
    ;;
  init) "$ON" hub "$STORE" <<'EOS'
set -eu
command -v rsync > /dev/null || { echo "hub init: no rsync on the hub (apt-get install rsync, with the user's OK)" >&2; exit 1; }
install -d -m 700 "$1"
[ -e .ssh/authorized_keys.bak-20261029-sts2store ] || cp -a .ssh/authorized_keys .ssh/authorized_keys.bak-20261029-sts2store
df -h "$1" | tail -1
EOS
    ;;
  *) sed -n 2,17p "$0"; exit 2 ;;
esac
