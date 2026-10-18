# תוכנית MVP — סינון אתרים ל-Windows (גרסה 2)

> סטטוס: **מאושר** · עודכן 2026-10-18
> מחליף את [Prompt.md](legacy/Prompt.md) כהגדרת היעד. הקוד הקודם נשמר בתגית `legacy-v1` ולא יפותח הלאה.
> מסמך נלווה: [SIMULATION-PLAN.md](SIMULATION-PLAN.md) — מערך הסימולציות לבדיקת הסינון.

---

## 1. למה הגרסה הקודמת לא עובדת

הבעיה העיקרית היא ארכיטקטונית, לא אוסף באגים. הממצאים מהקוד, מ-[FIX_LOG.md](legacy/FIX_LOG.md) ומ-[REPAIR_STATUS.md](legacy/REPAIR_STATUS.md):

| # | בעיה | ראיה | השלכה |
|---|------|------|-------|
| 1 | **מנגנון הסינון המרכזי הוא שרת DNS מקומי** (`127.0.0.1:53`) + שינוי DNS של כרטיס הרשת | `DnsInterceptorService.cs` | DNS רואה רק שם דומיין — לא נתיב (אי אפשר לסנן ערוץ YouTube), לא יודע **איזה משתמש** שאל (כל השאילתות מגיעות משירות ה-DNS Client של Windows), ונעקף ע"י DoH, VPN, קובץ hosts ומטמון. שינוי DNS דורש הרשאות מנהל. |
| 2 | **"טלאים" על מנגנון 1**: הריגת דפדפנים | `PolicyEnforcementService.cs`, לוג 19: `Terminating unauthorized browser: chrome` | Chrome ו-Firefox נהרגים **לכל המשתמשים, כולל מבוגרים**. |
| 3 | **WFP דרך P/Invoke ידני מ-C#** | Fix 2.5/2.6 | מבני נתונים שגויים גרמו ל-`AccessViolation` שהפיל את כל התהליך. ה"תיקון" היה לכבות את WFP כשאין הרשאות מנהל — כלומר הוא מעולם לא נבדק במצב אמיתי. |
| 4 | **אין מקור אמת אחד לנתונים** | 4 הגדרות סכמה שונות: `schema.sql`, `urlfilter.sql`, `DatabaseService.CreateBasicTablesAsync`, `UserDatabaseService` של ממשק הניהול | טבלאות סותרות (`audit_log` מול `audit_logs`, `system_settings` מול `admin_settings`). ממשק הניהול פותח את קובץ ה-DB ישירות; השירות (כ-SYSTEM) יוצר אותו עם הרשאת קריאה בלבד למשתמשים (Fix 2.4) — כך ששינויים מממשק הניהול לא נשמרים או לא מגיעים לשירות. |
| 5 | **"עובד" נבדק רק במצב קונסול ללא הרשאות** | REPAIR_STATUS: "Elevation-gated" | השירות לא הותקן ונבדק כשירות אמיתי מול חשבון ילד. הבדיקה היחידה שעברה היא DNS smoke test. |
| 6 | **היקף עצום, עומק רדוד** | ~15,000 שורות ב-Windows, `MainWindow.xaml.cs` 1,089 שורות, Android עם 72 שגיאות קומפילציה של מחלקות שלא נכתבו | הפרומפט ביקש הכול (Android, ארגונים, K8s, AI) ולכן נבנה רוחב בלי ליבה עובדת. |
| 7 | יעד `.NET 6` | כל ה-csproj | לא נתמך מאז 2024. |

**המסקנה:** לא מתקנים — בונים מחדש עם מנגנון שמתאים לדרישות (סינון לפי URL מלא ולפי משתמש Windows), ומוכיחים אותו *לפני* שבונים סביבו.

---

## 2. הגדרת המטרה מחדש

### 2.1 משפט המטרה

> כמה מחשבי Windows 10/11 בבית (לא בדומיין), מנוהלים **ממחשב אחד** (Hub) דרך הרשת הביתית. ההורים הם חשבונות **מנהל** מקומיים, הילדים הם חשבונות **רגילים** (Standard). בכל מחשב מותקן סוכן שאוכף מקומית — גם כשה-Hub כבוי וגם מחוץ לבית: סינון גלישה ב-**Edge וב-Chrome** לפי משתמש Windows (רשימות היתר/חסימה, מצב "רק אתרים מאושרים", ערוצי YouTube מאושרים), **הרשאת רשת לפי אפליקציה**, שעות ומכסה יומית **משותפת לכל המחשבים** של הילד, ויומן. ממשק ניהול בעברית. דפדפנים אחרים חסומים לילדים. התקנה של מחשב: פחות מ-10 דקות.

### 2.2 בתוך ה-MVP

