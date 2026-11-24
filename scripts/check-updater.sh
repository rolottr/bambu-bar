#!/bin/zsh
# Refuse a direct-download build whose Sparkle updater cannot work:
#   - Sparkle.framework, Autoupdate, and Updater.app embedded (XPCServices removed; direct app is not sandboxed)
#   - Every Mach-O slice links @rpath/Sparkle.framework and carries @executable_path/../Frameworks rpath
#   - SUFeedURL == https://bambubar.app/appcast.xml
#   - SUPublicEDKey base64-decodes to a 32-byte Ed25519 key
#   - SUEnableAutomaticChecks == true
#   - codesign --verify --strict passes on Sparkle.framework, Autoupdate, and Updater.app
# Usage: scripts/check-updater.sh "build/App.noindex/Bambu Bar.app"
set -euo pipefail

APP=${1:?Pass the .app bundle}
BINARY="$APP/Contents/MacOS/Bambu Bar"
SPARKLE="$APP/Contents/Frameworks/Sparkle.framework"
PLIST="$APP/Contents/Info.plist"
EXPECTED_FEED="https://bambubar.app/appcast.xml"

fail() {
  echo "Updater check failed: $*" >&2
  exit 1
}

[[ -d "$APP" ]] || fail "app bundle not found: $APP"
[[ -f "$BINARY" ]] || fail "executable not found: $BINARY"
[[ -f "$PLIST" ]] || fail "Info.plist not found: $PLIST"
[[ -f "$SPARKLE/Versions/B/Sparkle" ]] || fail "Sparkle.framework is missing from Contents/Frameworks"
[[ -f "$SPARKLE/Versions/B/Autoupdate" ]] || fail "Sparkle.framework lacks Autoupdate"
[[ -d "$SPARKLE/Versions/B/Updater.app" ]] || fail "Sparkle.framework lacks Updater.app"
[[ ! -e "$SPARKLE/Versions/B/XPCServices" && ! -L "$SPARKLE/XPCServices" ]] \
  || fail "Sparkle XPCServices should be removed (direct Bambu Bar is not sandboxed)"

FRAMEWORK_ARCHS=" $(lipo -archs "$SPARKLE/Versions/B/Sparkle") "
APP_ARCHS=(${=$(lipo -archs "$BINARY")})
(( ${#APP_ARCHS} > 0 )) || fail "no architectures found in $BINARY"

for arch in "${APP_ARCHS[@]}"; do
  otool -arch "$arch" -L "$BINARY" | grep -qF "@rpath/Sparkle.framework/Versions/B/Sparkle" \
    || fail "the $arch executable does not link @rpath/Sparkle.framework/Versions/B/Sparkle"
  otool -arch "$arch" -l "$BINARY" | grep -A2 LC_RPATH | grep -qF "path @executable_path/../Frameworks " \
    || fail "the $arch executable lacks the @executable_path/../Frameworks rpath"
  [[ "$FRAMEWORK_ARCHS" == *" $arch "* ]] \
    || fail "Sparkle.framework has no $arch slice"
done

FEED=$(plutil -extract SUFeedURL raw "$PLIST" 2>/dev/null) || fail "Info.plist has no SUFeedURL"
[[ "$FEED" == "$EXPECTED_FEED" ]] \
  || fail "SUFeedURL must be $EXPECTED_FEED (got: $FEED)"

KEY=$(plutil -extract SUPublicEDKey raw "$PLIST" 2>/dev/null) || fail "Info.plist has no SUPublicEDKey"
KEY_BYTES=$(print -rn -- "$KEY" | base64 -D 2>/dev/null | wc -c | tr -d ' ')
[[ "$KEY_BYTES" == "32" ]] \
  || fail "SUPublicEDKey must base64-decode to 32 bytes (got $KEY_BYTES bytes): $KEY"

AUTO_CHECKS=$(plutil -extract SUEnableAutomaticChecks raw "$PLIST" 2>/dev/null) \
  || fail "Info.plist has no SUEnableAutomaticChecks"
[[ "$AUTO_CHECKS" == "true" || "$AUTO_CHECKS" == "1" ]] \
  || fail "SUEnableAutomaticChecks must be true (got: $AUTO_CHECKS)"

codesign --verify --strict "$SPARKLE/Versions/B/Autoupdate" \
  || fail "Sparkle Autoupdate signature does not verify"
codesign --verify --strict "$SPARKLE/Versions/B/Updater.app" \
  || fail "Sparkle Updater.app signature does not verify"
codesign --verify --strict "$SPARKLE" \
  || fail "Sparkle.framework signature does not verify"

SPARKLE_VERSION=$(plutil -extract CFBundleShortVersionString raw "$SPARKLE/Resources/Info.plist" 2>/dev/null || echo "unknown")
echo "Updater verified: Sparkle $SPARKLE_VERSION, feed $FEED, automatic checks enabled."
