#!/bin/zsh
# Render the README animations (.github/assets/*.webp) from the app itself, with a scripted demo printer:
# no personal data, no network, nothing on this Mac is touched. Needs img2webp (brew install webp).
set -euo pipefail

ROOT=${0:A:h:h}
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
cd "$ROOT"

APP="$ROOT/build/App.noindex/Bambu Bar.app"
BIN="$APP/Contents/MacOS/Bambu Bar"
[[ -x "$BIN" && "${1:-}" != "--build" ]] || CONFIG=Release "$ROOT/scripts/build.sh"
mkdir -p "$ROOT/.github/assets"
echo "==> Rendering frames"
"$BIN" --demo printing --fleet --readme-frames "$WORK/frames" >/dev/null

for name in showcase menubar widgets; do
  frames=("$WORK/frames/$name"/frame-*.png)
  echo "==> $name.webp (${#frames} frames at 15 fps)"
  args=()
  for i in {1..${#frames}}; do
    # 15 fps: alternate 67/66 ms so the total stays exact.
    args+=(-d $(( (i * 1000 + 7) / 15 - ((i - 1) * 1000 + 7) / 15 )) "${frames[$i]}")
  done
  # Every frame a keyframe (-kmax 1): with lossy sub-frames, img2webp treats small changes as unchanged, so a
  # crossfade between pages leaves the old page behind.
  img2webp -loop 0 -lossy -q 78 -m 4 -kmax 1 "${args[@]}" -o "$ROOT/.github/assets/$name.webp" >/dev/null
  ls -lh "$ROOT/.github/assets/$name.webp" | awk '{print "    " $5}'
done
"$ROOT/scripts/unregister-build-apps.sh" "$APP"
"$ROOT/scripts/check-one-copy.sh" --allow-uninstalled dev.rolottr.BambuBar