- **ניהול מרכזי:** Hub אחד (על מחשב ההורים) מנהל את כל המחשבים ברשת הביתית; צימוד מאובטח; סנכרון הגדרות, שימוש ואירועים
- **בני משפחה** שמקושרים לחשבונות בכמה מחשבים, עם מכסה יומית משותפת
- סינון לפי משתמש Windows (מזוהה ע"י מערכת ההפעלה, לא ניתן לזיוף)
- שלושה מצבי סינון: **ללא הגבלה** / **רשימה שחורה** / **רק אתרים מאושרים**
- תבניות התאמה: דומיין (כולל תתי-דומיינים), wildcard, קידומת URL, regex (מתקדם)
- רשימות פתיחה מוכנות: חינוכי, רשתות חברתיות, משחקים
- YouTube: מצב מוגבל (Restricted Mode), חסימת ערוצים, או **רק ערוצים מאושרים**
- **שכבת WFP (בלי דרייבר):** לילד רק דפדפנים נתמכים ואפליקציות מאושרות מקבלים רשת; כשהזמן נגמר — כל האפליקציות שלו מנותקות
- זמן גלישה: חלונות זמן לפי יום בשבוע + מכסה יומית, אזהרות לפני סיום, הארכה מהירה, מצב שיעורי בית
- SafeSearch כפוי ב-Google/Bing
- יומן: חסימות, ניסיונות עקיפה, שינויי הגדרות, אפליקציות שניסו להתחבר
- מסך חסימה ידידותי עם סיבה וכפתור "בקש גישה"
- ממשק ניהול WPF בעברית (RTL) + אשף הגדרה ראשונית
- הגנה מפני עקיפה ברמת דפדפן ומערכת (גלישה בסתר, אורח, פרופיל חדש, מצב מפתח, דפדפנים אחרים, אפליקציות VPN)
- מתקין MSI (תפקיד Hub או סוכן) והסרה נקייה
- **מערך סימולציות** בחמש רמות שמריצים אחרי כל בנייה ([SIMULATION-PLAN.md](SIMULATION-PLAN.md))

### 2.3 מחוץ ל-MVP (שלב 2 ואילך)

| נושא | למה לא עכשיו |
|------|--------------|
| Android | פלטפורמה נפרדת; רק אחרי שהליבה מוכחת ב-Windows. ה-MVP כבר מכין אותה (סעיף 13) |
| ניהול מחוץ לבית (ממסר בענן), אפליקציית טלפון, ממשק web | ב-MVP הסנכרון רק ברשת הביתית; ערוץ התקשורת ניתן להחלפה |
| AD / Entra ID / MSAL / MDM | MVP ביתי; מנהל = מנהל Windows מקומי |
| בדיקת HTTPS (MITM) ודרייבר ליבה של WFP | לא נחוצים: התוסף רואה את ה-URL, ושכבת WFP פועלת בלי דרייבר |
| Firefox | נחסם לילדים ב-MVP; תמיכה בשלב 2 (מדיניות Firefox + תוסף חתום ב-AMO) |
| זמן מסך מלא (נעילת המחשב) | ה-MVP סופר **זמן גלישה**; נעילת session בשלב 2 |
| שכבת רשת משלנו (שרת DNS ב-Hub) | ב-MVP: המלצה להגדיר DNS משפחתי בראוטר (Cloudflare 1.1.1.3) או AdGuard Home — בלי פיתוח |
| דוחות גרפיים מתקדמים, המלצות AI, תגמולים | ערך נוסף, לא ליבה |
| עדכון אוטומטי של השירות | MSI upgrade ידני ב-MVP (התוסף מתעדכן אוטומטית מהחנות) |

### 2.4 קריטריוני הצלחה ל-MVP (מדידים)

1. ילד בחשבון רגיל **לא מצליח** להגיע לאתר חסום ב-Edge או ב-Chrome, ולא להפעיל אפליקציה לא מאושרת עם רשת, באף אחד מתרחישי העקיפה (סימולציות רמה 4).
2. מבוגר לא מרגיש שום הבדל בגלישה ובאפליקציות.
3. החלטת סינון בסוכן: p99 < 2ms עם 10,000 כללים. הסוכן: < 100MB זיכרון, < 1% CPU במנוחה.
4. התקנה על VM נקי: < 10 דקות למחשב כולל צימוד. הסרה: לא נשארות מדיניות דפדפן ולא מסנני WFP.
5. המערכת ממשיכה לעבוד אחרי אתחול ועדכון Windows/דפדפן.
6. שינוי בממשק הניהול נאכף בדפדפן של הילד, **בכל מחשב ברשת**, תוך פחות מ-5 שניות.
7. כש-Hub כבוי: כל הסוכנים ממשיכים לאכוף לפי ההגדרות האחרונות; השימוש מסתנכרן כשהוא חוזר.
8. כל חבילות הסימולציה של רמות 1–4 עוברות.

---

## 3. ארכיטקטורה

### 3.1 תמונה כללית — כמה מחשבים

```
        מחשב ההורים                                  מחשבי הילדים
 ┌──────────────────────────────┐              ┌──────────────────────────────┐
 │ WebFilter.Admin (WPF)        │              │ Agent (אותה תוכנה, תפקיד סוכן)│
 │        │ admin pipe          │   HTTPS      │ • אכיפה מקומית (תוסף + WFP)  │
 │ WebFilter.Service  [Hub]     │◄────────────►│ • עותק אחרון של ההגדרות      │
 │ • הגדרות ראשיות (master)     │  TLS הדדי    │ • ספירת שימוש + תור אירועים  │
 │ • בני משפחה, מכשירים, צימוד  │  ברשת הביתית │ • ממשיך לעבוד בלי Hub        │
 │ • צבירת שימוש משותף, דוחות   │◄────────────►│                              │
 │ • גם סוכן מקומי (אם יש ילדים) │              └──────────────────────────────┘
 └──────────────────────────────┘
               ראוטר: DNS משפחתי לכל שאר המכשירים (טלפונים, טלוויזיה) — הגדרה, לא פיתוח
```

### 3.2 תמונה כללית — בתוך מחשב אחד

```
 ┌─────────────── session של הילד (משתמש רגיל) ───────────────┐
 │  Edge / Chrome                                              │
 │  ┌──────────────────────────────┐                           │
 │  │ תוסף WebFilter (MV3, כפוי)   │  כללי DNR, YouTube,       │
 │  │                              │  מסך חסימה, זמן           │
 │  └──────────────┬───────────────┘                           │
 │                 │ Native Messaging (stdin/stdout)           │
 │  ┌──────────────▼───────────────┐                           │
 │  │ WebFilter.NativeHost.exe     │ רץ בהרשאות הילד           │
 │  └──────────────┬───────────────┘                           │
 └─────────────────┼───────────────────────────────────────────┘
                   │ Named Pipe  \\.\pipe\WebFilter.Agent
                   │ (השירות מזהה את המשתמש מה-token של התהליך)
 ┌─────────────────▼───────────────────────────────────────────┐
 │ WebFilter.Service  (Windows Service, LocalSystem)           │
 │ • מנוע החלטות על חבילת ההגדרות של המכשיר                   │
 │ • ספירת זמן גלישה                                           │
 │ • מדיניות דפדפן (HKLM + HKCU של ילדים) + שמירה עליה         │
 │ • WfpManager ──► WebFilter.Wfp.Native.dll (C++) ──► WFP     │
 │ • שומר תהליכים: הורג דפדפנים לא נתמכים בסשן של ילד          │
 │ • HubSync (סוכן) / HubServer (Hub)                          │
 │ • SQLite  (%ProgramData%\WebFilter, רק SYSTEM+Admins)       │
 └─────────────────────────────────────────────────────────────┘
```

### 3.3 עקרונות שמונעים את הכשלים הקודמים

1. **ניהול מרכזי, אכיפה מקומית.** ה-Hub הוא מישור הניהול בלבד. כל החלטת סינון מתקבלת בסוכן במחשב עצמו, כך שכיבוי ה-Hub, ניתוק רשת או יציאה מהבית לא מבטלים את ההגנה.
2. **בעלות ברורה על נתונים.** ה-Hub הוא הבעלים של ההגדרות; הסוכן הוא הבעלים של השימוש הגולמי והאירועים שלו. ממשק הניהול והתוסף לא נוגעים ב-DB — רק דרך pipe. סכמה אחת לכל תפקיד, migrations ממוספרים.
3. **זהות המשתמש מגיעה ממערכת ההפעלה.** השירות מזהה את הלקוח לפי ה-token של התהליך שמחובר ל-pipe — לא לפי מה שהלקוח טוען.
4. **סינון איפה שהמידע גלוי.** כתובת מלאה — בתוך הדפדפן. איזו אפליקציה ואיזה משתמש — ב-WFP. בלי DNS כמנגנון ראשי ובלי MITM.
5. **Fail-closed לילדים.** אם התוסף לא מצליח לדבר עם השירות — גלישה חסומה למשתמש שאינו מנהל.
6. **כל שכבה נבדקת במצב אמיתי** — שירות מותקן, חשבון ילד אמיתי, VM נקי, מכמה מחשבים. לא "עובד בקונסול".
7. **ממשקי API נתמכים בלבד.** WFP דרך רכיב C++ שמשתמש בכותרות הרשמיות (`fwpmu.h`), עם API שטוח ופשוט ל-C# — בלי מבני נתונים שנכתבו ידנית.
8. **פרוטוקול Hub נייטרלי לפלטפורמה.** JSON עם גרסאות, בלי מושגים של Windows, כדי שסוכן Android יתחבר אליו בעתיד.

### 3.4 למה דווקא תוסף כפוי + WFP?

| חלופה | רואה URL מלא | יודע איזה משתמש | יודע איזו אפליקציה | נעקף ע"י DoH/VPN | מורכבות |
|-------|:---:|:---:|:---:|:---:|:---:|
| DNS מקומי (הגרסה הקודמת) | ✗ | ✗ | ✗ | ✓ נעקף | בינונית |
| פרוקסי + MITM | ✓ | חלקית | ✗ | חלקית | גבוהה מאוד, שובר אפליקציות |
| WFP עם דרייבר ליבה | ✗ (רק IP/SNI) | ✓ | ✓ | חלקית | גבוהה מאוד, חתימת Microsoft |
| **תוסף כפוי + WFP בלי דרייבר** | ✓ | ✓ | ✓ | ✗ לא נעקף | בינונית |

החלוקה: **WFP קובעת איזו אפליקציה רשאית להתחבר בכלל, התוסף קובע לאיזו כתובת.**

---

## 4. רכיבים

### 4.1 `WebFilter.Core` (ספריה, ללא תלות ב-Windows)

- מודלים: `FamilyMember`, `Device`, `Rule`, `Schedule`, `YouTubeChannel`, `AppRule`, `PolicyBundle`, `Decision`
- **נרמול URL** (נקודה רגישה לעקיפה): אותיות קטנות בשם המארח, IDN → punycode, הסרת נקודה סופית ופורט ברירת מחדל, הסרת `user:pass@`, פענוח percent-encoding בשם המארח, זיהוי כתובות IP (כולל צורות עשרוניות/הקסה ו-IPv6)
- **התאמת תבניות** וסדר עדיפויות (סעיף 6)
- **מפענח URL של YouTube**: `/channel/UC…`, `/@handle`, `/c/…`, `/user/…`, `watch?v=`, `youtu.be/…`, `/shorts/…`, `/embed/…`, `m.`, `music.`, `youtube-nocookie.com`
- **מחשבון זמן**: חלונות זמן, מכסה, הארכות, מעבר יום, שעון קיץ, מכסה משותפת — עם שעון מוזרק
- **מהדר חבילות** — הופך את הגדרות המשפחה לחבילת מדיניות למכשיר (`PolicyBundle`): רשימות, חלונות זמן, ערוצים, אפליקציות
- **מהדר DNR** — הופך חבילה לכללי declarativeNetRequest למשתמש

### 4.2 `WebFilter.Service` (Worker Service, `UseWindowsService`) — תפקיד Agent / Hub

אותו קובץ הרצה; התפקיד נקבע בהתקנה. ה-Hub מריץ גם סוכן מקומי.

| תת-רכיב | תפקיד | אחריות |
|---------|:---:|--------|
| `Storage` | שניהם | SQLite (Microsoft.Data.Sqlite + Dapper), migrations מוטמעים, ACL על התיקייה |
| `AgentPipeServer` | שניהם | pipe לתוסף. אימות: `GetNamedPipeClientProcessId` → נתיב התהליך חייב להיות ה-NativeHost שלנו ב-Program Files; זהות = SID מה-impersonation |
| `AdminPipeServer` | שניהם | pipe לממשק הניהול. DACL מאפשר רק `BUILTIN\Administrators` — token לא מוגבה נדחה. בסוכן: רק סטטוס והשהיית חירום מקומית |
| `DecisionService` | שניהם | מחזיק את חבילת המדיניות בזיכרון, מחזיר החלטות, מהדר DNR לכל משתמש, דוחף שינויים לתוספים מחוברים |
| `UsageTracker` | שניהם | צובר דקות לפי heartbeats; מניעת ספירה כפולה; שולח ל-Hub; מחשב מכסה משותפת לפי הסכום האחרון מה-Hub + שימוש מקומי |
| `WfpManager` | שניהם | מסנני WFP קבועים (persistent) תחת provider ו-sublayer משלנו (סעיף 5.3); מתעדכן לפי חבילה ומצב זמן; מדווח על חיבורים שנחסמו |
| `PolicyManager` | שניהם | כותב מדיניות דפדפן (סעיף 5), עוקב אחרי שינויים (`RegNotifyChangeKeyValue`) ומשחזר; כותב HKCU של ילד בעת התחברות (`OnSessionChange`) |
| `ProcessGuard` | שניהם | מאזין להפעלת תהליכים (ETW/WMI). בסשן של ילד: דפדפן שאינו Edge/Chrome חתום (Authenticode + `OriginalFilename`) — נהרג ונרשם |
| `TamperMonitor` | שניהם | דפדפן בסשן של ילד בלי heartbeat מהתוסף > 2 דקות → אירוע; קפיצות שעון; מסנני WFP או מדיניות שנמחקו |
| `ProfileCache` | שניהם | `%ProgramData%\WebFilter\profiles\<SID>.json`: תפקיד המשתמש ורשימת "מותר תמיד" (ACL: SYSTEM כתיבה, אותו משתמש קריאה). משמש את ה-NativeHost כשהשירות לא זמין |
| `HubSync` | סוכן | חיבור ל-Hub: משיכת חבילה לפי גרסה, דחיפת שימוש ואירועים (תור יוצא עם מזהי idempotency), התחברות מחדש |
| `HubServer` | Hub | HTTPS (Kestrel) עם TLS הדדי; צימוד; רישום מכשירים; הידור חבילה לכל מכשיר; צבירת שימוש משותף; דחיפת שינויים (SSE/WebSocket) |
| `Discovery` | שניהם | mDNS `_webfilter._tcp` + הזנת כתובת ידנית כגיבוי; כלל חומת אש נכנס ב-Hub (פרופיל Private בלבד) |
| `EventLog` | שניהם | Serilog → קובץ מתגלגל + Windows Event Log |
| `YouTubeResolver` | Hub | פענוח handle/URL ל-channel ID (מטא-דאטה של דף הערוץ; YouTube Data API אופציונלי) |
| `SimulationHooks` | — | **רק בתצורת build `Simulation`**: שעון ניתן לשליטה ופקודות בדיקה. לא קיים בקבצים שמשוחררים (`#if SIMULATION`) |

הגדרות שירות: הפעלה אוטומטית, recovery = הפעלה מחדש בכשל, רישום ב-SafeBoot\Network.

### 4.3 `WebFilter.NativeHost` (קונסול, NativeAOT)

- Chrome/Edge מפעילים אותו כתהליך בן של הדפדפן, **בהרשאות המשתמש**
- מעביר הודעות בין stdin/stdout (מסגרת: 4 בתים אורך + JSON) לבין ה-pipe של השירות
- אם השירות לא זמין: מנהל → "ללא הגבלה"; משתמש מוגבל → רק אתרי "מותר תמיד" מה-`ProfileCache`, וכל השאר **חסום** עם הודעה "שירות הסינון לא פועל"; אין קובץ → חסימה מלאה
- NativeAOT כדי שיעלה מהר (עשרות ms)
- רישום: `HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.webfilter.host` ו-`HKLM\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.webfilter.host`; ה-manifest מתיר רק את מזהי התוסף שלנו (מזהה שונה ב-Chrome Web Store וב-Edge Add-ons)

### 4.4 התוסף (Manifest V3, TypeScript)

| מנגנון | שימוש |
|--------|-------|
| Static ruleset `failclosed` (פעיל כברירת מחדל) | חוסם כל `main_frame`/`sub_frame` עד שהשירות מאשר את פרופיל המשתמש. למבוגר — מכובה מיד אחרי ה-hello |
| Dynamic rules (עד 30,000; עד 5,000 "unsafe" כמו redirect) | הכללים שהשירות הידר למשתמש. דומיינים נארזים במערכי `requestDomains` |
| Session rules | "הזמן נגמר" — חסימה של הכול חוץ מאתרים שמותרים תמיד |
| Content script ב-`*.youtube.com` | זיהוי ערוץ וחסימה (סעיף 7) |
| `blocked.html` | מסך חסימה: סיבה, כפתור "בקש גישה", דיווח לשירות |
| Badge + notifications | זמן שנותר, אזהרות 10/5/1 דקות |

- מסננים רק `main_frame` ו-`sub_frame` — לא משאבי משנה (CDN, תמונות)
- `chrome.runtime.connectNative` שומר על ה-service worker בחיים כל עוד ה-port פתוח
- heartbeat כל 30 שניות: `{focused, idleState, hasWebTab}`
- אותו קוד ל-Chrome ול-Edge
- כל דף של התוסף מציג כותרת חלון קבועה (למשל `WebFilter · חסום`) — כך סימולציות רמה 4 מזהות את התוצאה בלי DevTools

### 4.5 `WebFilter.Admin` (WPF, MVVM עם CommunityToolkit.Mvvm)

- `requireAdministrator` → UAC. רק מנהל Windows נכנס
- עברית RTL, מחרוזות ב-`.resx`
- **במחשב ה-Hub:** ניהול מלא של כל המשפחה וכל המחשבים
- **במחשב סוכן:** סטטוס, "מנוהל ע"י Hub ‹שם›", והשהיית חירום מקומית (נרשמת ומסונכרנת)

| מסך | תוכן |
|-----|------|
| אשף הגדרה ראשונית | בני משפחה → תפקיד וגיל → תבנית (ילדים קטנים / נוער / מבוגר) → בדיקת דרישות → הוראות לצימוד מחשבים נוספים |
| מכשירים | רשימת מחשבים (מחובר/לא, גרסה, סנכרון אחרון), צימוד מחשב חדש (קוד), קישור חשבונות Windows של כל מחשב לבני משפחה, הסרת מכשיר |
| לוח בקרה | כרטיס לכל בן משפחה: איפה מחובר, זמן שנוצל/נותר (סה"כ בכל המחשבים), חסימות היום. פעולות מהירות: +15/+30 דק', השהייה, מצב שיעורי בית. התראות ובקשות גישה |
| בן משפחה | מצב סינון, לוח שבועי, מכסה יומית, כללים אישיים, ערוצי YouTube, **אפליקציות מאושרות** |
| רשימות | רשימות גלובליות, קטגוריות מוכנות, ייבוא/ייצוא JSON |
| YouTube | הוספת ערוץ לפי URL או handle, היתר/חסימה לכל בן משפחה או לכולם |
| יומן | חסימות, אפליקציות שנחסמו, ניסיונות עקיפה, שינויי הגדרות; סינון וייצוא CSV |
| הגדרות ואבחון | אזור זמן, מצב השירות בכל מחשב, מדיניות, WFP, heartbeat של התוסף, המלצה ל-DNS משפחתי בראוטר |

### 4.6 מתקין (WiX, MSI x64 per-machine)

- בחירת תפקיד: **Hub** או **סוכן**; לסוכן — כתובת Hub (או גילוי אוטומטי) + קוד צימוד
- קבצים ל-`C:\Program Files\WebFilter\`, נתונים ב-`C:\ProgramData\WebFilter\`
- `ServiceInstall` + `ServiceControl`; רישום Native Host ומדיניות HKLM ע"י MSI
- ב-Hub: כלל חומת אש נכנס לפורט ה-Hub (Private בלבד)
- Custom action בהסרה: `WebFilter.Service.exe --cleanup` — מוחק מדיניות HKCU מכל הפרופילים, את מסנני ה-WFP שלנו, ומבטל רישום מה-Hub
- `MajorUpgrade` לשדרוגים

---

## 5. מדיניות דפדפן ו-WFP

**חשוב:** במחשב שאינו בדומיין, Chrome ו-Edge מתקינים בכפייה **רק תוסף שמפורסם בחנות שלהם** (Chrome Web Store / Microsoft Edge Add-ons). התוסף יתפרסם בשתי החנויות כ-unlisted. חלופה ל-Chrome: Chrome Enterprise Core.

### 5.1 HKLM — לכל המשתמשים (התוסף קיים אצל כולם; למבוגרים הוא שקוף)

| מדיניות (Chrome ו-Edge) | ערך | מטרה |
|------------------------|-----|------|
| `ExtensionInstallForcelist` | `<id>;<update-url של החנות>` | התקנה כפויה; המשתמש לא יכול להסיר או לכבות |
| `NativeMessagingUserLevelHosts` | `0` | מונע רישום host מזויף ב-HKCU |

### 5.2 HKCU של משתמש מוגבל בלבד (`HKEY_USERS\<SID>\Software\Policies\…`)

| מדיניות | ערך | מטרה |
|---------|-----|------|
| `IncognitoModeAvailability` (Chrome) / `InPrivateModeAvailability` (Edge) | `1` | אין גלישה בסתר |
| `BrowserGuestModeEnabled` | `0` | אין מצב אורח |
| `BrowserAddPersonEnabled` | `0` | אין פרופילים חדשים |
| `ExtensionDeveloperModeSettings` | `1` | אין מצב מפתח |
| `DeveloperToolsAvailability` | `2` | אין כלי מפתח |
| `ExtensionInstallBlocklist` | `*` | אין התקנת תוספים אחרים |
| `ForceGoogleSafeSearch` (+ `ForceBingSafeSearch` ב-Edge) | `1` | SafeSearch |
| `ForceYouTubeRestrict` | `1`/`2` לפי פרופיל | YouTube Restricted Mode |

> אם ספייק S1 יראה שחלק מהמדיניות לא נאכפת מ-HKCU במחשב לא מנוהל — חוזרים ל-HKLM לאותן מדיניות (המחיר: גם המבוגרים יאבדו גלישה בסתר והתקנת תוספים).

### 5.3 WFP — הרשאת רשת לפי משתמש ואפליקציה

- **רכיב:** `WebFilter.Wfp.Native.dll` (C++), API שטוח: `AddUserAppPolicy(sid, allowedAppPaths[], mode)`, `RemoveAll()`, `SubscribeBlockedEvents(callback)`
- **מסננים:** בשכבות `ALE_AUTH_CONNECT_V4/V6` (ו-`ALE_AUTH_RECV_ACCEPT` לפי הצורך), תנאים `ALE_USER_ID` (המשתמש המוגבל) + `ALE_APP_ID` (נתיב האפליקציה) / `ALE_PACKAGE_ID` (אפליקציות Store)
- **מדיניות למשתמש מוגבל:**
  - מצב רגיל: דפדפנים נתמכים + אפליקציות מאושרות → מותר; כל השאר → חסום
  - הזמן נגמר / מחוץ לשעות: רק דפדפנים (התוסף מציג "הזמן נגמר" ומתיר "מותר תמיד") + אפליקציות "מותר תמיד" (למשל אפליקציית בית הספר); כל השאר → חסום
- **מבוגרים ו-SYSTEM לא מושפעים** — שירותי Windows (עדכונים, DNS) רצים כ-SYSTEM
- **persistent** — המסננים שורדים קריסת שירות ואתחול; השירות מיישב אותם מול החבילה בכל הפעלה
- העתקת `chrome.exe` לנתיב אחר = נתיב לא מאושר = חסום
- חיבור שנחסם → אירוע "אפליקציה ניסתה להתחבר" → ההורה יכול לאשר מלוח הבקרה

---

## 6. סמנטיקה של החלטת סינון

סדר הבדיקה ל-URL ראשי (`main_frame`/`sub_frame`):

1. משתמש **ללא הגבלה**, או שההגבלות מושהות לו כרגע → **מותר** (וזמן לא נספר)
2. סכמה שאינה http/https → לפי מדיניות הדפדפן, לא דרכנו
3. **מצב שיעורי בית** פעיל → רק קטגוריית "חינוכי" + "מותר תמיד" מותרים; זמן לא נספר
4. **זמן**: מחוץ לחלון → **חסום (`blocked_time_window`)**; המכסה נגמרה → **חסום (`blocked_daily_limit`)** — אלא אם האתר ב"מותר תמיד"
5. כתובת IP ישירה → **חסום (`blocked_ip_literal`)**, אלא אם הותרה במפורש
6. **כללים אישיים** של בן המשפחה: הכלל הספציפי ביותר קובע
7. **כללים גלובליים** וקטגוריות פעילות: הכלל הספציפי ביותר קובע
8. ברירת מחדל לפי מצב: "רק מאושרים" → **חסום**; "רשימה שחורה" → **מותר**

**ספציפיות:** קידומת URL → מארח מדויק → `*.domain` (יותר רמות = יותר ספציפי) → regex. בתיקו — **חסימה גוברת**.

**חלונות זמן:** `[התחלה, סוף)` — ההתחלה כלולה, הסוף לא. לפי שעון קיר באזור הזמן שנשמר בהגדרות.

**דוגמאות:** חסימת `*.google.com` + היתר `docs.google.com` → Docs מותר. חסימת `blocked.com` לא חוסמת את `blocked.com.evil.com` — זה דומיין אחר (הבדיקה ב-Prompt.md שציפתה לחסום כל URL שמכיל "blocked" שגויה).

**Regex:** חייב להיות תואם RE2 (מתורגם ל-`regexFilter`, עד 1,000). ממשק הניהול דוחה lookaround ו-backreferences.

**קודי סיבה** (משותפים לכל הרכיבים ולסימולציות): `allowed_unrestricted`, `allowed_paused`, `allowed_always`, `allowed_rule`, `allowed_category`, `allowed_default`, `allowed_homework`, `allowed_youtube_channel`, `allowed_browser`, `blocked_rule`, `blocked_category`, `blocked_not_in_allowlist`, `blocked_time_window`, `blocked_daily_limit`, `blocked_homework`, `blocked_ip_literal`, `blocked_youtube_channel`, `blocked_youtube_unknown`, `blocked_youtube_off`, `blocked_service_unavailable`, `blocked_app_network`, `redirect_youtube_home`.

**חשבון לא מקושר:** חשבון Windows לא-מנהל שעדיין לא קושר לבן משפחה מקבל רק "מותר תמיד", עד שההורה מקשר אותו.

הסמנטיקה המדויקת (גבולות, ספירת דקות, הארכות, ניתוק Hub) מוגדרת ב-[tests/simulation/README.md](../tests/simulation/README.md) ונבדקת בתרחישים.

---

## 7. YouTube

| מצב | מימוש |
|-----|-------|
| כבוי | YouTube חסום לגמרי (`blocked_youtube_off`) |
| מוגבל | `ForceYouTubeRestrict` + חסימת ערוצים ספציפיים |
| **רק ערוצים מאושרים** | content script, ראו למטה |
| ללא הגבלה | בלי התערבות |

**מצב "רק ערוצים מאושרים":**
- content script ב-`document_start` וב-`all_frames`, מסתיר את הדף עד שמתקבלת החלטה
- מזהה ערוץ: מה-URL (`/channel/UC…`); בסרטון/Shorts — מנתוני הדף (`videoDetails.channelId`); בדף ערוץ לפי handle — מהמטא-דאטה (`externalId`/canonical)
- ניווט בתוך האתר (SPA): אירועי `yt-navigate-finish`
- ההחלטה מקומית — רשימת ה-channel IDs המותרים מסונכרנת לתוסף
- דף הבית והחיפוש → דף "הערוצים שלי" של התוסף
- בסרטון מותר: הסתרת המלצות הצד ומסך הסיום
- הטמעות — מותרות רק אם הערוץ מאושר; אם אי אפשר לזהות → חסום (`blocked_youtube_unknown`)

**הוספת ערוץ:** מדביקים URL או `@handle` בממשק הניהול → ה-Hub מפענח ל-channel ID ושומר שם ותמונה.

---

## 8. זמן גלישה

- **מקור האמת לשימוש גולמי:** הסוכן בכל מחשב. דקה נספרת אם התקבל בה לפחות heartbeat "פעיל" אחד (חלון בפוקוס + `idle=active` + לשונית web). Edge ו-Chrome באותו מחשב = דקה אחת
- **מכסה משותפת:** ה-Hub צובר דקות מכל המחשבים (איחוד לפי דקה — שני מחשבים באותה דקה = דקה אחת) ומחזיר לכל סוכן את הסכום
- **כשה-Hub לא זמין:** הסוכן אוכף לפי הסכום האחרון שקיבל + השימוש המקומי שלו. חריגה אפשרית בזמן ניתוק ממושך — כשהחיבור חוזר והסכום עובר את המכסה, הזמן נגמר מיד בכל המחשבים
- **חלונות זמן:** כמה חלונות לכל יום בשבוע; **מכסה יומית** לכל יום; איפוס בחצות לפי אזור הזמן שבהגדרות (לא לפי אזור הזמן של המערכת — משתמש רגיל יכול לשנות אותו)
- **אזהרות:** 10/5/1 דקות. בסיום: התוסף מפעיל session rule של חסימה, ו-WFP מנתק אפליקציות לא "מותרות תמיד"
- **פעולות הורה:** +15/+30/+60 להיום, השהייה, **מצב שיעורי בית** (לזמן קצוב: רק חינוכי, לא נספר)
- כל התאמה נרשמת (`time_adjustments`): מי, מתי, למה

---

## 9. מודל נתונים

מזהים: ULID (ממוינים לפי זמן, ייחודיים בין מכשירים). כל טבלה שמסתנכרנת כוללת `updated_at` ו-`version`.

### 9.1 Hub (מקור האמת להגדרות)

```sql
family_members(id PK, display_name, role CHECK(role IN('child','teen','adult')), age, avatar NULL,
               filter_mode CHECK(filter_mode IN('unrestricted','blocklist','allowlist')),
               youtube_mode CHECK(youtube_mode IN('off','restricted','allowlist','unrestricted')),
               paused_until NULL, homework_until NULL, created_at, updated_at, version)

devices(id PK, name, os CHECK(os IN('windows','android')), role CHECK(role IN('hub','agent')),
        client_cert_thumbprint, capabilities_json, agent_version, last_seen_at, paired_at, revoked_at NULL)

device_accounts(device_id, account_id,   -- account_id: SID ב-Windows; מחרוזת כללית בפרוטוקול
                account_name, member_id NULL REFERENCES family_members, PK(device_id, account_id))

rules(id PK, member_id NULL, pattern, pattern_type CHECK(IN('domain','wildcard','prefix','regex')),
      action CHECK(IN('allow','block')), always_allowed BOOL, category NULL, expires_at NULL,
      created_at, created_by)

categories(id PK, name, description, source) · category_domains(category_id, domain)
member_categories(member_id, category_id, action)

app_rules(id PK, member_id NULL, match_type CHECK(IN('path','package','publisher')), value,
          action CHECK(IN('allow','block')), always_allowed BOOL, created_at, created_by)

schedules(id PK, member_id, day_of_week 0-6, start_minute, end_minute)
daily_limits(member_id, day_of_week, minutes, PK(member_id, day_of_week))
time_adjustments(id PK, member_id, local_date, delta_minutes, kind, reason, created_by, created_at)

youtube_channels(id PK, channel_id, handle, title, thumbnail_url, member_id NULL,
                 action CHECK(IN('allow','block')), added_by, added_at)

usage_minutes(member_id, device_id, minute_utc, PK(member_id, device_id, minute_utc))  -- מאוחסן 35 יום
events(id PK, device_id, ts, member_id NULL, type, url NULL, app NULL, reason NULL, details_json NULL)
access_requests(id PK, member_id, device_id, kind CHECK(IN('url','app')), target, note, ts,
                status CHECK(IN('pending','approved','denied')), decided_by, decided_at)
pairing_codes(code_hash PK, created_at, expires_at, used_at NULL)
config_versions(version PK, created_at, created_by, summary)
settings(key PK, value) · schema_version(version, applied_at)
```

### 9.2 Agent (עותק ותורים)

```sql
policy_bundle(version PK, received_at, bundle_json)        -- החבילה האחרונה מה-Hub
shared_usage(member_id, local_date, total_minutes, as_of)   -- הסכום האחרון מה-Hub
local_usage(member_id, minute_utc, PK(member_id, minute_utc))
outbox(id PK, kind CHECK(IN('usage','event','access_request')), payload_json, created_at, sent_at NULL)
hub_identity(hub_id, hub_ca_cert, client_cert, client_key_protected)   -- המפתח מוגן ב-DPAPI (machine)
settings(key PK, value) · schema_version(version, applied_at)
```

Retention של `events`: 90 יום כברירת מחדל.

---

## 10. פרוטוקולים

### 10.1 Agent (תוסף ↔ Host ↔ שירות)

מסגרת זהה ל-Native Messaging: אורך 4 בתים + JSON UTF-8. לכל הודעה `v` (גרסת פרוטוקול) ו-`id`.

| הודעה | כיוון | תוכן |
|-------|-------|------|
| `hello` | → | `{extVersion, browser}` ← `{user:{accountId,name,role}, mode, rulesVersion, serverVersion}` |
| `getRules` | → | ← `{version, dnrRules[], youtube:{mode, allowed[], blocked[]}, alwaysAllowed[], time:{state, remaining}}` |
| `heartbeat` | → | `{focused, idle, hasWebTab}` ← `{state, remainingMinutes}` |
| `reportBlock` | → | `{url, reason, ruleId}` |
| `requestAccess` | → | `{url, note}` |
| `resolveYouTube` | → | `{handle\|videoId}` ← `{channelId}` |
| `rulesChanged` | ← push | `{version}` |
| `timeState` | ← push | `{state: ok\|warn\|expired, remainingMinutes}` |

הודעה מה-host לתוסף עד 1MB — רשימות גדולות נשלחות בחלקים.

### 10.2 Admin (ממשק ↔ שירות)

אותה מסגרת. CRUD לכל ישות + `getDashboard`, `extendTime`, `pause`, `startHomework`, `getEvents`, `decideAccessRequest`, `createPairingCode`, `getDevices`, `getDiagnostics`. כל שינוי נרשם כ-`admin_change` עם שם המנהל (מה-token).

### 10.3 Hub ↔ Agent (HTTPS, TLS הדדי)

| נושא | הגדרה |
|------|-------|
| צימוד | ה-Hub מציג קוד חד-פעמי (12 תווים, ≈60 ביט, תקף 10 דקות). הסוכן יוצר זוג מפתחות, שולח CSR + HMAC(קוד, CSR + טביעת תעודת Hub); ה-Hub מאמת, מנפיק תעודת סוכן חתומה ע"י CA של ה-Hub, ומחזיר HMAC(קוד, תשובה). כך כל צד מוכיח שהוא מכיר את הקוד — אין Hub מזויף ואין סוכן מזויף |
| תעבורה | Kestrel ב-Hub, TLS הדדי; הסוכן מצמיד את ה-CA של ה-Hub; מכשיר שבוטל → התעודה נדחית |
| הגדרות | `GET /v1/bundle?since=<version>` + ערוץ push (SSE) ל-`bundleChanged` |
| שימוש ואירועים | `POST /v1/usage`, `POST /v1/events` — אצוות עם מזהי idempotency מה-outbox |
| מכסה משותפת | בתשובה ל-usage: `{member, localDate, totalMinutes}` |
| יכולות | הסוכן מדווח `capabilities` (למשל `urlPath`, `appNetwork`, `youtubeChannels`) — ממשק הניהול מציג מה נאכף בכל מכשיר |
| נייטרליות | JSON Schema עם גרסאות, בלי מושגים של Windows (`accountId` הוא מחרוזת) |

---

## 11. מחסנית טכנולוגית

| שכבה | בחירה | הערה |
|------|-------|------|
| Runtime | **.NET 10 (LTS, נתמך עד נובמבר 2028)** | ‏.NET 8 ו-9 יוצאים מתמיכה ב-10.11.2026. SDK 10 כבר מותקן |
| שירות | Worker Service + `Microsoft.Extensions.Hosting.WindowsServices`; Kestrel ל-Hub | |
| DB | Microsoft.Data.Sqlite + Dapper, migrations כקבצי SQL מוטמעים | |
| WFP | C++20, `fwpuclnt.lib`, API שטוח עם `extern "C"` | |
| לוגים | Serilog (File + EventLog) | |
| ממשק ניהול | WPF + CommunityToolkit.Mvvm | |
| Native Host | .NET 10 NativeAOT + System.Text.Json source generators | |
| תוסף | TypeScript, MV3, esbuild, Vitest | |
| סימולציות | xUnit (רמות 1–2), Playwright (רמה 3), PowerShell + Hyper-V + FlaUI (רמה 4), NBomber/BenchmarkDotNet (רמה 5) | ראו [SIMULATION-PLAN.md](SIMULATION-PLAN.md) |
| מתקין | WiX Toolset (גרסה יציבה עדכנית, נבחרת ב-M0) | |
| CI (אופציונלי) | GitHub Actions `windows-latest` | build + רמות 1–3 + MSI |

### מבנה תיקיות

```
/src
  WebFilter.Core/            מודלים, נרמול, התאמה, זמן, YouTube, מהדרי חבילה ו-DNR
  WebFilter.Contracts/       DTO של כל הפרוטוקולים (+ JSON Schema של Hub API)
  WebFilter.Service/         Agent + Hub
  WebFilter.Wfp.Native/      C++
  WebFilter.NativeHost/
  WebFilter.Admin/
/extension                   src/, rulesets/, pages/ (blocked, timeup, my-channels)
/installer                   WiX
/tests
  WebFilter.Core.Tests/
  WebFilter.Service.Tests/
  simulation/                תרחישים, סכמה, מריצים, מעבדת VM (ראו SIMULATION-PLAN.md)
/data/starter-lists          educational.json, social.json, gaming.json
/docs
WebFilter.sln
CLAUDE.md
```

---

## 12. אבני דרך

כל אבן דרך מסתיימת **רק** כשתנאי הסיום מתקיימים במצב אמיתי, **כולל חבילות הסימולציה שלה**.

### M0 — ניקוי וספייקים (מאמתים את הארכיטקטורה לפני שבונים)

- ✅ תיוג הקוד הקודם `legacy-v1`, ענף `v2`, שלד ה-solution, `CLAUDE.md`, תיקון בעלות ה-repo (`safe.directory`)
- **S1 — התקנה כפויה ומדיניות:** תוסף unlisted בשתי החנויות; ב-VM לא בדומיין (Home + Pro): forcelist מ-HKLM עובד, המדיניות מסעיף 5.2 נאכפת מ-HKCU
- **S2 — Native Messaging וזהות:** הדפדפן מפעיל את ה-host כמשתמש; השירות מקבל את ה-SID הנכון; host מזויף ב-HKCU נחסם
- **S3 — DNR:** redirect ל-`blocked.html` עם ה-URL המקורי; 100,000 דומיינים ב-`requestDomains`; service worker נשאר חי
- **S4 — YouTube:** זיהוי ערוץ בסרטון, Shorts, handle, SPA והטמעה
- **S5 — WFP:** רכיב C++ מוסיף מסנן persistent לפי משתמש + אפליקציה; אפליקציה של ילד נחסמת, מבוגר לא מושפע, שירותי Windows לא נפגעים, שורד אתחול, הסרה נקייה; אירוע על חיבור שנחסם
- **S6 — Hub:** צימוד + TLS הדדי בין שני VMs; גילוי mDNS; חומת אש

**תנאי סיום:** כל הספייקים עברו, או שיש חלופה מתועדת. **נקודת go/no-go.**

### M1 — ליבה + מריץ סימולציות רמה 1

`Core` + `Contracts` + סכמות. מריץ רמה 1 שטוען את קבצי התרחישים. **תנאי סיום:** כל תרחישי `engine` עוברים, benchmark עומד ביעד.

### M2 — סוכן (מחשב יחיד)

שירות בתפקיד סוכן עם Hub מקומי פשוט, pipes, זיהוי, DecisionService, UsageTracker, אירועים, ProfileCache. **תנאי סיום:** מותקן כ-LocalSystem; תרחישי `integration` עוברים; admin pipe דוחה תהליך לא מוגבה.

### M3 — תוסף + Native Host

במקביל ל-M2 מול שירות מדומה. **תנאי סיום:** תרחישי `browser` (Playwright) עוברים, כולל fail-closed ו-YouTube.

### M4 — אכיפה והגנה

PolicyManager, WfpManager, ProcessGuard, TamperMonitor. **תנאי סיום:** תרחישי `system` של עקיפה ואפליקציות עוברים על VM, עם התוסף המותקן מהחנות.

### M5 — ממשק ניהול

אחרי M2, במקביל ל-M3/M4. **תנאי סיום:** הורה מגדיר משפחה מאפס ורואה את הגלישה של הילד בלוח הבקרה; שינוי נאכף תוך פחות מ-5 שניות.

### M6 — ריבוי מחשבים

HubServer, צימוד, HubSync, מכסה משותפת, מסך מכשירים, השהיית חירום מקומית. **תנאי סיום:** תרחישי `multi-device` עוברים במעבדה של 3 VMs (Hub + 2 סוכנים), כולל Hub כבוי וחזרה.

### M7 — מתקין ותיעוד

MSI עם בחירת תפקיד, cleanup בהסרה, מדריכים בעברית. **תנאי סיום:** VM נקי → התקנה + צימוד < 10 דקות; הסרה מחזירה דפדפנים ורשת למצב רגיל.

### M8 — קבלה

כל חבילות הסימולציה ברמות 1–5 על Home ועל Pro, אתחולים, עדכון דפדפן, ריצת soak של 24 שעות. **תנאי סיום:** כל קריטריוני סעיף 2.4.

```
M0 ──► M1 ──► M2 ──► M4 ──► M6 ──► M7 ──► M8
          └─► M3 ──┘   ▲     ▲
               M5 ─────┴─────┘   (M5 מתחיל אחרי M2)
```

**גודל יחסי:** M0 בינוני (חסום ע"י פרסום בחנויות) · M1 בינוני · M2 בינוני · M3 גדול · M4 גדול · M5 גדול · M6 בינוני-גדול · M7 קטן-בינוני · M8 קטן

---

## 13. הכנה ל-Android (בלי לבנות אותו)

1. פרוטוקול Hub נייטרלי (סעיף 10.3) — סוכן Android יתחבר בלי שינוי ב-Hub
2. המדיניות מופשטת; כל סוכן מתרגם אותה למנגנון שלו (DNR ב-Windows, VPN/DNS ב-Android)
3. דיווח יכולות לכל מכשיר — ממשק הניהול מציג בכנות מה נאכף
4. סוכנים "טיפשים" — ה-Hub מכין חבילה פשוטה, מעט לוגיקה לשכפל ב-Kotlin
5. **תרחישי הסימולציה ברמה 1 הם קבצי JSON משותפים** — מימוש Android חייב לעבור את אותם קבצים (תרחישים שמסומנים `platforms: ["windows","android"]`)
6. ערוץ הסנכרון ניתן להחלפה — ממסר בענן יתווסף כשיגיע Android

---

## 14. סיכונים

| סיכון | סבירות | מענה |
|-------|:---:|------|
| מדיניות מ-HKCU לא נאכפת במחשב לא מנוהל | בינונית | S1; חלופה: HKLM לכולם |
| ביקורת החנויות מעכבת עדכונים לתוסף | גבוהה | הלוגיקה בשירות; התוסף יציב; פרוטוקול עם גרסאות (N ו-N-1) |
| YouTube משנה את מבנה הדף | בינונית | כמה מקורות לזיהוי; fail-closed; תרחיש live שבועי מול YouTube האמיתי |
| WFP חוסם אפליקציות לגיטימיות (Store, משגרי משחקים עם תהליכי עזר) | גבוהה | אירוע + אישור בלחיצה מלוח הבקרה; התאמה לפי package/publisher ולא רק נתיב; S5 |
| Hub כבוי כשההורה רוצה לתת עוד זמן | בינונית | השהיית חירום מקומית בכל מחשב (דורשת מנהל); בעתיד Hub במכשיר שדולק תמיד |
| חריגה ממכסה משותפת בזמן ניתוק מה-Hub | נמוכה | מתועד; יישור מיד כשהחיבור חוזר |
| חומת אש / mDNS חסומים ברשת הביתית | בינונית | הזנת כתובת ידנית; בדיקת קישוריות באשף |
| דפדפנים מוטמעים (WebView2, Electron) | גבוהה | WFP חוסם אפליקציות לא מאושרות — כולל אלה |
| ילד עם חשבון מנהל | — | תנאי מקדים; האשף מזהיר וחוסם את סיום ההגדרה |
| קבצים לא חתומים → אזהרת SmartScreen | ודאית בלי תעודה | לקנות תעודה עד M7, או לשחרר MVP לא חתום |
| `SimulationHooks` דולפים לגרסת שחרור | נמוכה | `#if SIMULATION` + בדיקת CI שמוודאת שהסמלים לא קיימים ב-Release |

---

## 15. החלטות (אושרו 2026-10-18)

| # | החלטה | הוחלט |
|---|-------|-------|
| 1 | קהל יעד | מחשבים ביתיים, לא בדומיין |
| 2 | "זמן" | זמן גלישה ב-MVP; זמן מסך בשלב 2 |
| 3 | שפת הממשק | עברית RTL, עם תשתית לאנגלית |
| 4 | שם עבודה | `WebFilter` |
| 5 | חשבונות מפתח (Chrome Web Store, Microsoft Partner Center) | נדרשים ב-S1 — **פעולה שלך** |
| 6 | תעודת חתימת קוד | נדחה עד M7 |
| 7 | הקוד הישן | תגית `legacy-v1` + ענף `v2` נקי באותו repo |
| 8 | Firefox לילדים | חסום ב-MVP |
| 9 | ארכיטקטורה | משולבת: אכיפה מקומית בכל מחשב + Hub לניהול + DNS משפחתי בראוטר כהמלצה |
| 10 | מיקום ה-Hub | מחשב ההורים; בנוי כך שאפשר להעביר למכשיר שדולק תמיד |
| 11 | ממשק ניהול | WPF על מחשב ה-Hub; web/טלפון בשלב 2 |
| 12 | שכבת WFP | בתוך ה-MVP, בכפוף להצלחת S5 |
| 13 | ריבוי מחשבים | בתוך ה-MVP (M6) |
| 14 | סימולציות | חמש רמות, תרחישים כקבצי JSON משותפים, חובה בכל תנאי סיום |
