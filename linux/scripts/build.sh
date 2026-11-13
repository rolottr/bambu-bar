#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo/linux"
case "$(uname -m)" in x86_64) rid=linux-x64;; aarch64|arm64) rid=linux-arm64;; *) printf 'Unsupported architecture\n' >&2; exit 1;; esac
sdk="${DOTNET:-dotnet}"
out="$repo/build/linux/$rid"
revision="${BAMBUBAR_COMMIT:-$(git -C "$repo" rev-parse HEAD)}"
"$sdk" publish src/BambuBar.Linux -c Release -r "$rid" --self-contained true -p:SourceRevisionId="$revision" -o "$out"
codec_work="${XDG_CACHE_HOME:-$HOME/.cache}/bambu-bar/ffmpeg-sdk50"
mkdir -p "$codec_work" "$out/ffmpeg"
# Compile native dependencies against the runtime, never the builder distro's newer glibc.
flatpak run --command=bash --share=network --filesystem="$repo":ro --filesystem="$codec_work" --filesystem="$out" --env=BAMBUBAR_FFMPEG_WORK="$codec_work" org.gnome.Sdk//50 "$repo/linux/scripts/build-ffmpeg.sh" "$out/ffmpeg"
layer_work="${XDG_CACHE_HOME:-$HOME/.cache}/bambu-bar/layer-shell-sdk50"
mkdir -p "$layer_work" "$out/layer-shell"
flatpak run --command=bash --share=network --filesystem="$repo":ro --filesystem="$layer_work" --filesystem="$out" --env=BAMBUBAR_LAYER_SHELL_WORK="$layer_work" org.gnome.Sdk//50 "$repo/linux/scripts/build-layer-shell.sh" "$out/layer-shell"
cp "$repo/LICENSE" "$out/BAMBU-BAR-LICENSE.txt"
cp "$repo/linux/packaging/THIRD-PARTY-NOTICES.txt" "$out/THIRD-PARTY-NOTICES.txt"
version="$("$out/bambu-bar" --version | awk '{print $3}')"
./scripts/package-flatpak.sh "$out" "$repo/build/linux/Bambu-Bar-$version-$rid.flatpak"
if command -v gnome-extensions >/dev/null; then ./scripts/package-gnome-extension.sh; fi
./scripts/package-plasma-widget.sh
