#!/usr/bin/env bash
# Spike A0.3 Q2/Q4: set a URLBlocklist/URLAllowlist through the DPC, then navigate Chrome to each probe URL
# and record the result from the visible page text.
#   blocked  = Chrome shows the admin block page ("is blocked")
#   allowed  = the page's own text is visible (every test page says "ALLOWED ..." or the marker given)
#   other    = neither (recorded as-is, never guessed)
# Usage: rules-probe.sh <case-name> <blocklist csv> <allowlist csv> <url>...
set -u
ADB="/c/Users/Eran Finish/AppData/Local/Android/Sdk/platform-tools/adb.exe"
S=emulator-5554
C=com.webfilter.spike.a03
EV="/c/Projests/WebFilterApp/WebFilterApp/spikes/A0-3/evidence"
CASE=$1; BL=$2; AL=$3; shift 3
OUT="$EV/rules-$CASE.txt"
dump_text() { "$ADB" -s $S shell "uiautomator dump /sdcard/r.xml >/dev/null 2>&1; cat /sdcard/r.xml" | tr -d '\r'; }
{
echo "case=$CASE blocklist=[$BL] allowlist=[$AL]"
"$ADB" -s $S shell am broadcast -W -n $C/.CmdReceiver --es op chrome_clear >/dev/null
# 2 s between clear and set: with 1 s, the path rules of case C and Q4 were not enforced (v1 results, see report).
sleep 2
# An empty --es value makes `am broadcast` fail ("Argument expected after allowlist"), so the set was never
# sent for cases with an empty allowlist in v1-v3. Pass the extra only when it is non-empty.
EXTRA=()
if [ -n "$AL" ]; then EXTRA=(--es allowlist "$AL"); fi
"$ADB" -s $S shell am broadcast -W -n $C/.CmdReceiver --es op chrome_set --es mode array --es blocklist "$BL" "${EXTRA[@]}" | tr -d '\r'
"$ADB" -s $S shell am broadcast -W -n $C/.CmdReceiver --es op chrome_read | tr -d '\r'
# Q1 measured ~5.5 s from the DPC call to enforcement; wait longer than that before the first probe.
sleep 10
CHROME_ACT=com.android.chrome/org.chromium.chrome.browser.ChromeTabbedActivity
for U in "$@"; do
  # Two fixes after the first pass: (1) an intent without an explicit component could resolve to another
  # app (org.chromium.webview_shell showed up in Q4), so target Chrome's activity; (2) if the URL equals
  # the tab's current URL Chrome may not navigate, so go to about:blank first.
  "$ADB" -s $S shell am start -n $CHROME_ACT -a android.intent.action.VIEW -d "about:blank" >/dev/null
  sleep 1
  # Quote the URL for the device shell: unquoted, `&` ends the command there, so ?udm=2&q=x arrived as ?udm=2 (v1-v3 Q4 results).
  "$ADB" -s $S shell am start -n $CHROME_ACT -a android.intent.action.VIEW -d "'$U'" >/dev/null
  sleep 4
  FG=$("$ADB" -s $S shell dumpsys activity activities | grep -m1 mResumedActivity | tr -d '\r')
  X=$(dump_text)
  if echo "$X" | grep -q "is blocked"; then R=BLOCKED
  elif echo "$X" | grep -q "ALLOWED"; then R=ALLOWED
  else R=OTHER; fi
  # First visible texts, so OTHER results can be read by hand.
  SNIP=$(echo "$X" | grep -o 'text="[^"]\+"' | head -n 3 | tr '\n' ' ')
  echo "$R  $U  | fg=$(echo $FG | grep -o 'com\.[a-z.]*' | head -n1) | $SNIP"
done
} | tee "$OUT"
