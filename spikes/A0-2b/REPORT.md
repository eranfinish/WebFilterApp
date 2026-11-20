# ספייק A0.2b: טאנל מלא עם forwarder ב-user space, always-on עם lockdown (AOSP emulator, Android 17 / API 37)

**מסקנה: GO מותנה לאפשרות (א) עבור תעבורת האפליקציות, עם ממצא פתוח אחד שדורש החלטת הבעלים.**
- תעבורת אפליקציה שאינה ב-uid החריג (הדפדפן, אפליקציה שנייה, WebView) נחסמת כשהטאנל מת (`EACCES` תוך כ-2 שניות, שלוש הרצות). בזמן האתחול כללי ה-prohibit נמדדו בכל דגימה מ-32.7 s עד 43.8 s, ושם לא עלתה רשת. החלון 44.3–45.6 s (wlan0 קיים, הטאנל עוד לא) **לא נדגם**, ולכן זה נשאר תנאי פתוח (§6).
- **ממצא פתוח:** תעבורה של מערכת (בדיקת תקינות רשת, DNS לשרת הרשת, DoT לפורט 853, HTTPS ל-Google) יוצאת מחוץ לטאנל על ממשק הסלולר (`eth0`), גם כשה-lockdown פעיל ושלושת כללי ה-prohibit קיימים. זו לא תעבורה של אפליקציה שילד יכול להשתמש בה, אבל היא סותרת את "כל התעבורה דרכנו". זה דורש החלטה לפני ייצור (§6).
- לא נבדק: הפעלה מחדש אוטומטית (אין watchdog, והטאנל לא חזר ב-90 שניות בשלוש הרצות), ו-Wi-Fi (`wlan0`) לא נכלל בלכידה.

הכול על אמולטור בלבד. מכשירים אמיתיים, IPv6 אמיתי, SIM ומכשיר S8 לא נבדקו.

---

## 1. סביבה ושיטה

| פריט | ערך |
|---|---|
| מכשיר | AVD `Medium_Phone_API_37.0`, Android 17 (`ro.build.version.sdk=37`), image `google_apis_playstore`, fingerprint `google/sdk_gphone64_x86_64/emu64xa:17/CE2A.260420.051.A2/16418371:user/release-keys`. Emulator 37.2.13, `-port 5580` |
| דגלי הפעלה | `-crash-report-mode never` (בלעדיו ה-emulator נתקע על דיאלוג הסכמה לדוח קריסה, כמו ב-A0.2). ב-cold start האחרון גם `-no-snapshot-load` |
| מזהה ADB | `emulator-5580` בלבד. נשלחו פקודות רק לנתיב הזה. `emulator-5590` (Tablet_A12, סשן אחר) רץ במקביל חלק מהזמן, כולל ב-Q4. ראו §3.1 |
| בנייה | AGP 8.9.1, Kotlin 2.0.21, Gradle 8.11.1, `compileSdk 36`, `targetSdk 36`, `minSdk 31`. JDK 17 |
| תהליכים | ה-VPN רץ בתהליך נפרד (`:vpn`). ה-DPC בתהליך הראשי. שניהם **uid 10237** (אותו uid) |
| בדיקות יחידה | 19/19 עוברים: 8 ב-`ForwarderTest` (TCP על loopback אמיתי, UDP, DNS, ICMP/ICMPv6, דחייה) ו-11 ב-`PacketTest`. XML: `evidence/TEST-*.xml` |

## 2. בחירת forwarder

הספייק **לא** השתמש בספרייה חיצונית. כל מנוע native (hev, tun2socks, firestack, lwIP) דורש הורדה מ-JitPack/GitHub, NDK או AAR בינארי, וזה דורש אישור לפי ההוראות. לכן נכתב relay קטן ב-Kotlin (כ-1,150 שורות ב-`src/main`, בלי הבדיקות): `net/` (פרסור IPv4/IPv6/TCP/UDP וחישוב checksum), `dns/` (שאילתה, NXDOMAIN, כללי חסימה), `vpn/Forwarder.kt` (466 שורות, מכונת מצבים TCP פשוטה ללא window scaling, SACK או fast retransmit), `vpn/TunVpnService.kt`.

