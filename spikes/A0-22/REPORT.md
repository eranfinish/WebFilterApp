# ספייק A0.22: שכבה משלימה מה-Device Owner ו-Family Link כ-PO של משתמש הילד

**סטטוס:** סופי, עם מגבלות מפורשות. לא בוצע ביטול של Private DNS או של ההגבלה. נדרש אישור לפני ביטול.

**תאריך:** 2026-11-21 (שעוני המכשיר UTC; ישראל = UTC+2). **מכשיר:** `Tablet_A12` (`emulator-5590`, AOSP-based, Android 12, API 31, build `SE1A.211212.001.B1`, Chrome 91.0.4472.114 בתמונה). **מודל הבדיקה:** Family Link הוא PO של משתמש הילד (`control`, user 11) עם חשבון מפוקח, וה-Device Owner שלנו מוסיף שכבות ברמת המכשיר בלבד (user 0). **לא נבדק על מכשיר אמיתי.**

**שיטה:** DPC בדיקה (`spikes/A0-22/app`) עם אותו package כמו A0.19 (`com.webfilter.spike.a19`), כדי ש-Device Owner על user 0 יישמר. עודכן (לא הוחלף) באמצעות `adb install -r`. לא נמחק משתמש או חשבון, ול-`Medium_Phone_API_37.0` (`emulator-5580`) לא נגעו. בדיקות המכשיר נעשו בקריאה בלבד (adb, logcat, dumpsys), ופעולות שינוי היו רק דרך ה-DO: הגדרת Private DNS והגבלה אחת. הדוח נשען על לוגים ועל דיווחי הורה, ולא על בדיקת דפדפן עצמאית.

---

## תשובות

### 1. מה ה-DO יכול להגדיר ב-Private DNS?
- **`setGlobalPrivateDnsModeSpecifiedHost` נחסם.** השגיאה: `SecurityException: operation not allowed when device has unaffiliated users`. user 10 (PO שלנו) ו-user 11 (PO של Family Link) אינם affiliated, ו-affiliated הוא רק user 0 (`evidence/05`, `05c`).
- **`DevicePolicyManager.setGlobalSetting` עם `private_dns_mode` ו-`private_dns_specifier` עובד.** שני המפתחות התקבלו בלי חריגה, והמכשיר מדווח `PROVIDER_HOSTNAME` עם host `family.cloudflare-dns.com` (`evidence/10`, `11`). **זו אסימטריית API:** ההגבלה של affiliation חלה על ה-API הייעודי, לא על `setGlobalSetting`.
- **DoT פעיל ומאומת:** `dumpsys connectivity` מציג `UsePrivateDns: true`, `PrivateDnsServerName: family.cloudflare-dns.com`, `ValidatedPrivateDnsAddresses: [1.0.0.3,1.1.1.3]` ברשת Wi-Fi הראשית, ו-`/proc/net/tcp` מציג חיבורים ל-`1.0.0.3:853` מ-uid 0 (netd) (`evidence/12`–`13`, `19`–`20`).
- **הסתייגות:** `setGlobalSetting` עשוי להיסגר בגרסאות Android מאוחרות יותר. **נדרשת בדיקה נוספת על API 37** (`Medium_Phone_API_37.0`), ועל הטאבלט האמיתי לפני שמסתמכים עליו.

### 2. האם החסימה מחזיקה במשתמש הילד?
- **תוצאת הורה ב-u11 (`control`):** `ynet` נפתח; **`malware.testcategory.com` (השם המדויק) נחסם ב-Chrome** (דיווח הורה). ההפעלה של Chrome הייתה ב-u11: `START u11` ב-18:48:12 וב-18:48:47, ותהליך u11 `u11_a115` (pid 5494, uid 1110115) (`evidence/24`).
- **ברמת המערכת (u0 shell):** `malware.testcategory.com` נפתר ל-`127.0.0.1` (A) ול-`::` (AAAA). `www.ynet.co.il` נפתר ל-`23.221.30.50`, זהה במכשיר ובמארח (`evidence/12`, `16`).
- **הדומיין הסופי נבדק על ידי ההורה בלבד** (לא על ידי הספייק). הדיווח הראשון על "malware נפתח" היה שגוי: ההורה הקליד `maleware.testcategory.com` (אות מיותרת). השם הזה נפתר לכתובות אמיתיות של Cloudflare (`104.18.4.35`, `2606:4700::6812:523`), ו-Cloudflare Gateway הציגה את דף הבדיקה שלה. זה ממצא לרישום בלבד, ולא נחשב לכשל החסימה (`evidence/23`).

