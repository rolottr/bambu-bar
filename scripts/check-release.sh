#!/bin/zsh
# The guard before a release becomes "latest" (CLAUDE.local.md): every installed Mac copy updates from
# releases/latest, so that release must carry the Mac zip and an appcast.xml with a Mac item for its version. Since
# every release ships both platforms, it also needs both Windows installers and their items.
#   scripts/check-release.sh v2.2.0        a release about to be marked latest
#   scripts/check-release.sh               the release GitHub marks latest now
#   MAC_ONLY=1 scripts/check-release.sh …  releases from before Windows shipped
# Exits non-zero, saying what's missing, so `scripts/check-release.sh v2.2.0 && gh release edit v2.2.0 --latest`.
set -euo pipefail

REPO=rolottr/bambu-bar
TAG=${1:-$(gh release view --repo "$REPO" --json tagName --jq .tagName)}
VERSION=${TAG#v}
ASSETS=$(gh release view "$TAG" --repo "$REPO" --json assets --jq '.assets[].name')
problems=()
has() { print -r -- "$ASSETS" | grep -qx -- "$1" || problems+=("no $1 attached"); }

has "Bambu-Bar-$VERSION-macos.zip"
has "appcast.xml"
[[ -n "${MAC_ONLY:-}" ]] || { has "Bambu-Bar-$VERSION-windows-x64.exe"; has "Bambu-Bar-$VERSION-windows-arm64.exe"; }

if print -r -- "$ASSETS" | grep -qx appcast.xml; then
  WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
  gh release download "$TAG" --repo "$REPO" -p appcast.xml -D "$WORK"
  xmllint --noout "$WORK/appcast.xml" || problems+=("appcast.xml isn't well-formed")
  # Items as "os version url": the Mac's has no sparkle:os (or "macos").
  ITEMS=$(python3 - "$WORK/appcast.xml" <<'PY'
import sys, xml.etree.ElementTree as ET
S = "{http://www.andymatuschak.org/xml-namespaces/sparkle}"
for item in ET.parse(sys.argv[1]).getroot().iter("item"):
    enc = item.find("enclosure")
    os_ = (enc.get(S + "os") if enc is not None else None) or "macos"
    ver = item.findtext(S + "shortVersionString") or ""
    sig = "signed" if enc is not None and enc.get(S + "edSignature") else "unsigned"
    print(os_, ver, sig, enc.get("url") if enc is not None else "")
PY
)
  check_item() {
    local os=$1 file=$2
    local line=$(print -r -- "$ITEMS" | awk -v o="$os" -v v="$VERSION" '$1 == o && $2 == v' | head -1)
    if [[ -z "$line" ]]; then problems+=("appcast.xml has no $os item for $VERSION")
    else
      [[ "$line" == *" signed "* ]] || problems+=("the $os item isn't signed")
      [[ "$line" == *"/$TAG/$file" ]] || problems+=("the $os item doesn't point at $TAG/$file")
    fi
  }
  check_item macos "Bambu-Bar-$VERSION-macos.zip"
  if [[ -z "${MAC_ONLY:-}" ]]; then
    check_item windows-x64 "Bambu-Bar-$VERSION-windows-x64.exe"
    check_item windows-arm64 "Bambu-Bar-$VERSION-windows-arm64.exe"
  fi
fi

if (( ${#problems} )); then
  echo "$TAG can't be the latest release:" >&2
  printf '  - %s\n' "${problems[@]}" >&2
  exit 1
fi
if [[ -n "${MAC_ONLY:-}" ]]; then what="the Mac zip"; else what="the Mac zip, both Windows installers"; fi
echo "==> $TAG has $what and a signed appcast item for each"