**טבלת מועמדים** (מידע מעמודי GitHub שנשלפו. **לא הורדו** ולא נבדקו קבצים בתוכם. "לא מאומת" = העמוד לא הראה את העובדה):

| פרויקט | רישיון | גרסה אחרונה | Android | גודל |
|---|---|---|---|---|
| `heiher/hev-socks5-tunnel` (C) | MIT לפי ה-sidebar וה-README. קובץ LICENSE: "Copyright (c) 2022 hev" | 2.20.0, 13 Nov 2026 (Latest) | README מתאר AAR שנבנה ב-CI. לא נמצא ב-Maven Central או JitPack בעמוד. דורש שרת SOCKS5 בתהליך | Android .so: 167–271 KB לארכיטקטורה; tarball 980 KB |
| `xjasonlyu/tun2socks` (Go) | MIT מ-v2.6.0 (release notes). גרסאות ישנות GPL-3.0 | v2.7.0, 12 Jul 2026 | לא מוצג AAR. Go (נדרש gomobile) | zip 4.38 MB (linux-amd64); לא נמצא asset אנדרואיד |
| `celzero/firestack` | MPL-2.0 (LICENSE ו-sidebar) | אין releases בעמוד | README: AAR דרך JitPack או Maven Central (OSSRH) | לא מוצג |
| `M66B/NetGuard` (רק כדוגמה ל-GPL) | GPL-3.0 | לא נבדק | לא נבדק | לא נבדק |
| lwIP | לא מאומת (דף Savannah שנפתח לא הכיל את המידע) | לא מאומת | לא מאומת | לא מאומת |

**מסקנה לבחירה למוצר:** מועמדים עם רישיון מתאים: hev (MIT) ו-tun2socks (MIT מ-v2.6.0). firestack (MPL-2.0) הוא copyleft חלש. NetGuard (GPL-3.0) לא מתאים למוצר. ה-relay שנכתב כאן הוא ספייק בלבד. לפני ייצור צריך בחירה סופית ובדיקת הרישיון ב-LICENSE של הגרסה הנבחרת.

## 3. מטריצה

