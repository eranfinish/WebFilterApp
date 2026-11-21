# ספייק A0.21: watchdog לטאנל אחרי נפילה, תחת lockdown

**מסקנה: GO מותנה למנגנון `svc` (סרוויס קשור ב-foreground בתהליך הראשי) כ-watchdog ראשי.** ב-3 הרצות הטאנל חזר 216–324 ms אחרי ההרג, והדפדפן (sampler) הצליח 261–531 ms אחרי ההרג. ארבע הרגות ברצף החזירו את הטאנל תוך 164–292 ms.

**התנאי הפתוח:** ההפעלה מחדש אחרי **אתחול** לא נמדדה. שני ניסיונות אתחול עם ה-watchdog נכשלו (ראו §5). לכן אחרי אתחול, קריסה של `:vpn` משאירה את הילד בלי רשת, עד שמפעילים את ה-watchdog ידנית. זה חוסם ייצור עד שיוכח שהמקלט של `BOOT_COMPLETED` עובד.

**המערכת לבד לא מחזירה את הטאנל באופן אמין.** ב-A0.20 חזר פעם אחת. ב-T1, עם אותו מצב `startRequested=true`, לא חזר. הסיבה לא נמצאה.

הכול על **AOSP emulator, Android 12 (API 31), Tablet_A12** בלבד. לא נבדקו מכשירי אמת, מנגנוני סגירה של יצרנים (battery killers), Doze, או API 37. כל מספר כאן מגובה בקובץ ב-[evidence/](evidence/), אלא אם מסומן אחרת.

---

## 1. סביבה ושיטה

| פריט | ערך |
|---|---|
| מכשיר | `emulator-5590`, AVD `Tablet_A12` (API 31). `emulator-5580` (סשן אחר) לא נגעתי בו |
| DPC | `com.webfilter.spike.a021`, Device Owner, testOnly, uid 10147. הטאנל ב-`:vpn` (uid 10147). המקלט `WatchBootReceiver` ו-`WatchService` רצים בתהליך הראשי |
| Probe | `com.webfilter.spike.a021.probe`, uid 10148 (10149 אחרי התקנה מחדש), `INTERNET` ו-`ACCESS_NETWORK_STATE` בלבד |
| מדידה | sampler ב-probe: ניסיון TCP+HTTP ל-`104.20.26.136:80` (example.org) כל 250 ms, בברירת המחדל, בלי pool. כל ניסיון נרשם עם חותמת `System.currentTimeMillis()`, אותו שעון כמו ה-DPC. הרג: `KILL_SELF` של `:vpn`, ה-DPC מדפיס `wall=` לפני ההרג. `establish ok wall=` מסמן את החזרה |
| הערה על המדידה | ה-sampler בודק דרך IP, **לא דרך DNS**. מדידת DNS נפרדה לא בוצעה |
| סקריפטים | [scripts/run21.sh](scripts/run21.sh): ריצה אחת (מצב, sampler, הרג, שמירת לוגים, סיכום) |

**הרג:** `adb shell run-as` נחסם (SELinux: `kill: Permission denied`), ומעטפת ה-`shell` לא יכולה להרוג תהליך של אפליקציה. לכן ההרג נעשה בתוך התהליך (`Process.killProcess`, כמו ב-A0.2b). זו נפילה עצמית, לא הרג חיצוני. ההבדל לא משנה את מה ש-AMS רואה (התהליך מת), אבל כן מוסיף הנחה.

**שני מצבי הפעלה** (`start path`) נבחנו, כי ההפעלה משנה את מה ש-AMS יכול לשחזר:
- `boot`: רק always-on קושר את השירות. `ServiceRecord` עם `Bindings` של VpnService, בלי `startRequested`.
- `start`: הפרוב קורא `startService`, כך ש-`startRequested=true`.

---

## 2. השחזור של המערכת לבד (בלי watchdog)

| ריצה | מצב הפעלה | START | חשיבות בזמן המוות | שחזור של AMS | kill→establish | EACCES אחרי ההרג | ראיה |
|---|---|---|---|---|---|---|---|
| obs0 (A0.20 build) | `start` (`startRequested=true`) | STICKY | `vis BFGS` | **כן**: `Scheduling restart … in 1000ms for start-requested` | **1.56 s** | — | [obs0](evidence/obs0-kill-restart-a020-logcat.txt) |
| T1 (A0.21) | `start` (`startRequested=true`, אומת ב-[dumpsys](evidence/T1-control-start-sticky-true-dumpsys-services.txt)) | STICKY | `cch+5 CEM` | **לא** | לא הוחזר ב-45 s | 129 דגימות, 0 OK | [T1](evidence/T1-control-start-sticky-true-trial.txt) |
| T0 (A0.21) | `boot` | NOT_STICKY | `cch+5 CEM` | לא | לא הוחזר ב-40 s | 133 דגימות, 0 OK | [T0](evidence/T0-control-boot-sticky-false-trial.txt) |
| cold boot 1 (A0.21 בלי watchdog) | `boot` (always-on בעלייה) | NOT_STICKY | — | לא | לא הוחזר ב-40 s | 135 דגימות (0 OK אחרי ההרג) | ראו §5. *הלוג של הריצה לא נשמר לקובץ* |

