#!/bin/zsh
# Writes build/appcast.xml, the feed Sparkle reads for in-app updates, for a packaged and published release.
#   scripts/appcast.sh               the version in build/App.noindex (run after package.sh and gh release create)
#   VERSION=1.4.0 scripts/appcast.sh
#   UPLOAD=1 scripts/appcast.sh      also attaches appcast.xml to the GitHub release. The app reads
#                                    releases/latest/download/appcast.xml, so the newest release's copy is the feed.
#   NOTES=notes.md scripts/appcast.sh
#                                    release notes from a Markdown file instead of the GitHub release's text
#   DOWNLOAD_URL=http://… scripts/appcast.sh
#                                    another download location for the zip, to try an update locally
# Windows: when build/Bambu-Bar-$VERSION-windows-x64.exe and -arm64.exe are there (the CI installers, downloaded),
# each gets an item of its own, signed with the same key, with sparkle:os="windows-x64" / "windows-arm64" on its
# enclosure. Sparkle on the Mac keeps only items without sparkle:os (or "macos"), so it never sees them; the Windows
# app reads only its own architecture's (windows/src/BambuBar.Core/Updates/Appcast.cs). Both must be there, or neither.
# The zip is signed with the EdDSA key in the login keychain (Sparkle's generate_keys made it; its public half is
# SUPublicEDKey in Info.plist). Keep a backup: generate_keys -x <file> exports it, -f <file> imports it again.
set -euo pipefail

ROOT=${0:A:h:h}
REPO=rolottr/bambu-bar
SPARKLE_BIN="$ROOT/build/DerivedData.noindex/SourcePackages/artifacts/sparkle/Sparkle/bin"
[[ -x "$SPARKLE_BIN/sign_update" ]] || { echo "Sparkle's tools are missing: run scripts/build.sh first" >&2; exit 2; }

VERSION=${VERSION:-$(plutil -extract CFBundleShortVersionString raw "$ROOT/build/App.noindex/Bambu Bar.app/Contents/Info.plist")}
ZIP="$ROOT/build/Bambu-Bar-$VERSION-macos.zip"
[[ -f "$ZIP" ]] || { echo "$ZIP is missing: run scripts/package.sh first" >&2; exit 2; }
URL=${DOWNLOAD_URL:-https://github.com/$REPO/releases/download/v$VERSION/${ZIP:t}}

# Sparkle compares CFBundleVersion (the build timestamp), so read it from the zip that ships.
INFO=$(unzip -p "$ZIP" "Bambu Bar.app/Contents/Info.plist")
BUILD=$(plutil -extract CFBundleVersion raw -o - - <<< "$INFO")
MINIMUM_SYSTEM=$(plutil -extract LSMinimumSystemVersion raw -o - - <<< "$INFO")
SIGNATURE=$("$SPARKLE_BIN/sign_update" "$ZIP")

# The release text minus its install instructions, rendered by GitHub the way the release page shows it.
if [[ -n "${NOTES:-}" ]]; then
  MARKDOWN=$(<"$NOTES")
else
  MARKDOWN=$(gh release view "v$VERSION" --repo "$REPO" --json body --jq .body)
fi
MARKDOWN=$(print -r -- "$MARKDOWN" | sed '/^\*\*Install:\*\*/,$d')
HTML=$(gh api markdown -f mode=gfm -f context="$REPO" -f text="$MARKDOWN")

DESCRIPTION="<description><![CDATA[
<style>
  :root { color-scheme: light dark; }
  body { font: 13px/1.45 -apple-system, \"Segoe UI Variable Text\", \"Segoe UI\", sans-serif; margin: 10px 14px; }
  ul { padding-left: 1.2em; } li { margin: 0.35em 0; }
  code { font: 12px ui-monospace, Consolas, monospace; }
  a { color: #2fae66; }
</style>
$HTML
      ]]></description>"
PUBDATE=$(LC_ALL=C date -u '+%a, %d %b %Y %H:%M:%S +0000')

# The Windows installers, one item per architecture (sparkle:version is X.Y.Z there: no build timestamp).
WINDOWS_ITEMS=""
X64="$ROOT/build/Bambu-Bar-$VERSION-windows-x64.exe"
ARM64="$ROOT/build/Bambu-Bar-$VERSION-windows-arm64.exe"
if [[ -f "$X64" || -f "$ARM64" ]]; then
  [[ -f "$X64" && -f "$ARM64" ]] || { echo "Only one Windows installer in build/: both ship, or neither" >&2; exit 2; }
  for arch in x64 arm64; do
    EXE="$ROOT/build/Bambu-Bar-$VERSION-windows-$arch.exe"
    EXE_URL=${WINDOWS_DOWNLOAD_URL:-https://github.com/$REPO/releases/download/v$VERSION}/${EXE:t}
    EXE_SIGNATURE=$("$SPARKLE_BIN/sign_update" "$EXE")
    WINDOWS_ITEMS+="
    <item>
      <title>Bambu Bar $VERSION</title>
      <pubDate>$PUBDATE</pubDate>
      <sparkle:version>$VERSION</sparkle:version>
      <sparkle:shortVersionString>$VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>10.0.18362</sparkle:minimumSystemVersion>
      <sparkle:fullReleaseNotesLink>https://github.com/$REPO/releases</sparkle:fullReleaseNotesLink>
      $DESCRIPTION
      <enclosure url=\"$EXE_URL\" sparkle:os=\"windows-$arch\" type=\"application/octet-stream\" $EXE_SIGNATURE />
    </item>"
  done
fi

cat > "$ROOT/build/appcast.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
  <channel>
    <title>Bambu Bar</title>
    <link>https://github.com/$REPO</link>
    <item>
      <title>Bambu Bar $VERSION</title>
      <pubDate>$PUBDATE</pubDate>
      <sparkle:version>$BUILD</sparkle:version>
      <sparkle:shortVersionString>$VERSION</sparkle:shortVersionString>
      <sparkle:minimumSystemVersion>$MINIMUM_SYSTEM</sparkle:minimumSystemVersion>
      <sparkle:fullReleaseNotesLink>https://github.com/$REPO/releases</sparkle:fullReleaseNotesLink>
      $DESCRIPTION
      <enclosure url="$URL" type="application/octet-stream" $SIGNATURE />
    </item>$WINDOWS_ITEMS
  </channel>
</rss>
EOF
xmllint --noout "$ROOT/build/appcast.xml"
echo "==> Wrote $ROOT/build/appcast.xml ($VERSION, build $BUILD${WINDOWS_ITEMS:+, with the Windows x64 and ARM64 items})"

if [[ -n "${UPLOAD:-}" ]]; then
  gh release upload "v$VERSION" "$ROOT/build/appcast.xml" --repo "$REPO" --clobber
  echo "==> Attached appcast.xml to v$VERSION"
fi