| יכולת | ספק | API | תוצאה | ראיה |
|---|---|---|---|---|
| 1. טאנל מלא, `0.0.0.0/0` ו-`::/0` ב-tun0, lockdown פעיל | AOSP emulator | 37 | **עובד.** ב-tun0 יש ברירת מחדל IPv4 ו-IPv6 (`table 1024`). DNS של המערכת עובר ל-`10.8.0.1` ומשם ל-`10.0.2.3` (ראו שורה 19) | [q1-webview-rerun.txt](evidence/q1-webview-rerun.txt) (routes) |
| 2. Chrome טוען example.org תחת lockdown | AOSP emulator | 37 | **עובד** | [q1-chrome-example-org-2.png](evidence/q1-chrome-example-org-2.png) |
| 3. Chrome חוסם `en.wikipedia.org` | AOSP emulator | 37 | **נחסם**: `DNS_PROBE_FINISHED_NXDOMAIN` | [q1-chrome-wikipedia-blocked.png](evidence/q1-chrome-wikipedia-blocked.png) |
| 4. אפליקציה שנייה (`:probe`, uid נפרד): DNS, HTTP, raw TCP | AOSP emulator | 37 | **עובד**: example.org נפתר ונטען (200). שמות חסומים נכשלים | [q1-browsing-lockdown.txt](evidence/q1-browsing-lockdown.txt) |
| 5. WebView (uid של `:probe`) | AOSP emulator | 37 | **עובד**: example.org נטען; `en.wikipedia.org` → `ERR_NAME_NOT_RESOLVED` | [q1-webview-rerun.txt](evidence/q1-webview-rerun.txt) |
| 6. נפילה של `:vpn` (3 הרצות, `Process.killProcess` עצמי) | AOSP emulator | 37 | **נסגר.** ב-3 הרצות: בחלון עד `establish` מחדש, הצלחה אחת בהרצה 1 (DNS, +71 ms מ-`KILL_SELF`, הבקשה יצאה לפני ההרג); 0 בהרצות 2–3. `EACCES` תוך כ-1.9–2.2 שניות מ-`KILL_SELF` (מרווח בדיקה ~2 שניות). **לא חזר לבד** 90 שניות. חזרה ב-`lockdown_on`: 1.5–2.4 שניות | [q2-kill-summary.txt](evidence/q2-kill-summary.txt), `q2-kill-run{1,2,3}-logcat.txt` |
| 6b. מצב ה-dead window, `ip rule` והבדיקות | AOSP emulator | 37 | **נחסם.** התהליך מת (pid ריק), 3 שורות prohibit ב-`ip rule`, `connect` נכשל ב-`EACCES` תוך 11–16 ms. (בדיקת tun0 באותו חלון לא אמינה, ולכן לא מדווחת; ראו הערה בקובץ) | [q2-deadwindow-iprule-probes.txt](evidence/q2-deadwindow-iprule-probes.txt) |
| 7. `am force-stop` על ה-DPC (4 ניסיונות) | AOSP emulator | 37 | **לא ניתן לבדיקה**: המערכת דוחה: `Ignoring request to force stop protected package`. pid לא השתנה בכל הניסיונות. שורת הדחייה נשמרה לניסיון 1 (בלוג ההרצה) ולניסיון 4 (ללא סינון). ניסיונות 2–3 נבדקו רק לפי pid; שורת הדחייה שלהם הופיעה בקונסול בלבד | [q2-forcestop-run1-logcat.txt](evidence/q2-forcestop-run1-logcat.txt), [q2-forcestop-attempts-2-3.txt](evidence/q2-forcestop-attempts-2-3.txt), [q2-forcestop-attempt4-unfiltered.txt](evidence/q2-forcestop-attempt4-unfiltered.txt) |
| 8. `stopSelf()` (STOP_SELF) | AOSP emulator | 37 | **לא עוצר את הטאנל.** השירות ביצע `stopSelf`, אבל התהליך והטאנל נשארו, והבדיקות הצליחו לאורך 90 שניות | [q2-stopself-run1-logcat.txt](evidence/q2-stopself-run1-logcat.txt) |
| 9. מחזור `lockdown_off` → `lockdown_on` (onRevoke) | AOSP emulator | 37 | **עובד** עם הקוד המתוקן: `onRevoke → stopTunnel → onDestroy`, ואז `onCreate → establish ok` באותו pid, והתהליך עונה ל-`stats` | [q2-onrevoke-cycle.txt](evidence/q2-onrevoke-cycle.txt) |
| 10. Cold start (ללא snapshot), כל הזמנים ב-uptime של המכשיר | AOSP emulator | 37 | first contact ב-**14.5 s** (אתחול קר). ב-14.7 s: 0 prohibit. ב-**32.7 s**: 3 prohibit. תהליך ה-VPN נראה ב-**43.8 s**. wlan0 נוצר ב-44.3 s (validated 51.8 s). tun נוצר ב-45.6 s. DNS example.org → OK ב-**~58.7 s**, HTTP → OK ב-**~64.1 s** (לולאת הבדיקות התחילה ~5 s אחרי שתהליך ה-VPN נראה, ולכן זה חסם עליון לזמן עד ש"הגלישה עובדת": כ-13–18 s אחרי שהטאנל עלה) | [q2-coldstart2-timing.txt](evidence/q2-coldstart2-timing.txt) |
| 11. תעבורת מערכת על eth0 בזמן האתחול | AOSP emulator | 37 | 61 חבילות, DNS ל-`10.0.2.3`, TCP 853 ל-`10.0.2.3`, TCP 80/443 ל-Google, כולן מ-`10.0.2.15`. eth0 נוצר ב-58.9 s, **אחרי** שהטאנל עלה (45.6 s), ולכן זה נתיב הבדיקה של שורה 12 ולא דליפה לפני הטאנל. **הלכידה לא מכילה אף כתובת Wi-Fi** (`10.0.2.17`, `104.20.26.136`, `172.66.157.237`), למרות שבדיקות ה-DNS וה-HTTP של התהליך עברו ב-Wi-Fi באותו חלון. לכן הלכידה מכסה eth0 בלבד | [q2-coldstart2-nic.pcap](evidence/q2-coldstart2-nic.pcap) |
| 12. תעבורת מערכת על eth0 תחת lockdown מלא (3 prohibit) | AOSP emulator | 37 | **אותו דפוס יוצא גם תחת lockdown**: `svc data disable/enable`, ואחריו DNS, TCP 853 ו-TCP 80/443 מ-`10.0.2.15`. לא מיוחס ל-uid. ממצא פתוח, §6 | [q2-lockdown-toggle.pcap](evidence/q2-lockdown-toggle.pcap) |
| 13. נתיבי התהליך עצמו (Q3): protected / plain / bound-to-Wi-Fi | AOSP emulator | 37 | **protected**: מחוץ לטאנל (מונה ה-SYN לא זז). **plain**: דרך הטאנל (+1). **bound-to-Wi-Fi**: **מחוץ לטאנל ב-lockdown** (מונה לא זז). uid 10237 מוחרג מכללי ה-prohibit (`uidrange 1-10236`, `10238-20236`, `20238-99999`). uid 0 גם מחוץ לטווחים | [q3-own-traffic.txt](evidence/q3-own-traffic.txt) |
| 14. Q4 עלות (RTT 100 + 50 MB × 3) | AOSP emulator | 37 | **לא מכריע** (§3.1). בהרצה הדדית באותו מופע: אין הבדל מובהק | [q4-novpn-rerun.txt](evidence/q4-novpn-rerun.txt), [q4-vpn-clean.txt](evidence/q4-vpn-clean.txt) |
| 15. QUIC (UDP/443) | AOSP emulator | 37 | **נדחה ב-ICMP port unreachable.** במהלך ביקור ב-Chrome: 23 דחיות (0 קודם), וחיבורי TCP עלו מ-1 ל-14. הדף נטען (צילום של Q1). ההסקה ש-Chrome עובר ל-TCP היא מסקנה, לא נמדדה ישירות | [q5q6-counters.txt](evidence/q5q6-counters.txt), [q1-chrome-example-org-2.png](evidence/q1-chrome-example-org-2.png) |
| 16. IPv6 דרך הטאנל | AOSP emulator | 37 | **נדחה מהר**: ICMPv6 no-route. `2606:4700:4700::1111` נכשל ב-`ENETUNREACH` אחרי 5 ms | [q5q6-counters.txt](evidence/q5q6-counters.txt) |
| 17. IPv6 בלי VPN (השוואה) | AOSP emulator | 37 | `ECONNREFUSED` אחרי 4 ms: יש מסלול, אבל לא לאינטרנט אמיתי. **IPv6 אמיתי לא נבדק** | [q6-novpn-ipv6-check.txt](evidence/q6-novpn-ipv6-check.txt) |
| 18. DoT (TCP 853) ו-TCP DNS (53) | AOSP emulator | 37 | **RST מיידי** (לא נשמטו בשקט). נמדד `tcp853_rst`. **`tcp53_rst` לא נצפה** | [q5q6-counters.txt](evidence/q5q6-counters.txt) |
| 19. DNS דרך ה-resolver שלנו | AOSP emulator | 37 | **עובד**: שאילתה ל-`10.8.0.1` מועברת ל-DNS של רשת שאינה VPN (`10.0.2.3`). שמות חסומים מקבלים NXDOMAIN מקומי. תיקון: הקריאה לשרת DNS נעשית בזמן ריצה (לא בהפעלה) | [q1-browsing-lockdown.txt](evidence/q1-browsing-lockdown.txt), [q2-coldstart-earlier-boot-upstream-null.txt](evidence/q2-coldstart-earlier-boot-upstream-null.txt) |

