# ספייק A0.24: מודל משולב על Android 17 (API 37)

**סטטוס:** סופי, עם מגבלות. כל שש בדיקות ההורה נענו (דיווח הורה, לא נבדקו בידי הספייק); הסיבה לנעילת Secure DNS של Chrome (בדיקה 4) מסומנת כהשערה. לא בוצע ביטול של Private DNS או של ההגבלה. לא נבדק דומיין למבוגרים, הטאבלט האמיתי, או שרת ה-DoT של ה-hub.

**תאריך:** 2026-11-22 (שעון המכשיר UTC; ישראל = UTC+2). **מכשיר:** `Medium_Phone_API_37.0` (`emulator-5580`), אמולטור של Google, Android 17 (API 37), build `CE2A.260420.051.A2`, GMS 26.45.31, Chrome 145.0.7632.218. **לא נבדק על מכשיר אמיתי.**

**מודל הבדיקה:** Device Owner (`com.webfilter.spike.a24`) על user 0. Family Link הוא Profile Owner של user 10 (`fl-kid`) עם חשבון מפוקח. ה-DO מוסיף Private DNS ברמת המכשיר ואת ההגבלה `DISALLOW_CONFIG_PRIVATE_DNS`. בדיקות הדפדפן והאפליקציות בוצעו על ידי ההורה דרך הסשן המתחיל, ומסומנות כ**דיווח הורה**. לא נשמרו צילומי מסך.

**שיטה:** DPC בדיקה (`spikes/A0-24/app`, testOnly) הותקן וה-DO הוגדר לפני יצירת חשבון. user 10 נוצר עם `pm create-user`. בדיקות המכשיר היו בקריאה בלבד (adb, logcat, dumpsys). פעולות השינוי היו רק דרך ה-DO: `setGlobalSetting` לשני מפתחות Private DNS, והגבלה אחת. לא נמחק משתמש או חשבון.

**אירועים תפעוליים:** שלוש יציאות של האמולטור מהמארח, שנרשמו ב-Windows כ-APPCRASH של `qemu-system-x86_64.exe` (0xc0000005). פעמיים הקריסה הייתה ב-Intel GPU driver (`igxelpicd64.dll`), ופעם אחת ב-`libgfxstream_backend.dll` תחת `angle_indirect`. אחרי כל יציאה הופעל מחדש עם `-no-snapshot`, ובאחרונה עם `swiftshader_indirect`. המצב נשמר בכל פעם: DO, PO, Private DNS, ההגבלה והחשבונות. פרטים ב-`evidence/10`. הקריסות נראות כבעיית גרפיקה של המארח, לא של ה-DPC; זה לא נבדק עד הסוף.

---

## תשובות

### 1. האם ה-DO מגדיר Private DNS ב-API 37?
- `setGlobalPrivateDnsModeSpecifiedHost` נחסם: `SecurityException: operation not allowed when device has unaffiliated users`. זה זהה ל-A0.22 על API 31 (`evidence/05`).
- `setGlobalSetting(private_dns_mode, hostname)` ו-`setGlobalSetting(private_dns_specifier, family.cloudflare-dns.com)` התקבלו בלי חריגה. `DevicePolicyManager` מדווח `PROVIDER_HOSTNAME` עם `family.cloudflare-dns.com` (`evidence/05`, `11`).
- DoT מאומת: `dumpsys connectivity` מציג `UsePrivateDns: true`, `PrivateDnsServerName: family.cloudflare-dns.com`, `ValidatedPrivateDnsAddresses: [1.0.0.3,1.1.1.3]` ברשתות CELLULAR ו-WIFI (`evidence/05`).
- **משמעות:** ה-API הייעודי חסום ו-`setGlobalSetting` עובד, בדיוק כמו על API 31. זה נבדק רק על API 37. לגרסאות מאוחרות יותר זה לא נבדק.

### 2. האם ההגבלה נשמרת על המשתמש הילד?
- `DISALLOW_CONFIG_PRIVATE_DNS` נוסף מה-DO ומופיע ב-u0 ב-global restrictions, וגם ברשימה האפקטיבית של user 10 כשהמשתמש רץ ב-foreground (`evidence/07`). **זה סוגר את הפער מ-A0.22**, שבו הרשימה האפקטיבית של משתמש עצור לא חושבה.
- אחרי ההפעלות מחדש ההגבלה מופיעה שוב ב-`dumpsys user` (`evidence/10`). לא נבדקה בנפרד הרשימה האפקטיבית של user 10 אחרי אתחול כשהמשתמש עצור.

