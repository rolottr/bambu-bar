#!/usr/bin/env bash
# Install only Bambu Bar's native UI and activation entry. Never replace desktop configuration.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
source_dir="$root/native"
target="${XDG_DATA_HOME:-$HOME/.local/share}/bambu-bar-native"
python3 -c 'from gi.repository import Gio, GLib'
if [[ -d /usr/share/omarchy/shell ]]; then
  command -v qs >/dev/null
  [[ -f /usr/share/omarchy/shell/Ui/PopupCard.qml && -f /usr/share/omarchy/shell/Commons/Style.qml ]]
else
  QT_QPA_PLATFORM=offscreen python3 - <<'PY'
from PyQt6.QtWidgets import QApplication
from PyQt6.QtQml import QQmlEngine, QQmlComponent
from PyQt6.QtCore import QUrl
app = QApplication([])
engine = QQmlEngine()
component = QQmlComponent(engine)
component.setData(b'import QtQuick\nimport QtQuick.Controls\nimport QtQuick.Dialogs\nimport QtQuick.Layouts\nimport QtQuick3D\nimport org.kde.kirigami\nNode {}', QUrl())
if component.isError():
    raise SystemExit('Install PyQt6 QtQuick/QtQml, Kirigami and Qt Quick 3D before installing Bambu Bar: ' + component.errorString())
PY
fi
[[ -f "$root/../shared/Printers/qt-models.tar.gz" ]]
mkdir -p "${XDG_STATE_HOME:-$HOME/.local/state}/bambu-bar/backups"
backup="$(mktemp -d "${XDG_STATE_HOME:-$HOME/.local/state}/bambu-bar/backups/native-$(date +%Y%m%d-%H%M%S)-XXXXXX")"
mkdir -p "$target" "${XDG_DATA_HOME:-$HOME/.local/share}/dbus-1/services"
if [[ -f "$target/host.py" ]]; then cp -a "$target" "$backup/"; fi
cp -a "$source_dir/." "$target/"
mkdir -p "$target/common/models"
tar -xzf "$root/../shared/Printers/qt-models.tar.gz" -C "$target/common/models"
if [[ -d /usr/share/omarchy/shell ]]; then
  command -v qs >/dev/null
  mkdir -p "$target/omarchy/common"
  cp -a "$target/common/." "$target/omarchy/common/"
  ln -sfn /usr/share/omarchy/shell/Ui "$target/omarchy/Ui"
  ln -sfn /usr/share/omarchy/shell/Commons "$target/omarchy/Commons"
fi
service="${XDG_DATA_HOME:-$HOME/.local/share}/dbus-1/services/dev.rolottr.BambuBar.Native.service"
if [[ -f "$service" ]]; then cp -a "$service" "$backup/"; fi
python3 - "$service" "$target/host.py" <<'PY'
from pathlib import Path
import shlex, sys
Path(sys.argv[1]).write_text('[D-BUS Service]\nName=dev.rolottr.BambuBar.Native\nExec=' + shlex.join(['/usr/bin/python3', sys.argv[2]]) + '\n')
PY
printf 'Native UI installed: %s\nBackup: %s\n' "$target" "$backup"
