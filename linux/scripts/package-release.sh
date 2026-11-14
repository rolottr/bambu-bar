#!/usr/bin/env bash
# Collect only this build's assets. Does not create, upload or publish a release.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
rid="${1:?usage: package-release.sh <rid> <version> <revision>}"
version="${2:?version}"
revision="${3:?revision}"
case "$rid" in linux-x64|linux-arm64) ;; *) exit 2;; esac
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || exit 2
[[ "$revision" =~ ^[0-9a-f]{40}$ ]] || { printf 'A full source commit is required.\n' >&2; exit 2; }
out="$repo/build/linux/release/$rid"
mkdir -p "$out"
assets=("Bambu-Bar-$version-$rid.flatpak" "bambu-bar@rolottr.dev.shell-extension.zip" "Bambu-Bar.plasmoid" "Bambu-Bar-hyprland.tar.gz" "Bambu-Bar-native-desktop.tar.gz")
for asset in "${assets[@]}"; do cp "$repo/build/linux/$asset" "$out/$asset"; done
printf 'Version: %s\nSource commit: %s\nArchitecture: %s\nRuntime: org.gnome.Platform//50\n' \
  "$version" "$revision" "$rid" > "$out/SOURCE-linux.txt"
(cd "$out" && sha256sum "${assets[@]}" SOURCE-linux.txt > SHA256SUMS-linux.txt)
printf 'Local release assets: %s\n' "$out"
