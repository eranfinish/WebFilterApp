# ספייק A0.20: האם אפליקציה רגילה יכולה לצאת מחוץ לטאנל תחת lockdown, ומה הפערים של DoH

**מסקנה (א): על API 31 לא נמצאה בריחה של אפליקציה רגילה תחת lockdown** (uid 10148, בלי הרשאות מיוחדות). נבדקו `bindSocket`, `openConnection`, `bindProcessToNetwork`, `SO_MARK`, ו-DNS של הרשת המקושרת. בנוסף נבדקה הורדה דרך `DownloadManager`, שהיא proxy של שירות מערכת ולא תעבורת המערכת עצמה. התנאי של התוכנית ("אפליקציה רגילה לא יכולה לצאת באותה דרך") מתקיים על האמולטור, בכפוף לנתיבים שנבדקו בלבד.

**מסקנה (ב): נשארו פערים בתוך הטאנל** בסינון ה-DNS: DoH לפי IP עוקף את הסינון לפי שם, HTTP לפי IP עובר, שכבת ה-SNI חלקית, ושכבת ה-IP מכסה רק כתובות ברשימה. זה לא בריחה, אבל הוא נדרש לפני ייצור. ראו §4.

הכול על **AOSP emulator, Android 12 (API 31), Tablet_A12** בלבד. לא נבדקו מכשירי אמת. כל פריט בטבלה מגובה בקובץ ב-[evidence/](evidence/). תוצאות שנפסלו מסומנות ב-[e20-00-CORRECTIONS.txt](evidence/e20-00-CORRECTIONS.txt).

---

## 1. סביבה ושיטה

| פריט | ערך |
|---|---|
| מכשיר | AVD `Tablet_A12`, Android 12, `ro.build.version.sdk=31`, `google_apis_playstore`, x86_64. מזהה ADB `emulator-5590` בלבד (`emulator-5580`, של סשן אחר, לא נגעתי בו) |
| Chrome | 91.0.4472.114 (מותקן מראש) |
| DPC (`com.webfilter.spike.a020`) | uid 10147, Device Owner, testOnly. מריץ את הטאנל ב-`:vpn` (uid 10147) |
| Probe (`com.webfilter.spike.a020.probe`) | uid **10148**. הרשאות: `INTERNET`, `ACCESS_NETWORK_STATE` בלבד. בלי `sharedUserId`, בלי הרשאה מיוחדת. לא ברשימת חריגים של lockdown (ראו `ip rule`: `uidrange 1-10146` ו-`10148-99999` עם `prohibit`) |
| טאנל | forwarder ה-Kotlin של A0.2b, עם שכבות DoH (ראו §2). lockdown דולק בכל הבדיקות שמוצגות כ"תחת lockdown" |
| מעקב | מונים של ה-forwarder (`STATS`) **ולוג של כל SYN חדש** (`A020 syn <ip>:<port>`). הלוג הוא הראיה הקובעת: מונים יכולים לקבל תעבורה של מערכת |
| בדיקות יחידה | 26/26 עוברות (`ForwarderTest` 8, `PacketTest` 11, `TlsAndRulesTest` 7) |

**שני תיקונים לשיטה שמשפיעים על התוצאות (פירוט ב-[e20-00-CORRECTIONS.txt](evidence/e20-00-CORRECTIONS.txt)):**
1. **פורט כמחרוזת.** `--es port 853` נקרא על ידי הפרוב כ-`getIntExtra` ונשאר ברירת המחדל 443. בריצות הראשונות "853" היה 443. התיקון: `--ei`.
2. **pool של HTTP.** `HttpURLConnection` של Android משתמש ב-pool משלו, גם כש-`http.keepAlive=false`. בקשה חדשה יכולה להשתמש בשקע שכבר בתוך הטאנל, ואז אין SYN חדש. לכן כל בדיקת "בריחה" נעשית עם `raw Socket` (op `http_host`), בלי pool.

---

## 2. מה נבדק ומה התוצאה (מטריצה)

