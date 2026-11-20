#!/usr/bin/env bash
# Spike A0.3 Q1: how long from a DPC setApplicationRestrictions call until Chrome enforces a URLBlocklist change.
# All times use the DEVICE clock (adb shell date), so the DPC's t0 and the probe times are comparable.
# Usage: q1-timing.sh <condition: warm|cold> <blocked-url> <blocked-marker> <allowed-url> <allowed-marker> <run-id>
#   warm = Chrome already running when the policy changes; cold = Chrome force-stopped before the change.
set -u
ADB="/c/Users/Eran Finish/AppData/Local/Android/Sdk/platform-tools/adb.exe"
S=emulator-5554
C=com.webfilter.spike.a03
COND=$1; BURL=$2; BMARK=$3; AURL=$4; AMARK=$5; RUN=$6
# BURL is the blocklist rule (host/path, no scheme). Navigation needs a full URL: the first attempt
# navigated to the bare rule, Chrome rejected the intent, and the loop reported "no block".
NAV="http://$BURL"
EV="/c/Projests/WebFilterApp/WebFilterApp/spikes/A0-3/evidence"
OUT="$EV/q1-$COND-run$RUN.txt"
dev_ms() { "$ADB" -s $S shell "date +%s%3N" | tr -d '\r'; }
shot_text() { "$ADB" -s $S shell "uiautomator dump /sdcard/q1.xml >/dev/null 2>&1; cat /sdcard/q1.xml" | tr -d '\r'; }

{
echo "condition=$COND run=$RUN blocked=$BURL allowed=$AURL"
if [ "$COND" = "cold" ]; then "$ADB" -s $S shell am force-stop com.android.chrome; fi
# Start from the allowed page so the blocked URL is a fresh navigation after the change.
"$ADB" -s $S shell am start -a android.intent.action.VIEW -d "$AURL" com.android.chrome >/dev/null
sleep 5
echo "before: allowed marker present? $(shot_text | grep -c "$AMARK")"
# Clear, then set the blocklist; the DPC prints its own t0 (device clock, ms).
"$ADB" -s $S shell am broadcast -W -n $C/.CmdReceiver --es op chrome_clear >/dev/null
sleep 2
RESULT=$("$ADB" -s $S shell am broadcast -W -n $C/.CmdReceiver --es op chrome_set --es mode array --es blocklist "$BURL" | tr -d '\r')
echo "dpc: $RESULT"
T0=$(echo "$RESULT" | sed -n 's/.*t0=\([0-9]*\).*/\1/p')
# Probe: relaunch the blocked URL once a second until the blocked marker shows.
TFIRST=""
for i in $(seq 1 40); do
  "$ADB" -s $S shell am start -a android.intent.action.VIEW -d "$NAV" com.android.chrome >/dev/null
  sleep 3   # the error page needs ~3 s to render; a 1 s wait missed the block in the first attempt
  X=$(shot_text)
  NOW=$(dev_ms)
  if echo "$X" | grep -q "$BMARK"; then TFIRST=$NOW; echo "probe $i at $NOW: BLOCKED"; break; fi
  echo "probe $i at $NOW: not blocked yet"
done
if [ -n "$TFIRST" ]; then echo "RESULT t0=$T0 first_blocked=$TFIRST latency_ms=$((TFIRST - T0))"; else echo "RESULT no block within 40 probes"; fi
} | tee "$OUT"
