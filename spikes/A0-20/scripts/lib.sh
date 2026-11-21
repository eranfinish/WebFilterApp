#!/bin/bash
# Helpers for spike A0.20. Device: emulator-5590 only (API 31, Tablet_A12). Never touch emulator-5580.
export PATH="$PATH:/c/Users/Eran Finish/AppData/Local/Android/Sdk/platform-tools"
S="-s emulator-5590"
DPC=com.webfilter.spike.a020
PROBE=com.webfilter.spike.a020.probe
EVI="/c/Projests/WebFilterApp/WebFilterApp/spikes/A0-20/evidence"

# dpc <op> [extra args for am broadcast, e.g. --es ips "1.2.3.4"]  -> prints the result data
dpc() {
  local op="$1"; shift
  timeout 60 adb $S shell am broadcast -W -n $DPC/.CmdReceiver --es op "$op" "$@" | grep -o 'data=".*"'
}

# probe <op> [extras...]  -> fire and forget; the result arrives in logcat under A020P
probe() {
  timeout 20 adb $S shell am broadcast -n $PROBE/.ProbeReceiver --es op "$@" >/dev/null
}

# probe_ex <args...>  (extras that are not --es op, e.g. --ei)
probe_raw() {
  timeout 20 adb $S shell am broadcast -n $PROBE/.ProbeReceiver "$@" >/dev/null
}

logclear() { timeout 15 adb $S logcat -c; }

# dump the probe and DPC log lines (since the last logclear) to a file, and print them
dumpLog() {
  local f="$EVI/$1"
  timeout 20 adb $S logcat -d -s A020P:I A020:I | grep -E "A020P|A020 |A020:|RULES|STATS|sni|doh" > "$f"
  cat "$f"
}

# stats of the forwarder (DPC STATS line)
stats() {
  timeout 30 adb $S shell am broadcast -n $DPC/.CmdReceiver --es op stats >/dev/null
  sleep 2
  timeout 15 adb $S logcat -d -s A020:I | grep "STATS" | tail -1
}