| # | יכולת | ספק / API | תוצאה | ראיה |
|---|---|---|---|---|
| E1 | `Network.socketFactory.createSocket()` + bind לרשת wifi (netId 100) ו-cell (eth0, netId 102), תחת lockdown | AOSP, API 31 | **חסום**: `Binding socket to network … failed: EPERM`. בקרה (ברירת מחדל) מתחברת | [e20-03](evidence/e20-03-escape-bindsocket-openconnection-bindprocess.txt) |
| E2 | `Network.openConnection(url)` על wifi/cell | AOSP, API 31 | **חסום**: EPERM (אותו מנגנון) | [e20-03](evidence/e20-03-escape-bindsocket-openconnection-bindprocess.txt) |
| E3 | `bindProcessToNetwork(wifi/cell)` + `raw Socket` ל-`104.20.26.136:80` | AOSP, API 31 | **ללא בריחה**: ה-SYN נרשם בלוג של ה-forwarder (`srcPort` ייחודי לכל בקשה), והבקשה הצליחה דרך הטאנל. הפרוב לא רשם את ערך ההחזרה של `bindProcessToNetwork`, לכן לא ידוע אם ה-bind נדחה או הועבר לטאנל. התוצאה (ללא בריחה) זהה בשני המקרים | [e20-17 wifi](evidence/e20-17-raw-bindproc-wifi-example-by-ip.txt), [e20-17 cell](evidence/e20-17-raw-bindproc-cell-example-by-ip.txt) |
| E4 | אותו מבחן עם `HttpURLConnection` (בלי `raw`) | AOSP, API 31 | **לא תקף** (pool). תוצאה "בלי SYN, 200" הייתה ארטיפקט | [e20-00](evidence/e20-00-CORRECTIONS.txt) |
| E5 | `SO_MARK` = `0x20001` ו-`1`, דרך `Os.setsockoptInt` | AOSP, API 31 | **חסום**: `EPERM` לשני הערכים (דורש CAP_NET_ADMIN). הערך 36 = `SO_MARK` של לינוקס (לא חשוף ב-SDK הציבורי) | [e20-19](evidence/e20-19-so-mark.txt) |
| E6 | DNS של הרשת דרך `Network.getAllByName("en.wikipedia.org")` על wifi | AOSP, API 31 | **נשאר בטאנל**: הבקשה הגיעה ל-forwarder (`dns blocked`), התוצאה UnknownHost. בקרה `example.org` עובדת על wifi ו-cell | [e20-18](evidence/e20-18-netdns-wifi-blocked-name.txt), [e20-18b](evidence/e20-18b-netdns-any-control.txt) |
| E7 | DNS של `bindProcessToNetwork(wifi)` לשם חסום | AOSP, API 31 | נשאר בטאנל (`dns_blocked`) | [e20-15-A](evidence/e20-15-A-bindproc-wifi-blocked-name-dns.txt) |
| E8 | UDP DNS ל-`8.8.8.8:53` עם `example.org` | AOSP, API 31 | **דרך הטאנל**: `REPLY rcode=0`, `dns_relayed` עולה | [e20-08](evidence/e20-08-udp-dns-dot-tcp53.txt) |
| E9 | UDP DNS ל-`8.8.8.8:53` ול-`1.1.1.1:53` עם `en.wikipedia.org` | AOSP, API 31 | **נחסם בשם**: `rcode=3` מה-forwarder (`dns_blocked`) | [e20-08](evidence/e20-08-udp-dns-dot-tcp53.txt) |
| E10 | TCP ל-`1.1.1.1:853` (DoT) | AOSP, API 31 | **נדחה מהר**: RST מה-forwarder, `ECONNREFUSED` ב-3–5 ms (`tcp853_rst`) | [e20-12](evidence/e20-12-ports-853-53-8853-fixed-int.txt) |
| E11 | TCP ל-`8.8.8.8:53` | AOSP, API 31 | **נדחה מהר**: RST (`tcp53_rst`), `ECONNREFUSED` | [e20-12](evidence/e20-12-ports-853-53-8853-fixed-int.txt) |
| E12 | TCP ל-`1.1.1.1:8853` (בקרה) | AOSP, API 31 | **לא ברור** (ראו §5): ה-SYN נרשם והעליה נכשלה ב-timeout, אבל הפרוב דיווח `CONNECTED` אחרי 1018 ms. לא משתמש בזה כראיה | [e20-12](evidence/e20-12-ports-853-53-8853-fixed-int.txt) |
| E13 | `DownloadManager` ל-`http://example.org/` (proxy של שירות מערכת, לא uid של האפליקציה) | AOSP, API 31 | **דרך הטאנל** בריצה המקורית: `SUCCESSFUL`, ו-SYN ל-`172.66.157.237:80` נרשם ב-forwarder. *הפלט נרשם בטרמינל בלבד ולא נשמר לקובץ* (ראו §5) | ראו §5 |
| E14 | `DownloadManager` ל-`http://en.wikipedia.org/` | AOSP, API 31 | **נחסם בשם** בריצה המקורית: `dns blocked`, סטטוס `TIMEOUT`. *לא נשמר לקובץ* | ראו §5 |
| D0 | DoH בלי חסימה (`dns.google`, `cloudflare-dns.com`), IP+SNI, ושם | AOSP, API 31 | **דלף**: תשובת DoH עם `en.wikipedia.org → dyna.wikimedia.org → 185.15.59.224`, גם בשם וגם ב-IP | [e20-doh-D0-none](evidence/e20-doh-D0-none.txt), [e20-15-B](evidence/e20-15-B-dohhost-ips.txt) |
| D1 | שכבת DNS (`dohNames`): שם DoH חדש נחסם | AOSP, API 31 | **נחסם בשם**: `1dot1dot1dot1.cloudflare-dns.com` ← `dns blocked`. בקרה `example.org` עובד. **אבל DoH לפי IP+SNI ממשיך לעבוד** | [e20-doh-name-layer-D1-fresh](evidence/e20-doh-name-layer-D1-fresh.txt), [e20-doh-D1-dns](evidence/e20-doh-D1-dns.txt) |
| D2 | שכבת IP (`dohIp`) + DNS | AOSP, API 31 | **נחסם לכתובות ברשימה**: `8.8.4.4`, `8.8.8.8`, `104.16.248.249` נדחות ב-SYN (`doh_ip_rst`). כתובת שלא ברשימה (ה-IP של ויקיפדיה) נשארת פתוחה | [e20-doh-D2-dns-ip](evidence/e20-doh-D2-dns-ip.txt) |
| D3 | שכבת SNI (`sni`) לבד | AOSP, API 31 | **נחסם לפי SNI**: `dns.google` (8.8.4.4 ו-8.8.8.8), `cloudflare-dns.com` (104.16.248.249), ו-`en.wikipedia.org` ל-`185.15.59.224`: כולם `Connection reset` בלחיצת היד | [e20-doh-D3-sni](evidence/e20-doh-D3-sni.txt) |
| C1 | Chrome, `DnsOverHttpsMode=secure` (מדיניות דרך app restrictions) | AOSP, Chrome 91 | **לא נבדק בסבב הסופי**: ה-DPC לא פעיל (`No active admin`), לכן המדיניות לא הוגדרה, והטאנל לא עלה (`start` מהמעטפת נדחה: `BIND_VPN_SERVICE`). ראיה מוקדמת מ-A0.20 (לפני התקלה, טאנל פעיל): SYNs מ-Chrome ל-`8.8.4.4:443` ו-`8.8.8.8:443` אחרי הפעלת `secure`. זה מתאים ל-DoH, אבל בלי השוואה ל-`off` זה לא מוכיח שהמדיניות נקראה, והדף לא נטען. | [e20-20](evidence/e20-20-chrome-secure-control-after-wait.xml) |
| C2 | Chrome, `DnsOverHttpsMode=off` וקריאת `chrome://policy` | — | **לא נבדק**: אותה סיבה כמו C1. ההורה אישר את מסך הפתיחה של Chrome ידנית (לא הסוכן). לאחר מכן הופיע "Turn on sync?", ונלחץ "No thanks" (בלי כניסה). מצב Chrome: [e20-22 לפני](evidence/e20-22-chrome-state-check.xml), [e20-22 אחרי](evidence/e20-22-chrome-state-after-no-thanks.xml) | — |

