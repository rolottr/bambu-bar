#!/usr/bin/env bash
# Run inside the GNOME SDK so the companion library uses the Flatpak ABI.
set -euo pipefail
version=1.3.0
work="${BAMBUBAR_LAYER_SHELL_WORK:?set BAMBUBAR_LAYER_SHELL_WORK}"
out="${1:?output folder}"
mkdir -p "$work" "$out"
archive="$work/gtk4-layer-shell-$version.tar.gz"
if [ ! -f "$archive" ]; then curl -fL --retry 3 "https://codeload.github.com/wmww/gtk4-layer-shell/tar.gz/refs/tags/v$version" -o "$archive"; fi
printf '%s  %s\n' 1ebb01ab14e98afd1727f68f64981c37bd23305b1f131f5667c02b94cf593192 "$archive" | sha256sum -c -
if [ ! -d "$work/gtk4-layer-shell-$version" ]; then tar -xf "$archive" -C "$work"; fi
source_dir="$work/gtk4-layer-shell-$version"
if [ ! -f "$source_dir/build/build.ninja" ]; then
  meson setup "$source_dir/build" "$source_dir" --buildtype=release -Dintrospection=false -Dvapi=false -Dexamples=false -Dtests=false -Ddocs=false
fi
ninja -C "$source_dir/build"
for library in libgtk4-layer-shell.so libgtk4-layer-shell.so.0 "libgtk4-layer-shell.so.$version"; do
  cp -a "$source_dir/build/src/$library" "$out/"
done
cp "$source_dir/LICENSE" "$out/LICENSE.txt"
printf 'GTK4 Layer Shell %s, MIT.\nSource: https://github.com/wmww/gtk4-layer-shell/tree/v%s\nBuild recipe: linux/scripts/build-layer-shell.sh\n' "$version" "$version" > "$out/SOURCE.txt"
