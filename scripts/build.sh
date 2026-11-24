#!/bin/zsh
# Builds Bambu Bar.app (the menu bar app plus its widget extension) with Xcode.
#   scripts/build.sh                     release build into build/App.noindex/Bambu Bar.app
#                                        (.noindex keeps build copies out of Spotlight and Launchpad)
#   CONFIG=Debug scripts/build.sh
#   ARCHS=arm64 scripts/build.sh        one architecture (default: universal)
#   SIGN_IDENTITY="Developer ID Application: …" scripts/build.sh
#                                        signing identity (default: the first Developer ID Application
#                                        identity in the keychain, else ad-hoc; the widget needs a real one)
#   TEAM_ID=XXXXXXXXXX scripts/build.sh  team for the app group shared with the widget (default: PCZ6F8Q9DZ)
set -euo pipefail

ROOT=${0:A:h:h}
CONFIG=${CONFIG:-Release}
TEAM_ID=${TEAM_ID:-PCZ6F8Q9DZ}
APP_GROUP="$TEAM_ID.dev.rolottr.BambuBar"
BUILD="$ROOT/build"
APP="$BUILD/App.noindex/Bambu Bar.app"
DERIVED="$BUILD/DerivedData.noindex"
cd "$ROOT"
mkdir -p "$BUILD"

# Use Xcode without changing the global developer directory (the widget needs the full SDK).
if [[ -z "${DEVELOPER_DIR:-}" ]] && ! xcodebuild -version >/dev/null 2>&1; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
"$ROOT/scripts/generate.sh"

echo "==> xcodebuild ($CONFIG)"
build_apps=("$DERIVED/Build/Products/$CONFIG/Bambu Bar.app" "$APP")
trap '"$ROOT/scripts/unregister-build-apps.sh" "${build_apps[@]}"' EXIT
xcodebuild -project BambuBar.xcodeproj -scheme BambuBar -configuration "$CONFIG" \
  -derivedDataPath "$DERIVED" -destination 'generic/platform=macOS' \
  DEVELOPMENT_TEAM="$TEAM_ID" CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
  CURRENT_PROJECT_VERSION="$(date +%Y%m%d%H%M)" ${ARCHS:+ARCHS=$ARCHS} \
  -quiet build 2>&1 | grep -v "^Failed frontend command" | grep -vE "^/Applications/Xcode.*swift-frontend" | grep -v "IDERunDestination" || true
[[ ${pipestatus[1]} -eq 0 ]] || { echo "xcodebuild failed" >&2; exit 1; }

rm -rf "$APP"
mkdir -p "${APP:h}"
ditto "$DERIVED/Build/Products/$CONFIG/Bambu Bar.app" "$APP"

# Sign inside-out: Sparkle and the widget (sandboxed) first, then the app. Entitlements get the concrete app group.
if [[ -z "${SIGN_IDENTITY:-}" ]]; then
  SIGN_IDENTITY=$(security find-identity -v -p codesigning | awk -F'"' '/Developer ID Application/ {print $2; exit}')
fi
ENT="$BUILD/entitlements"
mkdir -p "$ENT"
for f in BambuBar BambuBarWidget; do
  sed "s/\$(APP_GROUP)/$APP_GROUP/g" "$ROOT/Resources/Signing/$f.entitlements" > "$ENT/$f.entitlements"
done
# Sparkle's feed and public key go into this build only: the App Store build doesn't update itself.
/usr/libexec/PlistBuddy -c "Merge $ROOT/Resources/Sparkle-Info.plist" "$APP/Contents/Info.plist" >/dev/null

WIDGET="$APP/Contents/PlugIns/BambuBarWidget.appex"
SPARKLE="$APP/Contents/Frameworks/Sparkle.framework"
# The app isn't sandboxed, so Sparkle installs updates itself and its XPC services aren't needed.
rm -rf "$SPARKLE/Versions/B/XPCServices" "$SPARKLE/XPCServices"
if [[ -n "$SIGN_IDENTITY" ]]; then
  echo "==> Signing with $SIGN_IDENTITY"
  SIGN=(--force --options runtime --timestamp --sign "$SIGN_IDENTITY")
else
  echo "==> Ad-hoc signing (no Developer ID identity found; the widget won't load)"
  SIGN=(--force --sign -)
fi
codesign "${SIGN[@]}" "$SPARKLE/Versions/B/Autoupdate"
codesign "${SIGN[@]}" "$SPARKLE/Versions/B/Updater.app"
codesign "${SIGN[@]}" "$SPARKLE"
codesign "${SIGN[@]}" --entitlements "$ENT/BambuBarWidget.entitlements" "$WIDGET"
codesign "${SIGN[@]}" --entitlements "$ENT/BambuBar.entitlements" "$APP"
codesign --verify --strict --deep "$APP"
"$ROOT/scripts/unregister-build-apps.sh" "${build_apps[@]}"
trap - EXIT
echo "==> Built $APP"
"$ROOT/scripts/check-one-copy.sh" --allow-uninstalled dev.rolottr.BambuBar
