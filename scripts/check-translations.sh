#!/bin/zsh
# Checks shared/Localizable.xcstrings against the code: scripts/check-translations.sh
# Builds both editions (GitHub and App Store) so the compiler lists every localizable string, then fails if a
# string is missing from the catalog, if the catalog keeps strings no code uses, or if a string lacks one of the
# app's languages. Command-line builds don't update the catalog, so without this new text ships in English.
# Text built as a plain String (not String(localized:), Text("…") and the like) never reaches the compiler's
# list; this check can't see it.
set -euo pipefail

ROOT=${0:A:h:h}
DERIVED="$ROOT/build/TranslationCheck.noindex"
cd "$ROOT"

if [[ -z "${DEVELOPER_DIR:-}" ]] && ! xcrun --find xcstringstool >/dev/null 2>&1; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
"$ROOT/scripts/generate.sh" >/dev/null

for scheme config in BambuBar Debug "BambuBar App Store" AppStore; do
  echo "==> Building $scheme ($config) for its strings"
  xcodebuild -project BambuBar.xcodeproj -scheme "$scheme" -configuration "$config" \
    -derivedDataPath "$DERIVED" -destination 'generic/platform=macOS' ARCHS=arm64 \
    CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO -quiet build >/dev/null 2>&1 \
    || { echo "xcodebuild failed for $scheme; run it without -quiet to see why" >&2; exit 1; }
done

# The catalog's file name is its table name, so the copy keeps it.
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
cp shared/Localizable.xcstrings "$WORK/"
find "$DERIVED/Build/Intermediates.noindex" -name '*.stringsdata' -path '*/Objects-normal/arm64/*' \
  -not -path '*Tests.build*' > "$WORK/stringsdata.txt"
[[ -s "$WORK/stringsdata.txt" ]] || { echo "No .stringsdata found; is SWIFT_EMIT_LOC_STRINGS on?" >&2; exit 1; }
xcrun xcstringstool sync "$WORK/Localizable.xcstrings" --stringsdata ${(f)"$(<"$WORK/stringsdata.txt")"} \
  2>&1 | grep -v '^notice:' || true

python3 -I - shared/Localizable.xcstrings "$WORK/Localizable.xcstrings" <<'EOF'
import json, sys
catalog = json.load(open(sys.argv[1], encoding="utf-8"))["strings"]
synced = json.load(open(sys.argv[2], encoding="utf-8"))["strings"]
langs = set(catalog["Menu bar"]["localizations"])

def done(entry):
    if "stringUnit" in entry: return entry["stringUnit"].get("state") == "translated"
    forms = [f for v in entry.get("variations", {}).values() for f in v.values()]
    return bool(forms) and all(done(f) for f in forms)

missing = sorted(k for k in synced if k not in catalog)
# The sync marks unused translated strings stale and drops unused untranslated ones.
unused = sorted({k for k, v in synced.items() if v.get("extractionState") == "stale"} | (set(catalog) - set(synced)))
partial = sorted(
    (k, sorted(l for l in langs if not done(v.get("localizations", {}).get(l, {}))))
    for k, v in catalog.items()
    if v.get("shouldTranslate", True) and k.strip() and k not in unused
    and any(not done(v.get("localizations", {}).get(l, {})) for l in langs))

for title, items in (("Missing from the catalog", missing), ("In the catalog but unused", unused)):
    if items:
        print(f"{title} ({len(items)}):")
        for k in items: print(f"  {k!r}")
if partial:
    print(f"Not translated into every language ({len(partial)}):")
    for k, ls in partial: print(f"  {k!r}: {' '.join(ls)}")
if missing or unused or partial:
    sys.exit(1)
print(f"All {len(catalog)} strings are in the catalog, used, and translated into {len(langs)} languages")
EOF
