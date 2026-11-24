#!/bin/zsh
# lsregister also returns an error when a build product was never registered.
set -eu
lsregister=/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister
for app in "$@"; do
  "$lsregister" -u "$app" >/dev/null 2>&1 || true
done
