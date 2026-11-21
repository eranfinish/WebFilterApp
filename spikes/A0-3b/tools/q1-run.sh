#!/usr/bin/env bash
# Q1 update-latency run. Usage: q1-run.sh <run-number>
# Timeline: page on 8131 polls the blocked origin every 500 ms -> set URLBlocklist (DPC t0) -> keep polling.
# Host clock only (server log ts and host timestamps around the broadcast), so both bounds use one clock.
. "$(dirname "$0")/lib.sh"
RUN=$1
LOG="$EVDIR/serve-a03b.log"
OUT="$EVDIR/q1-run$RUN.txt"
START=$(wc -l < "$LOG")
{
  echo "q1 run=$RUN"
  dpc --es op chrome_clear >/dev/null
  sleep 5
  adbs shell "am start -p com.android.chrome -a android.intent.action.VIEW -d 'http://10.0.2.2:8131/q1-poll.html?run=$RUN'" >/dev/null
  sleep 6
  HB=$(date +%s%3N)                                  # host, before the DPC call
  RES=$(dpc --es op chrome_set --es mode array --es blocklist "'10.0.2.2:8130'" | tail -n1)
  HA=$(date +%s%3N)                                  # host, after the DPC call returned
  echo "$RES"
  echo "host_before_broadcast_ms=$HB host_after_broadcast_ms=$HA"
  sleep 9
  adbs shell "uiautomator dump /sdcard/q1.xml >/dev/null 2>&1; cat /sdcard/q1.xml" | tr -d '\r' | grep -o 'text="n=[0-9]*"' | head -1
  tail -n +$((START+1)) "$LOG" > "$OUT.server.tmp"
  echo "server_lines_for_run=$(wc -l < "$OUT.server.tmp")"
} > "$OUT" 2>&1
python tools/q1-analyze.py "$RUN" >> "$OUT" 2>&1
cat "$OUT"
