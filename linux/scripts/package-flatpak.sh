#!/usr/bin/env bash
# Wrap a self-contained Linux publish (including ffmpeg/) in the tested GNOME runtime. Local bundle only.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
publish="$(cd "${1:?usage: package-flatpak.sh <publish-folder> <output.flatpak>}" && pwd)"
output="$(realpath -m "${2:?output.flatpak}")"
arch="$(uname -m)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
stage="$work/app"
mkdir -p "$stage/files/lib/bambu-bar" "$stage/files/bin" "$stage/files/share/applications" "$stage/files/share/metainfo" "$stage/files/share/icons/hicolor/256x256/apps"
cp -a "$publish/." "$stage/files/lib/bambu-bar/"
ln -s ../lib/bambu-bar/bambu-bar "$stage/files/bin/bambu-bar"
cp "$repo/linux/packaging/dev.rolottr.BambuBar.desktop" "$stage/files/share/applications/"
cp "$repo/linux/packaging/dev.rolottr.BambuBar.metainfo.xml" "$stage/files/share/metainfo/"
cp "$publish/AppIcon.png" "$stage/files/share/icons/hicolor/256x256/apps/dev.rolottr.BambuBar.png"
mkdir -p "$stage/files/share/icons/hicolor/scalable/status"
cp "$publish/tray-icons/"*.svg "$stage/files/share/icons/hicolor/scalable/status/"
cat > "$stage/metadata" <<META
[Application]
name=dev.rolottr.BambuBar
runtime=org.gnome.Platform/$arch/50
command=bambu-bar

[Context]
shared=network;ipc;
sockets=wayland;fallback-x11;
devices=dri;
filesystems=xdg-config/BambuStudio:ro;xdg-config/OrcaSlicer:ro;~/.local/state/omarchy/current:ro;xdg-config/omarchy/current:ro;

[Session Bus Policy]
org.freedesktop.secrets=talk
org.kde.StatusNotifierWatcher=talk
dev.rolottr.BambuBar.Hyprland=talk
dev.rolottr.BambuBar.Shell=talk
dev.rolottr.BambuBar.Desktop=own
META
flatpak build-finish "$stage"
flatpak build-export "$work/repo" "$stage" linux-local
mkdir -p "$(dirname "$output")"
flatpak build-bundle "$work/repo" "$output" dev.rolottr.BambuBar linux-local --runtime-repo=https://flathub.org/repo/flathub.flatpakrepo
printf 'Local Flatpak: %s\n' "$output"
