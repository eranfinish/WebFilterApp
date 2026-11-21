# ספייק A0.19: Family Link וחשבון Google מפוקח במשתמש שני עם DPC שלנו

**סטטוס:** סופי, עם מגבלות מפורשות (ראו "מה לא מאומת"). לא נשארו בדיקות פתוחות שמחייבות שינוי במכשיר. שלב (ב) (Chrome בלי חשבון מכשיר) לא בוצע.

**תאריך:** 2026-11-20 עד 2026-11-21 (שעוני המכשירים UTC; ישראל = UTC+2). **מכשירים (כולם אמולטור):**
- `Tablet_A12` (`emulator-5590`): AOSP-based, Android 12, API 31, build `SE1A.211212.001.B1`, GMS 26.45.31, Chrome 91.0.4472.114, YouTube 16.25.39 (2021, לא עודכן). עם Device Owner שלנו על user 0.
- `Medium_Phone_API_37.0` (`emulator-5580`): Android 17, API 37, GMS 26.45.31, Chrome 145.0.7632.218, YouTube 21.18.164. **בלי Device Owner כלל.**

**שיטה:** adb ו-logcat לקריאה בלבד, ו-DPC בדיקה מינימלי (`spikes/A0-19/app`), שהוגדר כ-Device Owner רק על ה-`Tablet_A12`. החשבונות הוזנו רק בידי ההורה. שמות, כתובות דוא"ל וצילומי מסך עם פרטים לא נשמרו בשום קובץ.

**מה לא נבדק על מכשיר אמיתי:** הכול נבדק על אמולטורים. לא נבדק Lenovo TB328XU, ולא נבדקה הבנייה האמיתית (אבטחה, GMS, Chrome).

---

## המסקנה

1. **`THIRD_PARTY_DEVICE_MANAGEMENT_REQUIRED` הוא התפרצות זמנית במהלך הגדרת Family Link, בשני המכשירים, עם Device Owner שלנו ובלעדיו.** בטאבלט: התפרצות ב-u11 עד 2026-11-20 14:12:13 UTC, כשה-Device Owner שלנו היה על user 0. בטלפון (ללא DO): התפרצויות ב-17:47:59–17:53:56 ו-17:58:01–17:58:15 UTC, אחרי שה-PO של Family Link הוגדר. **הסירוב אינו נגרם על ידי ה-Device Owner שלנו.** ההסבר לגבי מקורו (GMS) נשען על שם הסטטוס ועל המיקום בלוג, לא על בקרה נוספת.
2. **הגורם החוסם הוא ה-Profile Owner של המשתמש של הילד.** כש-PO שלנו נמצא במשתמש (`kid-test`), Family Link לא יכול להפוך ל-PO. ההגדרה לא מסתיימת, הסירובים ממשיכים, וה-GMS מוחק את החשבון המפוקח (13:29:06 UTC). כש-Family Link הוא ה-PO של המשתמש (הטאבלט, user 11, ה-Device Owner שלנו נשאר על user 0; והטלפון, user 10, בלי DO), ההגדרה מגיעה לשלב `Review apps` (ב-14:13:12 בטאבלט), והחשבון נשאר. סיום ההגדרה עצמו לא נרשם בלוג; הוא נשען על דיווח ההורה.
3. **הוכחות ל"החשבון עובד":** דיווחי ההורה בלבד בשלב הזה. בטאבלט ההורה דיווח פעמיים ש-Chrome ב-u11 פתח `classroom.google.com` מחובר עם חשבון הילד. בטלפון ההורה דיווח ש-classroom נפתח ב-Chrome, ו-YouTube נפתח עם הבהוב של המסך. **מצב ההתחברות של YouTube בטלפון לא אושר.** הלוג לא מכיל אישור של הצלחת token לחשבון בשום מכשיר.