### 3. האם הילד יכול לשנות את Private DNS?
- **תוצאת הורה ב-u11:** Settings → Private DNS **נעול**.
- **הגבלה שהוגדרה:** `DISALLOW_CONFIG_PRIVATE_DNS` נוסף מה-DO ומופיע ב-u0 ב-"Device policy global restrictions" וב-"Effective restrictions" (`evidence/06`, `07`, `09`). ב-u11 הרשימה האפקטיבית לא מחושבת כשהמשתמש עצור. הלוג לא מראה שום הגדרה מקומית של user 11 שמסבירה את הנעילה.
- **מה שאינו ידוע:** הסיבה שהנעילה מופיעה ב-u11 **נסמכת על ההגבלה שהוגדרה ב-u0, אבל לא הוכחה ישירות בלוג.** לכן מסומן כ"עקבי עם ההגבלה, לא מוכח".

### 4. Chrome Secure DNS
- **תוצאת הורה ב-u11:** Secure DNS **כבוי ונעול**.
- **מי נועל?** לא נקבע (**cause not determined**). בלוג u11 לא נמצאה שום שורת מדיניות של Chrome (`PolicyService`, `DnsOverHttpsMode`, `managed`) (`evidence/25`). ההגדרה אפשרית מאחת מאלה: מדיניות של Family Link על Chrome, מצב ה-managed של ה-DO, או Chrome שמעביר את הבחירה ל-Private DNS של המערכת. אין לנו ראיה שמכריעה בין האפשרויות.

### 5. האם classroom עדיין עובד?
- **תוצאת הורה ב-u11:** `classroom.google.com` **נפתח** ב-Chrome.
- **מחובר לחשבון הילד?** לא נבדק בשלב זה (לא נמסר על ידי ההורה).

---

## מטריצה

| יכולת | ספק (vendor) | API | תוצאה | ראיה |
|---|---|---|---|---|
| `dpm set-device-owner` על user 0, התקנה מחדש בלי מחיקה | AOSP emulator | 31 | הצליח; ה-DO נשמר | `evidence/04`, `04b`, `04c` |
| `setGlobalPrivateDnsModeSpecifiedHost` מה-DO | AOSP emulator | 31 | נחסם: "unaffiliated users" | `evidence/05`, `05c` |
| `setGlobalSetting(private_dns_mode, hostname)` מה-DO | AOSP emulator | 31 | הצליח; DPM: `PROVIDER_HOSTNAME` | `evidence/10`, `11` |
| `setGlobalSetting(private_dns_specifier, family.cloudflare-dns.com)` מה-DO | AOSP emulator | 31 | הצליח | `evidence/10`, `11` |
| DoT מאומת על הרשת הראשית | AOSP emulator | 31 | `ValidatedPrivateDnsAddresses` = `[1.0.0.3,1.1.1.3]` | `evidence/13`, `19`, `20` |
| `DISALLOW_CONFIG_PRIVATE_DNS` מה-DO | AOSP emulator | 31 | מופיע ב-u0 global ו-effective | `evidence/06`, `07`, `09` |
| נעילת Private DNS ב-u11 (Settings) | AOSP emulator | 31 | נעול (דיווח הורה) | — (דיווח הורה) |
| מחסום לדומיין `malware.testcategory.com` ב-u11, Chrome | AOSP emulator | 31 | נחסם (דיווח הורה); Chrome ב-u11 | `evidence/24` |
| מערכת: `malware.testcategory.com` מחזירה `127.0.0.1` / `::` | AOSP emulator | 31 | חסום במערכת | `evidence/12`, `23` |
| מארח: `malware.testcategory.com` דרך 1.1.1.3 מחזיר `0.0.0.0` | Host (Cloudflare Families) | — | חסום | `evidence/14`, `23` |
| נורמלי: `www.ynet.co.il` נפתר ונפתח | AOSP emulator | 31 | נפתח (דיווח הורה); כתובת זהה במכשיר ובמארח | `evidence/12`, `14`, `16` |
| `maleware.testcategory.com` (הקלדה שגויה) | AOSP emulator | 31 | נפתר לכתובות אמיתיות; דף Cloudflare Gateway, לא חסימה | `evidence/23` |
| Chrome Secure DNS ב-u11 | AOSP emulator | 31 | כבוי ונעול (דיווח הורה); הגורם לא נקבע | `evidence/25` |
| `classroom.google.com` ב-u11 | AOSP emulator | 31 | נפתח (דיווח הורה); חיבור לחשבון לא נבדק | — (דיווח הורה) |