**מה זה אומר:**
- **obs0 ו-T1 זהים בהתנהגות של ההפעלה** (שניהם `startRequested=true`), ובכל זאת התוצאה שונה: ב-obs0 חזר, ב-T1 לא. ההבדל היחיד שנרשם הוא חשיבות התהליך בזמן המוות (`vis BFGS` לעומת `cch+5 CEM`). **זו השערה מ-3 נקודות נתונים.** לא ידוע מה הייתה החשיבות ב-obs0 (הפרוב היה ברקע או בחזית), והשוואה מבוקרת לא נעשתה.
- ב-A0.2b (API 37, לפי הלוג שנשמר) הטאנל חזר אחרי ~91 שניות, עם סיבה `started-service`. שורת `Scheduling restart` לא נשמרה שם, ולכן לא ניתן לדעת אם זה נבע מ-API 37 או מדרך ההפעלה.
- **בלי watchdog אין שחזור אמין.** ברוב הריצות הטאנל לא חזר, והילד נשאר בלי רשת (fail-closed, אבל בלי חזרה).

---

## 3. מטריצה (יכולת | ספק | API | תוצאה | ראיה)

| יכולת | ספק | API | תוצאה | ראיה |
|---|---|---|---|---|
| שחזור מערכת לבד, `start`, STICKY | AOSP emulator | 31 | פעם אחת ב-A0.20 (vis BFGS). **לא** ב-T1 (cch+5) | [obs0](evidence/obs0-kill-restart-a020-logcat.txt), [T1](evidence/T1-control-start-sticky-true-trial.txt) |
| שחזור מערכת לבד, `boot`, NOT_STICKY (בקרה) | AOSP emulator | 31 | לא | [T0](evidence/T0-control-boot-sticky-false-trial.txt) |
| **`svc`**: FGS בתהליך הראשי, `bindService` ל-`WatchTarget` ב-`:vpn`, `onServiceDisconnected`→`startService` | AOSP emulator | 31 | **עובד**, 3 ריצות: kill→establish 216, 324, 232 ms; kill→OK ראשון 279, 531, 261 ms. OK לפני establish: 0 בכל ריצה. EACCES: 1, 2, 1 דגימות | [W1](evidence/W1-svc-boot-sticky-false-summary.txt), [W2](evidence/W2-svc-boot-sticky-false-summary.txt), [W3](evidence/W3-svc-boot-sticky-false-summary.txt) |
| **`alarm`**: `setAndAllowWhileIdle` (inexact) כל ~1 s | AOSP emulator | 31 | **עובד, איטי**: 3 ריצות, kill→OK ראשון 3273, 3513, 3466 ms. OK לפני establish: 0. EACCES: 13, 14, 13. exact alarm נדחה (ראו שורה להלן) | [A1](evidence/A1-alarm-boot-sticky-false-summary.txt), [A2](evidence/A2-alarm-boot-sticky-false-summary.txt), [A3](evidence/A3-alarm-boot-sticky-false-summary.txt) |
| **`job`**: JobScheduler חד-פעמי, `minimumLatency=1000`, מתזמן את עצמו | AOSP emulator | 31 | **עובד**, ריצה אחת: kill→OK ראשון 1370 ms. דגימה אחת של OK נרשמה 8 ms לפני שורת `establish ok` (גבול, לא דליפה; ראו הערה). 1 ריצה בלבד | [J1](evidence/J1-job-boot-sticky-false-summary.txt) |
| **`always`**: `setAlwaysOnVpnPackage` עם אותו ערך, כל 1 s | AOSP emulator | 31 | **עובד**, ריצה אחת: kill→OK ראשון 1538 ms; OK לפני establish: 0. 1 ריצה בלבד | [AL1](evidence/AL1-always-boot-sticky-false-summary.txt) |
| backoff: 4 הרגות ב-~10 s זו מזו (`svc`) | AOSP emulator | 31 | kill→establish: 171, 164, 198, 292 ms. לא נמדד גידול עקבי | [B1](evidence/B1-backoff-svc-4kills-trial.txt) |
| `onRevoke` (נתיב הרג) | AOSP emulator | 31 | **לא נקרא** באף ריצה (0 שורות בכל הלוגים). הרג לא מפעיל `onRevoke`. נתיב ביטול VPN לא נבדק | כל קובצי `*-trial.txt` |
| exact alarm | AOSP emulator | 31 | **נדחה**: `SecurityException … needs SCHEDULE_EXACT_ALARM` (targetSdk 36). fallback ל-inexact | [A1 trial](evidence/A1-alarm-boot-sticky-false-trial.txt) (שורה `exact alarm refused`) |
| cold boot בלי watchdog | AOSP emulator | 31 | always-on הפעיל את הטאנל לבד: `onCreate` ב-uptime 31,231 ms, `establish ok` ב-31,590 ms; הדפדפן הצליח ~0.4 s אחרי ההפעלה. אחרי הרג: לא הוחזר (135 EACCES). **הלוג של הריצה לא נשמר לקובץ** (ראו §5) | ראו §5 |
| cold boot עם watchdog (`svc`, `BOOT_COMPLETED`) | AOSP emulator | 31 | **לא נמדד**: 2 ניסיונות. הטאנל לא עלה ב-boot, המקלט לא רץ (ראו §5) | ראו §5 |