### מגבלות (לא מוכח)
- **לא מוכח: הצלחת token מתועדת בלוג ב-u11.** לא נרשמה תוצאה כזו; רק היעדר כשלים אחרי הזמן שבו ההגדרה נסתיימה.
- **לא מוכח: אמולטור הוא סביבה נאמנה לאותה התנהגות כמו מכשיר אמיתי.** הטאבלט הוא image מ-2021 עם Chrome 91 ו-YouTube 2021. הטלפון הוא API 37 עם GMS וChrome עדכניים. שתי התמונות לא זהות לטאבלט האמיתי.
- **לא מוכח: סיבה ישירה לסירוב.** הסטטוס נכתב על ידי GMS, אבל הסיבה הפנימית לא נמצאה בלוג.
- **לא מוכח: סיבת מחיקת החשבון** ב-GMS (נרשם `Removed account with reason %s` עם הסיבה מוסתרת).
- **לא מוכח: מה הפעיל את יצירת משתמש 11 ה-`SUPERVISING` בטלפון** ב-17:59:29 UTC (הלוג מציג את היצירה בלבד).
- **לא מוכח: מצב ההתחברות של YouTube בטלפון** (הבהוב דווח; אישור ההתחברות לא).

---

## תשובות לשלוש השאלות

### 1. האם הוספת חשבון Google מפוקח כחשבון מכשיר מצליחה, נכשלת, או מחליפה/מבטלת את ה-Profile Owner שלנו?
**מצליחה, אבל לא מבטלת את ה-PO, והחשבון לא נשמר כשה-PO שלנו נוכח.**
- **`kid-test` (user 10, עם ה-PO שלנו):** החשבון נוסף ב-13:02:15 UTC. Family Link ניסה להפוך ל-PO דרך מסך ההגדרות `SET_PROFILE_OWNER`, ונכשל בכל ניסיון עם `Admin is already added` (`evidence/29`, `32`, `35`). ב-13:03:22 `ProfileOwnerReceiver` של GMS הפך לאדמין פעיל באותו משתמש. ה-PO שלנו נשאר (`evidence/31`, `37`). ב-13:29:06 GMS מחק את החשבון (`evidence/52`–`54`).
- **`control` (user 11, בלי PO שלנו, עם DO על user 0):** החשבון נוסף ב-13:36:17 UTC; הסירוב הופיע; ההורה דיווח על שגיאה "Something went wrong" (400) אחרי אישור ההורים; ב-13:38:25 GMS מחק את החשבון (`evidence/61`).
- **Tablet_A12, user 11 (אחרי שהחשבון נוסף מחדש ב-14:10:21):** `SET_PROFILE_OWNER` מ-GMS הצליח ב-14:10:51 כי לא היה שם PO אחר (`evidence/66`–`68`). ההגדרה הסתיימה, וה-PO של Family Link נשאר.
- **הטלפון, user 10 (בלי DO):** Family Link הוגדר כ-PO ב-17:48:38 (`evidence/85`–`86`), והחשבון נשאר.

### 2. האם כניסה ב-Chrome לחשבון הילד (בלי חשבון מכשיר) עובדת, ומפעילה פיקוח?
**לא נבדק.** שלב (ב) לא בוצע. אין לנו ראיה לגבי משתמש ללא חשבון מכשיר.

### 3. האם YouTube ומשחקים עובדים עם חשבון הילד, ודרך איזה נתיב?
- **YouTube ב-`kid-test` (user 10, עם PO שלנו):** לא עובד. לפני ההסרה הציג "There was a problem signing in to your account" ואחריה שגיאה `[400]` במסך; בלוג אין שגיאת HTTP (`evidence/43`). הסירובים של token נרשמו (`evidence/39`).
- **YouTube ב-`control` (user 11, הטאבלט):** נסתר על ידי Family Link, אבל ההורה פתח את YouTube Music במקום (`evidence/72`–`76`). לא נבדק YouTube עצמו.
- **YouTube בטלפון (`plainkid`, user 10, Family Link PO):** נפתח עם הבהוב; לא אושר אם מחובר (`evidence/88`–`90`). התהליך רץ (`u10_a186`), ולא נרשם בו סירוב משלו.
- **Chrome בטאבלט (user 11):** דיווח ההורה: מחובר ל-`classroom.google.com` עם חשבון הילד (`evidence/81`, `82`, ראיה ראשית מהדיווח).
- **Chrome בטלפון (`plainkid`):** נפתח `classroom.google.com` לפי דיווח ההורה. מחובר לא אושר (`evidence/90`).
- **משחקים / Play Games:** לא נבדקו. Play Games לא מותקן באף אחת מהתמונות (`evidence/07`, `47`, `62`).

