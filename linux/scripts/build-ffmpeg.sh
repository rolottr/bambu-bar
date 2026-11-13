#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
pins="$repo/windows/ffmpeg/version.txt"
read_pin() { sed -n "s/^$1=//p" "$pins"; }
version="$(read_pin ffmpeg-version)"
work="${BAMBUBAR_FFMPEG_WORK:-${XDG_CACHE_HOME:-$HOME/.cache}/bambu-bar/ffmpeg-$version}"
out="$(mkdir -p "${1:?usage: build-ffmpeg.sh <output-folder>}" && cd "$1" && pwd)"
mkdir -p "$work"
archive="$work/ffmpeg.tar.xz"
if [ ! -f "$archive" ]; then curl -fL --retry 3 "$(read_pin ffmpeg-url)" -o "$archive"; fi
printf '%s  %s\n' "$(read_pin ffmpeg-sha256)" "$archive" | sha256sum -c -
if [ ! -d "$work/ffmpeg-$version" ]; then tar -xf "$archive" -C "$work"; fi
cd "$work/ffmpeg-$version"
if [ ! -f ffbuild/config.mak ]; then
  ./configure --prefix="$work/install" --enable-small --disable-everything --disable-autodetect --enable-pthreads --enable-shared --disable-static --disable-debug --disable-programs --disable-doc --disable-network --disable-avformat --disable-avdevice --disable-avfilter --disable-swresample --enable-swscale --enable-decoder=h264,hevc,mjpeg --extra-ldflags='-Wl,-rpath,$ORIGIN'
fi
make -j"$(nproc)"
make install
cp -a "$work/install/lib/"*.so* "$out/"
cp COPYING.LGPLv2.1 "$out/LICENSE.txt"
printf 'FFmpeg %s, unmodified LGPL-2.1-or-later shared libraries.\nSource: %s\nBuild recipe: linux/scripts/build-ffmpeg.sh\n' "$version" "$(read_pin ffmpeg-url)" > "$out/SOURCE.txt"
