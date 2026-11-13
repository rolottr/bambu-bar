#!/usr/bin/env bash
# Local bundle; does not upload or enable anything.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$repo/build/linux"
mkdir -p "$out"
gnome-extensions pack "$repo/linux/gnome" --force --out-dir="$out" --extra-source=bambu-bar-symbolic.svg --extra-source=widgets.js --extra-source=widget-layout.js --extra-source="$repo/LICENSE"
printf 'GNOME panel bundle: %s\n' "$out/bambu-bar@rolottr.dev.shell-extension.zip"