**הערות לשורות:**
- **שורה 6:** הצלחת DNS בהרצה 1 נרשמה 71 ms אחרי `KILL_SELF`, והבקשה יצאה לפני ההרג. בהרצות 2 ו-3 אין הצלחה בחלון.
- **שורה 10 (שעונים):** בהפעלה הקודמת (ללא `-no-snapshot-load`) השעון של המכשיר לא התאים לזמן ההפעלה ולכן לא השתמשתי בה. בהפעלה האחרונה השעון עקבי: `/proc/uptime` ב-first contact 14.5 s. הזמנים בשורה מחושבים מ-`dumpsys connectivity` (`created`, `firstValidated`, שהם uptime במילישניות) ומ-`ip rule`.
- **שורה 11:** הלכידה כוללת רק eth0 (10.0.2.15). אין בה מקור 10.0.2.17 (wlan0). לא נבדק אם האמולטור לוכד את wlan0. הלכידה התחילה ב-`first contact`, ומהחבילה הראשונה (12:16:05 UTC) זה תואם את יצירת eth0 (12:16:04.7 UTC).
- **שורה 12:** בדיקה ב-`svc data disable/enable` עם שלושת כללי ה-prohibit פעילים. הפעלה מחדש של הסלולר הפעילה אותו burst.

