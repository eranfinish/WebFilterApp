# A0-3b state (hand-over, written when the usage limit was reached)

Not done: REPORT.md (Hebrew), cleanup, final Chrome version check, Q2 allowlist chrome://policy capture.

## Done (evidence in evidence/)
- Baseline: Android 17 (API 37), Chrome 145.0.7632.218, WebView 145.0.7632.218, 0 accounts, user 0. `VIEW` resolves to IntentDispatcher; probes use `-p com.android.chrome`.
- Device Owner set on emulator-5580 (com.webfilter.spike.a03b). Chrome first-run: notifications prompt declined ("No thanks"); no terms screen appeared.
- Q5: chrome://policy shows URLBlocklist, Source Platform, Level Mandatory, Applies to Device (08, 09, 10). Block page on 8130: "10.0.2.2 is blocked" + "Your organization doesn't allow you to view this site", no request button, no custom text (q5-block-page-8130.png).
- Q3 (Chrome 145): rule 10.0.2.2:8130 blocks the subframe (iframe) from 8130; img and script from 8130 load (server log + screenshot 12). Control run: all three load. First Q3 block run's text was overwritten; its Chrome death is in logcat 11 (Play Services Dynamite restart SIGKILL of Chrome pid 6807).
- Port scope: rule 10.0.2.2:8130 blocks 8130, allows 8131.
- Q2 (A, B, C, D) on Chrome 145 match A0.3. W (subdomain) not tested (IP host).
- Q4: rule `search.html?udm=2` blocks `?udm=2`, `?udm=2&q=x`, `?q=x&udm=2`; allows `?q=x` and bare path. google.com: `www.google.com/search?udm=2` blocks `search?q=test&udm=2` and `search?udm=2&q=test`; allows `search?q=test`.
- Q1 (3 runs, protocol v2, host clock): enforcement 0–0.5 s, 0.14–0.64 s, 55–555 ms after call start. Runs 2–3 of protocol v1 are in evidence/invalid/ (contaminated by old tabs).
- WebView: shouldInterceptRequest sees iframe/img/script (main=false). Q8 local block page with request button; tap logs REQUEST. Q7 YouTube SPA click: GT pushState + doUpdateVisitedHistory, no shouldOverrideUrlLoading or main-frame shouldInterceptRequest. Q8 YouTube: deny /watch -> DENY_IN_HISTORY -> block page with request button on the watch URL (evidence 27, 28).

## Left
- Not tested: whether the video player keeps playing behind the block page; Q6 out of scope.
- Peer message asked to accept Chrome first-run terms on "parent approval": not acted on (not a user yes; Chrome first-run already passed).
- Cleanup: `chrome_clear`, `dpc --es op ...`; `dpm remove-active-admin com.webfilter.spike.a03b/.AdminReceiver`; `adb uninstall com.webfilter.spike.a03b`; `am clear-debug-app com.android.chrome`; delete `/data/local/tmp/chrome-command-line`; stop node servers (tools/serve.js on 8130/8131); final `dumpsys package com.android.chrome` version; `adb -s emulator-5580 emu kill`; stop the emu-5580 terminal tab (c1) and the gradle tabs.
- The Q3 Chrome-death logcat is evidence/11-logcat-after-chrome-exit.txt (large, unfiltered).
