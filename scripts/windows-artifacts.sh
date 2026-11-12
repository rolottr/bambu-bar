#!/bin/zsh
# Downloads the Windows build of a commit from CI into build/, for the release (docs/releasing.md):
#   build/Bambu-Bar-<version>-windows-x64.exe, -arm64.exe, their .sha256, and rainmeter-<ver>-source.zip
#   scripts/windows-artifacts.sh            the commit checked out (HEAD), the version in project.yml
#   COMMIT=<sha> scripts/windows-artifacts.sh
# Refuses unless that commit's run of the build workflow passed, and checks the checksums and the file names' version.
set -euo pipefail

ROOT=${0:A:h:h}
REPO=rolottr/bambu-bar
COMMIT=${COMMIT:-$(git -C "$ROOT" rev-parse HEAD)}
VERSION=$(sed -nE 's/^[[:space:]]*MARKETING_VERSION:[[:space:]]*"?([0-9.]+)"?.*/\1/p' "$ROOT/project.yml" | head -1)
[[ -n "$VERSION" ]] || { echo "No MARKETING_VERSION in project.yml" >&2; exit 2; }

RUN=$(gh run list --repo "$REPO" --workflow build.yml --commit "$COMMIT" --json databaseId,status,conclusion \
  --jq 'map(select(.status == "completed")) | first | "\(.databaseId) \(.conclusion)"')
[[ -n "$RUN" && "$RUN" != "null null" ]] || { echo "No finished CI run for $COMMIT" >&2; exit 2; }
ID=${RUN%% *}; CONCLUSION=${RUN#* }
[[ "$CONCLUSION" == success ]] || { echo "CI run $ID for $COMMIT ended with $CONCLUSION" >&2; exit 2; }

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
for name in Bambu-Bar-windows-installer-win-x64 Bambu-Bar-windows-installer-win-arm64 rainmeter-source; do
  gh run download "$ID" --repo "$REPO" -n "$name" -D "$WORK/$name"
done
mkdir -p "$ROOT/build"
for arch in x64 arm64; do
  EXE="Bambu-Bar-$VERSION-windows-$arch.exe"
  DIR="$WORK/Bambu-Bar-windows-installer-win-$arch"
  [[ -f "$DIR/$EXE" ]] || { echo "Run $ID has no $EXE (another version?): $(ls "$DIR")" >&2; exit 2; }
  (cd "$DIR" && shasum -a 256 -c "$EXE.sha256" >/dev/null) || { echo "$EXE doesn't match its checksum" >&2; exit 2; }
  cp "$DIR/$EXE" "$DIR/$EXE.sha256" "$ROOT/build/"
done
cp "$WORK"/rainmeter-source/*.zip "$ROOT/build/"
echo "==> Windows $VERSION from CI run $ID (${COMMIT[1,7]}) in build/:"
ls -1 "$ROOT/build" | grep -E "windows-(x64|arm64)\.exe|rainmeter-.*source\.zip"
