#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
output="$repo/build/linux/Bambu-Bar.plasmoid"
mkdir -p "$(dirname "$output")"
python3 - "$repo/linux/plasma" "$output" "$repo/LICENSE" <<'PY'
import pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(root.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(root))
    archive.write(sys.argv[3], 'LICENSE')
PY
printf 'Local Plasma widget: %s\n' "$output"
