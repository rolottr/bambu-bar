#!/usr/bin/env bash
# User-local popup/autostart service; optional, narrowly scoped main-window rule.
set -euo pipefail
if [[ ${1:-} != "" && ${1:-} != --compact-window ]]; then
  printf 'Usage: %s [--compact-window]\n' "$0" >&2; exit 2
fi
repo="$(cd "$(dirname "$0")/../.." && pwd)"
python3 -c 'import gi; gi.require_version("GtkLayerShell", "0.1"); from gi.repository import Gio, GLib, GtkLayerShell; import cairo'
command -v hyprctl >/dev/null
base="${XDG_DATA_HOME:-$HOME/.local/share}"
lib="$base/bambu-bar-hyprland"
services="$base/dbus-1/services"
mkdir -p "$lib" "$services"
for file in "$lib/anchor.py" "$services/dev.rolottr.BambuBar.Hyprland.service"; do
  if [[ -e "$file" ]]; then cp -p "$file" "$file.backup-$(date +%s)"; fi
done
install -m 755 "$repo/linux/hyprland/anchor.py" "$lib/anchor.py"
# Quote paths according to the D-Bus Exec grammar; no shell interpolation at activation.
escaped="${lib//\\/\\\\}"
escaped="${escaped//\"/\\\"}"
cat > "$services/dev.rolottr.BambuBar.Hyprland.service" <<SERVICE
[D-BUS Service]
Name=dev.rolottr.BambuBar.Hyprland
Exec=/usr/bin/python3 "$escaped/anchor.py"
SERVICE
printf 'Installed Hyprland popup/autostart service. No bar config changed.\n'

if [[ ${1:-} == --compact-window ]]; then
  python3 - "$repo/linux/hyprland/window.lua" <<'PYTHON'
import os, shutil, sys, time
from pathlib import Path
config = Path(os.environ.get('XDG_CONFIG_HOME', Path.home() / '.config')) / 'hypr'
main = config / 'hyprland.lua'
rule = config / 'bambu-bar.lua'
if not main.is_file():
    raise SystemExit('Compact window requires an existing Hyprland Lua configuration.')
source = Path(sys.argv[1]).read_text()
if rule.exists() and not rule.read_text().startswith('-- Bambu Bar compact main window (managed by install-hyprland-integration.sh).'):
    raise SystemExit('Refusing to overwrite an independently managed bambu-bar.lua.')
include = 'dofile((os.getenv("XDG_CONFIG_HOME") or (os.getenv("HOME") .. "/.config")) .. "/hypr/bambu-bar.lua")'
if rule.exists():
    shutil.copy2(rule, str(rule) + '.backup-' + str(time.time_ns()))
rule.write_text(source)
text = main.read_text()
if include not in text.splitlines():
    shutil.copy2(main, str(main) + '.backup-' + str(time.time_ns()))
    with main.open('a') as output:
        output.write('\n-- Bambu Bar compact main window. Remove this line to restore tiling.\n' + include + '\n')
PYTHON
  printf 'Installed optional compact main-window rule. Reload Hyprland to apply.\n'
fi