---

## תצפיות

- **127.0.0.1 מול 0.0.0.0:** הרזולבר של Cloudflare Families (מהמארח) מחזיר `0.0.0.0` לשם החסום, אבל המכשיר מחזיר `127.0.0.1`. לאותו שם, בשני המקורות, התשובה שונה. **לא נחקר המקור.** בשני המקרים התשובה אינה כתובת שאפשר להגיע אליה, ולכן החסימה עצמה נראית תקפה בשתי הצורות.
- **הטעות ב-`maleware`:** דיווח "malware נפתח" הגיע מ-`maleware.testcategory.com`, ולכן מסומן כלא תקף. הבדיקה התקינה היא עם השם המדויק, שנחסם.
- **קלט לתיקון:** הרשימה האפקטיבית של u11 לא מחושבת כשהמשתמש עצור, לכן בדיקת ההגבלה ב-u11 נשענת על דיווח הורה ולא על dumpsys.

---

## מגבלות
- **אמולטור API 31 בלבד** (תמונה מ-2021), Chrome 91. לא נבדק API 37 (`Medium_Phone_API_37.0`), וכל מסקנה על `setGlobalSetting` דורשת בדיקה שם.
- **Cloudflare Families שימש כ-resolver מחליף** לבדיקת המנגנון. לא נבדק שרת ה-DoT של ה-hub שלנו, ולא התעודה שלו.
- **רשת ביתית (Wi-Fi) בלבד.** הטאבלט האמיתי לא נבדק. האמולטור מציג גם רשת סלולרית עם אותה הגדרה, ולכן לא ניתן לקבוע מכאן מה קורה בסלולר.
- **הטאבלט האמיתי (Lenovo TB328XU) לא נבדק.**
- **`setGlobalSetting` עשוי להיסגר בגרסאות Android מאוחרות יותר.** הבדיקה על API 37 נדרשת לפני שמסתמכים עליו.
- **סיבת נעילת Chrome Secure DNS לא נקבעה.** גם הנעילה של Private DNS נסמכת על ההגבלה שהוגדרה, בלי אישור ישיר בלוג.
- **ביטול:** לא בוצע. ביטול Private DNS או `DISALLOW_CONFIG_PRIVATE_DNS` דורש אישור מראש.

## מה לא מאומת
- שכבת DNS לדומיין למבוגרים (לא נבדק; הדומיין שנבדק הוא מדגם של Cloudflare).
- סיבה ישירה לנעילת Private DNS ולנעילת Secure DNS של Chrome.
- חיבור הילד ל-classroom (נפתח, אבל לא נבדק אם מחובר).
- התנהגות ב-API 37 ובטאבלט האמיתי.

## ראיות
כל הקבצים תחת [evidence/](evidence/). ראו במיוחד: `04`–`04c` (התקנה והשארת ה-DO), `05`–`05c` (חסימת affiliation), `06`–`07` (ההגבלה), `09` (קו בסיס ב-u0), `10`–`11` (`setGlobalSetting`), `12`–`14` (DNS ו-DoT), `16` (אחרי דיווח ynet/malware), `19`–`20` (חיבורי DoT ו-Chrome), `23` (הטעות `maleware`), `24` (אימות ההפעלה ב-u11), `25` (בדיקת מדיניות Chrome).
