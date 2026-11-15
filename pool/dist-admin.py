#!/usr/bin/env python3
"""dist-admin.py: the hub's private download of the sim, for the user's own machines and the pool's workers (dist.py,
install.sh, worker.py; the hub's address in the repository's .env, through hub.sh).

  dist-admin.py setup         makes the key here (~/.config/sts2/dist-key, mode 600), and on the hub the certificate, the
                              service sts2-dist (STS2_HUB_DIST_PORT), the pool's token (pool/token) and, for the workers,
                              dist/pool.json (the dealer's URL and that token) and dist/worker.py (worker.py). Run
                              again it keeps the key, certificate and token, and restarts the service: run it again after
                              worker.py changes, for the notebooks.
  dist-admin.py bundle REV    packs the sim at REV (a build of tmp/build.sh, made if missing), .NET and the game files it
                              needs into dist/sts2-REV.tar.zst on the hub, where the workers take it (box.py run calls it).
  dist-admin.py publish REV   the bundle, and install.sh with it: machines that install from now on get REV.
  dist-admin.py command       prints the line to paste on a new machine. It holds the key: keep it to yourself.
  dist-admin.py cert          prints the certificate the notebook pins, and keeps it in ~/.config/sts2/dist-cert.pem.
"""

import hashlib
import secrets
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

POOL = Path(__file__).resolve().parent
ROOT = POOL.parent
HUB, ON = POOL / "hub.sh", POOL / "on.sh"
CONFIG = Path.home() / ".config" / "sts2"
KEY, PIN, CERT = CONFIG / "dist-key", CONFIG / "dist-pin", CONFIG / "dist-cert.pem"
GAME = "v0.113.1"
# all the sim loads of the game's folder: the rest of the snapshot is .NET's, which comes from dotnet/, and nothing else is read
GAME_FILES = [
    "sts2.dll", "GodotSharp.dll", "0Harmony.dll", "Sentry.dll", "Sentry.Godot.dll", "Steamworks.NET.dll",
    "SmartFormat.dll", "SmartFormat.ZString.dll", "System.IO.Hashing.dll", "release_info.json",
]

HUB_SETUP = r"""
set -eu
d=/root/sts2-dist
install -d -m 700 "$d" /root/sts2-store/dist
printf '%s\n' "$1" > "$d/key.sha256"
cat > "$d/dist.py"
[ -e "$d/key.pem" ] || openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:prime256v1 -nodes -days 3650 \
  -subj /CN=sts2-dist -keyout "$d/key.pem" -out "$d/cert.pem" 2> /dev/null
cat > /etc/systemd/system/sts2-dist.service <<EOS
[Unit]
Description=sts2: private download of the sim (pool/dist.py)
After=network.target
[Service]
ExecStart=/usr/bin/python3 $d/dist.py /root/sts2-store $2 $d/cert.pem $d/key.pem $d/key.sha256
Restart=always
RestartSec=2
Nice=19
[Install]
WantedBy=multi-user.target
EOS
systemctl daemon-reload
systemctl enable -q sts2-dist
systemctl restart sts2-dist
sleep 1
systemctl is-active sts2-dist
# the work pool: its token, and what a worker reads first (deal.py, worker.py)
p=/root/sts2-store/pool
install -d -m 700 "$p"
[ -s "$p/token" ] || (umask 077; openssl rand -hex 16 > "$p/token")
printf '{"deal": "%s", "token": "%s"}\n' "$3" "$(cat "$p/token")" > /root/sts2-store/dist/pool.json
cat > /root/sts2-store/dist/worker.py
echo "pin $(openssl x509 -in "$d/cert.pem" -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | base64)"
cat "$d/cert.pem"
"""


def output(*cmd) -> str:
    return subprocess.run([str(c) for c in cmd], check=True, stdout=subprocess.PIPE, text=True).stdout.strip()


def setup() -> None:
    CONFIG.mkdir(parents=True, exist_ok=True)
    if not KEY.exists():
        KEY.touch(mode=0o600)
        KEY.write_text(secrets.token_urlsafe(32) + "\n")
    key_hash = hashlib.sha256(KEY.read_text().strip().encode()).hexdigest()
    # on.sh hands its input to bash: the service's and the worker's sources follow the setup as here-documents
    script = HUB_SETUP.replace('cat > "$d/dist.py"', "cat > \"$d/dist.py\" <<'__dist__'\n" + (POOL / "dist.py").read_text() + "__dist__")
    script = script.replace("cat > /root/sts2-store/dist/worker.py", "cat > /root/sts2-store/dist/worker.py <<'__worker__'\n" + (POOL / "worker.py").read_text() + "__worker__")
    done = subprocess.run([str(ON), "hub", key_hash, output(HUB, "dist").rsplit(":", 1)[1], output(HUB, "url")], input=script, text=True, check=True, stdout=subprocess.PIPE)
    print("\n".join(line for line in done.stdout.splitlines() if line.startswith(("active", "pin "))))
    pin = next(line.split()[1] for line in done.stdout.splitlines() if line.startswith("pin "))
    PIN.write_text(pin + "\n")
    CERT.write_text(done.stdout[done.stdout.index("-----BEGIN"):].strip() + "\n")