### 3. האם החשבון וה-PO נשמרו? (תנאי החזקה)
- Owners: user 0 = ה-DO של ה-DPC שלנו; user 10 = `com.google.android.gms.supervision` ProfileOwnerReceiver (`evidence/04`, `10`).
- חשבונות (ספירה בלבד): user 0 = 0, user 10 = 1.
- תנאי ה-15 דקות אחרי יריית ה-refusal האחרונה (`11-21 23:09:05`) מתקיים: אחריה לא נרשמה שורת `THIRD_PARTY_DEVICE_MANAGEMENT_REQUIRED` חדשה. סה"כ 99 שורות מהחלון הקודם (`evidence/08`, `09`). נבדק שוב ב-`11-22 10:39` (0 חדשות) ואחרי כל אתחול (0 מאז האתחול, `evidence/10`).

### 4. DNS מה-u0 (המשאב ברמת המכשיר)
- `malware.testcategory.com` → `0.0.0.0,::` (DPC), ו-`0.0.0.0` / `::` (nslookup מהמארח דרך 1.1.1.3). **חסום.**
- `phishing.testcategory.com` → `0.0.0.0,::` (DPC), ו-`0.0.0.0` / `::` (nslookup). גיבוי לבדיקה 2 אם הדומיין הראשון ייחסם על ידי Family Link.
- `www.ynet.co.il` → `23.221.30.50` ו-IPv6 (DPC). במארח: CNAME ל-Akamai עם אותה כתובת. **נורמלי.**
- `ynet.co.il` → `2.20.34.50` (DPC ומארח). **נורמלי.**
- **`ping` מה-shell** מחזיר `127.0.0.1` לשמות החסומים. אין להם רשומה ב-`/system/etc/hosts`. זה זהה ל-A0.22 ולא הוסבר. לכן ההוכחה לחסימה נשענת על ה-DPC ועל ה-nslookup, לא על ה-shell.

### 5. בדיקות ההורה (user 10, דיווח הורה, ללא צילומי מסך)
| בדיקה | תוצאה | הערה |
|---|---|---|
| (1) www.ynet.co.il | **נחסם** עם דף "Ask your parent" של Family Link (12:09) | לא DNS: ה-resolver מחזיר כתובת נורמלית. החסימה מגיעה מ-Family Link |
| (2) malware.testcategory.com | **עבר** אחרי שההורה אישר את האתר ב-Family Link. Chrome: `ERR_CONNECTION_REFUSED` (12:47) | זו תשובת ה-DNS: `0.0.0.0` מ-Private DNS ברמת המכשיר. ניסיונות קודמים ("malware" ו-"maleware") לא נחשבים: "malware" הגיע ל-malware.google.com ונחסם ב-Family Link, ו-"maleware" הקלדה שגויה |
| (3) Settings → Private DNS | נעול, מציג `family.cloudflare-dns.com` | דיווח הורה |
| (4) Chrome → "Use secure DNS" | כבוי ואפור (לא ניתן לשינוי). הכותרת המשנית: "This setting is disabled on managed browsers". "Use your current service provider" נבחר (אפור); "Choose another provider" אפור | דיווח הורה (צילום מסך 12:49, לא נשמר). Chrome מתייחס לעצמו כ**מנוהל** ב-fl-kid, ולכן לא משתמש ב-DoH משלו; השמות נפתרים דרך ה-resolver של המערכת (Private DNS). **הסיבה: מצב "מנוהל" של Chrome. מיוחס ל-supervision (Family Link), לא הוכח:** לא הוגדרה אצלנו מדיניות Chrome ב-user 10 |
| (5) classroom.google.com | נפתח, **מחובר לחשבון הילד** | דיווח הורה |
| (6) YouTube | מחובר, באינטרנט (m.youtube.com) ובאפליקציה | דיווח הורה (שני צילומי מסך, לא נשמרו). באפליקציה ב-API 37 לא הופיעה שגיאת 400 |

