#!/bin/zsh
# Renders the Mac App Store screenshots and app preview from the app itself, with the scripted demo printers
# the README animations use: no personal data, no network, nothing on this Mac is touched.
#   scripts/appstore-media.sh                      into build/AppStore/Media (builds the app first if needed)
#   scripts/appstore-media.sh --screenshots-only   skips the preview (it takes a few minutes)
# Screenshots: 2880×1800 PNG without alpha. Preview: 1920×1080, 30 fps, 29 s, H.264 at ~11 Mbps with a silent
# stereo AAC track, as App Store Connect asks for. Needs ffmpeg (brew install ffmpeg).
set -euo pipefail

ROOT=${0:A:h:h}
OUT="$ROOT/build/AppStore/Media"
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
APP="$ROOT/build/App.noindex/Bambu Bar.app"
BIN="$APP/Contents/MacOS/Bambu Bar"

[[ -x "$BIN" ]] || "$ROOT/scripts/build.sh"
echo "==> Rendering"
"$BIN" --demo printing --fleet --store-media "$WORK" "$@" >/dev/null
rm -rf "$OUT"
mkdir -p "$OUT"
mv "$WORK/screenshots" "$OUT/Screenshots"
if [[ -d "$WORK/preview" ]]; then
  echo "==> Encoding the preview"
  # CBR so the bit rate stays in Apple's 10-12 Mbps; the silent track satisfies the audio spec.
  ffmpeg -loglevel error -y -framerate 30 -i "$WORK/preview/frame-%05d.png" \
    -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 -map 0:v -map 1:a -shortest \
    -c:v libx264 -profile:v high -level:v 4.0 -pix_fmt yuv420p -r 30 \
    -b:v 11M -minrate 11M -maxrate 11M -bufsize 11M -x264-params nal-hrd=cbr \
    -c:a aac -b:a 256k -ar 48000 -movflags +faststart "$OUT/Bambu Bar Preview.mp4"
fi
echo "==> $OUT"
ls -1 "$OUT" "$OUT/Screenshots"
"$ROOT/scripts/unregister-build-apps.sh" "$APP"
"$ROOT/scripts/check-one-copy.sh" --allow-uninstalled dev.rolottr.BambuBar
