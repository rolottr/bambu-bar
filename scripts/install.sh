#!/bin/zsh
# Install Bambu Bar.app into /Applications and open it.
#   scripts/install.sh           uses the existing build, or builds if missing
#   scripts/install.sh --build   rebuilds before installing
set -euo pipefail

ROOT=${0:A:h:h}
APP="$ROOT/build/App.noindex/Bambu Bar.app"
DEST="/Applications/Bambu Bar.app"
if [[ "${1:-}" == "--build" || ! -d "$APP" ]]; then
  "$ROOT/scripts/build.sh"
fi
pkill -x "Bambu Bar" 2>/dev/null && sleep 1 || true
rm -rf "$DEST"
ditto "$APP" "$DEST"
# Register the app (and its widget) with Launch Services so the widget gallery finds it.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST"
# macOS keeps a running widget extension alive across app updates; stop it so widgets load the new one.
pkill -f "Bambu Bar.app/Contents/PlugIns/BambuBarWidget.appex" 2>/dev/null || true
open "$DEST"
echo "==> Installed $DEST"
"$ROOT/scripts/check-one-copy.sh" dev.rolottr.BambuBar