---

## 3. מה זה אומר על השאלה של התוכנית

**הסעיף של התוכנית (תעבורת מערכת מחוץ לתחום בתנאי ש"אפליקציה רגילה לא יכולה לצאת באותה דרך") נבדק בשלושה נתיבים מתוך שלושה שהתוכנית מונה:**
- `bindSocket`/`openConnection`: חסום (EPERM).
- `bindProcessToNetwork`: נשאר בטאנל (SYN נרשם). ה-DNS של הרשת המקושרת נשאר בטאנל גם הוא.
- fwmark (`SO_MARK`): חסום (EPERM).
- ה-DownloadManager, שרץ בתהליך מערכת: נשאר בטאנל (SYN נרשם).

**לא מצאתי נתיב בריחה לאפליקציה רגילה תחת lockdown על API 31.** זה תומך בהחלטה "תעבורת מערכת מחוץ לתחום". אבל ההחלטה הזאת נשענת על ראיות בעלות תוקף מוגבל (ראו §5), לכן היא לא "GO" נקי.

---

## 4. פערים בתוך הטאנל (לא בריחה, אבל גם לא סגור)

הפערים האלה לא נובעים מבריחה. הם נובעים מכך שהסינון מבוסס על DNS בלבד:

1. **DoH לפי IP עוקף את סינון ה-DNS.** ב-D1 (שכבת DNS לבד) DoH לפי `8.8.8.8` עם SNI `dns.google` מחזיר את התשובה לשם חסום. זה הפער שהתוכנית ציינה.
2. **שכבת ה-IP חלקית.** היא חוסמת רק כתובות שמופיעות ברשימה, וה-DoH של Google וCloudflare משתמש בהרבה כתובות. הרשימה צריכה תחזוקה.
3. **שכבת ה-SNI** חוסמת לפי שם השרת בלחיצת היד. מגבלות: בודקת רק את **החבילה הראשונה** בפורט 443; לא בודקת ClientHello שמפוצל על פני כמה חבילות; ו-ECH (Encrypted Client Hello) לא נבדק. גודל ה-ClientHello של הפרוב לא נמדד.
4. **HTTP רגיל לפי IP עובר.** `http_host` לכתובת של `en.wikipedia.org` עם `Host:` שלו מחזיר `301` דרך הטאנל ([e20-16-B](evidence/e20-16-B-http-by-ip-default-route.txt)). ה-forwarder לא בודק Host.
5. **DNS לפורטים אחרים** (למשל UDP 5353, DoQ על UDP 853) לא נבדק. DoT ב-TCP 853 נחסם; DoH3 (UDP 443) נדחה ב-ICMP.

