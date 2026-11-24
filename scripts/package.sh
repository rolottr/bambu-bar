#!/bin/zsh
# Build Bambu Bar for distribution: a universal, Developer ID signed app, zipped with a SHA-256 checksum.
#   scripts/package.sh
#   NOTARY_PROFILE=<profile> scripts/package.sh
#     also notarizes the app with Apple and staples the ticket before zipping it. The profile is a
#     notarytool keychain profile (xcrun notarytool store-credentials <profile> …).
#   SIGN_IDENTITY="Developer ID Application: …" overrides the signing identity (see build.sh).
set -euo pipefail

ROOT=${0:A:h:h}
CONFIG=Release "$ROOT/scripts/build.sh"
APP="$ROOT/build/App.noindex/Bambu Bar.app"
VERSION=$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")
ARCHIVE="Bambu-Bar-$VERSION-macos.zip"
cd "$ROOT/build"
rm -f "$ARCHIVE" "$ARCHIVE.sha256"

if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  # Notarization needs a Developer ID signature with the Hardened Runtime (build.sh applies both).
  [[ "$(codesign -dvv "$APP" 2>&1)" == *"Authority=Developer ID Application"* ]] \
    || { echo "Notarizing needs a Developer ID signed build (set SIGN_IDENTITY)" >&2; exit 2; }
  echo "==> Notarizing (this takes a few minutes)"
  ditto -c -k --sequesterRsrc --keepParent "$APP" notarize.zip
  xcrun notarytool submit notarize.zip --keychain-profile "$NOTARY_PROFILE" \
    --wait --output-format json > notarize.json || true
  rm -f notarize.zip
  ID=$(plutil -extract id raw notarize.json)
  STATUS=$(plutil -extract status raw notarize.json)
  xcrun notarytool log "$ID" --keychain-profile "$NOTARY_PROFILE" notarize-log.json >/dev/null || true
  echo "    submission $ID: $STATUS (log: build/notarize-log.json)"
  [[ "$STATUS" == Accepted ]] || exit 1
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  spctl --assess --type execute --verbose=2 "$APP"
fi

ditto -c -k --sequesterRsrc --keepParent "$APP" "$ARCHIVE"
shasum -a 256 "$ARCHIVE" > "$ARCHIVE.sha256"
echo "==> Packaged $ROOT/build/$ARCHIVE"
"$ROOT/scripts/check-one-copy.sh" --allow-uninstalled dev.rolottr.BambuBar
