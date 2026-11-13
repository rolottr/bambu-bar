#!/usr/bin/env bash
# User-local, D-Bus-activated popup/autostart service. Does not modify desktop config.
set -euo pipefail
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
printf 'Installed Hyprland popup/autostart service. No Waybar/Hyprland config changed.\n'
