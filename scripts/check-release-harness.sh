#!/bin/zsh
# Refuse a release build that leaks /Users/ build paths or contains DEBUG-only harness symbols.
#   scripts/check-release-harness.sh <app bundle or executable>
set -euo pipefail

TARGET=${1:?Usage: scripts/check-release-harness.sh <app bundle or executable>}
BINARIES=()
if [[ -d "$TARGET" && -f "$TARGET/Contents/MacOS/Bambu Bar" ]]; then
  BINARIES+=("$TARGET/Contents/MacOS/Bambu Bar")
  WIDGET_BIN="$TARGET/Contents/PlugIns/BambuBarWidget.appex/Contents/MacOS/BambuBarWidget"
  [[ -f "$WIDGET_BIN" ]] && BINARIES+=("$WIDGET_BIN")
else
  BINARIES+=("$TARGET")
fi

FORBIDDEN_SYMBOLS=(
  'BAMBU_BAR_LICENSE_BYPASS'
  'bypassForDevelopment'
  '--selftest'
  '--license-probe'
  '--notify-probe'
  '--perf-probe'
  '--e2e'
  'E2ERunner'
  'SelfTest'
  'LicenseProbe'
  'NotificationProbe'
  'PerfProbe'
  'polar_oat_'
  'polar_at_'
)

CHECK_DIR=$(mktemp -d "${TMPDIR:-/tmp}/bambubar-harness.XXXXXX")
trap 'rm -rf "$CHECK_DIR"' EXIT

for BINARY in "${BINARIES[@]}"; do
  [[ -f "$BINARY" ]] || { echo "Executable not found: $BINARY" >&2; exit 1; }
  ARCH_LIST=$(lipo -archs "$BINARY" 2>/dev/null) || { echo "Not a Mach-O executable: $BINARY" >&2; exit 1; }
  ARCHITECTURES=(${=ARCH_LIST})
  (( ${#ARCHITECTURES} > 0 )) || { echo "No executable architectures found in $BINARY" >&2; exit 1; }

  for arch in "${ARCHITECTURES[@]}"; do
    SLICE="$CHECK_DIR/${BINARY:t}-$arch"
    if (( ${#ARCHITECTURES} == 1 )); then
      cp "$BINARY" "$SLICE"
    else
      lipo "$BINARY" -thin "$arch" -output "$SLICE"
    fi

    FOUND=()
    for marker in "${FORBIDDEN_SYMBOLS[@]}"; do
      if grep -aqF -- "$marker" "$SLICE"; then
        FOUND+=("$marker")
      fi
    done
    if (( ${#FOUND} )); then
      echo "Release harness check failed (${BINARY:t} $arch): DEBUG-only symbols found: ${FOUND[*]}" >&2
      exit 1
    fi

    # Search raw bytes (catches linker debug-map entries in the symbol table that `strings` skips)
    # as well as printable strings for leaked /Users/ paths.
    RAW_PATHS=$(grep -aoE '/Users/[[:print:]]{3,}' "$SLICE" 2>/dev/null || true)
    STR_PATHS=$(strings "$SLICE" 2>/dev/null | grep -E '/Users/[[:print:]]{3,}' || true)
    if [[ -n "$RAW_PATHS" || -n "$STR_PATHS" ]]; then
      echo "Release harness check failed (${BINARY:t} $arch): leaked /Users/ path(s) (run strip -S? #filePath?):" >&2
      { print -r -- "$RAW_PATHS"; print -r -- "$STR_PATHS"; } | sed '/^$/d' | sort -u | head -10 | sed 's/^/  /' >&2
      exit 1
    fi
  done

  echo "Release harness verified: ${BINARY:t} ($ARCH_LIST)."
done
