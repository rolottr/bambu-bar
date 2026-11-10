#!/bin/zsh
# Starts the mock fleet in Docker and opens Bambu Bar against it. Nothing is saved: not the printers, their
# access codes, the history or the widgets' data.
#   shared/scripts/fleet/run.sh            fleet + app with the printers already added
#   shared/scripts/fleet/run.sh --import   fleet + app on the Add Printer page, to try "Add All"
#   SSDP=1 shared/scripts/fleet/run.sh     the printers also announce themselves, with names and models
#   SERIAL_CASE=mixed shared/scripts/fleet/run.sh
#                                          serials in another case in the settings file and announcements (fleet.py)
#   shared/scripts/fleet/run.sh --stop     stops the fleet
set -euo pipefail

HERE=${0:A:h}
ROOT=${HERE:h:h:h}
APP="$ROOT/build/App.noindex/Bambu Bar.app/Contents/MacOS/Bambu Bar"
if [[ "${1:-}" == "--stop" ]]; then
  docker compose -f "$HERE/compose.yaml" down
  exit 0
fi
rm -f "$HERE/out/BambuStudio.conf"
SSDP_TARGET=${SSDP:+host.docker.internal} docker compose -f "$HERE/compose.yaml" up --build -d
for _ in {1..60}; do [[ -f "$HERE/out/BambuStudio.conf" ]] && break; sleep 0.5; done
docker compose -f "$HERE/compose.yaml" logs fleet | grep -v "^\s*$" | tail -14
[[ -x "$APP" ]] || "$ROOT/scripts/build.sh"
pkill -f "$APP" 2>/dev/null || true
if [[ "${1:-}" == "--import" ]]; then
  "$APP" --emulator "$HERE/out/BambuStudio.conf" >/dev/null 2>&1 &
else
  "$APP" --emulator "$HERE/out/BambuStudio.conf" --add-all >/dev/null 2>&1 &
fi
echo "==> Bambu Bar is watching the mock fleet"
