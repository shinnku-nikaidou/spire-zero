#!/bin/sh
# Builds the Workshop item from one commit ($1, default HEAD) into out/Sts2Ai/, ready to upload (plan
# plans/2026-11-07-1333-steam-workshop-release.md); out/commit names the commit. Intermediate files: tmp/item/REV/.
#
#   out/Sts2Ai/Sts2Ai.json, Sts2Ai.dll    the mod (one build, for every platform: it runs on the game's own .NET)
#   out/Sts2Ai/brain/RID/                 the brain for each platform, self-contained (BundledBrain starts it)
#   out/Sts2Ai/brain/champion.json        the NoSL champion's file, its nets under brain/nets/: the brain plays it for
#                                         NoSL and, with the SL planner and L0 on the node's clone, for SL
#
# The brain is a dotnet publish -r RID each with the profile Properties/PublishProfiles/brain.pubxml; the game's
# assemblies are only compiled against, and the brain loads the player's own at run time (STS2_BIN_DIR).
# The nets lose their .json: the game reads every .json under a mod's folder as a possible manifest, and a net's
# "name" field makes it log an error.
# Apple silicon runs only signed code, and the launcher made here is unsigned (macOS kills it, exit 137), so it is signed
# ad-hoc on a Mac with Xcode's codesign: STS2_MAC=user@host in the repository's .env, reached by ssh.
set -e
R=$(cd "$(dirname "$0")/.." && pwd)
. "$R/.env"
: "${STS2_MAC:?set STS2_MAC=user@host in .env, a Mac to sign the osx-arm64 launcher on}"
rev=$(git -C "$R" rev-parse --short "${1:-HEAD}")
B=$R/tmp/item/$rev
I=$R/out/Sts2Ai
rids="linux-x64 osx-arm64 win-x64"

rm -rf "$B"
mkdir -p "$B/src"
git -C "$R" archive "$rev" | tar -x -C "$B/src"
export DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet="$HOME/.dotnet/dotnet"
# The archive sits inside this checkout, so left alone the build would stamp the checkout's HEAD, not REV.
set -- -p:Sts2BinDir="$R/tmp/gamebin/v0.113.0" -p:SourceRevisionId="$(git -C "$R" rev-parse "$rev")" \
  -p:UseArtifactsOutput=true -p:ArtifactsPath="$B/art" -v q -nologo

"$dotnet" build "$B/src/src/Sts2Ai.Mod" -c Release "$@"
# One after another, as they share the libraries' outputs.
for rid in $rids; do
  "$dotnet" publish "$B/src/src/Sts2Ai.SimWorker" "$@" -p:PublishProfile=brain -r "$rid" -o "$B/brain/$rid"
done

launcher=$B/brain/osx-arm64/Sts2Ai.SimWorker
ssh "$STS2_MAC" 'f=$(mktemp) && cat > "$f" && codesign -s - -f "$f" && codesign -v "$f" && cat "$f" && rm -f "$f"' \
  < "$launcher" > "$launcher.signed"
mv "$launcher.signed" "$launcher"
chmod +x "$launcher"

# NoSL is not trained for now: it plays with SL's nets, the V and the combat net nosl.json names as sl.json does (PIMC
# with them for NoSL hosts, the SL planner for SL hosts).
champion=$B/src/eval/champions/nosl.json
mkdir -p "$B/brain/nets"
# Net files are not in git, so the commit's export holds none: they come from this checkout's weights/all, which
# weights/fetch.sh fills with the champions' nets.
for net in $(grep -o 'weights/all/[^"]*\.json' "$champion" | sort -u); do
  cp "$R/$net" "$B/brain/nets/$(basename "$net" .json).net"
done
sed 's#weights/all/\([^"]*\)\.json#nets/\1.net#g' "$champion" > "$B/brain/champion.json"

rm -rf "$R/out"
mkdir -p "$I"
cp "$B/art/bin/Sts2Ai.Mod/release/Sts2Ai.dll" "$B/art/bin/Sts2Ai.Mod/release/Sts2Ai.json" "$I/"
mv "$B/brain" "$I/brain"
echo "$rev" > "$R/out/commit"
du -sh "$I" "$I"/brain/*/
