#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
output="$repo/build/linux/Bambu-Bar.plasmoid"
mkdir -p "$(dirname "$output")"
python3 - "$repo/linux/plasma" "$output" "$repo/LICENSE" <<'PY'
import pathlib, sys, zipfile, tempfile, subprocess
root = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(sys.argv[2], 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(root.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(root))
    archive.write(sys.argv[3], 'LICENSE')
    native = root.parent / 'native'
    archive.write(native / 'kde/Kit.qml', 'contents/ui/NativeKit.qml')
    for path in sorted((native / 'common').glob('*.qml')):
        archive.write(path, 'contents/ui/common/' + path.name)
    with tempfile.TemporaryDirectory() as directory:
        subprocess.run(['python3', str(root.parent / 'scripts/generate-tray-icons.py'), directory], check=True)
        for path in sorted(pathlib.Path(directory).glob('*.svg')):
            archive.write(path, 'contents/ui/tray-icons/' + path.name)
PY
printf 'Local Plasma widget: %s\n' "$output"
