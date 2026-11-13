#!/usr/bin/env bash
# Standalone, source-only host helper; installing it is a separate user action.
set -euo pipefail
repo="$(cd "$(dirname "$0")/../.." && pwd)"
out="$repo/build/linux/Bambu-Bar-hyprland.tar.gz"
mkdir -p "$(dirname "$out")"
COPYFILE_DISABLE=1 tar -czf "$out" -C "$repo" LICENSE LINUX.md \
  linux/hyprland/anchor.py linux/hyprland/window.lua \
  linux/scripts/install-hyprland-integration.sh
printf 'Local Hyprland integration: %s\n' "$out"