**הערות:**
- **`alarm`:** נכשל בשתי הריצות הראשונות (A1, A2 הישנות), כי `setExactAndAllowWhileIdle` נדחה. הריצות שנשארו בתיקייה הן של הגרסה עם fallback. נרשם בלוג.
- **`job`:** הדגימה 8 ms לפני `establish ok` היא גבול בין עליית ה-tun לבין רישום השורה. לא נספרה כדליפה, אבל כתוב כאן כדגימה אחת בגבול, לא כ-0.
- **`always`:** השחזור הגיע מהקריאה החוזרת (inference): במצב `always` ה-`WatchService` לא קושר שום שירות, והלוג לא מדפיס כל קריאה. זו הסקה, לא מדידה ישירה.
- **EACCES:** במנגנוני `svc` רק 1–2 דגימות נפלו בחלון. ההוכחה ש"הרשת חסומה כל הזמן" נשענת על T0 (133 דגימות, 0 OK) ועל ריצות `alarm` (13–14 כל אחת).
- **`WorkManager`:** לא נבדק (דורש הורדה מ-Maven). הוא רץ על JobScheduler ב-API 23+.

---

## 4. המסקנה לתוכנית

- **`svc` הוא המנגנון הטוב ביותר שנמדד** (216–324 ms, והכי מעט דגימות EACCES). מומלץ כ-watchdog ראשי.
- **`alarm`, `job`, `always` עובדים, איטיים יותר** (1.3–3.5 שניות). הם יכולים לשמש גיבוי, בתנאי שייבדק Doze (לא נבדק כאן).
- **השחזור של המערכת לבד אינו מספיק לייצור** (§2).
- **תנאי GO:** התהליך הראשי (DPC) חייב להישאר חי כשהטאנל נופל. לכן `svc` רץ כ-FGS. בלי FGS, תהליך DPC שנעלם לא יפעיל watchdog. נצפה בספייק A0.20 שהתהליך הראשי נעלם אחרי ה-broadcast (בבדיקת `ps` קונסולית, לא נשמר לקובץ).
- **תנאי פתוח:** watchdog אחרי אתחול לא נבדק (§5). עד שזה ייבדק, קריסה אחרי אתחול משאירה את הילד offline.

---

## 5. Cold start

- **cold boot 1 (בלי watchdog, always-on בעלייה):** always-on הפעיל את הטאנל לבד: `onCreate` ב-uptime 31,231 ms, `establish ok` ב-uptime 31,590 ms, והדפדפן הצליח אחרי ~0.4 s. אחרי הרג לא היה שחזור (135 EACCES, 0 OK). **הלוג נקרא מהטרמינל ולא נשמר לקובץ**, לכן זה "נצפה, לא נשמר".
- **cold boot עם `svc` (קבלת `BOOT_COMPLETED`), 2 ניסיונות: לא נמדד.** בשניהם ה-DPM רשם `Active admin for device owner not found` בזמן האתחול (לפני השלמת הסריקה), והטאנל לא עלה ב-boot. המקלט `BOOT_COMPLETED` לא רשם כלום. לא ידוע למה. ההשערה הקודמת (חבילה במצב stopped אחרי `install -r`) לא נבדקה, ואולי לא נכונה.
- **בשני הניסיונות נרשם גם `Odd, missing scanned package`** בזמן אתחול. הבעלות נשארה רשומה (`dpm list-owners`), אבל ה-DPC לא נטען בזמן האתחול. זה מתאים לחוסר עקביות בין ה-PackageManager ל-DPM אחרי התקנה מחדש.