---

## 5. מה לא נבדק או לא תקף

- **E13 ו-E14 (DownloadManager):** הפלט של הריצה המקורית היה בטרמינל בלבד ולא נשמר לקובץ. ניסיון חזרה על ה-build של A0.21 נכשל: ה-DPC איבד את ה-admin (`No active admin`), לא עלה טאנל, ולכן ההורדות עברו ברשת רגילה. הפלט מסומן כלא תקף: [e20-21-downloadmanager-rerun-INVALID-no-tunnel.txt](evidence/e20-21-downloadmanager-rerun-INVALID-no-tunnel.txt). המסקנה על E13/E14 נשענת על הריצה המקורית בלבד.
- **E12 (8853):** הפרוב דיווח `CONNECTED` אחרי 1018 ms, אבל העליה של ה-forwarder נכשלה ב-timeout. ייתכן שזה באג ב-relay (נשלח SYN-ACK לפני שהחיבור לשרת מוכן, או טיפול כפול ב-SYN). לא השתמשתי בזה. זה נכנס לרשימת בטיחות ה-relay (תנאי 4 בתוכנית).
- **Chrome (C1 חלקי, C2 לא נבדק):** מסך הפתיחה של Chrome מחכה לאישורך (תנאי השימוש של Google). ללא אישור לא נלחץ, ולכן `chrome://policy` ו-`DnsOverHttpsMode=off` לא נבדקו.
- **Chrome דרך המדיניות מול דפדפן רגיל:** לא נבדק.
- **שמות מוחזקים ב-cache של netd:** בשלבי D0–D3 שמות DoH כמו `dns.google` כבר נפתרו קודם, ולכן הבדיקה "לפי שם" בשלבים האלה מושפעת מה-cache. ה-cache אי אפשר לנקות ממעטפת `adb shell` (`ndc` נדחה). לכן השכבה "לפי שם" נבדקה רק עם שם שלא נפתר קודם (`1dot1dot1dot1.cloudflare-dns.com`, ב-D1).
- **IPv6:** לא נבדק DoH ב-IPv6.
- **מכשירי אמת, סוללה, מעבר Wi-Fi/סלולר:** לא נבדקו.
- **Android 17 (API 37):** לא נבדק בספייק הזה.

---

## 6. החלטה ושאלה למשתמש

- **החלטה:** לא מצאתי בריחה של אפליקציה רגילה דרך שלושת הנתיבים (E1–E5 ו-E13). **תעבורת המערכת מחוץ לתחום** נשארת נכונה, בתנאי שמקבלים את הפערים של §4.
- **חובה לפני ייצור:** שכבת SNI בתוספת רשימת IP של DoH שמתחזקים (D2 לבד לא מספיקה), והחלטה בנוגע ל-HTTP לפי IP (§4.4).
- **Chrome:** מסך הפתיחה אושר ונלחץ ידנית על ידי ההורה. לא הוכחה הגדרת המדיניות, כי ה-DPC לא פעיל (ראו הטבלה, C1/C2). התקלה היא אותה תקלה של Device Owner שמתוארת ב-A0.21 §7, ולכן לא ניתן לבדוק את המדיניות בלי איפוס של ה-AVD.

---

## 7. קבצים

- קוד: [kotlin/](kotlin/) (`app/` ה-DPC וה-forwarder, `probe/` האפליקציה השנייה).
- סקריפטים: [scripts/lib.sh](scripts/lib.sh) (`--ei` למספרים, `MSYS_NO_PATHCONV=1` לנתיבי מכשיר), [scripts/doh-layers.sh](scripts/doh-layers.sh).
- ראיות: [evidence/](evidence/). ניסיונות שנפסלו מסומנים ב-[e20-00-CORRECTIONS.txt](evidence/e20-00-CORRECTIONS.txt).
- שינויים מחוץ לספייק: אין. לא בוצע commit.
- מצב סופי: ה-DPC של A0.20 הוסר כ-Device Owner לפני ספייק A0.21 (`dpm remove-active-admin`), ושתי האפליקציות של A0.20 הוסרו.
