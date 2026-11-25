#!/bin/zsh
# Builds Bambu Bar.app (the menu bar app plus its widget extension) with Xcode.
#   scripts/build.sh                     release build into build/App.noindex/Bambu Bar.app
#                                        (.noindex keeps build copies out of Spotlight and Launchpad)
#   CONFIG=Debug scripts/build.sh        (also accepts lowercase debug/release)
#   ARCHS=arm64 scripts/build.sh         one architecture (default: universal; ARCH=arm64|x86_64|universal also works)
#   SIGN_IDENTITY="Developer ID Application: …" scripts/build.sh
#                                        signing identity (default: the first Developer ID Application
#                                        identity in the keychain, else ad-hoc; pass SIGN_IDENTITY="" to force ad-hoc)
#   TEAM_ID=XXXXXXXXXX scripts/build.sh  team for the app group shared with the widget (default: PCZ6F8Q9DZ)
#   REQUIRE_THIRD_PARTY=1                fail if Resources/ThirdParty/ is missing (always fails if present and empty)
set -euo pipefail

ROOT=${0:A:h:h}
CONFIG=${CONFIG:-Release}
case "$CONFIG" in
  release|Release) CONFIG=Release ;;
  debug|Debug)     CONFIG=Debug ;;
  *) echo "CONFIG must be Release or Debug (got: $CONFIG)" >&2; exit 2 ;;
esac

if [[ -z "${ARCHS:-}" && -n "${ARCH:-}" ]]; then
  case "$ARCH" in
    universal) unset ARCHS ;;
    arm64|x86_64) ARCHS="$ARCH" ;;
    native) ARCHS="$(uname -m)" ;;
    *) echo "ARCH must be universal, native, arm64, or x86_64 (got: $ARCH)" >&2; exit 2 ;;
  esac
fi

TEAM_ID=${TEAM_ID:-PCZ6F8Q9DZ}
APP_GROUP="$TEAM_ID.dev.rolottr.BambuBar"
BUNDLE_ID="dev.rolottr.BambuBar"
WIDGET_BUNDLE_ID="dev.rolottr.BambuBar.Widget"
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

WIDGET="$APP/Contents/PlugIns/BambuBarWidget.appex"
SPARKLE="$APP/Contents/Frameworks/Sparkle.framework"

# Copy third-party notices into the app bundle. Fail fast if the directory exists and is empty,
# or if missing when REQUIRE_THIRD_PARTY=1 is enforced.
THIRD_PARTY_SRC="$ROOT/Resources/ThirdParty"
if [[ -d "$THIRD_PARTY_SRC" ]]; then
  if [[ -z "$(find "$THIRD_PARTY_SRC" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]; then
    echo "Resources/ThirdParty/ is empty; add third-party notices before building." >&2
    exit 1
  fi
  mkdir -p "$APP/Contents/Resources/ThirdParty"
  ditto "$THIRD_PARTY_SRC" "$APP/Contents/Resources/ThirdParty"
elif [[ "${REQUIRE_THIRD_PARTY:-0}" == 1 ]]; then
  echo "Resources/ThirdParty/ is missing (REQUIRE_THIRD_PARTY=1)." >&2
  exit 1
else
  echo "warning: Resources/ThirdParty/ not found; skipping third-party notices copy." >&2
fi

# Sparkle's feed and public key go into this build only: the App Store build doesn't update itself.
/usr/libexec/PlistBuddy -c "Merge $ROOT/Resources/Sparkle-Info.plist" "$APP/Contents/Info.plist" >/dev/null

# The app isn't sandboxed, so Sparkle installs updates itself and its XPC services aren't needed.
rm -rf "$SPARKLE/Versions/B/XPCServices" "$SPARKLE/XPCServices"

# Release: strip debug-map symbols (-S) before signing to drop /Users/ build paths while keeping crash symbols.
if [[ "$CONFIG" == Release ]]; then
  echo "==> Stripping debug map from release binaries"
  strip -S "$APP/Contents/MacOS/Bambu Bar"
  [[ -f "$WIDGET/Contents/MacOS/BambuBarWidget" ]] && strip -S "$WIDGET/Contents/MacOS/BambuBarWidget"
fi

# Extended attributes have no place in a signed bundle.
xattr -cr "$APP"

# Sign inside-out: Sparkle and the widget (sandboxed) first, then the app. Entitlements get the concrete app group.
if [[ -z "${SIGN_IDENTITY+x}" ]]; then
  SIGN_IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null | awk -F'"' '/Developer ID Application/ {print $2; exit}')
fi
ENT="$BUILD/entitlements"
mkdir -p "$ENT"
for f in BambuBar BambuBarWidget; do
  sed "s/\$(APP_GROUP)/$APP_GROUP/g" "$ROOT/Resources/Signing/$f.entitlements" > "$ENT/$f.entitlements"
done

if [[ -n "${SIGN_IDENTITY:-}" && "$SIGN_IDENTITY" != "-" ]]; then
  echo "==> Signing with $SIGN_IDENTITY"
  SIGN=(--force --options runtime --timestamp --sign "$SIGN_IDENTITY")
  codesign "${SIGN[@]}" "$SPARKLE/Versions/B/Autoupdate"
  codesign "${SIGN[@]}" "$SPARKLE/Versions/B/Updater.app"
  codesign "${SIGN[@]}" "$SPARKLE"
  codesign "${SIGN[@]}" --entitlements "$ENT/BambuBarWidget.entitlements" "$WIDGET"
  codesign "${SIGN[@]}" --entitlements "$ENT/BambuBar.entitlements" "$APP"
else
  echo "==> Ad-hoc signing with stable designated requirements (the widget won't load without Developer ID)"
  codesign --force --sign - "$SPARKLE/Versions/B/Autoupdate"
  codesign --force --sign - "$SPARKLE/Versions/B/Updater.app"
  codesign --force --sign - "$SPARKLE"
  codesign --force --sign - --identifier "$WIDGET_BUNDLE_ID" \
    -r='designated => identifier "dev.rolottr.BambuBar.Widget"' \
    --entitlements "$ENT/BambuBarWidget.entitlements" "$WIDGET"
  codesign --force --sign - --identifier "$BUNDLE_ID" \
    -r='designated => identifier "dev.rolottr.BambuBar"' \
    --entitlements "$ENT/BambuBar.entitlements" "$APP"
fi

codesign --verify --strict --deep "$APP"

if [[ "$CONFIG" == Release ]]; then
  echo "==> Release guards"
  for guard_script in check-updater.sh check-direct-licensing.sh check-release-harness.sh; do
    guard_path="$ROOT/scripts/$guard_script"
    [[ -x "$guard_path" ]] || { echo "Required release guard missing or not executable: $guard_path" >&2; exit 1; }
    "$guard_path" "$APP"
  done
fi

"$ROOT/scripts/unregister-build-apps.sh" "${build_apps[@]}"
trap - EXIT
echo "==> Built $APP ($CONFIG)"
"$ROOT/scripts/check-one-copy.sh" --allow-uninstalled "$BUNDLE_ID"
