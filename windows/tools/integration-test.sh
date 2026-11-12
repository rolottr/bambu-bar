#!/bin/zsh
# Runs the Windows Core's integration tests against the Docker simulators (shared/scripts/integration/compose.yaml),
# the same ones the Mac's scripts/integration-test.sh uses: the mock fleet (serials in mixed case) and the mock camera.
#   windows/tools/integration-test.sh           start the simulators, run the tests, stop them
#   windows/tools/integration-test.sh --keep    leave the simulators running afterwards
# Needs Docker and the .NET 10 SDK. Loopback ports 322, 6000, 18080 and 18801-18817 must be free, and UDP 2122.
set -euo pipefail

ROOT=${0:A:h:h:h}
HERE="$ROOT/shared/scripts/integration"
COMPOSE=(docker compose -f "$HERE/compose.yaml")

docker info >/dev/null 2>&1 || { echo "Docker isn't running" >&2; exit 2; }
stop() { "${COMPOSE[@]}" down --remove-orphans >/dev/null 2>&1 || true; }
[[ "${1:-}" == "--keep" ]] || trap stop EXIT

echo "==> Starting the simulators"
rm -f "$HERE/out/BambuStudio.conf"
"${COMPOSE[@]}" up --build --detach --wait 2>&1 | grep -vE "^\s*$" | tail -4
for _ in {1..60}; do
  [[ -f "$HERE/out/BambuStudio.conf" ]] && curl -sf http://127.0.0.1:18080/scenario >/dev/null && break
  sleep 0.5
done
[[ -f "$HERE/out/BambuStudio.conf" ]] || { "${COMPOSE[@]}" logs fleet | tail -20; echo "The fleet didn't start" >&2; exit 1; }

echo "==> Testing"
export BAMBUBAR_INTEGRATION=1 BAMBUBAR_FLEET_SETTINGS="$HERE/out/BambuStudio.conf"
# From windows/, so dotnet finds global.json (it selects Microsoft.Testing.Platform).
cd "$ROOT/windows"
dotnet test --project tests/BambuBar.Integration.Tests
