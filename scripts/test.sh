#!/bin/zsh
# Runs the unit tests with Xcode: scripts/test.sh
# The integration tests against the Docker simulators are scripts/integration-test.sh.
set -euo pipefail

ROOT=${0:A:h:h}
cd "$ROOT"

if [[ -z "${DEVELOPER_DIR:-}" ]] && ! xcodebuild -version >/dev/null 2>&1; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
# Draws the app icon too: the project refers to it, and a fresh checkout doesn't have it.
"$ROOT/scripts/generate.sh"
xcodebuild -project BambuBar.xcodeproj -scheme BambuBarTests -destination 'platform=macOS' \
  -derivedDataPath "$ROOT/build/DerivedData.noindex" CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
  -quiet test