### 3.1 עלות (Q4)

| מצב | RTT p50 | RTT p95 | הורדה 50 MB, חציון (3 הרצות) | הערה |
|---|---|---|---|---|
| ללא VPN, הרצה הדדית (`q4-novpn-rerun.txt`) | 5.0 ms | 9.7 ms | 2.68 MB/s (2.51, 2.68, 2.79) | `emulator-5590` רץ |
| עם VPN (`q4-vpn-clean.txt`) | 4.8 ms | 10.2 ms | 2.57 MB/s (3.23, 2.57, 1.87) | `emulator-5590` רץ |
| ללא VPN, הרצה ראשונה (`10-baseline-cost-noVPN-clean.txt`) | 15.2 ms | 27.4 ms | 0.69 MB/s (0.72, 0.69, 0.69) | מופע emulator אחר, לא מושווה |

- **ההשוואה בין שתי ההרצות הדדיות לא מובהקת.** הבדל בין ההרצה הנקייה הראשונה ללא VPN (0.69 MB/s) לבין ההרצה ההדדית (2.68 MB/s) הוא פי ~4 באותו מארח. זה לא מוסבר. ההרצות עם VPN ירדו מ-3.23 ל-1.87 MB/s, וההרצות ללא VPN נשארו יציבות. **לא מוסבר.**
- **עלות CPU** (`/proc/<pid>/stat`, `CLK_TCK=100`, תהליך `:vpn` בלבד): במהלך RTT (100 בקשות) ו-3 הורדות של 50 MB, כ-14,326 ticks ≈ **143 שניות CPU** (RTT ≈ 7 שניות, הורדות ≈ 136 שניות) על פני כ-64 שניות wall של הורדה. בערך שתי ליבות, רובן kernel time. זו אינדיקציה בלבד, והיא מדידה של ה-relay הזה, לא של המנגנון.
- RTT מודד חיבורי keep-alive ולכן מסתיר את עלות יצירת חיבור דרך ה-relay.

## 4. מה נכשל ולמה (כולל טעויות שלי)

