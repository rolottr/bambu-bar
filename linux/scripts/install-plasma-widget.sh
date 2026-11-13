#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
"$repo/linux/scripts/package-plasma-widget.sh"
if kpackagetool6 --type Plasma/Applet --show dev.rolottr.BambuBar >/dev/null 2>&1; then
    kpackagetool6 --type Plasma/Applet --upgrade "$repo/build/linux/Bambu-Bar.plasmoid"
else
    kpackagetool6 --type Plasma/Applet --install "$repo/build/linux/Bambu-Bar.plasmoid"
fi
printf 'Add Bambu Bar from Plasma Edit Mode → Add Widgets. Enable desktop widgets in the app.\n'
