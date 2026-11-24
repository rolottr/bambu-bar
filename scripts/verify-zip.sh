#!/bin/zsh
# Post-extraction verification of a packaged Bambu Bar release ZIP.
# Extracts to a temporary .noindex directory with ditto -x -k, verifies notarization, Gatekeeper,
# code signature, universal slices, Info.plist versions, updater, licensing, release harness, and
# localizations, then unregisters and deletes the temporary extraction on exit.
#   scripts/verify-zip.sh build/Bambu-Bar-<version>-macos.zip
#   scripts/verify-zip.sh --allow-unnotarized build/Bambu-Bar-<version>-macos.zip
set -euo pipefail

ROOT=${0:A:h:h}
ALLOW_UNNOTARIZED=0
if [[ "${1:-}" == "--allow-unnotarized" ]]; then
  ALLOW_UNNOTARIZED=1
  shift
fi

ZIP=${1:?Usage: scripts/verify-zip.sh [--allow-unnotarized] <zip>}
[[ -f "$ZIP" ]] || { echo "ZIP not found: $ZIP" >&2; exit 1; }

VERIFY_ROOT="$ROOT/build/Verify.noindex"
mkdir -p "$VERIFY_ROOT"
WORK_DIR=$(mktemp -d "$VERIFY_ROOT/verify.XXXXXX")
EXTRACTED_APP="$WORK_DIR/Bambu Bar.app"
cleanup() {
  if [[ -d "$EXTRACTED_APP" ]]; then
    "$ROOT/scripts/unregister-build-apps.sh" "$EXTRACTED_APP" || true
  fi
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

fail() {
  echo "ZIP verification failed: $*" >&2
  exit 1
}

echo "==> Extracting $ZIP"
ditto -x -k "$ZIP" "$WORK_DIR"
[[ -d "$EXTRACTED_APP" ]] || fail "Bambu Bar.app not found inside $ZIP"

PLIST="$EXTRACTED_APP/Contents/Info.plist"
APP_BIN="$EXTRACTED_APP/Contents/MacOS/Bambu Bar"
WIDGET_BIN="$EXTRACTED_APP/Contents/PlugIns/BambuBarWidget.appex/Contents/MacOS/BambuBarWidget"

BUNDLE_ID=$(plutil -extract CFBundleIdentifier raw "$PLIST" 2>/dev/null) || fail "missing CFBundleIdentifier"
[[ "$BUNDLE_ID" == "dev.rolottr.BambuBar" ]] || fail "unexpected CFBundleIdentifier: $BUNDLE_ID"

VERSION=$(plutil -extract CFBundleShortVersionString raw "$PLIST" 2>/dev/null) || fail "missing CFBundleShortVersionString"
BUILD=$(plutil -extract CFBundleVersion raw "$PLIST" 2>/dev/null) || fail "missing CFBundleVersion"
[[ -n "$VERSION" && -n "$BUILD" ]] || fail "empty version ($VERSION) or build ($BUILD)"
echo "    Bundle: $BUNDLE_ID $VERSION ($BUILD)"

echo "==> Code signature"
codesign --verify --deep --strict "$EXTRACTED_APP" || fail "codesign --verify --deep --strict failed"

if (( ! ALLOW_UNNOTARIZED )); then
  echo "==> Notarization & Gatekeeper"
  SIG=$(codesign -dvv "$EXTRACTED_APP" 2>&1)
  [[ "$SIG" == *"Authority=Developer ID Application"* ]] || fail "missing Developer ID Application authority"
  [[ "$SIG" == *"TeamIdentifier=PCZ6F8Q9DZ"* ]] || fail "unexpected TeamIdentifier (expected PCZ6F8Q9DZ)"
  [[ "$SIG" == *"(runtime)"* ]] || fail "missing Hardened Runtime flag"
  xcrun stapler validate "$EXTRACTED_APP" || fail "stapler validate failed"
  SPCTL_OUT=$(spctl -a -vv "$EXTRACTED_APP" 2>&1) || fail "spctl assessment failed: $SPCTL_OUT"
  [[ "$SPCTL_OUT" == *"Notarized Developer ID"* ]] || fail "spctl did not report Notarized Developer ID: $SPCTL_OUT"
  echo "    $SPCTL_OUT"
else
  echo "==> Skipping stapler/spctl notarization checks (--allow-unnotarized)"
fi

echo "==> Universal architectures"
[[ -f "$APP_BIN" ]] || fail "app executable missing: $APP_BIN"
APP_ARCHS=$(lipo -archs "$APP_BIN")
[[ " $APP_ARCHS " == *" x86_64 "* && " $APP_ARCHS " == *" arm64 "* ]] \
  || fail "app executable is not universal (x86_64 arm64): $APP_ARCHS"
[[ -f "$WIDGET_BIN" ]] || fail "widget executable missing: $WIDGET_BIN"
WIDGET_ARCHS=$(lipo -archs "$WIDGET_BIN")
[[ " $WIDGET_ARCHS " == *" x86_64 "* && " $WIDGET_ARCHS " == *" arm64 "* ]] \
  || fail "widget executable is not universal (x86_64 arm64): $WIDGET_ARCHS"
echo "    App: $APP_ARCHS | Widget: $WIDGET_ARCHS"

echo "==> Localizations"
LPROJ_COUNT=$(find "$EXTRACTED_APP/Contents/Resources" -maxdepth 1 -type d -name '*.lproj' | wc -l | tr -d ' ')
(( LPROJ_COUNT >= 18 )) || fail "expected >= 18 .lproj directories, found $LPROJ_COUNT"
echo "    $LPROJ_COUNT .lproj bundles"

echo "==> Updater, licensing & release harness guards"
"$ROOT/scripts/check-updater.sh" "$EXTRACTED_APP"
"$ROOT/scripts/check-direct-licensing.sh" "$EXTRACTED_APP"
"$ROOT/scripts/check-release-harness.sh" "$EXTRACTED_APP"

echo "==> Verified $ZIP ($VERSION build $BUILD)"
