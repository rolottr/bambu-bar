#!/bin/zsh
# Install Bambu Bar.app into /Applications (single-copy, atomic backup + rollback) and open it.
#   scripts/install.sh                  install from build/App.noindex/Bambu Bar.app (builds if missing)
#   scripts/install.sh --build          rebuild before installing
#   scripts/install.sh --zip <path>     extract a release ZIP into build/Install.noindex/ and install it
#   scripts/install.sh --no-open        install without launching the app afterwards
# Never falls back to ~/Applications.
set -euo pipefail

ROOT=${0:A:h:h}
BUNDLE_ID="dev.rolottr.BambuBar"
APP="$ROOT/build/App.noindex/Bambu Bar.app"
DEST="/Applications/Bambu Bar.app"
DO_BUILD=0
NO_OPEN=0
ZIP_PATH=""

while (( $# > 0 )); do
  case "$1" in
    --build)
      DO_BUILD=1
      shift
      ;;
    --zip)
      [[ $# -ge 2 ]] || { echo "Missing path after --zip" >&2; exit 2; }
      ZIP_PATH="$2"
      shift 2
      ;;
    --no-open)
      NO_OPEN=1
      shift
      ;;
    -h|--help)
      echo "Usage: scripts/install.sh [--build | --zip <path>] [--no-open]"
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 2
      ;;
  esac
done

if (( DO_BUILD )) && [[ -n "$ZIP_PATH" ]]; then
  echo "Cannot combine --build and --zip" >&2
  exit 2
fi

if [[ -n "$ZIP_PATH" ]]; then
  [[ -f "$ZIP_PATH" ]] || { echo "ZIP not found: $ZIP_PATH" >&2; exit 1; }
elif (( DO_BUILD )) || [[ ! -d "$APP" ]]; then
  "$ROOT/scripts/build.sh"
fi

python3 - "$ROOT" "$APP" "$ZIP_PATH" "$BUNDLE_ID" <<'PY'
import datetime
import os
import pathlib
import plistlib
import shutil
import signal
import subprocess
import sys
import time

root = pathlib.Path(sys.argv[1])
default_app = pathlib.Path(sys.argv[2])
zip_path = pathlib.Path(sys.argv[3]).resolve() if sys.argv[3] else None
expected_bundle_id = sys.argv[4]

dest_dir = pathlib.Path('/Applications')
if not dest_dir.is_dir() or not os.access(dest_dir, os.W_OK):
    raise SystemExit('/Applications is not writable; refusing to fall back to ~/Applications.')

destination = dest_dir / 'Bambu Bar.app'
workspace = root / 'build/Install.noindex'
backups_dir = root / 'build/Backups.noindex'
lsregister = '/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister'
unregister_script = str(root / 'scripts/unregister-build-apps.sh')


def unregister(*paths):
    items = [str(p) for p in paths if p and pathlib.Path(p).exists()]
    if items:
        subprocess.run([unregister_script, *items], check=False)


def read_info(bundle: pathlib.Path) -> dict:
    plist_path = bundle / 'Contents/Info.plist'
    if not plist_path.is_file():
        raise RuntimeError(f'Missing Info.plist in {bundle}')
    with plist_path.open('rb') as stream:
        value = plistlib.load(stream)
    if value.get('CFBundleIdentifier') != expected_bundle_id:
        raise RuntimeError(
            f'Refusing to touch bundle with CFBundleIdentifier={value.get("CFBundleIdentifier")!r} '
            f'(expected {expected_bundle_id!r}): {bundle}'
        )
    return value


def verify(bundle: pathlib.Path) -> dict:
    value = read_info(bundle)
    subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(bundle)], check=True)
    return value


def matching_pids(app_paths) -> list[int]:
    prefixes = []
    for path in app_paths:
        resolved = str(path)
        prefixes.append(f'{resolved}/Contents/MacOS/')
        prefixes.append(f'{resolved}/Contents/PlugIns/BambuBarWidget.appex/')
    rows = subprocess.check_output(['ps', '-axo', 'pid=,comm='], text=True).splitlines()
    pids = []
    for row in rows:
        parts = row.strip().split(None, 1)
        if len(parts) != 2:
            continue
        pid_str, comm = parts
        if any(comm.startswith(prefix) for prefix in prefixes) or comm.endswith('/BambuBarWidget'):
            try:
                pid = int(pid_str)
            except ValueError:
                continue
            if pid != os.getpid():
                pids.append(pid)
    return sorted(set(pids))


