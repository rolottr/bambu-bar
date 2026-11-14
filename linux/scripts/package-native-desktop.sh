#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
output="$repo/build/linux/Bambu-Bar-native-desktop.tar.gz"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
mkdir -p "$stage/bambu-bar-native/linux/scripts" "$stage/bambu-bar-native/shared/Printers"
cp -a "$repo/linux/native" "$repo/linux/omarchy" "$stage/bambu-bar-native/linux/"
cp "$repo/shared/Printers/qt-models.tar.gz" "$stage/bambu-bar-native/shared/Printers/"
cp "$repo/linux/scripts/"{install-native-desktop.sh,install-omarchy-plugin.py,generate-tray-icons.py} "$stage/bambu-bar-native/linux/scripts/"
cp "$repo/LICENSE" "$stage/bambu-bar-native/"
# Python's cache belongs to the build host, never a release package.
find "$stage" -type d -name __pycache__ -exec rm -r {} +
mkdir -p "$(dirname "$output")"
tar -czf "$output" -C "$stage" bambu-bar-native
printf 'Native desktop package: %s\n' "$output"
