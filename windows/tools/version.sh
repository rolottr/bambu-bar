#!/bin/zsh
# Prints the app's version (X.Y.Z), shared by the Mac and Windows: MARKETING_VERSION in project.yml is the only place
# it's written. Directory.Build.props reads it the same way for the Windows build.
#   windows/tools/version.sh
set -euo pipefail
ROOT=${0:A:h:h:h}
version=$(sed -nE 's/^[[:space:]]*MARKETING_VERSION:[[:space:]]*"?([0-9]+\.[0-9]+\.[0-9]+).*/\1/p' "$ROOT/project.yml" | head -1)
[[ -n "$version" ]] || { echo "No MARKETING_VERSION in $ROOT/project.yml" >&2; exit 1; }
print -r -- "$version"
