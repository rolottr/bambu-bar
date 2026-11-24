#!/bin/zsh
# Measure Bambu Bar CPU %, RSS MiB, and idle wakeups using the Bambu Bar ps -o time= + top method.
# Runs against the installed Release build in /Applications/Bambu Bar.app.
# Usage:
#   scripts/perf.sh idle
#   scripts/perf.sh panel
#   scripts/perf.sh panel-closed
#   scripts/perf.sh window
#   scripts/perf.sh after-close
#   scripts/perf.sh page <name>
#   scripts/perf.sh cycle
#   scripts/perf.sh soak
# Environment overrides:
#   PERF_IDLE_MAX=1.0   PERF_PANEL_MAX=3.0   PERF_WINDOW_MAX=5.0   PERF_PAGE_MAX=5.0
set -euo pipefail

ROOT=${0:A:h:h}
APP_NAME="Bambu Bar"
URL_SCHEME="bambubar"
APP="/Applications/$APP_NAME.app"
OUT="$ROOT/build/perf"
mkdir -p "$OUT"

PERF_IDLE_MAX=${PERF_IDLE_MAX:-1.0}
PERF_PANEL_MAX=${PERF_PANEL_MAX:-3.0}
PERF_WINDOW_MAX=${PERF_WINDOW_MAX:-5.0}
PERF_PAGE_MAX=${PERF_PAGE_MAX:-5.0}

[[ -d "$APP" ]] || { print -u2 "Installed release app not found at $APP (run scripts/install.sh first)"; exit 1; }
BUILD=$(plutil -extract CFBundleVersion raw "$APP/Contents/Info.plist" 2>/dev/null || echo "unknown")

cpu_s() {
  ps -o time= -p "$1" | awk '{ n=split($1,a,":"); s=0; for(i=1;i<=n;i++) s=s*60+a[i]; printf "%.6f\n", s }'
}

measure() { # <label> <settle_secs> <sample_secs> <budget_pct>
  local label=$1 settle=$2 secs=$3 budget=$4
  (( settle > 0 )) && sleep "$settle"
  local pid
  pid=$(pgrep -x "$APP_NAME" | head -n 1) || { print -u2 "$APP_NAME is not running"; return 1; }
  local a b rss_kib rss_mib pct top_line idlew
  a=$(cpu_s "$pid")
  sleep "$secs"
  b=$(cpu_s "$pid")
  rss_kib=$(ps -o rss= -p "$pid" | tr -d ' ')
  rss_mib=$(( rss_kib / 1024 ))
  pct=$(awk -v a="$a" -v b="$b" -v s="$secs" 'BEGIN { printf "%.2f", (b - a) / s * 100.0 }')
  top_line=$(top -l 2 -s 3 -pid "$pid" -stats cpu,idlew | tail -1 | awk '{$1=$1; print}')
  idlew=$(print -r -- "$top_line" | awk '{print $2}' | tr -dc '0-9')
  [[ -n "$idlew" ]] || idlew=0

  printf '%-14s %6.2f%%  (budget %s%%)  rss %d MiB  idlew %s  top[%s]\n' \
    "$label" "$pct" "$budget" "$rss_mib" "$idlew" "$top_line"

  local log_file="$OUT/$(date +%F).jsonl"
  printf '{"state":"%s","cpu":%s,"budget":%s,"rssMiB":%d,"rssKiB":%d,"idleWakeups":%d,"build":"%s","at":"%s"}\n' \
    "$label" "$pct" "$budget" "$rss_mib" "$rss_kib" "$idlew" "$BUILD" "$(date -u +%FT%TZ)" >> "$log_file"

  if ! awk -v p="$pct" -v b="$budget" 'BEGIN { exit !(p <= b) }'; then
    print -u2 "over budget: $label (${pct}% > ${budget}%)"
    return 1
  fi
}

ax() {
  osascript -e "tell application \"System Events\" to tell process \"$APP_NAME\" to $1" >/dev/null
}

panel() {
  ax 'click menu bar item 1 of menu bar 2'
}

esc() {
  osascript -e 'tell application "System Events" to key code 53' >/dev/null
}

relaunch_background() {
  pkill -x "$APP_NAME" 2>/dev/null || true
  sleep 2
  open -g "$APP" --args --background
}

case "${1:-cycle}" in
  idle)
    relaunch_background
    measure idle 12 60 "$PERF_IDLE_MAX"
    ;;
  panel)
    panel
    measure panel 5 30 "$PERF_PANEL_MAX"
    ;;
  panel-closed)
    panel
    sleep 5
    esc
    measure panel-closed 8 60 "$PERF_IDLE_MAX"
    ;;
  window)
    open "$URL_SCHEME://open/main"
    measure window 8 30 "$PERF_WINDOW_MAX"
    ;;
  page)
    [[ $# -ge 2 ]] || { print -u2 "Usage: scripts/perf.sh page <name>"; exit 2; }
    open "$URL_SCHEME://open/$2"
    measure "page:$2" 8 30 "$PERF_PAGE_MAX"
    ;;
  after-close)
    for i in 1 2; do
      open "$URL_SCHEME://open/main"
      sleep 6
      ax 'keystroke "w" using command down'
      measure "after-close-$i" 8 60 "$PERF_IDLE_MAX"
    done
    ;;
  cycle)
    for state in idle panel panel-closed window after-close; do
      "$0" "$state"
    done
    ;;
  soak)
    relaunch_background
    for i in {1..360}; do
      measure soak 0 5 "$PERF_IDLE_MAX" || true
    done
    ;;
  -h|--help)
    echo "Usage: scripts/perf.sh <idle|panel|panel-closed|window|after-close|page <name>|cycle|soak>"
    ;;
  *)
    print -u2 "Unknown mode: $1 (expected idle|panel|panel-closed|window|after-close|page <name>|cycle|soak)"
    exit 2
    ;;
esac
