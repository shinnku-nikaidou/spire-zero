#!/bin/sh
# Publishes the brain for every platform the Workshop item ships (plan tmp/plans/2026-11-07-1333-steam-workshop-release.md):
# one self-contained folder per runtime identifier, all from one commit ($1, default HEAD), into tmp/brain/REV/RID/.
# The settings are the publish profile src/Sts2Ai.SimWorker/Properties/PublishProfiles/brain.pubxml.
# The game's assemblies are only compiled against; the brain loads the player's own at run time (STS2_BIN_DIR).
# Apple silicon runs only signed code, and the launcher made here is unsigned (macOS kills it, exit 137), so it is signed
# ad-hoc on a Mac with Xcode's codesign: MAC=user@host, reached by ssh.
set -e
: "${MAC:?set MAC=user@host, a Mac to sign the osx-arm64 launcher on}"
R=$(cd "$(dirname "$0")/.." && pwd)
rev=$(git -C "$R" rev-parse --short "${1:-HEAD}")
B=$R/tmp/brain/$rev
rids="linux-x64 osx-arm64 win-x64"

rm -rf "$B"
mkdir -p "$B/src"
git -C "$R" archive "$rev" | tar -x -C "$B/src"
export DOTNET_ROOT="$HOME/.dotnet" DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet="$HOME/.dotnet/dotnet"
project=$B/src/src/Sts2Ai.SimWorker
# The archive sits inside this checkout, so left alone the build would stamp the checkout's HEAD, not REV.
set -- -p:Sts2BinDir="$R/tmp/gamebin/v0.111.0" -p:SourceRevisionId="$(git -C "$R" rev-parse "$rev")" \
  -p:PublishProfile=brain -p:UseArtifactsOutput=true -p:ArtifactsPath="$B/art" -v q -nologo

# One after another, as they share the libraries' outputs.
for rid in $rids; do
  "$dotnet" publish "$project" "$@" -r "$rid" -o "$B/$rid"
done

launcher=$B/osx-arm64/Sts2Ai.SimWorker
ssh "$MAC" 'f=$(mktemp) && cat > "$f" && codesign -s - -f "$f" && codesign -v "$f" && cat "$f" && rm -f "$f"' \
  < "$launcher" > "$launcher.signed"
mv "$launcher.signed" "$launcher"
chmod +x "$launcher"
du -sh "$B"/*-*/