---

## מטריצה

| יכולת | ספק (vendor) | API | תוצאה | ראיה |
|---|---|---|---|---|
| `dpm set-device-owner` על user 0 (APK עם `-t`) | AOSP emulator | 31 | הצליח | `evidence/04` |
| `createAndManageUser` עם PO שלנו + `LEAVE_ALL_SYSTEM_APPS_ENABLED` | AOSP emulator | 31 | הצליח; GMS, GSF, Play Store, Chrome, YouTube נשארו | `evidence/06`, `07` |
| PO שלנו פעיל מבפנים במשתמש 10 | AOSP emulator | 31 | `isProfileOwnerApp=true` לאורך הריצה | `evidence/08`, `25`, `37`, `38` |
| הוספת חשבון Google מפוקח ב-`kid-test` | AOSP emulator | 31 | הצליח (13:02:15) | `evidence/27` |
| Family Link מנסה להיות PO דרך `SET_PROFILE_OWNER` ב-`kid-test` | AOSP emulator | 31 | נכשל: `Admin is already added`; ה-PO שלנו נשאר | `evidence/29`, `32`, `35` |
| `ProfileOwnerReceiver` של GMS הופך לאדמין פעיל ב-`kid-test` | AOSP emulator | 31 | הצליח (onEnabled 13:03:22) | `evidence/33`, `34` |
| מחיקת חשבון מפוקח על ידי GMS ב-`kid-test` | AOSP emulator | 31 | הצליח 13:29:06; סיבה מוסתרת | `evidence/52`–`54` |
| הוספת חשבון מפוקח ב-`control` (user 11, בלי PO שלנו) | AOSP emulator | 31 | הצליח 13:36:17 | `evidence/61` |
| `THIRD_PARTY_DEVICE_MANAGEMENT_REQUIRED` ב-`control` (עם DO על user 0) | AOSP emulator | 31 | הופיע 13:36:24–38 | `evidence/61` |
| Family Link מנסה להיות PO ב-`control` | AOSP emulator | 31 | לא נצפה | `evidence/57`, `61` |
| `SET_PROFILE_OWNER` מ-GMS ב-user 11 של הטאבלט (אחרי הוספה מחדש) | AOSP emulator | 31 | הצליח 14:10:51 | `evidence/66`–`68` |
| הסירוב ב-user 11 של הטאבלט אחרי ש-Family Link הפך ל-PO | AOSP emulator | 31 | 391 כשלים מ-14:05; האחרון 2026-11-20 14:12:13 | `evidence/66`, `67` |
| הגדרת Family Link מגיעה לשלב Review apps בטאבלט | AOSP emulator | 31 | `fetchReviewAppsList` ב-14:13:12; סיום לא נרשם בלוג | `evidence/70` |
| `apps.kids` (Family Link) מותקן | AOSP emulator | 31 | לא מותקן באף משתמש | `evidence/57`, `69` |
| הוספת חשבון מפוקח ב-`plainkid` (בלי DO בטלפון) | AOSP emulator | 37 | הצליח; PO של Family Link הוגדר 17:48:38 | `evidence/85`–`86` |
| `THIRD_PARTY_DEVICE_MANAGEMENT_REQUIRED` בטלפון (בלי DO) | AOSP emulator | 37 | 300 שורות 17:47:59–17:53:56; ועוד 14 ב-17:58:01–15 | `evidence/86`, `89`, `90` |
| Chrome ב-user 11 של הטאבלט מחובר ל-classroom | AOSP emulator | 31 | דיווח ההורה (פעמיים); הלוג לא מאשר | `evidence/81`–`84` |
| YouTube מחובר ב-`kid-test` | AOSP emulator | 31 | לא עובד (400 במסך; אין HTTP בלוג) | `evidence/43`, `39` |
| YouTube מחובר ב-user 0 של הטאבלט | AOSP emulator | 31 | שגיאת 400 במסך; ארטיפקט של אפליקציה מיושנת | `evidence/79` |
| YouTube בטלפון (`plainkid`) | AOSP emulator | 37 | נפתח עם הבהוב; מחובר לא אושר | `evidence/88` |
| Chrome כניסה בלי חשבון מכשיר (ב) | AOSP emulator | 31/37 | לא נבדק | — |
| Play Games | AOSP emulator | 31/37 | לא נבדק; לא מותקן | `evidence/07`, `47`, `62` |

