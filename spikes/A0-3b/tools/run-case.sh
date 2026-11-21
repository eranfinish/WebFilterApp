#!/usr/bin/env bash
# One probe case: set a Chrome URLBlocklist (or clear it), wait for enforcement, navigate Chrome's omnibox to a URL,
# dump the screen text, and keep only the server-log lines this case produced.
# A case is INVALID if Chrome's main process pid changed during it (Chrome killed itself: seen as a Play Services
# Dynamite "forcing restart" in logcat, evidence/11-logcat-after-chrome-exit.txt). Chrome is relaunched first if absent.
# Usage: run-case.sh <case-name> <blocklist-csv or -> <url> [wait-seconds] [allowlist-csv]
. "$(dirname "$0")/lib.sh"
NAME=$1; BL=$2; URL=$3; WAIT=${4:-9}; ALLOW=${5:-}
LOG="$EVDIR/serve-a03b.log"
OUT="$EVDIR/case-$NAME.txt"
n=2
while [ -e "$OUT" ]; do OUT="$EVDIR/case-$NAME-run$n.txt"; n=$((n+1)); done
# Never overwrite an earlier run: the suffix shows which attempt is which.
START=$(wc -l < "$LOG" 2>/dev/null || echo 0)
chrome_pid() { adbs shell pidof com.android.chrome | tr -d ' '; }
{
  echo "case=$NAME blocklist=[$BL] allowlist=[$ALLOW] url=$URL"
  if [ -z "$(chrome_pid)" ]; then
    echo "chrome not running before case: relaunching"
    adbs shell am start -W -p com.android.chrome -a android.intent.action.VIEW -d about:blank >/dev/null
    sleep 8
  fi
  PID0=$(chrome_pid); echo "chrome_pid_before=$PID0"
  dpc --es op chrome_clear >/dev/null
  if [ "$BL" != "-" ]; then
    # Values are single-quoted for the device shell: an unquoted * was glob-expanded to adb_keys (invalid run, evidence/invalid/).
    if [ -n "$ALLOW" ]; then dpc --es op chrome_set --es mode array --es blocklist "'$BL'" --es allowlist "'$ALLOW'";
    else dpc --es op chrome_set --es mode array --es blocklist "'$BL'"; fi
  fi
  dpc --es op chrome_read
  sleep 10   # enforcement measured at ~5.5 s on Chrome 91 (A0.3 Q1); 10 s wait before the probe
  omnibox_go "about:blank" >/dev/null; sleep 2
  T_NAV=$(date +%s%3N)
  if [ "${NAV_MODE:-omnibox}" = "intent" ]; then
    # Intent with the URL single-quoted for the device shell: keeps & and ? intact (A0.3 Q4 lost the & to the shell).
    adbs shell "am start -p com.android.chrome -a android.intent.action.VIEW -d '$URL'" >/dev/null
  else
    omnibox_go "$URL" >/dev/null
  fi
  echo "navigated_at_ms=$T_NAV"
  sleep "$WAIT"
  PID1=$(chrome_pid); echo "chrome_pid_after=$PID1"
  if [ "$PID0" != "$PID1" ]; then echo "INVALID: chrome pid changed during case"; fi
  FG=$(adbs shell dumpsys activity activities | grep -m1 topResumedActivity)
  echo "fg: $(echo "$FG" | grep -o 'com\.[a-z.]*/[A-Za-z.]*' | head -1)"
  XML=$(ui_dump)
  echo "$XML" > "${OUT%.txt}-ui.xml"
  URLBAR=$(echo "$XML" | grep -o '<node[^>]*url_bar[^>]*' | head -1 | grep -o ' text="[^"]*"' | head -1)
  echo "url_bar: $URLBAR"
  echo "screen: $(echo "$XML" | grep -o 'text="[^"]\+"' | grep -v -E 'Sort |Pixel|Search Google' | head -12 | tr '\n' ' ')"
  if [ -n "${SHOT:-}" ]; then "$ADB" -s $S exec-out screencap -p > "${OUT%.txt}.png"; echo "screenshot=${OUT%.txt}.png"; fi
  echo "--- server log lines for this case:"
  tail -n +$((START+1)) "$LOG" 2>/dev/null | grep -v -E '"path":"/favicon' || true
} > "$OUT" 2>&1
cat "$OUT"
