#!/usr/bin/env bash
set -euo pipefail
source_dir="$(cd "${1:?usage: install.sh <publish-folder>}" && pwd)"
prefix="${XDG_DATA_HOME:-$HOME/.local/share}"
app_dir="$HOME/.local/opt/bambu-bar"
install -d "$app_dir/versions" "$HOME/.local/bin" "$prefix/applications" "$prefix/icons/hicolor/256x256/apps"
stage="$(mktemp -d "$app_dir/versions/build-XXXXXXXX")"
cp -a "$source_dir/." "$stage/"
chmod +x "$stage/bambu-bar"
ln -s "$stage" "$app_dir/current-next"
mv -Tf "$app_dir/current-next" "$app_dir/current"
ln -sfn "$app_dir/current/bambu-bar" "$HOME/.local/bin/bambu-bar"
install -d "$prefix/icons/hicolor/scalable/status"
install -m644 "$app_dir/current/tray-icons/"*.svg "$prefix/icons/hicolor/scalable/status/"
install -m644 "$app_dir/current/AppIcon.png" "$prefix/icons/hicolor/256x256/apps/dev.rolottr.BambuBar.png"
# Desktop Exec paths need escaping distinct from shell escaping.
python3 - "$HOME/.local/bin/bambu-bar" "$prefix/applications/dev.rolottr.BambuBar.desktop" <<'PY'
import sys
exe=sys.argv[1].replace('\\','\\\\').replace('"','\\"').replace('`','\\`').replace('$','\\$').replace('%','%%')
with open(sys.argv[2], 'w') as f:
    f.write(f'[Desktop Entry]\nType=Application\nName=Bambu Bar\nComment=Your Bambu Lab printers, one click away\nExec="{exe}"\nIcon=dev.rolottr.BambuBar\nTerminal=false\nCategories=Utility;Monitor;\nStartupNotify=true\nDBusActivatable=false\nActions=QuickView;Quit;\n\n[Desktop Action QuickView]\nName=Quick view\nExec="{exe}" --panel\n\n[Desktop Action Quit]\nName=Quit\nExec="{exe}" --quit\n')
PY
touch "$prefix/icons/hicolor"
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -f -t "$prefix/icons/hicolor" >/dev/null || true
command -v update-desktop-database >/dev/null && update-desktop-database "$prefix/applications" || true
printf 'Installed Bambu Bar: %s\n' "$app_dir/bambu-bar"
