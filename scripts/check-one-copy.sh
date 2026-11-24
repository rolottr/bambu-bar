#!/bin/zsh
# Check for a single installed copy and at most one running process.
#   scripts/check-one-copy.sh [--allow-uninstalled] <bundle-id> [<old bundle id or old app name>...]
# Build and packaging jobs can run before the app is installed, including on CI.
# Fails when Spotlight/Launch Services know more than one copy of the app (worktree build/ folders, extracted ZIPs,
# DerivedData, archive intermediates, old names), when the one copy isn't in /Applications, or when more than one
# process runs from any copy. Read-only: it lists what to remove and never deletes anything itself.
set -euo pipefail
allow_uninstalled=0
if [[ ${1:-} == --allow-uninstalled ]]; then
  allow_uninstalled=1
  shift
fi
BUNDLE_ID=${1:?usage: check-one-copy.sh [--allow-uninstalled] <bundle-id> [old ids or names...]}
shift
problems=()

copies=("${(@f)$(mdfind "kMDItemCFBundleIdentifier == '$BUNDLE_ID'" 2>/dev/null)}")
copies=(${copies:#})
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
registered=$("$lsregister" -dump Bundle | awk -v id="$BUNDLE_ID" '
  /^path:/ { path = $0; sub(/^path:[[:space:]]+/, "", path); sub(/ \(0x[[:xdigit:]]+\)$/, "", path) }
  $1 == "identifier:" && $2 == id { print path }
')
copies+=("${(@f)registered}")
for old in "$@"; do
  more=("${(@f)$(mdfind "kMDItemCFBundleIdentifier == '$old' || kMDItemFSName == '$old.app'" 2>/dev/null)}")
  copies+=(${more:#})
done
copies=(${(u)copies:#})

print "Copies found by Spotlight or Launch Services (${#copies}):"
for c in $copies; do print "  $c"; done
if (( allow_uninstalled && ${#copies} == 0 )); then
  print "==> no installed copy on this build host"
  exit 0
fi
(( ${#copies} == 1 )) || problems+=("${#copies} copies registered; keep only the one in /Applications (move the rest under a *.noindex folder or delete them, then lsregister -u)")
if (( ${#copies} >= 1 )) && [[ ${copies[(r)/Applications/*]:-} == "" ]]; then
  problems+=("no copy in /Applications")
fi

running=0
for c in $copies; do
  exe=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$c/Contents/Info.plist" 2>/dev/null) || continue
  n=$({ pgrep -f -- "$c/Contents/MacOS/$exe" || true; } | wc -l | tr -d ' ')
  (( n == 0 )) || print "Running from $c: $n"
  running=$(( running + n ))
done
(( running <= 1 )) || problems+=("$running processes running; quit all but the installed copy")

if (( ${#problems} )); then
  printf '  - %s\n' "${problems[@]}" >&2
  exit 1
fi
print "==> one copy, $running running"