workspace.mkdir(parents=True, exist_ok=True)
backups_dir.mkdir(parents=True, exist_ok=True)
extract_dir = workspace / 'extract'
stage = workspace / 'Bambu Bar.staged.app'

try:
    if extract_dir.exists():
        shutil.rmtree(extract_dir)
    if stage.exists():
        unregister(stage)
        shutil.rmtree(stage)

    if zip_path is not None:
        extract_dir.mkdir(parents=True, exist_ok=True)
        subprocess.run(['/usr/bin/ditto', '-x', '-k', str(zip_path), str(extract_dir)], check=True)
        source = extract_dir / 'Bambu Bar.app'
        if not source.is_dir():
            candidates = list(extract_dir.glob('*.app'))
            if len(candidates) == 1:
                source = candidates[0]
            else:
                raise SystemExit(f'ZIP did not contain Bambu Bar.app: {zip_path}')
    else:
        source = default_app
        if not source.is_dir():
            raise SystemExit(f'App bundle not found: {source}. Build first with scripts/build.sh.')

    verify(source)
    subprocess.run(['/usr/bin/ditto', str(source), str(stage)], check=True)
    verify(stage)

    # Check both /Applications and any stray ~/Applications copy so single-copy holds.
    existing_candidates = [
        folder / 'Bambu Bar.app'
        for folder in (dest_dir, pathlib.Path.home() / 'Applications')
        if (folder / 'Bambu Bar.app').exists()
    ]
    for path in existing_candidates:
        read_info(path)

    stop_targets = existing_candidates if existing_candidates else [destination]
    pids = matching_pids(stop_targets)
    for pid in pids:
        try:
            os.kill(pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
    deadline = time.monotonic() + 10.0
    while matching_pids(stop_targets) and time.monotonic() < deadline:
        time.sleep(0.2)
    remaining = matching_pids(stop_targets)
    if remaining:
        raise SystemExit(
            f'Bambu Bar or its widget is still running (PIDs: {remaining}); '
            'the installed application has not been replaced.'
        )

    stamp = datetime.datetime.now().strftime('%Y%m%d-%H%M%S')
    moved = []
    try:
        for index, path in enumerate(existing_candidates):
            backup = backups_dir / f'Bambu Bar-{stamp}-{index}.app'
            if backup.exists():
                read_info(backup)
                unregister(backup)
                shutil.rmtree(backup)
            shutil.move(str(path), str(backup))
            unregister(backup)
            moved.append((path, backup))

        shutil.move(str(stage), str(destination))
        verify(destination)
    except Exception:
        if destination.exists() and not stage.exists():
            shutil.move(str(destination), str(stage))
        for path, backup in reversed(moved):
            if backup.exists():
                shutil.move(str(backup), str(path))
        raise

    subprocess.run([lsregister, '-f', str(destination)], check=True)
    # Stop any newly respawned old widget extension so widgets reload from the new bundle.
    for pid in matching_pids([destination]):
        # Only terminate widget processes; the app itself has not been opened yet.
        try:
            comm = subprocess.check_output(['ps', '-o', 'comm=', '-p', str(pid)], text=True).strip()
            if 'BambuBarWidget' in comm:
                os.kill(pid, signal.SIGTERM)
        except (ProcessLookupError, subprocess.CalledProcessError):
            pass

    info = read_info(destination)
    print(f'==> Installed {destination} ({info.get("CFBundleShortVersionString", "?")} build {info.get("CFBundleVersion", "?")})')
finally:
    for item in (stage, extract_dir / 'Bambu Bar.app'):
        unregister(item)
    if workspace.exists():
        shutil.rmtree(workspace, ignore_errors=True)
PY

if (( ! NO_OPEN )); then
  open "$DEST"
fi
"$ROOT/scripts/check-one-copy.sh" "$BUNDLE_ID"
