#!/bin/zsh
# Build Bambu Bar for distribution: universal, Developer ID signed (PCZ6F8Q9DZ), optionally notarized,
# zipped with SHA-256 checksum, verified post-extraction, Sparkle EdDSA-signed, and recorded in .release.txt.
#   NOTARY_PROFILE=notary scripts/package.sh
#     builds, signs with Developer ID (PCZ6F8Q9DZ), notarizes, staples, verifies the extracted ZIP,
#     signs the ZIP with the shared Sparkle EdDSA key, writes .release.txt, and reveals the ZIP in Finder.
#   SIGN_IDENTITY="Developer ID Application: … (PCZ6F8Q9DZ)" overrides identity selection.
#   ALLOW_ADHOC=1 NO_REVEAL=1 scripts/package.sh   for unsigned CI / local dry runs only.
set -euo pipefail

ROOT=${0:A:h:h}
TEAM_ID="PCZ6F8Q9DZ"

# Fail fast on signing identity before starting the universal build.
if [[ -n "${SIGN_IDENTITY:-}" ]]; then
  if [[ "$SIGN_IDENTITY" != *"$TEAM_ID"* ]]; then
    echo "Refusing to package: SIGN_IDENTITY must belong to team $TEAM_ID (got: $SIGN_IDENTITY)." >&2
    exit 2
  fi
elif [[ "${ALLOW_ADHOC:-0}" != 1 ]]; then
  SIGN_IDENTITY=$(security find-identity -v -p codesigning 2>/dev/null \
    | sed -nE "s/.*\"(Developer ID Application: [^\"]*${TEAM_ID}[^\"]*)\".*/\1/p" | head -n 1)
  if [[ -z "$SIGN_IDENTITY" ]]; then
    echo "Refusing to package: no Developer ID Application ($TEAM_ID) identity found in keychain." >&2
    echo "Set SIGN_IDENTITY or pass ALLOW_ADHOC=1 for an unsigned CI/local dry run." >&2
    exit 2
  fi
  export SIGN_IDENTITY
else
  SIGN_IDENTITY=""
  export SIGN_IDENTITY
fi

# Fail fast if NOTARY_PROFILE is set but missing or unreadable.
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  [[ -n "${SIGN_IDENTITY:-}" && "$SIGN_IDENTITY" == *"$TEAM_ID"* ]] \
    || { echo "NOTARY_PROFILE requires a Developer ID ($TEAM_ID) SIGN_IDENTITY." >&2; exit 2; }
  if ! xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" >/dev/null 2>&1; then
    echo "Refusing to package: notarytool profile \"$NOTARY_PROFILE\" failed 'notarytool history'." >&2
    echo "Store credentials with: xcrun notarytool store-credentials $NOTARY_PROFILE --apple-id <email> --team-id $TEAM_ID" >&2
    exit 2
  fi
fi

CONFIG=Release ARCH=universal "$ROOT/scripts/build.sh"
APP="$ROOT/build/App.noindex/Bambu Bar.app"
# The DerivedData product too: packaging can register it again after build.sh unregistered it (seen on CI), and
# check-one-copy.sh below would count it as a second copy.
BUILD_APPS=("$APP" "$ROOT/build/DerivedData.noindex/Build/Products/Release/Bambu Bar.app")
trap '"$ROOT/scripts/unregister-build-apps.sh" "${BUILD_APPS[@]}" || true' EXIT
VERSION=$(plutil -extract CFBundleShortVersionString raw "$APP/Contents/Info.plist")
BUILD=$(plutil -extract CFBundleVersion raw "$APP/Contents/Info.plist")
ARCHIVE="Bambu-Bar-$VERSION-macos.zip"
ZIP="$ROOT/build/$ARCHIVE"
RELEASE_TXT="$ROOT/build/Bambu-Bar-$VERSION-macos.release.txt"

cd "$ROOT/build"
rm -f "$ARCHIVE" "$ARCHIVE.sha256" "$RELEASE_TXT"

