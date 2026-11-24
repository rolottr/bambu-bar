#!/bin/zsh
# Archives Bambu Bar for the Mac App Store: the sandboxed AppStore configuration, signed automatically by Xcode
# (it creates the App IDs, the App Store profiles and the distribution certificates it needs).
#   scripts/appstore.sh                  archive, then export a signed installer package into build/AppStore/
#   UPLOAD=1 scripts/appstore.sh         archive, then upload the build to App Store Connect (TestFlight / review)
#   BUILD_NUMBER=20261023.1 scripts/appstore.sh
#                                        build number (default: today's date and time, e.g. 20261023.1405);
#                                        every upload of a version needs a higher one
#   TEAM_ID=XXXXXXXXXX scripts/appstore.sh
# Xcode must be signed in to the team's Apple Account (Xcode → Settings → Accounts). The same archive can be
# made in Xcode instead: scripts/generate.sh, open BambuBar.xcodeproj, pick the "BambuBar App Store" scheme,
# Product → Archive, then Distribute App → App Store Connect in the Organizer.
set -euo pipefail

ROOT=${0:A:h:h}
TEAM_ID=${TEAM_ID:-PCZ6F8Q9DZ}
BUILD_NUMBER=${BUILD_NUMBER:-$(date +%Y%m%d.%H%M)}
OUT="$ROOT/build/AppStore"
ARCHIVE="$OUT/Archives.noindex/Bambu Bar.xcarchive"
APP="$ARCHIVE/Products/Applications/Bambu Bar.app"
DERIVED="$ROOT/build/DerivedData.noindex"
cd "$ROOT"

if [[ -z "${DEVELOPER_DIR:-}" ]] && ! xcodebuild -version >/dev/null 2>&1; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
"$ROOT/scripts/generate.sh"
mkdir -p "${ARCHIVE:h}"
legacy="$OUT/Bambu Bar.xcarchive"
if [[ -d "$legacy" ]]; then
  "$ROOT/scripts/unregister-build-apps.sh" "$legacy/Products/Applications/Bambu Bar.app"
  legacy_dir=$(mktemp -d "${ARCHIVE:h}/previous.XXXXXX")
  mv "$legacy" "$legacy_dir/"
fi
rm -rf "$ARCHIVE"
unregister_archive_apps() {
  local intermediates="$DERIVED/Build/Intermediates.noindex/ArchiveIntermediates/BambuBar App Store"
  "$ROOT/scripts/unregister-build-apps.sh" "$APP" \
    "$intermediates/InstallationBuildProductsLocation/Applications/Bambu Bar.app" \
    "$intermediates/BuildProductsPath/AppStore/Bambu Bar.app"
}
trap unregister_archive_apps EXIT

echo "==> Archiving (AppStore, build $BUILD_NUMBER)"
xcodebuild -project BambuBar.xcodeproj -scheme "BambuBar App Store" -configuration AppStore \
  -derivedDataPath "$DERIVED" -destination 'generic/platform=macOS' -archivePath "$ARCHIVE" \
  -allowProvisioningUpdates DEVELOPMENT_TEAM="$TEAM_ID" CURRENT_PROJECT_VERSION="$BUILD_NUMBER" \
  -quiet archive 2>&1 | grep -v "IDERunDestination" || true
[[ ${pipestatus[1]} -eq 0 && -d "$ARCHIVE" ]] || { echo "Archiving failed" >&2; exit 1; }

# App Review rejects a build whose app or extension isn't sandboxed; catch that before uploading.
for bundle in "$APP" "$APP/Contents/PlugIns/BambuBarWidget.appex"; do
  entitlements=$(codesign -d --entitlements - --xml "$bundle" 2>/dev/null | plutil -convert xml1 -o - - 2>/dev/null || true)
  [[ "$entitlements" == *"com.apple.security.app-sandbox</key>"$'\n'*"<true/>"* ]] \
    || { echo "${bundle:t} isn't sandboxed" >&2; exit 1; }
done
# Nor one that can update itself: Sparkle belongs to the GitHub build only.
if [[ -e "$APP/Contents/Frameworks/Sparkle.framework" ]] || otool -L "$APP/Contents/MacOS/Bambu Bar" | grep -q Sparkle \
   || plutil -extract SUFeedURL raw "$APP/Contents/Info.plist" >/dev/null 2>&1; then
  echo "Sparkle ended up in the App Store build" >&2; exit 1
fi
VERSION=$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")
echo "    Bambu Bar $VERSION ($BUILD_NUMBER), app and widget sandboxed, no updater"

DESTINATION=export
[[ -n "${UPLOAD:-}" ]] && DESTINATION=upload
cat > "$OUT/ExportOptions.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>method</key>
	<string>app-store-connect</string>
	<key>destination</key>
	<string>$DESTINATION</string>
	<key>teamID</key>
	<string>$TEAM_ID</string>
	<key>signingStyle</key>
	<string>automatic</string>
	<key>manageAppVersionAndBuildNumber</key>
	<false/>
	<key>uploadSymbols</key>
	<true/>
</dict>
</plist>
PLIST

if [[ $DESTINATION == upload ]]; then
  echo "==> Uploading to App Store Connect"
else
  echo "==> Exporting the App Store package"
fi
xcodebuild -exportArchive -archivePath "$ARCHIVE" -exportPath "$OUT/Export" \
  -exportOptionsPlist "$OUT/ExportOptions.plist" -allowProvisioningUpdates -quiet
unregister_archive_apps
trap - EXIT
if [[ $DESTINATION == upload ]]; then
  echo "==> Uploaded $VERSION ($BUILD_NUMBER). It shows up in App Store Connect → TestFlight once processed."
else
  echo "==> Exported $(ls "$OUT/Export"/*.pkg)"
  echo "    Upload it with UPLOAD=1 scripts/appstore.sh, the Organizer, or the Transporter app."
fi
"$ROOT/scripts/check-one-copy.sh" --allow-uninstalled dev.rolottr.BambuBar