---

## תופעות צד (ממצאים נלווים)

- **Family Link על user 11 של הטאבלט הגדיר מגבלות** (`evidence/78`): `no_debugging_features`, `no_remove_user`, `no_install_unknown_sources`, `no_config_location`, `no_config_credentials`, `no_config_date_time`, ובמצב אפקטיבי גם `no_sms` ו-`no_outgoing_calls`. בגללה, פקודות shell ל-user 11 נחסמות (`pm list packages --user 11`, `am switch-user 11` מחזירים `SecurityException`). לא נבדק אם הן מסתירות מ-shell פעילות ולוגים של user 11.
- **Family Link הסתיר את YouTube ב-user 11** דרך DPM (`HideAppProcessorV2`, 16:47:59 UTC) כשה-Device Owner שלנו קיים על user 0 (`evidence/72`–`74`).
- **באנר "This device is managed by your organization"** מופיע במסך "Add an account" של user 11 על הטאבלט, בזמן ש-Device Owner שלנו נמצא על user 0 (דיווח ההורה, `evidence/49`). המשמעות: ה-Device Owner נראה למשתמשים אחרים במכשיר.
- **משתמש `SUPERVISING` חדש בטלפון** (user 11, פרופיל), נוצר ב-17:59:29 UTC, לא ידוע מה הפעיל אותו (`evidence/91`).
- **YouTube 2021 מובנה** (`firstInstallTime` ו-`lastUpdateTime` 2009-01-01) מציג 400 גם ב-user 0 בלי חשבון (`evidence/79`). לכן שורות ה-400 שלו לא ראיה לשאלת הפיקוח.
- **Chrome 91 על ה-image של הטאבלט** ישן; הבדיקה של classroom בו אינה מייצגת דפדפן עדכני.

### תקלות סביבה של האמולטור (ללא קשר לפיקוח)
- **דיאלוג הסכמה לדיווח קריסה** חסם את האמולטור בהפעלה. עקפנו עם `-crash-report-mode disabled -no-metrics` (דגלי CLI בלבד) (`evidence/01`). זו סטייה מהפקודה שניתנה.
- **זיכרון לחוץ** בטאבלט: כ-306–402 MB זמינים מתוך 2 GB; `lowmemorykiller` הרג WebView, Chrome ו-NexusLauncher (`evidence/56`, `66`, `71`). לא מסביר את הסירובים.
- **הפעלה מחדש של הטאבלט** ב-14:57 ו-16:54 ו-17:0x (לא בידי הספייק): adb איבד אותו לכמה דקות; ה-PID השתנה, והמצב נשמר (`evidence/77`).
- **הזרמות logcat ברקע נכשלו** (exit 255) פעמיים; הניתוח נשען על תמונות `logcat -d` נקודתיות.
- **המסך נכבה לבד** אף שה-stay-on מוגדר ל-7, כי `mIsPowered=false` (לא מחובר לחשמל באמולטור) (`evidence/22`, `48`).
- **מקלדת:** `hw.keyboard` היה כבוי והופעל על ידי הסשן המפעיל (`evidence/24`).
- **עכבר כסטיילוס:** Gboard הציג "Try out your stylus" וחסם את הכניסה ב-5580; נוטרל ב-`stylus_handwriting_enabled=0` (`evidence/80`).
- **adb reboot:** לפי הסשן המפעיל, הפקודה גורמת לאמולטור לצאת במחשב הזה. לא נבדקה ולא נעשה בה שימוש.
- **bugreport מערכתי** ב-12:53 UTC (`dumpstate -S`) התחיל 29 שניות לפני קריסת ה-renderer הראשונה של WebView. לא ידוע מה הפעיל אותו (`evidence/19`).

