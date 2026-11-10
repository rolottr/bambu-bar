#!/bin/sh
# Runs the widget skins' Lua tests with LuaJIT (brew install luajit): every tests/*_test.lua, against
# rainmeter/samples. Exit code 0 when all pass.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/.." && pwd)
command -v luajit >/dev/null 2>&1 || { echo "luajit not found (brew install luajit)" >&2; exit 2; }
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
status=0
for t in "$here"/*_test.lua; do
	echo "$(basename "$t")"
	BB_ROOT="$root" BB_TMP="$tmp" LUA_PATH="$here/?.lua;;" luajit "$t" || status=1
done
[ $status -eq 0 ] && echo "All widget tests passed." || echo "Some widget tests failed." >&2
exit $status