NOTARIZED=0
if [[ -n "${NOTARY_PROFILE:-}" ]]; then
  SIG=$(codesign -dvv "$APP" 2>&1)
  [[ "$SIG" == *"Authority=Developer ID Application"* && "$SIG" == *"TeamIdentifier=$TEAM_ID"* && "$SIG" == *"(runtime)"* ]] \
    || { echo "Notarizing needs a Developer ID ($TEAM_ID) Hardened Runtime signature." >&2; exit 2; }
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
  NOTARIZED=1
else
  echo "==> Skipping notarization (NOTARY_PROFILE is not set)"
fi

ditto -c -k --sequesterRsrc --keepParent "$APP" "$ARCHIVE"
shasum -a 256 "$ARCHIVE" > "$ARCHIVE.sha256"
SHA256=$(awk '{print $1}' "$ARCHIVE.sha256")
SIZE=$(stat -f%z "$ARCHIVE")

echo "==> Verifying packaged ZIP post-extraction"
if (( NOTARIZED )); then
  "$ROOT/scripts/verify-zip.sh" "$ZIP"
else
  "$ROOT/scripts/verify-zip.sh" --allow-unnotarized "$ZIP"
fi

if (( NOTARIZED )); then
  echo "==> Signing ZIP for Sparkle (shared EdDSA key)"
  SPARKLE_BIN=""
  for candidate in \
    "$ROOT/build/DerivedData.noindex/SourcePackages/artifacts/sparkle/Sparkle/bin" \
    "$ROOT/.build/artifacts/sparkle/Sparkle/bin"; do
    if [[ -x "$candidate/sign_update" ]]; then
      SPARKLE_BIN="$candidate"
      break
    fi
  done
  [[ -n "$SPARKLE_BIN" ]] || { echo "Sparkle sign_update not found in DerivedData or .build" >&2; exit 1; }

  # Shared Clever Mechanisms EdDSA key in login keychain: never pass --account.
  ED_SIGNATURE=$("$SPARKLE_BIN/sign_update" -p "$ZIP")
  "$SPARKLE_BIN/sign_update" --verify "$ZIP" "$ED_SIGNATURE" \
    || { echo "Sparkle EdDSA signature verification failed for $ZIP" >&2; exit 1; }
  if [[ -x "$SPARKLE_BIN/generate_keys" ]]; then
    PUBLIC_KEY=$("$SPARKLE_BIN/generate_keys" -p)
    APP_KEY=$(plutil -extract SUPublicEDKey raw "$APP/Contents/Info.plist")
    [[ "$PUBLIC_KEY" == "$APP_KEY" ]] \
      || { echo "Keychain EdDSA public key ($PUBLIC_KEY) does not match SUPublicEDKey ($APP_KEY)" >&2; exit 1; }
  fi

  FEED_URL="https://bambubar.app/appcast.xml"
  DOWNLOAD_URL="https://bambubar.app/api/updates/download?version=$VERSION"
  cat > "$RELEASE_TXT" <<EOF
version: $VERSION
build: $BUILD
zipPath: $ZIP
sizeBytes: $SIZE
sha256: $SHA256
edSignature: $ED_SIGNATURE
feedURL: $FEED_URL
downloadURL: $DOWNLOAD_URL
EOF
  echo "==> Wrote $RELEASE_TXT"
  sed 's/^/    /' "$RELEASE_TXT"
fi

echo "==> Packaged $ZIP ($SIZE bytes, sha256 $SHA256)"
cat "$ARCHIVE.sha256"

if [[ "${NO_REVEAL:-0}" != 1 && -z "${CI:-}" ]]; then
  open -R "$ZIP" || true
fi

"$ROOT/scripts/unregister-build-apps.sh" "${BUILD_APPS[@]}"
trap - EXIT
"$ROOT/scripts/check-one-copy.sh" --allow-uninstalled dev.rolottr.BambuBar
