#!/usr/bin/env bash
# Native GNOME integration is installed outside the Flatpak, in the user's Shell extension directory.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
"$repo/linux/scripts/package-gnome-extension.sh"
gnome-extensions install --force "$repo/build/linux/bambu-bar@rolottr.dev.shell-extension.zip"
if gnome-extensions info bambu-bar@rolottr.dev >/dev/null 2>&1; then
  # Some Shell versions expose ReloadExtension only in their introspection XML.
  gdbus call --session --dest org.gnome.Shell.Extensions --object-path /org/gnome/Shell/Extensions --method org.gnome.Shell.Extensions.ReloadExtension bambu-bar@rolottr.dev 2>/dev/null || true
  gnome-extensions enable bambu-bar@rolottr.dev
  printf 'If updating an already-loaded extension, sign out and back in to load the new code.\n'
else
  gjs -c 'const s = new imports.gi.Gio.Settings({schema_id: "org.gnome.shell"}); const ids = s.get_strv("enabled-extensions"); if (!ids.includes("bambu-bar@rolottr.dev")) s.set_strv("enabled-extensions", [...ids, "bambu-bar@rolottr.dev"]);'
  printf 'Installed and enabled for the next session. Sign out and back in to load the GNOME panel.\n'
fi