def stage_tree(rev: str, stage: Path) -> None:
    """The tree install.sh unpacks into ~/sts2, as every host of the pool has it."""
    dotnet = Path.home() / ".dotnet"
    fxr = sorted((dotnet / "host" / "fxr").iterdir())[-1].name
    runtime = sorted((dotnet / "shared" / "Microsoft.NETCore.App").iterdir())[-1].name
    (stage / "dotnet" / "host" / "fxr").mkdir(parents=True)
    (stage / "dotnet" / "shared" / "Microsoft.NETCore.App").mkdir(parents=True)
    shutil.copy2(dotnet / "dotnet", stage / "dotnet" / "dotnet")
    shutil.copytree(dotnet / "host" / "fxr" / fxr, stage / "dotnet" / "host" / "fxr" / fxr)
    shutil.copytree(dotnet / "shared" / "Microsoft.NETCore.App" / runtime, stage / "dotnet" / "shared" / "Microsoft.NETCore.App" / runtime)
    game = stage / "gamebin" / GAME
    game.mkdir(parents=True)
    for name in GAME_FILES:
        shutil.copy(ROOT / "tmp" / "gamebin" / GAME / name, game / name)
    build = ROOT / "tmp" / "builds" / rev / "art" / "bin" / "Sts2Ai.SimWorker" / "release"
    release = stage / "builds" / rev / "release"
    # onnxruntime ships native libraries for every platform (240 MB); these machines need linux-x64 only
    shutil.copytree(build, release, ignore=lambda d, names: [n for n in names if Path(d).name == "runtimes" and n != "linux-x64"])
    launcher = stage / f"sim-{rev}.sh"
    launcher.write_text(
        "#!/bin/sh\nS=$HOME/sts2\n"
        "exec env DOTNET_ROOT=$S/dotnet DOTNET_ReadyToRun=0 DOTNET_CLI_TELEMETRY_OPTOUT=1 HOME=$S/home \\\n"
        f"  STS2_BIN_DIR=$S/gamebin/{GAME} nice -n 19 $S/dotnet/dotnet $S/builds/{rev}/release/Sts2Ai.SimWorker.dll \"$@\"\n"
    )
    launcher.chmod(0o755)


def bundle(rev: str) -> str:
    """The sim at REV on the hub's dist/, from a pushed commit; its short name."""
    rev = output("git", "-C", ROOT, "rev-parse", "--short", rev)
    subprocess.run(["git", "-C", ROOT, "fetch", "-q", "origin"], check=True)
    if subprocess.run(["git", "-C", ROOT, "merge-base", "--is-ancestor", rev, "origin/main"]).returncode:
        sys.exit(f"dist: {rev} is not on origin/main; push it first")
    if not (ROOT / "tmp" / "builds" / rev / "sim.sh").exists():
        print(f"dist: building {rev}")
        subprocess.run(["sh", str(ROOT / "tmp" / "build.sh"), rev], check=True, stdout=subprocess.DEVNULL)
    with tempfile.TemporaryDirectory(dir=ROOT / "tmp") as t:
        stage = Path(t) / "stage"
        stage.mkdir()
        stage_tree(rev, stage)
        packed = Path(t) / f"sts2-{rev}.tar.zst"
        tar = subprocess.Popen(
            ["nice", "-n", "19", "tar", "-C", str(stage), "--owner=0", "--group=0", "--mode=u+w", "-cf", "-", "."],
            stdout=subprocess.PIPE,
        )
        with packed.open("wb") as f:
            subprocess.run(["nice", "-n", "19", "zstd", "-q", "-12", "-T2"], stdin=tar.stdout, stdout=f, check=True)
        tar.stdout.close()
        if tar.wait():
            sys.exit("dist: tar failed")
        print(f"dist: {packed.name} {packed.stat().st_size / 1048576:.1f} MB")
        # rsync writes it under a temporary name and renames it: no worker sees half a bundle
        put_on_hub(packed)
    return rev


def publish(rev: str) -> None:
    rev = bundle(rev)
    pin = PIN.read_text().strip()
    mykey = (Path.home() / ".ssh" / "id_ed25519.pub").read_text().strip()
    with tempfile.TemporaryDirectory(dir=ROOT / "tmp") as t:
        out = Path(t)
        install = (POOL / "install.sh").read_text()
        for mark, value in (("@URL@", output(HUB, "dist")), ("@PIN@", pin), ("@MYKEY@", mykey)):
            install = install.replace(mark, value)
        (out / "install.sh").write_text(install)
        (out / "latest").write_text(rev + "\n")
        put_on_hub(out / "install.sh")
        # latest last: nobody is told of a bundle that is not there yet
        put_on_hub(out / "latest")
    print(f"dist: {rev} published")


def put_on_hub(*paths) -> None:
    subprocess.run(
        ["rsync", "-a", "--partial", "--chmod=F644", "-e", output(HUB, "ssh", "local"), *map(str, paths), f"{output(HUB, 'addr', 'local')}:dist/"],
        check=True,
    )


def cert() -> None:
    text = subprocess.run([str(ON), "hub"], input="cat /root/sts2-dist/cert.pem\n", text=True, check=True, stdout=subprocess.PIPE).stdout
    text = text[text.index("-----BEGIN"):].strip() + "\n"
    CERT.write_text(text)
    print(text, end="")


def command() -> None:
    key, pin = KEY.read_text().strip(), PIN.read_text().strip()
    print(f"export STS2_KEY='{key}'; curl -fsS -k --pinnedpubkey 'sha256//{pin}' -H \"x-api-key: $STS2_KEY\" {output(HUB, 'dist')}/install.sh | bash")


if __name__ == "__main__":
    args = sys.argv[1:]
    if args == ["setup"]:
        setup()
    elif len(args) == 2 and args[0] == "bundle":
        bundle(args[1])
    elif len(args) == 2 and args[0] == "publish":
        publish(args[1])
    elif args == ["command"]:
        command()
    elif args == ["cert"]:
        cert()
    else:
        sys.exit(__doc__)