**הערת בדיקה (בדיקה 2):** בדיקה 2 מוכיחה את שכבת ה-DNS רק אחרי שההורה אישר את האתר ב-Family Link. בלי האישור Family Link חוסמת לפני ה-DNS. התוצאה הזו מוכיחה את שכבת ה-DNS, לא את החסימה של Family Link.

### 6. השוואה ל-A0.22 (API 31)

| נושא | A0.22 (API 31) | A0.24 (API 37) |
|---|---|---|
| API ייעודי ל-Private DNS | נחסם: unaffiliated users | נחסם: זהה |
| `setGlobalSetting` | עובד | עובד |
| DoT מאומת | Wi-Fi | Wi-Fi ו-CELLULAR |
| הגבלה ברשימה האפקטיבית של המשתמש | לא חושבה כשהמשתמש עצור | חושבה ב-u10 כשהמשתמש רץ |
| ynet | נפתח (דיווח) | נחסם ב-Family Link (דיווח). לא DNS |
| malware | "נפתח" בטעות (הקלדה שגויה), ונחסם בשם המדויק | נחסם: `ERR_CONNECTION_REFUSED` אחרי אישור ב-Family Link |
| Private DNS ב-Settings | נעול (דיווח) | נעול (דיווח) |
| Chrome secure DNS | כבוי ונעול, סיבה לא נקבעה | כבוי ואפור (דיווח הורה). Chrome במצב "מנוהל" (מיוחס ל-supervision, לא הוכח), וה-DNS עובר ל-Private DNS של המערכת |
| classroom | נפתח | נפתח, מחובר לחשבון הילד (A0.24: דיווח הורה) |
| שם חסום ב-shell | 127.0.0.1 | 127.0.0.1 |

**הבדל ב-ynet:** ב-A0.22 ynet נפתח, וב-A0.24 הוא נחסם על ידי Family Link. ה-resolver נותן לשניהם תשובה נורמלית, כך שההבדל אינו ב-DNS. לא נבדק אם הסיבה היא גרסת Android או הגדרת Family Link של הילד (ייתכן מצב "רק אתרים מאושרים"). לא נקבע.

---

## מטריצה

| יכולת | ספק (vendor) | API | תוצאה | ראיה |
|---|---|---|---|---|
| `dpm set-device-owner` על user 0 (לפני חשבונות) | Google (אמולטור) | 37 | הצליח; ה-DO נשמר | `01`, `04` |
| Family Link כ-PO של user 10 | Google (אמולטור) | 37 | אומת (ProfileOwnerReceiver של supervision) | `04`, `10` |
| `setGlobalPrivateDnsModeSpecifiedHost` מה-DO | Google (אמולטור) | 37 | נחסם: unaffiliated users | `05` |
| `setGlobalSetting(private_dns_mode, hostname)` | Google (אמולטור) | 37 | הצליח | `05` |
| `setGlobalSetting(private_dns_specifier, family.cloudflare-dns.com)` | Google (אמולטור) | 37 | הצליח | `05`, `11` |
| DPM getter (mode, host) | Google (אמולטור) | 37 | `PROVIDER_HOSTNAME` / `family.cloudflare-dns.com` | `05`, `11` |
| DoT מאומת (WIFI ו-CELLULAR) | Google (אמולטור) | 37 | `[1.0.0.3,1.1.1.3]` | `05` |
| `DISALLOW_CONFIG_PRIVATE_DNS` (global, ו-effective ב-u10 כשרץ) | Google (אמולטור) | 37 | מופיע | `07`, `10` |
| DNS מה-u0 (DPC): malware.testcategory.com | Google (אמולטור) | 37 | `0.0.0.0,::` | `06`, `11` |
| DNS מה-u0 (DPC): phishing.testcategory.com | Google (אמולטור) | 37 | `0.0.0.0,::` | `09` |
| nslookup מהמארח דרך 1.1.1.3 | Cloudflare Families (מארח) | — | `0.0.0.0` / `::` | `09` |
| DNS מה-u0 (DPC): www.ynet.co.il | Google (אמולטור) | 37 | `23.221.30.50` (נורמלי) | `11` |
| `ping` מה-shell של שם חסום | Google (אמולטור) | 37 | `127.0.0.1` (לא הוסבר) | `06`, `09` |
| תנאי 15 הדקות אחרי ה-refusal | Google (אמולטור) | 37 | עבר: 0 שורות חדשות אחרי 23:09:05 | `08`–`10` |
| נעילת Private DNS ב-Settings (u10) | Google (אמולטור) | 37 | נעול (דיווח הורה) | `11` |
| Chrome secure DNS (u10) | Google (אמולטור) | 37 | כבוי ואפור: "disabled on managed browsers" (דיווח הורה). הסיבה: מצב מנוהל, מיוחס ל-supervision (לא הוכח) | `11` |
| `malware.testcategory.com` ב-Chrome (u10), אחרי אישור ב-Family Link | Google (אמולטור) | 37 | `ERR_CONNECTION_REFUSED` (דיווח הורה) | `11` |
| `www.ynet.co.il` ב-Chrome (u10) | Google (אמולטור) | 37 | נחסם ב-Family Link (דיווח הורה); לא DNS | `11` |
| `classroom.google.com` ב-Chrome (u10) | Google (אמולטור) | 37 | נפתח, מחובר לחשבון הילד (דיווח הורה) | `11` |
| YouTube באינטרנט ובאפליקציה (u10) | Google (אמולטור) | 37 | מחובר (דיווח הורה); אין 400 באפליקציה | `11` |
| שרידות אחרי שלוש יציאות של האמולטור | Google (אמולטור) | 37 | DO, PO, DNS, הגבלה וחשבונות נשמרו | `10` |

