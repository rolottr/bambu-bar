#!/bin/zsh
# Refuse to distribute a direct build that omits Polar licensing markers or contains a DEBUG bypass
# or placeholder URLs/tokens.
#   scripts/check-direct-licensing.sh <app bundle or executable>
set -euo pipefail

TARGET=${1:?Usage: scripts/check-direct-licensing.sh <app bundle or executable>}
if [[ -d "$TARGET" && -f "$TARGET/Contents/MacOS/Bambu Bar" ]]; then
  BINARY="$TARGET/Contents/MacOS/Bambu Bar"
else
  BINARY="$TARGET"
fi

[[ -f "$BINARY" ]] || { echo "Executable not found: $BINARY" >&2; exit 1; }
ARCH_LIST=$(lipo -archs "$BINARY" 2>/dev/null) || { echo "Not a Mach-O executable: $BINARY" >&2; exit 1; }
ARCHITECTURES=(${=ARCH_LIST})
(( ${#ARCHITECTURES} > 0 )) || { echo "No executable architectures found in $BINARY" >&2; exit 1; }

REQUIRED_MARKERS=(
  'api.polar.sh'
  'LicenseStore'
  '3560f84c-991a-4d50-9b5d-1009584204dd'
  'dev.rolottr.BambuBar.polar'
)

FORBIDDEN_MARKERS=(
  'BAMBU_BAR_LICENSE_BYPASS'
  'sandbox-api.polar.sh'
  'polar_cl_placeholder'
  'YOUR_BENEFIT_ID'
  'PLACEHOLDER_BENEFIT'
  'example.com'
  'polar_oat_'
  'polar_at_'
)

CHECK_DIR=$(mktemp -d "${TMPDIR:-/tmp}/bambubar-licensing.XXXXXX")
trap 'rm -rf "$CHECK_DIR"' EXIT

for arch in "${ARCHITECTURES[@]}"; do
  SLICE="$CHECK_DIR/$arch"
  if (( ${#ARCHITECTURES} == 1 )); then
    cp "$BINARY" "$SLICE"
  else
    lipo "$BINARY" -thin "$arch" -output "$SLICE"
  fi

  for marker in "${REQUIRED_MARKERS[@]}"; do
    if ! grep -aqF -- "$marker" "$SLICE"; then
      echo "Direct licensing check failed ($arch): missing required marker \"$marker\"" >&2
      exit 1
    fi
  done

  for marker in "${FORBIDDEN_MARKERS[@]}"; do
    if grep -aqF -- "$marker" "$SLICE"; then
      echo "Direct licensing check failed ($arch): forbidden marker \"$marker\" found in binary" >&2
      exit 1
    fi
  done

  echo "Direct licensing verified ($arch)."
done
