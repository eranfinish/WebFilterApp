# Shared helpers for spike A0.3b. Source with: . tools/lib.sh
ADB="/c/Users/Eran Finish/AppData/Local/Android/Sdk/platform-tools/adb.exe"
S="emulator-5580"
PKG=com.webfilter.spike.a03b
CMP=$PKG/.CmdReceiver
EVDIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../evidence" && pwd)"
adbs() { "$ADB" -s $S "$@" | tr -d '\r'; }
dpc() { adbs shell am broadcast -W -n $CMP "$@" | grep -E "Broadcast completed|Error" | tr -d '\r'; }
ui_dump() { adbs shell "uiautomator dump /sdcard/ui.xml >/dev/null 2>&1; cat /sdcard/ui.xml"; }
# Tap the centre of the first node whose text (or content-desc) equals $1.
tap_text() {
  local xml; xml=$(ui_dump)
  local b; b=$(echo "$xml" | grep -o "text=\"$1\"[^>]*bounds=\"\[[0-9]*,[0-9]*\]\[[0-9]*,[0-9]*\]\"" | grep -o 'bounds="[^"]*"' | head -1)
  if [ -z "$b" ]; then echo "tap_text: '$1' not found"; return 1; fi
  read -r x1 y1 x2 y2 <<< "$(echo "$b" | grep -oE '[0-9]+' | tr '\n' ' ')"
  adbs shell input tap $(( (x1+x2)/2 )) $(( (y1+y2)/2 ))
}
# Navigate Chrome's omnibox: tap the URL field (it shows about:blank or the current URL), type, Enter.
omnibox_go() {
  local xml cur
  xml=$(ui_dump)
  cur=$(echo "$xml" | grep -o 'resource-id="com.android.chrome:id/url_bar"[^>]*bounds="[^"]*"' | grep -o 'bounds="[^"]*"' | head -1)
  if [ -z "$cur" ]; then echo "omnibox_go: url_bar not found"; return 1; fi
  read -r x1 y1 x2 y2 <<< "$(echo "$cur" | grep -oE '[0-9]+' | tr '\n' ' ')"
  adbs shell input tap $(( (x1+x2)/2 )) $(( (y1+y2)/2 ))
  sleep 1
  adbs shell input keycombination CTRL a 2>/dev/null
  adbs shell input text "$1"
  adbs shell input keyevent 66
}