1. **`protect(Socket)` החזיר false על סוקט שלא נקשר.** `Socket()` חדש אין לו fd עד `bind`/`connect`. התוצאה: כל חיבור של אפליקציה ל-forwarder נפתח בלי הגנה וחזר לטאנל, כלומר לולאה. נרשמו 755 שורות `protect(Socket) failed` לפני שעצרתי (הלוג לא נשמר; יש SELFTEST בשורות `q2-kill-run*-logcat.txt`). **תוקן:** `bind` לפני `protect`, ו-`Forwarder` זורק חריגה כשה-protect נכשל במקום לחבר בלי הגנה.
2. **upstream DNS = null בהפעלה קודמת.** `realDnsServer()` נקרא פעם אחת לפני שרשת לא-VPN הייתה מוכנה. הלוג: `upstream dns=null` (נשמר ב-`q2-coldstart-earlier-boot-upstream-null.txt`, מתוך הפלט השמור של אותה בדיקה). **תוקן:** קריאה עצלה מרשת שאינה VPN, עם cache. בהפעלה האחרונה ה-DNS היה זמין בזמן הפעלה (`upstream dns at start=/10.0.2.3`), ולכן כישלון ההפעלה המקורי לא שוחזר; קריאה עצלה נרשמה ב-0.25 s אחרי `establish`.
3. **תהליך VPN אחד (pid 4011) הפסיק להגיב לשידורים.** אחרי `kill_vpn` ו-`lockdown_off` השידורים לא הגיעו לתהליך. לאחר התקנה מחדש (`install -r`) התהליך הוחלף. לא אובחנה הסיבה. נבדק מחזור `lockdown_off`→`lockdown_on` בבנייה המתוקנת (שורה 9): התהליך עונה.
4. **`adb reboot` מוציא את האמולטור מהאוויר במארח הזה.** קרה פעמיים (`q2-reboot-FINDING-*.txt`). לכן האתחול נבדק ב-cold start ולא ב-reboot.
5. **ה-emulator נתקע בדיאלוג הסכמה לדוח קריסה** (`Showing crashdialog to get consent`). פתרון: `-crash-report-mode never`.
6. **הרצה ראשונה של kill נכשלה** (`kill -9` מה-shell, uid 2000, לא יכול להרוג תהליך אפליקציה). נשמרה כ-`q2-kill-run1-INVALID-*`. צילום Chrome שחור נשמר כ-`q1-chrome-example-org-INVALID-black-screenshot.png`.
7. **cold start ראשון לא מהימן** (שעון לא עקבי, §3 שורה 10). הבדיקה הבאה הייתה עם `-no-snapshot-load`.
8. **הרצת probe ברקע נחסמת** על ידי `APP_BACKGROUND` של מערכת האמולטור. לכן WebProbeActivity נשאר בחזית.
9. **`goAsync` עם פעולה ארוכה גרם ל-ANR** אחרי 10 שניות. עברנו לביצוע ללא שידור, עם תוצאה בלוג.
10. **הצלחה בהרצה 1 של kill (DNS +71 ms)** בטבלה נשארה כפי שהיא, עם ההסבר.

## 5. מה נבדק ומה לא

**נבדק:** טאנל מלא עם lockdown; Chrome, אפליקציה שנייה ו-WebView; חסימת DNS דרך ה-resolver שלנו; kill של תהליך ה-VPN (3 הרצות) ו-dead window עם `ip rule`; force-stop (4 ניסיונות, נדחה); stop_self (לא עוצר); מחזור lockdown_off/on; נתיבי התהליך עצמו (Q3); cold start עם DNS ו-HTTP דרך הטאנל; תעבורת מערכת על eth0 בזמן אתחול ותחת lockdown; QUIC ו-IPv6 דרך מוני ה-forwarder; עלות יחסית ב-emulator; 19 מבחני JVM.