---

## מה לא מאומת
- **הטאבלט האמיתי (Lenovo TB328XU)** לא נבדק.
- **שרת ה-DoT של ה-hub שלנו** לא נבדק. הדומיין הוא Cloudflare Families.
- **שכבת הדומיין למבוגרים** לא נבדקה. הדומיינים שנבדקו הם שמות הבדיקה של Cloudflare, ו-ynet.
- **Chrome וגוגל-שירותים על מכשיר אמיתי** לא נבדקו. זה אמולטור של Google.
- **סיבת נעילת Private DNS ב-Settings (u10):** עקבית עם ההגבלה, לא מוכחת ישירות בלוג (כמו ב-A0.22).
- **Chrome secure DNS:** הערך כבוי ואפור (דיווח הורה, צילום מסך לא נשמר). הסיבה, מצב "מנוהל" של Chrome, מיוחסת ל-supervision ולא הוכחה. לא הוגדרה אצלנו מדיניית Chrome ב-user 10. בלוג הנוכחי (אחרי האתחול האחרון) לא נמצאה שורת מדיניית Chrome או DoH (4 שורות שנמצאו הן בדיקות רשת של המערכת), ולא נבדק אם Family Link היא המקור.
- **התחברות ל-classroom:** דווחה כמחוברת לחשבון הילד (דיווח הורה). לא נבדקה בידי הספייק.
- **`ping` מה-shell מחזיר 127.0.0.1:** לא הוסבר. זהה ל-A0.22.
- **הרשימה האפקטיבית של user 10 אחרי אתחול כשהמשתמש עצור:** לא נבדקה בנפרד.
- **סיבת הקריסות של המארח:** רק שמות המודולים ב-Windows APPCRASH. לא נבדקה עד הסוף, והיא לא קוד של הספייק.
- **הבדל ynet מול A0.22:** לא נקבע אם זה API או הגדרת Family Link.
- **`setGlobalSetting` בגרסאות Android מאוחרות:** נבדק רק על API 37.
- **`hw.keyboard` ב-config של ה-AVD:** לא אומת (הקובץ לא נמצא בחיפוש).
- **דיווחי ההורה:** לא אומתו בבדיקה עצמאית, וצילומי המסך לא נשמרו.

## ראיות
כל הקבצים תחת [evidence/](evidence/): `00-baseline` (מצב לפני), `01` (התקנה ו-DO), `02`–`03` (יצירת user 10 והגדרות), `04` (owners ו-Family Link), `05` (Private DNS: API חסום, `setGlobalSetting`, DoT), `06` (DNS מה-u0), `07` (הגבלה, effective ב-u10), `08` (refusal bursts), `09` (מועמד לבדיקה 2 ובדיקת תנאי ההחזקה), `10` (קריסות והפעלות מחדש), `11` (דיווחי ההורה ובדיקת ynet).
