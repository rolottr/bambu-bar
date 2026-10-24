#!/bin/zsh
# Runs the unit tests with Xcode: scripts/test.sh
set -euo pipefail

ROOT=${0:A:h:h}
cd "$ROOT"

if [[ -z "${DEVELOPER_DIR:-}" ]] && ! xcodebuild -version >/dev/null 2>&1; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
command -v xcodegen >/dev/null || { echo "xcodegen is required: brew install xcodegen" >&2; exit 2; }

xcodegen generate --quiet --spec "$ROOT/project.yml" --project "$ROOT"
xcodebuild -project BambuBar.xcodeproj -scheme BambuBarTests -destination 'platform=macOS' \
  -derivedDataPath "$ROOT/build/DerivedData.noindex" CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
  -quiet test