---

## 6. מה לא נבדק

- מכשירי אמת, Lenovo/Galaxy/Poco, battery killers של יצרנים.
- Doze, ופיגור של inexact alarm בפועל.
- API 37 (הטענה שההפעלה המערכתית זהה בכל API לא נבדקה).
- `onRevoke` כנתיב שחזור (נתיב ביטול VPN, `lockdown_off`). כאן נבדק רק שהרג לא מפעיל אותו.
- DNS דרך ה-sampler (לא נמדד בנפרד).
- cold boot עם `svc` (ראו §5). cold boot עם `job` ו-`always` לא נבדק.
- חזרה על ריצות בלי הגבלת תדירות (ראו backoff, §3).

---

## 7. ניקוי ומצב סופי

- **Device Owner לא הוסר.** `dpm remove-active-admin` נדחה: `Attempt to remove non-test admin`, גם אחרי שהתקנתי מחדש את ה-DPC עם הדגל `TEST_ONLY` (אומת ב-`pkgFlags`). נסיון ההסרה של החבילה נדחה: `DELETE_FAILED_DEVICE_POLICY_MANAGER`. ה-probe הוסר.
- **ניסיון חזרה על DownloadManager (A0.20) אחרי התיקון נכשל:** ה-DPC איבד את ה-admin (`No active admin`), לא עלה טאנל, והפלט מסומן כלא תקף. ראו [e20-21-downloadmanager-rerun-INVALID-no-tunnel.txt](../A0-20/evidence/e20-21-downloadmanager-rerun-INVALID-no-tunnel.txt).
- **מצב סופי** נשמר ב-[final-state-before-cleanup.txt](evidence/final-state-before-cleanup.txt) וב-[final-state-after-cleanup.txt](evidence/final-state-after-cleanup.txt). בשניהם הבעלות נשארת על `com.webfilter.spike.a021`.
- **האמולטור כבוי**, או פתוח בסיום, לפי ההחלטה על `-wipe-data` (ראו הודעה לסשן הראשי). ה-snapshot `default_boot` נשמר שוב (כמו ב-A0.2b).
- **החלטה נדרשת:** האם לאתחל את Tablet_A12 עם `-wipe-data` כדי לנקות את הבעלות. זו פעולה הרסנית על ה-AVD, ולכן לא בוצעה בלי אישור.
- **השלכה לייצור (חשוב להתאוששות והסרה):** `dpm remove-active-admin` נדחה עם `Attempt to remove non-test admin`, גם אחרי התקנה מחדש של ה-DPC עם `TEST_ONLY` (אומת ב-`pkgFlags`). ייתכן שמנגנון ההסרה של Device Owner ב-API 31 דורש `testOnly` מוגדר בזמן הרישום המקורי, ושחבילה שכבר נרשמה ללא דגל כזה לא תוסר בלי wipe. זה לא נבדק עד הסוף. עד שיוכח אחרת, יש להניח שהסרה של Device Owner ב-production לא תתאפשר בלי איפוס ליצרן, ושזה צריך להיכנס לתכנון ההתאוששות.
- **Chrome (C1/C2) לא נבדק.** ההורה אישר את מסך הפתיחה של Chrome ידנית על המכשיר. אחר כך הופיע "Turn on sync?", ונלחץ "No thanks" (בלי כניסה). ניסיון להגדיר את המדיניות נכשל כי ה-DPC לא פעיל (`No active admin`), ואי אפשר להקים טאנל מהמעטפת (`BIND_VPN_SERVICE`). לכן `DnsOverHttpsMode` ו-`chrome://policy` לא נבדקו. זה נשאר "not verified", וזו השלכה נוספת של בעיית ה-Device Owner.

---

## 8. קבצים

- קוד: [kotlin/](kotlin/) (`app/` ה-DPC וה-watchdog, `probe/` האפליקציה השנייה).
- סקריפט הריצה: [scripts/run21.sh](scripts/run21.sh).
- ראיות: [evidence/](evidence/). לכל ריצה: `*-trial.txt` (לוגים מסוננים), `*-am.txt` (שורות AMS), `*-summary.txt`, `*-dumpsys-services.txt`. בדיקות יחידה: `TEST-*.xml` (8 + 11 + 7, כולן עוברות).
- לא בוצע commit.
