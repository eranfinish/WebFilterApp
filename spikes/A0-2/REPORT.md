# ספייק A0.2: VPN של DNS בלבד, always-on עם lockdown, על Android 17 (API 37)

**מסקנה: NO-GO לתכנון כפי שהוא** (DNS בלבד + always-on עם lockdown) על AOSP emulator, API 37. תחת lockdown הטאנל מעביר רק את כתובת ה-DNS שלו: תעבורה TCP ישירה נחסמת ב-`EACCES`, וגם חיפושי DNS של המערכת לא מגיעים לטאנל. בלי lockdown התכנון עובד (חסימה והרשאה עובדות ב-Chrome, באפליקציה השנייה וב-WebView), אבל כשתהליך ה-VPN נופל הרשת נפתחת מיד (**fail-open**). ההחלטה על הכיוון הבא שייכת לבעלים ([§6](#6-המלצה-להחלטת-הבעלים)).

מטרת הספייק: לענות על חמש השאלות ב-`docs/ANDROID-PLAN.md` (A0.2 ושורת האכיפה "דומיינים בכל אפליקציה"). הקוד ב-[kotlin/](kotlin/), הפלט הגולמי של כל פקודה ב-[evidence/](evidence/).

---

## 1. סביבה

| פריט | ערך |
|---|---|
| מכשיר | AVD `Medium_Phone_API_37.0`, Android 17 (`ro.build.version.sdk=37`), image `google_apis_playstore`, build `user`, fingerprint `google/sdk_gphone64_x86_64/emu64xa:17/CE2A.260420.051.A2/16418371:user/release-keys` |
| מזהה ADB | כל הבדיקות עד הרצת חלון הנפילה רצו על `emulator-5556`. ההרצה האחרונה (חלון הנפילה תחת lockdown) עלתה כ-`emulator-5554`, ואימתתי שזה `Medium_Phone_API_37.0` לפני כל פקודה ([§4](#4-מה-נכשל-ולמה-כולל-טעויות-שלי), פריט 9). לא שלחתי פקודה למכשיר אחר |
| בניה | AGP 8.9.1, Kotlin 2.0.21, Gradle wrapper 8.11.1 (כמו A0.1), `compileSdk 36`, `targetSdk 36`, `minSdk 31`. JDK 17. הקוד הפונקציונלי נבנה ורץ; שינוי אחרון בקוד היה הערה בלבד (ב-`DnsVpnService.kt`) |
| בדיקות יחידה (JVM) | 7/7 עברו: פרסור IPv4/UDP/DNS, בניית NXDOMAIN עם ID ו-question מוחזרים, checksum של כותרת IPv4, כללי חסימה. [evidence/01-unit-tests-summary.txt](evidence/01-unit-tests-summary.txt) |
| DNS upstream | `10.0.2.3`, כפי ש-LinkProperties מדווח לרשת (`DnsAddresses: [/10.0.2.3]`, [evidence/27](evidence/27-vpn-mtu-routing-check.txt)). סוכן אמיתי צריך לקרוא את ה-DNS מ-LinkProperties ולא לקודד אותו |
| כללים קשיחים | חסומים: `blocked.example`, `blocked.test`, `wikipedia.org` (כולל תתי-דומיינים). מותרים לבדיקה: `example.com`, `example.org`, `neverssl.com` |

**מה נבנה.** `:app` הוא ה-DPC וה-`VpnService` (`DnsVpnService`, `CmdReceiver`, `AdminReceiver`) וחבילת הפרסור הטהורה `dns/`. `:probe` הוא אפליקציה שנייה (uid נפרד) שמבצעת בקשות DNS, HTTP, TCP ו-`watchdns`, ויש בה `WebProbeActivity` ל-WebView. כל פעולה נשלטת מ-adb (`am broadcast -W -n ... --es op ...`), והתוצאות נרשמות בתג `A02` / `A02P`.

---

## 2. טבלת תוצאות (מטריצה)

שורה "לא נבדק" או "לא הוכרע" מסמנת שאין לי ראיה מספקת.

| # | יכולת | ספק | API | תוצאה | ראיה |
|---|---|---|---|---|---|
| 1 | `dpm set-device-owner` על מכשיר עם 0 חשבונות | AOSP emulator | 37 | **הצליח** | [10](evidence/10-install-owner-consent.txt), [66](evidence/66-run3-setup-alwayson.txt), [64](evidence/64-final-cleanup-state.txt) |
| 2 | הסכמת VPN בלי הענקה מ-adb | AOSP emulator | 37 | **Device Owner מעניק הסכמה.** בהתקנה נקייה `prepare()` מחזיר `intent(consent-needed)`. אחרי `setAlwaysOnVpnPackage` (lockdown=true, `consent=needed`) הטאנל עלה (`establish ok`) בלי פקודת `appops`, ו-`appops get` מראה אחר כך `ACTIVATE_VPN: allow` | [61](evidence/61-consent-no-adb-grant.txt), [66](evidence/66-run3-setup-alwayson.txt) |
| 3 | `setAlwaysOnVpnPackage(admin, pkg, true)` (lockdown) | AOSP emulator | 37 | **הצליח.** קריאה חוזרת: `pkg` ו-`lockdown=true` | [12](evidence/12-alwayson-lockdown-on.txt) |
| 4 | עלייה אוטומטית אחרי אתחול, עם lockdown (Q1) | AOSP emulator | 37 | **כן.** `boot_id` השתנה, ההגדרה נשמרה, `onCreate` ב-`…821747`, `establish ok` ב-`…822103` (כ-356 ms אחרי `onCreate`). `sys.boot_completed` נצפה ב-`…820301` בפולינג כל 2 שניות, כלומר הפער בין השלמת האתחול לטאנל הוא בין 1.8 ל-3.8 שניות | [33](evidence/33-reboot-lockdown.txt) |
| 5 | **Gate: תעבורה שאינה DNS, VPN למעלה, lockdown פעיל** | AOSP emulator | 37 | **נכשל.** `connect` ישיר ל-`172.66.147.243:443` נכשל ב-`EACCES`. חיפושי DNS של המערכת לא הגיעו לטאנל | [13](evidence/13-gate-and-rule-tests.txt), [18](evidence/18-lockdown-dns-check.txt), [32](evidence/32-lockdown-watchdns.txt) |
| 6 | Gate: אותה תצורה **בלי** lockdown | AOSP emulator | 37 | **עבר.** `connect` ישיר הצליח, DNS עבד | [16](evidence/16-experiment-vpn-up-no-lockdown.txt), [23](evidence/23-q3-probe-vpn-nolock.txt) |
| 7 | חסימת DNS בלי lockdown (Q3) | AOSP emulator | 37 | **עבד.** `resolve` לשמות החסומים נכשל (NXDOMAIN) | [23](evidence/23-q3-probe-vpn-nolock.txt) |
| 8 | חסימה בדפדפן Chrome (Q3) | AOSP emulator, Chrome מותקן מראש | 37 | **נחסם.** מסך `DNS_PROBE_FINISHED_NXDOMAIN` | [30](evidence/30-chrome-wiki-ui.txt), [30b-chrome-wiki.png](evidence/30b-chrome-wiki.png) |
| 9 | הרשאה בדפדפן Chrome (Q3) | AOSP emulator, Chrome | 37 | **עבד.** `https://example.org/` נטען | [31c](evidence/31c-chrome-example-org.txt), [31-chrome-example-org-vpn-nolock.png](evidence/31-chrome-example-org-vpn-nolock.png) |
| 10 | אפליקציה שנייה (`:probe`) וWebView (Q3) | AOSP emulator, WebView | 37 | **עבד.** WebView: חסום → `ERR_NAME_NOT_RESOLVED`, מותר → נטען | [23](evidence/23-q3-probe-vpn-nolock.txt), [57](evidence/57-webview-check.txt), [57b](evidence/57b-webview-example-org.txt) |
| 11 | נפילה של תהליך ה-VPN תחת lockdown (Q2) | AOSP emulator | 37 | **נחסם בזמן הנפילה, ולא חזר.** בהרצה 3 (אחרי `killProcess`): הטאנל נעלם, `ip rule` מכיל 3 כללי `prohibit`, `connect` ישיר נכשל ב-`EACCES`, DNS נכשל, ו-`watchdns` מדווח `DOWN`. ב-120 שניות לא נרשם `onCreate` חדש | [67](evidence/67-lockdown-down-window-probes.txt), [35](evidence/35-crash-recovery-watch.txt) |
| 12 | `stopSelf` מבוקש תחת lockdown (Q2) | AOSP emulator | 37 | **לא חזר** ב-60 שניות (לא נרשם `establish ok`). **חלון החסימה לא נבדק בבדיקה הזו**, ולכן אין לי ראיה לחסימה בזמן ה-stop | [37](evidence/37-stopvpn-lockdown-restart-check.txt) |
| 13 | `am force-stop` תחת lockdown (Q2) | AOSP emulator | 37 | **לא חזר** ב-60 שניות. **חלון החסימה לא נבדק** | [36](evidence/36-stop-and-forcestop-lockdown.txt) |
| 14 | נפילה בלי lockdown (ביקורת לשאלה 2) | AOSP emulator | 37 | **fail-open.** אחרי `killProcess` הטאנל נעלם, והשם החסום `www.wikipedia.org` נפתר | [38](evidence/38-control-nolockdown-vpn-dead.txt) |
| 15 | DoT opportunistic של Android (TCP 853 ל-`10.111.0.53`) | AOSP emulator | 37 | **נופל לטאנל ונזרק בשקט.** הבקשה מגיעה ל-tun וחוזרת על עצמה בהפסקות של 1, 2, 4 שניות, והשירות לא מחזיר `RST` | [26](evidence/26-drop-analysis.txt) |
| 16 | עקיפה דרך Chrome Secure DNS (DoH) (Q4) | AOSP emulator, Chrome | 37 | **לא נבדק.** עם ה-DO האפשרות הייתה נעולה ("This setting is disabled on managed browsers", `enabled=false`). אחרי הסרת ה-DO היא פתוחה (`enabled=true`), ובדיקת העקיפה עצמה לא הורצה | [45](evidence/45-secure-dns-options.txt), [46](evidence/46-secure-dns-nodes.txt), [60](evidence/60-chrome-secure-dns-control.txt) |
| 17 | Private DNS בחיבור strict דרך ה-DPC (`dns.google`) (Q4) | AOSP emulator | 37 | **לא מכריע.** גם שמות מותרים וגם חסומים נכשלו, ולכן לא הוכחה עקיפה ולא הוכחה חסימה | [47](evidence/47-q4-private-dns-strict-dpc.txt) |
| 18 | `DISALLOW_CONFIG_PRIVATE_DNS` (Q4) | AOSP emulator | 37 | **חוסם את המסך.** הגדרות > Private DNS: "Blocked by work policy". בקרה בלי ההגבלה (עם DO) מציגה את האפשרויות | [62](evidence/62-private-dns-DO-no-restriction.txt), [63](evidence/63-private-dns-restriction-control.txt) |
| 19 | `DISALLOW_CONFIG_VPN` (Q4) | AOSP emulator | 37 | **חוסם את המסך.** הגדרות > VPN: "Blocked by work policy". אותו דיאלוג הופיע גם בלחיצה על אפליקציית ה-VPN כשה-always-on פעיל, ולכן לא הפרדתי בין שתי הסיבות | [50b](evidence/50b-vpn-restriction-check.txt), [52](evidence/52-vpn-app-detail-lockdown.txt) |
| 20 | כיבוי VPN מ-Settings (Q4) | AOSP emulator | 37 | **לא ניתן דרך המסך שבדקתי.** עם always-on ו-lockdown המסך מציג "Connected / Always on", ולחיצה על האפליקציה נותנת "Blocked by work policy". לא נבדקו תפריט ההתראות וההגדרות המהירות | [51](evidence/51-vpn-settings-alwayson-lockdown.txt), [52](evidence/52-vpn-app-detail-lockdown.txt) |
| 21 | משתמש משני עם DPC כ-profile owner (Q5) | AOSP emulator | 37 | **נוצר** (`createAndManageUser` → `UserHandle{10}`), ו-`profileOwner=true`. always-on כ-PO **עבד** (`lockdown=false`): טאנל עלה, `connect` ישיר הצליח, חסימה והרשאה ב-DNS עבדו | [53](evidence/53-q5-create-user.txt), [54](evidence/54-q5-user10-setup.txt), [55](evidence/55-q5-user10-vpn-nolock.txt) |
| 22 | משתמש 10 עם lockdown (Q5) | AOSP emulator | 37 | **אותה התנהגות כמו משתמש 0.** `connect` ישיר נכשל ב-`EACCES`, `watchdns` הגיב UP, ו-`resolve` של שמות מותרים נכשל. **אתחול ונפילה במשתמש 10 לא נבדקו** | [56](evidence/56-q5-user10-lockdown.txt) |

---

## 3. הסיבה ל-EACCES תחת lockdown (A/B)

זו אותה תצורה של ה-VPN עם שני ערכים שונים בלבד: הדגל `lockdown`.

- **עם lockdown:** [12](evidence/12-alwayson-lockdown-on.txt) מראה כללי `prohibit` בעדיפות `14000` לטווחי uid שאינם ה-VPN. ב-[67](evidence/67-lockdown-down-window-probes.txt) נספרו 3 כללי `prohibit` שנשארים גם כשהטאנל נופל (הספירה בלבד, בלי העדיפות).
- **בלי lockdown:** [17](evidence/17-mechanism-vpn-up-nolock.txt) בלבד מראה שאין כללי `prohibit`. תעבורה שאינה בתוך ה-tun נשלחת לרשת הרגילה, וכך עובד ה-Gate.

לכן, לפי הראיות, lockdown מסנן כל תעבורה שאינה נתבת לטאנל. זה לא בא מכלל DNS, ולכן DNS בלבד לא יכול גם לשמור על נפילה סגורה וגם להעביר תעבורה אחרת.

**הסיבה המדויקת לכך שחיפושי המערכת לא מגיעים לטאנל תחת lockdown לא אותרה.** ב-`dumpsys dnsresolver` לא מצאתי פלט רלוונטי ([18](evidence/18-lockdown-dns-check.txt)). כן מצאתי ש-`watchdns`, ששולח UDP ישיר ל-`10.111.0.53`, מקבל תשובה מהטאנל ([32](evidence/32-lockdown-watchdns.txt)), כלומר הטאנל עצמו פעיל, ורק מסלול ה-DNS של המערכת לא מגיע אליו.

---

## 4. מה נכשל ולמה (כולל טעויות שלי)

1. **ה-Gate תחת lockdown נכשל.** זו התוצאה הקובעת. התכנון "DNS בלבד + lockdown" לא עומד כפי שנכתב ([§3](#3-הסיבה-ל-eacces-תחת-lockdown-ab)).
2. **פרסור ה-DNS לא נכשל.** כל השאילתות שהגיעו לטאנל פורשו והוחזרו נכון ([23](evidence/23-q3-probe-vpn-nolock.txt)). הכשל היה ברמת הניתוב, לא בקוד.
3. **מדידת הנפילה הראשונה (crash #1) לא תקפה.** הפולינג ב-[34](evidence/34-crash-tun-poll.txt) הראה את הטאנל כבר חסר לפני ההרג, ולכן לא השתמשתי בו. המדידה ב-[35](evidence/35-crash-recovery-watch.txt) ו-[67](evidence/67-lockdown-down-window-probes.txt) היא הנכונה.
4. **כשל רשת חולף בתחילת הספייק** ([03](evidence/03-baseline-probe.txt), [04](evidence/04-network-diagnostics-before-restart.txt)). כל החיפושים וה-TCP נכשלו. בדיקה בחזית ([06](evidence/06-foreground-recheck.txt)) ואחריה בדיקה ברקע ([07](evidence/07-background-recheck-after-foreground.txt)) הצליחו, כלומר זו הייתה תקלה חולפת ולא מגבלת רקע. הרצה חוזרת ([08](evidence/08-baseline-probe-rerun.txt), [09](evidence/09-baseline-dns-consistency.txt)) אישרה את הבסיס.
5. **כשל/תקיעה של HTTP ל-`neverssl.com` לא נבע מה-VPN.** בבדיקת A/B ([29](evidence/29-http-ab-vpn-down-vs-up.txt)) `neverssl` נכשל ב-2 מתוך 3 גם כשה-VPN כבוי, ו-`example.org` עבד בשתי המצבים. גם `https://example.com/` נכשל בשני המצבים ([24](evidence/24-control-http-vpn-down.txt)). אלו תקלות של הסביבה.
6. **אמולטור נסגר באופן לא מכוון בתחילת הספייק.** סגרתי את לשונית הטרמינל שהריצה את האמולטור (`stop_terminal_tab`), והתהליך נעצר. ניסיון ההתקנה שבא אחר כך נכשל כולו ב-`device 'emulator-5556' not found` ([20](evidence/20-reinstall-build2.txt) הוא הכישלון הזה בלבד). הפעלתי את האמולטור מחדש בפקודה הנתונה, ו-DO ושתי האפליקציות נמצאו מוגדרות ([21](evidence/21-emulator-restart-check.txt)). אחר כך התקנתי את בנייה 2 מעל הקיימת (`install -r`) ([22](evidence/22-reinstall-build2.txt)). ב-[22](evidence/22-reinstall-build2.txt) `ACTIVATE_VPN` חזר ל-`default` (הכוונה: הענקת ההסכמה התאפסה). לא ניתן להפריד בין שתי הסיבות האפשריות: ההפסקה הלא נקייה, או ההתקנה מחדש.
7. **ניסיון בנייה דרך כלי PowerShell נכשל** בשגיאת `Unable to establish loopback connection` של Gradle. הבנייה נעשתה דרך לשונית טרמינל. בבנייה כושלת אחת (`compileDebugKotlin`) שגיאות הקומפיילר היו `isAlwaysOnVpnPackageSupported` ו-`userHandles` שאינם חשופים ב-SDK 36, ותוקנו.
8. **Chrome Secure DNS נעול כשה-DO מוגדר.** הסיבה המדויקת לא הופרדה: גם ה-VPN היה פעיל באותו רגע. אחרי הסרת ה-DO האפשרויות פתוחות ([60](evidence/60-chrome-secure-dns-control.txt)).
9. **ההרצה האחרונה של האמולטור (חלון הנפילה) נכשלה בהתחלה, ושינתה את ה-serial.** הפעלה ראשונה נתקעה בדיאלוג "Showing crashdialog to get consent" ואחריו שגיאות `Failed to find ColorBuffer` של ה-GPU, והאמולטור לא עלה ל-adb. הדיאלוג נבע מדוח קריסה ישן ב-`emu-crash-37.2.13.db` (דיאלוג שמחכה להסכמה חוסם את העלייה). ניסיון עם `-no-crashreport` נדחה (`unknown option`). ההרצה שעלתה בסופו של דבר השתמשה ב-`-crash-report-mode never`, כלומר **הוספתי דגל אחד לפקודה הנתונה**, רק למניעת העלאת דוחות. באותה הרצה האמולטור שלי קיבל את הפורט `5554` (ולא `5556`), ו-adb הציג רק את `emulator-5554` עם ה-AVD `Medium_Phone_API_37.0` (ואימתתי זאת לפני כל פקודה). האמולטור `Tablet_A12` של ה-session האחר, שעלה ב-02:35, **עדיין קיים כתהליך** (`emulator.exe` וגם `qemu-system-x86_64.exe`), אבל בלי פורט מאזין ובלי מכשיר ב-adb ([69](evidence/69-emulator-process-and-port-check.txt)). לא עצרתי אותו: סגירת התהליכים כוונה רק לתהליכי `Medium_Phone_API_37.0` לפי שורת הפקודה. **לא שלחתי פקודה ל-Tablet_A12.** יש לבדוק אצל ה-session האחר אם הוא זקוק לאמולטור.

---

## 5. מה לא נבדק (ולמה)

- **ספקים אחרים, מכשירי אמת, SIM.** לא נבדקו. הספייק רץ על AOSP emulator בלבד.
- **הרג סוללה (battery killers).** מחוץ לתחום לפי הוראות הספייק.
- **IPv6.** נמצא ש-`::/0 unreachable` במסלולים של רשת ה-VPN ([27](evidence/27-vpn-mtu-routing-check.txt)), כלומר אין מסלול IPv6 כשה-VPN פעיל. האמולטור לא סיפק IPv6 תקין, ולכן לא נמדדה ההשפעה על אפליקציות. סביר שסוכן אמיתי יצטרך `allowFamily(AF_INET6)`, וזה לא נבדק. זה קשור לסעיף 11 בתכנית (SIM ישראלי עם IPv6/464XLAT).
- **DoH של Chrome (עקיפה).** לא הורצה, כי ההגדרה נעולה כשה-DO מוגדר (שורה 16).
- **Private DNS strict.** לא הוכרע (שורה 17). צריך חזרה עם המתנה ארוכה יותר ובדיקת סטטוס ההכרה של ה-DNS.
- **בקשות TCP ל-`10.111.0.53`.** הבקשות נזרקות בשקט. לא בדקתי תגובה ל-TCP.
- **חלון החסימה בעצירה רגילה (`stopSelf`) ובהרג ע"י `force-stop`.** לא נבדק; נבדק רק שהטאנל לא חזר (שורות 12–13).
- **מצבי נפילה נוספים.** לא נבדקו: חריגה לא מטופלת (uncaught exception), OOM kill, ו-restart שנגרם משינוי רשת. תהליך שנהרג ב-`killProcess` הוא מקרה אחד בלבד. החזרה האוטומטית לא נבדקה מעבר ל-120 שניות.
- **זמן החלון לפני ה-establish באתחול.** לא נמדד מתי הרשת נפתחת לגמרי בתוך ~26 שניות מהאתחול. נמדד רק הפער מ-`boot_completed` (שורה 4).
- **Q2 במשתמש 10.** לא נבדקו נפילה ואתחול במשתמש השני, רק הפעלה, Gate וחסימה.
- **הסכמה במשתמש 10.** גם שם ההסכמה הוענקה דרך `appops --user 10`, ולא נבדק הנתיב של ה-DO מסעיף 2.
- **`targetSdk 37`.** הבנייה עם `targetSdk 36` רצה על API 37, לכן התנהגות שמותנית ב-`targetSdk 37` לא נבדקה.
- **צורת טאנל מלאה (`excludeRoute` / ברירת מחדל) וכן tun2socks.** לא נבדקו. טאנל עם מסלול ברירת מחדל מוביל תעבורה לטאנל, ולכן דורש forwarder לכל התעבורה, וזה מחוץ לספייק. לא בניתי אותו.
- **החלפת משתמשים, `startUserInBackground`, `switchUser`.** לא נבדקו.
- **הגדרות מהירות ותפריט ההתראות** לכיבוי VPN (שורה 20).
- **אישור גרסת ה-APK.** שינוי אחרון בקוד היה הערה בלבד, וה-APK שרץ נבנה לפני ההערה. זה לא השפיע על הפונקציונליות.

---

## 6. המלצה להחלטת הבעלים

הספייק הזה לא מסיק מסקנה לגבי הכיוון הבא ולא בונה אותו. שתי אפשרויות שנשארו לא נבדקו, ושתיהן דורשות החלטה:

- **(א) טאנל מלא עם forwarder ברמת המשתמש** (ה-fallback שהתכנית מזכירה, tun2socks). זה מתכנס עם lockdown, אבל דורש ספייק חדש.
- **(ב) DNS בלי lockdown, עם מנגנון החלמה עצמי,** ומקבלים חלון fail-open מוגדר. המדידה כאן: אחרי נפילה, חסימת DNS נעלמת מיד ([38](evidence/38-control-nolockdown-vpn-dead.txt)).

**לא מומלץ:** overload של `setAlwaysOnVpnPackage` עם רשימת חבילות חריגות. זה פוטר אפליקציות שלמות מה-VPN, כולל ה-DNS שלהן, ולכן לא מהווה פתרון.

---

## 7. קבצים

- קוד: [kotlin/](kotlin/). אפליקציית ה-DPC ב-[kotlin/app/](kotlin/app/), אפליקציית ה-probe ב-[kotlin/probe/](kotlin/probe/).
- חבילת הפרסור הטהורה: [DnsPacket.kt](kotlin/app/src/main/java/com/webfilter/spike/a02/dns/DnsPacket.kt), [Rules.kt](kotlin/app/src/main/java/com/webfilter/spike/a02/dns/Rules.kt), ובדיקות: [DnsPacketTest.kt](kotlin/app/src/test/kotlin/com/webfilter/spike/a02/dns/DnsPacketTest.kt).
- ה-VPN: [DnsVpnService.kt](kotlin/app/src/main/java/com/webfilter/spike/a02/DnsVpnService.kt). הדרייבר לבדיקות: [CmdReceiver.kt](kotlin/app/src/main/java/com/webfilter/spike/a02/CmdReceiver.kt).
- ראיות: [evidence/](evidence/). `00-baseline.txt` מתעד את מצב האמולטור לפני כל שינוי, ו-`58` / `64` / `68` מתעדים את הניקוי.
- `.gitignore` של הספייק: [.gitignore](.gitignore).
- לא בוצע commit ולא push. לא שונה שום קובץ מחוץ ל-`spikes/A0-2/`. לא הותקנו חבילות SDK. בסוף הספייק האמולטור כובה (`emu kill`).
