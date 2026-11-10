#!/bin/zsh
# Usage: scripts/check-appstore-licensing.sh '/path/to/AppStore/Bambu Bar.app'
set -euo pipefail
APP=${1:?Pass the built Mac App Store app bundle}
BINARY="$APP/Contents/MacOS/Bambu Bar"
[[ -f "$BINARY" ]] || { print -u2 'App executable not found'; exit 1; }
if strings "$BINARY" | grep -E 'api\.polar\.sh|buy\.polar\.sh|nina-labs-ai-llc/portal|3560f84c-991a-4d50-9b5d-1009584204dd|LicenseStore|PolarClient|LicenseKeychain|Buy for \$9\.99|Support for \$9\.99|Manage Subscription|Manage Purchase'; then
  print -u2 'App Store executable contains direct licensing code or purchase copy'
  exit 1
fi
[[ ! -e "$APP/Contents/Frameworks/Sparkle.framework" ]] || { print -u2 'App Store bundle contains Sparkle'; exit 1; }
print 'App Store binary contains no Polar integration or Sparkle framework'