**לא נבדק (ולמה):**
- **מכשירי אמת ויצרנים**: Lenovo TB328XU, Galaxy A07, Poco, S8 לא נגעו; רק AOSP emulator.
- **battery killers של יצרנים**: לא נבדקו (מחוץ לתחום).
- **SIM אמיתי ו-IPv6 אמיתי ו-464XLAT**: האמולטור לא סיפק מסלול IPv6 לאינטרנט אמיתי, ולא נבדק SIM.
- **תעבורה של Wi-Fi (ברירת המחדל) בזמן האתחול**: הלכידה מכסה eth0 בלבד (שורה 11). לא ידוע אם משהו יצא דרך Wi-Fi לפני שהטאנל עלה. החלון 44.3–45.6 s לא נדגם.
- **הפעלה מחדש אוטומטית** אחרי נפילה: אין watchdog. בשלוש הרצות הטאנל לא חזר ב-90 שניות.
- **ניסיון אפליקציה לצאת בזמן האתחול (חלון 14–44 s)**: לא היה ממשק רשת בחלון הזה (wlan0 נוצר ב-44.3 s), ולכן אף uid לא יכול היה לצאת, בלי קשר ל-lockdown. בדיקה ישירה של אפליקציה ב-uid שאינו חריג נשארת פתוחה.
- **Wi-Fi (wlan0) בלכידה**: לא נכלל.
- **uid 10238 (probe) עם bind לרשת הסלולר**: לא נבדק ישירות (ראו §6).
- **DoH (HTTPS ל-resolver ציבורי)**: לא נבדק. הטאנל לא חוסם לפי שם HTTPS, רק DNS.
- **סוללה, OOM, חריגות לא מטופלות**: לא נבדקו.
- **`targetSdk 37`**: לא נבדק.

## 6. החלטה: GO מותנה, עם ממצא פתוח

**מה מוכח:** עבור אפליקציה בכל uid שאינו החריג, lockdown עם טאנל מלא חוסם את כל התעבורה כשהטאנל לא פעיל (dead window, `EACCES`), ו-DNS חסום מוחזר מקומית. בזמן האתחול כללי ה-prohibit נדגמו בכל דגימה מ-32.7 s עד 43.8 s, ואלה נדגמו לפני שרשת כלשהי עלתה ב-44.3 s. **מה שלא נדגם:** החלון 44.3–45.6 s (wlan0 קיים והטאנל עוד לא), ולא נוסה שום uid שאינו חריג לצאת בחלון הזה. אחרי הפעלה מחדש של הטאנל, DNS ו-HTTP דרכו עובדים. מחזור `lockdown_off`→`lockdown_on` עובד.

**הקשר לכללים:** כללי ה-prohibit מתאימים ל-`fwmark 0x0/0x20000 iif lo`, כלומר לסוקטים שלא מסומנים בסימון הזה. סוקטים שהמערכת מסמנת (`protect`, וגם תעבורת המערכת) נכנסים מהחוקה. **לא נבדק** אם אפליקציה רגילה יכולה לקבוע את הסימון הזה (לכן כלל משמעת הסוקטים בתנאי 3).

**ממצא פתוח (נדרשת החלטת הבעלים):** תעבורת מערכת יוצאת על הסלולר מחוץ לטאנל, גם תחת lockdown מלא: DNS ל-`10.0.2.3`, TCP 853, TCP 80/443 ל-Google (בדיקת תקינות רשת). זו לא תעבורה של אפליקציה, אבל "כל התעבורה דרכנו" לא נכון לגבי תעבורת מערכת. יש לקבוע לפני ייצור: (א) האם צריך לחסום את תעבורת המערכת הזו בדרך אחרת, (ב) האם מספיק לחסום אותה דרך הטאנל ברמה נמוכה יותר, או (ג) לקבל שהיא מחוץ לטווח המוצר. **ההשלכה להחלטה:** ללא החלטה כזו, הרישא "כל התעבורה" בתכנית לא מתקיימת.

