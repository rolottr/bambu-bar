#!/usr/bin/env bash
# Local bundle; does not upload or enable anything.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$repo/build/linux"
mkdir -p "$out"
python3 "$repo/linux/scripts/generate-tray-icons.py" "$out/tray-icons"
python3 - "$repo" "$out" <<'PY'
from pathlib import Path
import sys, zipfile
repo, out = map(Path, sys.argv[1:])
# No compiled schemas/translations: a Shell extension is a ZIP with metadata at
# its root. Package it on KDE/Hyprland builders without installing GNOME tools.
with zipfile.ZipFile(out / 'bambu-bar@rolottr.dev.shell-extension.zip', 'w', zipfile.ZIP_DEFLATED) as archive:
    for name in ('metadata.json', 'extension.js', 'stylesheet.css', 'widgets.js', 'widget-layout.js', 'widget-model.js'):
        archive.write(repo / 'linux/gnome' / name, name)
    archive.write(repo / 'LICENSE', 'LICENSE')
    for icon in sorted((out / 'tray-icons').glob('*.svg')):
        archive.write(icon, 'tray-icons/' + icon.name)
PY
printf 'GNOME panel bundle: %s\n' "$out/bambu-bar@rolottr.dev.shell-extension.zip"