---

## מה זה אומר ל-A8 על הטאבלט האמיתי (בזהירות)

- **הסירוב אינו מוכח כתוצאה של ה-Device Owner שלנו**, כי הוא הופיע גם בטלפון בלי DO. לכן אין כאן בסיס להחליט שה-DPC שלנו חוסם חשבונות מפוקחים.
- **כן מוכח בלוג: שה-PO שלנו במשתמש הילד חוסם את Family Link מלהפוך ל-PO**, ובכך גורם להתפרצויות הסירוב, לכשלון ההגדרה ולמחיקת החשבון ב-GMS.
- **הכיוון לתכנון A8 (לא החלטה):** כדי שחשבון ילד מפוקח יעבוד במשתמש של הילד, Family Link צריך להיות ה-PO של המשתמש. זה מתנגש עם ה-PO שלנו באותו משתמש. צריך לברר האם אפשר לספק את הפיקוח שלנו מבלי להיות PO במשתמש של הילד, או לוותר על חשבון מפוקח במכשיר של הילד. זו החלטת מוצר.
- **השלב הבא לפני A8:** בדיקה על מכשיר אמיתי שאינו אמולטור. עד אז, אין ראיה שהתנהגות זו זהה בטאבלט של Lenovo.

---

## מה לא מאומת
- **שלב (ב):** Chrome בלי חשבון מכשיר לא נבדק.
- **הצלחת token ב-u11 ובטלפון:** לא נרשמה בלוג.
- **מצב ההתחברות של YouTube בטלפון:** לא אושר.
- **חשבון ההורה במשתמש 0:** לא נבדק.
- **הטאבלט האמיתי (Lenovo TB328XU):** לא נבדק דבר עליו.
- **תמונות אמולטור ישנות:** הטאבלט עם Chrome 91 ו-YouTube 2021; הטלפון עם API 37 ו-Chrome 145. שתיהן אינן זהות לאמיתי.
- **הסיבה הפנימית לסירוב `THIRD_PARTY_DEVICE_MANAGEMENT_REQUIRED`** ב-GMS.
- **סיבת מחיקת החשבון** ב-GMS, ומה יצר את משתמש ה-`SUPERVISING` בטלפון.
- **תוכן ה-400** (לא ביומן; דורש פרוקסי רשת).
- **השפעת הזיכרון הנמוך** על תוצאות ה-400 במסכי ה-WebView (לא בודדה).
- **ציר הטוקנים מ-18:00 ואילך בטלפון:** רק לקרוא מה-logcat; לא נבדקה התנהגות ארוכה יותר.

## ראיות
כל הקבצים תחת [evidence/](evidence/). ראו במיוחד: `00` (קו בסיס), `04`/`06`/`07` (התקנה וה-PO של הילד), `27`, `29`–`35` (לולאת Family Link ב-`kid-test`), `38`–`39` (סירוב token), `45`–`47` (קו בסיס control), `51`–`61` (ריצת control עד המחיקה), `66`–`70` (טאבלט, user 11 אחרי הגדרה), `71`–`76` (YouTube ו-hide), `77`–`84` (אחרי הפעלה מחדש, נראות של u11), `85`–`91` (בקרה בלי DO בטלפון).
