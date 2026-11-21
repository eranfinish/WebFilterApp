#!/bin/bash
# One trial of spike A0.21. Usage:
#   run21.sh <trialName> <sticky:true|false> <mech:svc|alarm|job|always|none> <killAfterSec> <totalSec>
# Device: emulator-5590 only. Steps: tunnel up under lockdown -> watchdog mech set -> sampler (250 ms) -> kill :vpn
# (KILL_SELF, the DPC logs the wall time) -> sampler runs to the end -> logs saved to evidence/ -> summary line.
export PATH="$PATH:/c/Users/Eran Finish/AppData/Local/Android/Sdk/platform-tools"
export MSYS_NO_PATHCONV=1
S="-s emulator-5590"
DPC=com.webfilter.spike.a021
PROBE=com.webfilter.spike.a021.probe
EVI="/c/Projests/WebFilterApp/WebFilterApp/spikes/A0-21/evidence"
NAME="$1"; STICKY="$2"; MECH="$3"; KILLAT="$4"; TOTAL="$5"; START="${6:-start}"
OUT="$EVI/$NAME"
mkdir -p "$EVI"

dpc() { timeout 60 adb $S shell am broadcast -W -n $DPC/.CmdReceiver --es op "$@" | grep -o 'data=".*"'; }

# 1. state: tunnel up (lockdown on), watchdog mech and sticky flag as asked
dpc watch_stop >/dev/null
dpc lockdown_off >/dev/null
sleep 3
dpc sticky --ez on "$STICKY" >/dev/null
dpc lockdown_on >/dev/null
sleep 2
# start=start: our own startService (startRequested=true). start=boot: only always-on binds the service.
if [ "$START" = "start" ]; then dpc start >/dev/null; fi
sleep 4
dpc watch_start --es mech "$MECH" >/dev/null
sleep 3

# 2. sampler in the probe app (foreground activity keeps running), logs cleared at the same moment
timeout 15 adb $S logcat -c
timeout 20 adb $S shell am broadcast -n $PROBE/.ProbeReceiver --es op sample --ei dur $((TOTAL*1000)) --ei iv 250 >/dev/null
sleep "$KILLAT"

# 3. kill the VPN process (self-kill; it logs KILL_SELF wall=<ms> just before the kill)
dpc kill_vpn >/dev/null
sleep $((TOTAL - KILLAT))
sleep 3

# 4. save the logs
timeout 20 adb $S logcat -d > "$OUT-full-logcat.txt"
grep -E " S [0-9]+ | A021 | A021W |A021P   : S " "$OUT-full-logcat.txt" > "$OUT-trial.txt"
grep -E "has died|Scheduling restart|Start proc.*a021|a021:vpn" "$OUT-full-logcat.txt" | grep -v "Dynamite\|GsaVelour" > "$OUT-am.txt"
timeout 20 adb $S shell dumpsys activity services com.webfilter.spike.a021 > "$OUT-dumpsys-services.txt"
rm -f "$OUT-full-logcat.txt"

# 5. summary from the wall clock (ms)
KILL=$(grep -o 'KILL_SELF pid=[0-9]* wall=[0-9]*' "$OUT-trial.txt" | grep -o 'wall=[0-9]*' | cut -d= -f2 | head -1)
EST=$(grep -o 'establish ok .*wall=[0-9]*' "$OUT-trial.txt" | grep -o 'wall=[0-9]*' | cut -d= -f2 | awk -v k="$KILL" '$1>k' | head -1)
RETRY=$(grep -o 'onStartCommand startId=[0-9]* flags=[0-9]* retry=[a-z]*' "$OUT-trial.txt" | tail -1)
awk -v k="$KILL" -v e="$EST" '
  / S [0-9]+ /  {
    for (i=1;i<=NF;i++) if ($i=="S") { w=$(i+1); st=$(i+2); break }
    if (w+0 > k+0) {
      if (st=="OK" && !firstok) { firstok=w }
      if (st=="FAIL") { if (!firstfail) firstfail=w; if ($0 ~ /EACCES/) eacces++ }
      if (st=="OK" && e!="" && w+0 < e+0) leak++
    }
  }
  END {
    printf "%s kill=%s establish=%s offline_to_establish_ms=%s first_ok_after_kill_ms=%s offline_to_first_ok_ms=%s first_fail_ms=%s eacces_samples=%s ok_before_establish=%s\n",
      "'"$NAME"'", k, e, (e!=""?e-k:"NA"), (firstok!=""?firstok:"NA"), (firstok!=""?firstok-k:"NA"), (firstfail!=""?firstfail:"NA"), eacces+0, leak+0
  }' "$OUT-trial.txt" | tee "$OUT-summary.txt"
echo "restart: $RETRY" | tee -a "$OUT-summary.txt"
