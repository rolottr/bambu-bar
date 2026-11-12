#!/bin/zsh
# Runs the integration tests against the Docker simulators (shared/scripts/integration/compose.yaml): the mock
# fleet (shared/scripts/fleet, with serials in mixed case) and the mock camera (shared/scripts/camera).
#   scripts/integration-test.sh                 start the simulators, run every test, stop them
#   scripts/integration-test.sh --keep          leave the simulators running afterwards
#   ONLY=CameraIntegrationTests scripts/integration-test.sh
#                                               one test class (or Class/testMethod)
# Needs Docker, Xcode and XcodeGen. Loopback ports 322, 6000, 18080 and 18801-18817 must be free (stop
# shared/scripts/fleet/run.sh first), and UDP 2122.
set -euo pipefail

ROOT=${0:A:h:h}
HERE="$ROOT/shared/scripts/integration"
COMPOSE=(docker compose -f "$HERE/compose.yaml")
cd "$ROOT"

if [[ -z "${DEVELOPER_DIR:-}" ]] && ! xcodebuild -version >/dev/null 2>&1; then
  export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
fi
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
curl -sf http://127.0.0.1:18080/scenario >/dev/null || { "${COMPOSE[@]}" logs camera | tail -20; echo "The camera didn't start" >&2; exit 1; }

echo "==> Generating the project"
"$ROOT/scripts/generate.sh" >/dev/null

echo "==> Testing"
# xcodebuild hands TEST_RUNNER_* variables to the tests without the prefix.
export TEST_RUNNER_BAMBUBAR_INTEGRATION=1
export TEST_RUNNER_BAMBUBAR_FLEET_SETTINGS="$HERE/out/BambuStudio.conf"
xcodebuild -project BambuBar.xcodeproj -scheme BambuBarIntegrationTests -destination "platform=macOS,arch=$(uname -m)" \
  -derivedDataPath "$ROOT/build/DerivedData.noindex" CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO \
  ${ONLY:+-only-testing:BambuBarIntegrationTests/$ONLY} -quiet test