**תנאים לפני ייצור:**
1. **הפעלה מחדש אוטומטית** של הטאנל (watchdog מה-DPC). נדרש ספייק נפרד.
2. **ממצא תעבורת המערכת** (סעיף 6 למעלה): החלטה ואימות.
3. **משמעת סוקטים באפליקציה**: uid 10237 מוחרג מ-lockdown (גם ה-DPC חולק אותו uid). כל סוקט של האפליקציה חייב `protect` או לעבור דרך הטאנל. **לא** לפתוח `bindSocket` לרשת. הכלל נאכף בבדיקת קוד. בדיקה ישירה של uid 10238 עם bind לסלולר נשארת פתוחה.
4. **relay בטוח**: הקריאה העצלה ל-DNS עובדת, אבל ה-relay פשוט מדי לייצור (ללא window scaling, SACK או fast retransmit). לכתוב מחדש עם בדיקות, או לעבור לספרייה MIT (hev/tun2socks) אחרי אישור הבעלים.
5. **עלות CPU ו-throughput על מכשיר אמיתי** ובעומס. האמולטור לא מספיק, וההבדלים בין ההרצות לא מוסברים.
6. **רישיון**: הרכיב הסופי חייב להיות MIT/Apache/BSD. GPL (NetGuard) לא.
7. **בדיקת אתחול על מכשיר אמיתי**: תעבורה מuid שאינו חריג בחלון 44.3–45.6 s (wlan0 קיים, הטאנל עוד לא), ולכידה שמכסה גם Wi-Fi. בדיקה זו לא נעשתה על האמולטור.

**מה זה אומר ביחס ל-A0.2:** הטאנל המלא מתקן את שני הכשלים של A0.2 לגבי תעבורת אפליקציות: DNS של המערכת עובד תחת lockdown, וחיבורי TCP ישירים מקבלים מענה (לא `EACCES`) כשהטאנל פעיל. ב-A0.2 DoT נזרק בשקט. כאן הוא מקבל RST (`tcp853_rst`), וזה **תואם** את הסברה ש-Android חוזר ל-fallback, אבל לא נמדד ישירות. ה-A0.2 לא בדק תעבורת מערכת; הממצא החדש (§6) הוא שתעבורת מערכת יוצאת החוצה תחת lockdown.

## 7. קבצים

- קוד: `kotlin/` (`app/` ה-DPC וה-VPN, `probe/` האפליקציה השנייה, `app/src/test/` 19 מבחני JVM, ראו `evidence/TEST-*.xml`).
- סקריפטים: `scripts/` (`common.ps1`, `q1-browse.ps1`, `q2-kill.ps1`, `q2-coldstart.ps1`, `q2-coldstart2.ps1`, `q4-vpn.ps1`, `pcap_summary.py`).
- ראיות: `evidence/`. ניסיונות שנפסלו מסומנים `INVALID`, ותצפיות שלא נשמרו מסומנות `FINDING` או `INVALID`.
- כל לכידת pcap נוצרה על ידי `emu network capture start <bare-filename>`. הקובץ נכתב לתיקיית `C:\Users\Eran Finish\.android\avd\Medium_Phone.avd\console_out\` (מחוץ לספייק), ונעתק לראיות. העותקים בתיקיית ה-AVD נמחקו אחרי אישור.
- **שינויים מחוץ לספייק:** (א) כתיבת ה-pcap ל-`console_out` של ה-AVD; (ב) `emu kill` שומר מחדש את ה-snapshot `default_boot` של ה-AVD, כולל מצב ה-Device Owner של הבדיקה. ה-snapshot של האמולטור נשמר שלוש פעמים בזמן הספייק. ה-AVD לא נמחק.
- `.gitignore` של הספייק. לא בוצע commit ולא push. שום קובץ בתוך `src/` או `docs/` לא שונה.
- ניקוי: ראו `a0-2b-handover.md` בתיקיית ההערות המשותפת (נכתב בסוף הסשן).
