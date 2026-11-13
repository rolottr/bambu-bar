#!/usr/bin/env bash
# Local bundle; does not upload or enable anything.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$repo/build/linux"
mkdir -p "$out"
python3 "$repo/linux/scripts/generate-tray-icons.py" "$out/tray-icons"
gnome-extensions pack "$repo/linux/gnome" --force --out-dir="$out" --extra-source="$out/tray-icons" --extra-source=widgets.js --extra-source=widget-layout.js --extra-source=widget-model.js --extra-source="$repo/LICENSE"
printf 'GNOME panel bundle: %s\n' "$out/bambu-bar@rolottr.dev.shell-extension.zip"
