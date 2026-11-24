#!/bin/zsh
# Self-test for scripts/check-direct-licensing.sh against real Mach-O slices without contacting Polar.
#   scripts/test-direct-licensing.sh [app bundle or executable]
# When a built Bambu Bar.app exists (or is passed as $1), tests run against its real executable;
# otherwise a temporary Mach-O fixture containing the required Polar markers is compiled with clang.
set -euo pipefail

ROOT=${0:A:h:h}
CHECK="$ROOT/scripts/check-direct-licensing.sh"
DEFAULT_BINARY="$ROOT/build/App.noindex/Bambu Bar.app/Contents/MacOS/Bambu Bar"
TEST_DIR=$(mktemp -d "${TMPDIR:-/tmp}/bambubar-licensing-test.XXXXXX")
trap 'rm -rf "$TEST_DIR"' EXIT

expect_rejection() {
  if "$@" > "$TEST_DIR/rejection.log" 2>&1; then
    echo "Expected rejection but command succeeded: $*" >&2
    exit 1
  fi
}

BINARY=""
if [[ $# -ge 1 ]]; then
  if [[ -d "$1" && -f "$1/Contents/MacOS/Bambu Bar" ]]; then
    BINARY="$1/Contents/MacOS/Bambu Bar"
  else
    BINARY="$1"
  fi
elif [[ -f "$DEFAULT_BINARY" ]]; then
  BINARY="$DEFAULT_BINARY"
fi

if [[ -z "$BINARY" ]]; then
  cat > "$TEST_DIR/licensed.c" <<'EOF'
#include <stdio.h>
static const char *markers[] = {
    "https://api.polar.sh/v1/customer-portal/license-keys/",
    "LicenseStore",
    "3560f84c-991a-4d50-9b5d-1009584204dd",
    "dev.rolottr.BambuBar.polar",
};
int main(void) {
    return (int)(markers[0][0] + markers[1][0] + markers[2][0] + markers[3][0]) == 0;
}
EOF
  HOST_ARCH=$(uname -m)
  if xcrun clang -arch arm64 -arch x86_64 "$TEST_DIR/licensed.c" -o "$TEST_DIR/licensed-fixture" 2>/dev/null; then
    BINARY="$TEST_DIR/licensed-fixture"
  else
    xcrun clang -arch "$HOST_ARCH" "$TEST_DIR/licensed.c" -o "$TEST_DIR/licensed-fixture"
    BINARY="$TEST_DIR/licensed-fixture"
  fi
fi

# 1. Valid licensed binary passes.
"$CHECK" "$BINARY"

ARCHS=(${=$(lipo -archs "$BINARY")})
for arch in "${ARCHS[@]}"; do
  SLICE="$TEST_DIR/licensed-$arch"
  if (( ${#ARCHS} == 1 )); then
    cp "$BINARY" "$SLICE"
  else
    lipo "$BINARY" -thin "$arch" -output "$SLICE"
  fi
  "$CHECK" "$SLICE"

  # 2. Appended DEBUG bypass string must be rejected.
  cp "$SLICE" "$TEST_DIR/bypass-$arch"
  print -n 'BAMBU_BAR_LICENSE_BYPASS' >> "$TEST_DIR/bypass-$arch"
  expect_rejection "$CHECK" "$TEST_DIR/bypass-$arch"

  # 3. Appended placeholder / sandbox URL must be rejected.
  cp "$SLICE" "$TEST_DIR/sandbox-$arch"
  print -n 'https://sandbox-api.polar.sh/v1/customer-portal/license-keys/' >> "$TEST_DIR/sandbox-$arch"
  expect_rejection "$CHECK" "$TEST_DIR/sandbox-$arch"

  cp "$SLICE" "$TEST_DIR/placeholder-$arch"
  print -n 'https://example.com/polar_cl_placeholder' >> "$TEST_DIR/placeholder-$arch"
  expect_rejection "$CHECK" "$TEST_DIR/placeholder-$arch"
done

# 4. Unlicensed Mach-O binary must be rejected.
echo 'int main(void) { return 0; }' > "$TEST_DIR/unlicensed.c"
if xcrun clang -arch x86_64 "$TEST_DIR/unlicensed.c" -o "$TEST_DIR/unlicensed-x86_64" 2>/dev/null; then
  expect_rejection "$CHECK" "$TEST_DIR/unlicensed-x86_64"
  # 5. Mixed universal binary (one licensed slice + one unlicensed slice) must be rejected.
  if [[ -f "$TEST_DIR/licensed-arm64" ]]; then
    lipo -create "$TEST_DIR/licensed-arm64" "$TEST_DIR/unlicensed-x86_64" -output "$TEST_DIR/mixed"
    expect_rejection "$CHECK" "$TEST_DIR/mixed"
  fi
else
  xcrun clang "$TEST_DIR/unlicensed.c" -o "$TEST_DIR/unlicensed"
  expect_rejection "$CHECK" "$TEST_DIR/unlicensed"
fi

# 6. Non-Mach-O file must be rejected.
expect_rejection "$CHECK" "$TEST_DIR/unlicensed.c"

# 7. Missing file must be rejected.
expect_rejection "$CHECK" "$TEST_DIR/missing"

echo "Direct licensing guard self-tests passed: valid slices accepted; bypass, placeholder URLs, unlicensed, mixed, non-Mach-O, and missing inputs rejected."
