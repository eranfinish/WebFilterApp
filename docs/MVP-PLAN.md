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
2. מבוגר לא מרגיש שום הבדל בגלישה ובאפליקציות, מלבד ההגנה הבסיסית שהמשפחה בחרה ([PROTECTION-SPEC §13](PROTECTION-SPEC.md): אתרים למבוגרים, קפיצות לאתרים אחרים, פרסומות, SafeSearch — אפשר לכבות בהגדרות).
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
| `AgentPipeServer` | שניהם | pipe לתוסף. אימות: `GetNamedPipeClientProcessId` → handle לתהליך מיד בחיבור → נתיב התהליך חייב להיות ה-NativeHost שלנו ב-Program Files; זהות = SID **מטוקן התהליך**. בלי `RunAsClient` — ה-impersonation דלף לטיימרים וקרס ב-S2 ([דוח](../spikes/S2/REPORT.md)) |
| `AdminPipeServer` | שניהם | pipe לממשק הניהול. DACL מאפשר רק `BUILTIN\Administrators` — token לא מוגבה נדחה. בסוכן: רק סטטוס והשהיית חירום מקומית |
| `DecisionService` | שניהם | מחזיק את חבילת המדיניות בזיכרון, מחזיר החלטות, מהדר DNR לכל משתמש, דוחף שינויים לתוספים מחוברים |
| `UsageTracker` | שניהם | צובר דקות לפי heartbeats; מניעת ספירה כפולה; שולח ל-Hub; מחשב מכסה משותפת לפי הסכום האחרון מה-Hub + שימוש מקומי |
| `WfpManager` | שניהם | מסנני WFP קבועים (persistent) תחת provider ו-sublayer משלנו (סעיף 5.3); מתעדכן לפי חבילה ומצב זמן; מדווח על חיבורים שנחסמו |
| `PolicyManager` | שניהם | כותב מדיניות דפדפן (סעיף 5), עוקב אחרי שינויים (`RegNotifyChangeKeyValue`) ומשחזר; כותב HKCU של ילד בעת התחברות (`OnSessionChange`) |
| `ProcessGuard` | שניהם | מאזין להפעלת תהליכים (ETW/WMI). בסשן של ילד: דפדפן שאינו Edge/Chrome חתום (Authenticode + `OriginalFilename`) — נהרג ונרשם |
| `TamperMonitor` | שניהם | **שומר התוסף** ([PROTECTION-SPEC §14](PROTECTION-SPEC.md)): Chrome או Edge שרץ בחשבון מסונן בלי חיבור חי מהתוסף (הודעה ב-90 השניות האחרונות, מהמזהה של החנות) — אחרי חלון הורדה של 3 דקות מהפעלת הדפדפן הרשת שלו נחסמת בחשבון הזה עד שהתוסף מתחבר, וההורים מקבלים התראה (`tamper`, `extension_missing:chrome`); קפיצות שעון; מסנני WFP או מדיניות שנמחקו |
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
- אם השירות לא זמין: מנהל (לפי `TokenElevationType` = `limited`/`full` — `IsInRole` מחזיר false להורה שגולש לא-מוגבה) → "ללא הגבלה"; משתמש מוגבל → רק אתרי "מותר תמיד" מה-`ProfileCache`, וכל השאר **חסום** עם הודעה "שירות הסינון לא פועל"; אין קובץ → חסימה מלאה ההגנה הבסיסית להורים ולמבוגרים (PROTECTION-SPEC §13) מגיעה רק מהשירות, ולכן כשהוא לא זמין מנהל ומבוגר גולשים כמו קודם — בלי אותה ובלי שום חסימה נוספת.
- NativeAOT כדי שיעלה מהר (עשרות ms)
- רישום: `HKLM\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.webfilter.host` ו-`HKLM\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.webfilter.host`; ה-manifest מתיר רק את מזהי התוסף שלנו (מזהה שונה ב-Chrome Web Store וב-Edge Add-ons)

### 4.4 התוסף (Manifest V3, TypeScript)

| מנגנון | שימוש |
|--------|-------|
| Static ruleset `failclosed` (פעיל כברירת מחדל) | חוסם כל `main_frame`/`sub_frame` עד שהשירות מאשר את פרופיל המשתמש. למבוגר — מכובה מיד אחרי ה-hello. **רק בהתקנה כפויה** (`chrome.management.getSelf().installType === "admin"`); בהתקנה רגילה (למשל אצל בודקי החנות) מכובה בעלייה, כדי שהתוסף לא יחסום דפדפן שאין בו את WebFilter |
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
| הגדרות ואבחון | אזור זמן, מצב השירות בכל מחשב, מדיניות, WFP, heartbeat של התוסף, המלצה ל-DNS משפחתי בראוטר, **הגנה בסיסית להורים ולמבוגרים** (PROTECTION-SPEC §13) |

### 4.6 מתקין (WiX, MSI x64 per-machine)

- בחירת תפקיד: **Hub** או **סוכן**; לסוכן — כתובת Hub (או גילוי אוטומטי) + קוד צימוד
- קבצים ל-`C:\Program Files\WebFilter\`, נתונים ב-`C:\ProgramData\WebFilter\`
- `ServiceInstall` + `ServiceControl`; רישום Native Host ע"י MSI (את מדיניות הדפדפן ב-HKLM ו-HKCU כותב השירות)
- ב-Hub: כלל חומת אש נכנס לפורט ה-Hub (Private בלבד)
- Custom action בהסרה: `WebFilter.Service.exe cleanup` — מוחק מדיניות HKCU מכל הפרופילים, את מסנני ה-WFP שלנו, מבטל רישום מה-Hub, ומוחק את תיקיית הנתונים `%ProgramData%\WebFilter` (כך מדיניות הפרטיות מבטיחה)
  - ביטול הרישום (סוכן מצומד בלבד, לפני מחיקת התיקייה שבה המפתח): `POST /v1/unregister` בחיבור ה-TLS ההדדי הקיים, עד 5 שניות. ה-Hub **מבטל את הצימוד ולא מוחק** — המחשב נשאר ברשימת המחשבים שהצימוד שלהם בוטל, במצב "הסינון הוסר", ונרשם אירוע ביומן. כך ההורה רואה שהסינון הוסר ממחשב (גם אם לא הוא הסיר אותו), והיסטוריית השימוש והאירועים של המחשב נשמרת; צימוד מחדש של אותו מחשב אחרי התקנה מחדש מחזיר אותו לפעיל, עם קישורי החשבונות שלו. Hub כבוי או שלא עונה בזמן — ההסרה ממשיכה, נרשמת שורה ב-log, והמחשב נשאר "פעיל" עד שהורה מבטל אותו במסך המכשירים
- `MajorUpgrade` לשדרוגים

---

## 5. מדיניות דפדפן ו-WFP

**חשוב:** במחשב שאינו בדומיין, Chrome ו-Edge מתקינים בכפייה **רק תוסף שמפורסם בחנות שלהם** (Chrome Web Store / Microsoft Edge Add-ons). התוסף יתפרסם בשתי החנויות כ-unlisted. חלופה ל-Chrome: Chrome Enterprise Core.

### 5.1 HKLM — לכל המשתמשים (התוסף קיים אצל כולם; למבוגרים — רק ההגנה הבסיסית, PROTECTION-SPEC §13)

| מדיניות (Chrome ו-Edge) | ערך | מטרה |
|------------------------|-----|------|
| `ExtensionInstallForcelist` | `<id>;<update-url של החנות>` | התקנה כפויה; המשתמש לא יכול להסיר או לכבות |
| `NativeMessagingUserLevelHosts` | `0` | מונע רישום host מזויף ב-HKCU |

### 5.2 HKCU של משתמש מוגבל בלבד (`HKEY_USERS\<SID>\Software\Policies\…`)

| מדיניות | ערך | מטרה |
|---------|-----|------|
| `IncognitoModeAvailability` (Chrome) / `InPrivateModeAvailability` (Edge) | `1` | אין גלישה בסתר |
| `BrowserGuestModeEnabled` | `0` | אין מצב אורח |
| `BrowserAddPersonEnabled` (Chrome) / `BrowserAddProfileEnabled` (Edge) | `0` | אין פרופילים חדשים |
| `ExtensionDeveloperModeSettings` | `1` | אין מצב מפתח |
| `DeveloperToolsAvailability` | `2` | אין כלי מפתח |
| `ExtensionInstallBlocklist` | `*` | אין התקנת תוספים אחרים |
| `ForceGoogleSafeSearch` (+ `ForceBingSafeSearch` ב-Edge) | `1` | SafeSearch |
| `ForceYouTubeRestrict` | `1`/`2` לפי פרופיל | YouTube Restricted Mode |

> **חשבונות מנהלים ובני משפחה ללא הגבלה** לא מקבלים את הטבלה הזו. מההגנה הבסיסית להורים ולמבוגרים ([PROTECTION-SPEC §13](PROTECTION-SPEC.md)) הם מקבלים, כשהמתג פועל, רק `ForceGoogleSafeSearch = 1` (Chrome ו-Edge) ו-`ForceBingSafeSearch = 1` (Edge, מתון) — בלי שום מדיניות אחרת, ותיקון שלהן לא נרשם כניסיון עקיפה.

> אם ספייק S1 יראה שחלק מהמדיניות לא נאכפת מ-HKCU במחשב לא מנוהל — חוזרים ל-HKLM לאותן מדיניות (המחיר: גם המבוגרים יאבדו גלישה בסתר והתקנת תוספים).

### 5.3 WFP — הרשאת רשת לפי משתמש ואפליקציה

- **רכיב:** `WebFilter.Wfp.Native.dll` (C++), API שטוח: `AddUserAppPolicy(sid, allowedAppPaths[], mode)`, `RemoveAll()`, `SubscribeBlockedEvents(callback)`
- **מסננים:** בשכבות `ALE_AUTH_CONNECT_V4/V6` (ו-`ALE_AUTH_RECV_ACCEPT` לפי הצורך), תנאים `ALE_USER_ID` (המשתמש המוגבל) + `ALE_APP_ID` (נתיב האפליקציה) / `ALE_PACKAGE_ID` (אפליקציות Store)
- **מדיניות למשתמש מוגבל:**
  - מצב רגיל: דפדפנים נתמכים + אפליקציות מאושרות → מותר; כל השאר → חסום
  - דפדפנים נתמכים = Chrome ו-Edge שמותקנים לכל המחשב (Program Files; השירות מאתר אותם בכל מחשב), ועוד מה שב-`supportedBrowsers` של החבילה. כך משפחה חדשה, שהרשימה שלה ריקה, גולשת מהרגע הראשון. לעולם לא Chrome שמותקן למשתמש (`%LOCALAPPDATA%`), כי הילד יכול לכתוב שם
  - הזמן נגמר / מחוץ לשעות: רק דפדפנים (התוסף מציג "הזמן נגמר" ומתיר "מותר תמיד") + אפליקציות "מותר תמיד" (למשל אפליקציית בית הספר); כל השאר → חסום
  - דפדפן שהתוסף שלו לא מחובר (לא הותקן, גרסה ישנה, כובה) — אחרי חלון הורדה של 3 דקות מהפעלתו יוצא מהרשימה של החשבון הזה, עד שהתוסף מתחבר; הפעלה מחדש של הדפדפן פותחת חלון חדש (שומר התוסף, [PROTECTION-SPEC §14](PROTECTION-SPEC.md))
- **מבוגרים ו-SYSTEM לא מושפעים** — שירותי Windows (עדכונים, DNS) רצים כ-SYSTEM
- **persistent** — המסננים שורדים קריסת שירות ואתחול; השירות מיישב אותם מול החבילה בכל הפעלה
- העתקת `chrome.exe` לנתיב אחר = נתיב לא מאושר = חסום
- חיבור שנחסם → אירוע "אפליקציה ניסתה להתחבר" → ההורה יכול לאשר מלוח הבקרה

---

## 6. סמנטיקה של החלטת סינון

סדר הבדיקה ל-URL ראשי (`main_frame`/`sub_frame`):

1. משתמש **ללא הגבלה** (בן משפחה מבוגר, או חשבון מנהל) → **מותר** וזמן לא נספר, חוץ מ**ההגנה הבסיסית להורים** ([PROTECTION-SPEC §13](PROTECTION-SPEC.md)): אתר מקטגוריית `adult` → **חסום (`blocked_baseline_adult`)**, אלא אם כלל `allow` של ההורים מתאים לו; השהיה לא חלה עליהם. ההגבלות של בן משפחה מסונן מושהות לו כרגע → **מותר** (וזמן לא נספר)
2. סכמה שאינה http/https → לפי מדיניות הדפדפן, לא דרכנו
   - **חסימות מובנות** של בן המשפחה — חיפוש תמונות (`blockImageSearch`, [PROTECTION-SPEC §4](PROTECTION-SPEC.md)) → **חסום (`blocked_image_search`)**; AI Mode של Google (`udm=50`) כשהקטגוריות שלו חוסמות `ai-chat` (§11) → **חסום (`blocked_rule`, `builtin-ai-mode-google`)** — לפני שיעורי בית וזמן וגם מול "מותר תמיד". ב-DNR הן בעדיפות 327,680, מעל כללי "הזמן נגמר"
   - `accounts.youtube.com` (הכניסה לחשבון Google עוברת דרכו) — מצב ה-YouTube לא חוסם אותו לעולם, גם `off` ובשיעורי בית (§11)
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

**קודי סיבה** (משותפים לכל הרכיבים ולסימולציות): `allowed_unrestricted`, `allowed_paused`, `allowed_always`, `allowed_rule`, `allowed_category`, `allowed_default`, `allowed_homework`, `allowed_youtube_channel`, `allowed_browser`, `blocked_rule`, `blocked_category`, `blocked_not_in_allowlist`, `blocked_time_window`, `blocked_daily_limit`, `blocked_homework`, `blocked_ip_literal`, `blocked_youtube_channel`, `blocked_youtube_unknown`, `blocked_youtube_off`, `blocked_service_unavailable`, `blocked_app_network`, `redirect_youtube_home`, `blocked_image_search`, `blocked_jump`, `blocked_youtube_shorts` (שני אלה — החלטה של התוסף בלבד; השירות שומר את הסיבה שהתוסף דיווח), `blocked_baseline_adult` (אתר למבוגרים בהגנה הבסיסית להורים, PROTECTION-SPEC §13), `redirect_youtube_channel_search` (חיפוש ב-YouTube שלא מסונן לערוצים, אצל ילד שמותר לו לחפש ערוצים — מופנה לאותו חיפוש מסונן לערוצים, PROTECTION-SPEC §15).

**חשבון לא מקושר:** חשבון Windows לא-מנהל שעדיין לא קושר לבן משפחה מקבל רק "מותר תמיד", עד שההורה מקשר אותו. ההורה יכול גם לענות "להשאיר חסום (אורח)" (`guest` בחשבון): הסינון לא משתנה, רק ממשק הניהול מפסיק לשאול עליו.

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
- content script ב-`document_start` וב-`all_frames`, מסתיר את הדף עד שמתקבלת החלטה. סקריפט נוסף ב-`world: "MAIN"` קורא את הנתונים של YouTube
- מזהה ערוץ (אומת ב-S4, ראו [הדוח](../spikes/S3-S4/REPORT.md)) — מקור נחשב רק אם הוא קשור לכתובת הנוכחית:
  - `/channel/UC…` — מהכתובת
  - סרטון / Shorts — `#movie_player` / `#shorts-player` `.getPlayerResponse().videoDetails`, **רק** כש-`videoId` תואם לכתובת
  - דף ערוץ לפי handle — `ytd-browse:not([hidden]).data` (או `ytInitialData` בטעינה מלאה), **רק** כשה-handle תואם
  - הטמעה — `ytcfg.get("PLAYER_VARS").embedded_player_response` (גיבוי: לינק הערוץ בנגן)
  - **לא להשתמש** ב-`meta`, `og:url`, `canonical` או `ytInitialData` אחרי ניווט SPA — הם נשארים של הדף הקודם
- ניווט בתוך האתר (SPA): `yt-navigate-finish` **וגם** מעקב אחרי שינוי כתובת
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

### 8.1 בקשות גישה

הילד מבקש, ההורה מחליט מלוח הבקרה. ההחלטה מגיעה לכל המחשבים של הילד תוך פחות מ-5 שניות, ומסך החסימה נטען מחדש לבד.

| סוג | מאיפה מבקשים | מה קורה באישור |
|-----|--------------|----------------|
| `url` | מסך החסימה של אתר | כלל היתר אישי: **לאתר** (`domain` על המארח בלי `www.`, ברירת מחדל) או **לדף** (`prefix` על מארח + נתיב) |
| `youtube` | מסך החסימה של ערוץ | הערוץ נוסף כמאושר לבן המשפחה |
| `app` | התראה על אפליקציה שנחסמה | כלל אפליקציה מאושרת (לפי נתיב / package) |
| `time` | מסך "הזמן נגמר" — 15 או 30 דקות | הארכת זמן (ההורה יכול לאשר פחות ממה שהתבקש) |

- **משך האישור** (`url`, `youtube`, `app`): מספר דקות, **עד סוף היום**, או **תמיד**. אישור מוגבל יוצר כלל עם `expiresAt`
- **דחייה:** הילד רואה "הבקשה נדחתה" במסך החסימה. אותו מארח/ערוץ/אפליקציה לא ניתן לבקש שוב במשך 60 דקות (`cooldown`)
- **מניעת הצפה:** בקשה פתוחה אחת לכל יעד — בקשה חוזרת מתמזגת (`merged`); לכל היותר 5 בקשות חדשות ב-60 דקות מתגלגלות לכל בן משפחה (`rate_limited`)
- **Hub לא זמין:** הבקשה נשמרת בתור של הסוכן (`queued`) ונשלחת כשהחיבור חוזר. ב-Hub היא שומרת את המזהה שהסוכן נתן לה (`<מחשב>-q<n>`), כך שההחלטה חוזרת עם המזהה שמסך החסימה מכיר; בקשה נוספת לאותו יעד בזמן שהיא בתור מקבלת את אותו מזהה (`merged`)
- **ההחלטה בכל המחשבים:** במחשב ה-Hub השירות מודיע לדפדפנים (`accessDecided`) ברגע שההורה מחליט. סוכן לומד על החלטות מה-snapshot: ה-snapshot שה-Hub שולח לסוכנים כולל את הבקשות שהוחלטו ב-48 השעות האחרונות (`decisions`, עד 100, החדשות קודם), והסוכן מודיע פעם אחת על כל החלטה חדשה של בן משפחה שיש לו חשבון במחשב הזה — גם על בקשה שנשלחה ממחשב אחר — אחרי `rulesChanged`, כך שהתוסף מתקין את הכלל החדש לפני שמסך החסימה חוזר לאתר. מה שכבר נראה נשמר במסד של הסוכן (`decisions_seen`), ולכן הפעלה מחדש לא מודיעה שוב; סוכן שהיה מנותק יותר מ-48 שעות לא מודיע על החלטות ישנות יותר (הכללים עצמם מגיעים ממילא). Hub ישן בלי `decisions` — הסוכן לא מודיע (כמו לפני כן). מגבלה: בקשה שה-Hub קיבל אבל התשובה שלו לא הגיעה לסוכן נשמרת גם בתור ומגיעה ל-Hub פעמיים; החלטה על העותק הראשון לא מחזירה את מסך החסימה, שמחכה לעותק מהתור. (נמצא במעבדה ב-2026-10-24: עד אז ההחלטה הגיעה לסוכן רק ככלל, בלי `accessDecided`.)
- **שירות לא זמין:** אין כפתור בקשה (`unavailable`)
- **התראה להורה:** ב-MVP — בלוח הבקרה, והתראת Windows במחשב ה-Hub כשממשק הניהול פתוח. התראה לטלפון — שלב 2
- כל בקשה נרשמת כאירוע `access_request`, וכל החלטה כ-`admin_change`

---

## 9. מודל נתונים

מזהים: ULID (ממוינים לפי זמן, ייחודיים בין מכשירים). כל טבלה שמסתנכרנת כוללת `updated_at` ו-`version`.

> **מימוש (M2):** ההגדרות נשמרות כמסמך `FamilyBundle` אחד עם גרסה (אותו פורמט שעובר בין Hub לסוכנים); שימוש, אירועים ובקשות — בטבלאות. הסכמה בפועל: `src/WebFilter.Service/Storage/Migrations/`. הטבלאות למטה הן המודל הלוגי.

### 9.1 Hub (מקור האמת להגדרות)

```sql
family_members(id PK, display_name, role CHECK(role IN('child','teen','adult')), age, avatar NULL,
               filter_mode CHECK(filter_mode IN('unrestricted','blocklist','allowlist')),
               youtube_mode CHECK(youtube_mode IN('off','restricted','allowlist','unrestricted')),
               paused_until NULL, homework_until NULL, created_at, updated_at, version)

devices(id PK, name, os CHECK(os IN('windows','android')), role CHECK(role IN('hub','agent')),
        client_cert_thumbprint, capabilities_json, agent_version, last_seen_at, paired_at, revoked_at NULL,
        revoked_reason NULL)   -- NULL: הורה ביטל; 'uninstall': המכשיר ביטל את עצמו בהסרה

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
access_requests(id PK, member_id, device_id, kind CHECK(IN('url','youtube','app','time')),
                target, target_key,   -- target_key: מארח בלי www. / channelId / נתיב אפליקציה; לאיחוד ול-cooldown
                minutes NULL, note, ts,
                status CHECK(IN('pending','approved','denied')), decided_by, decided_at,
                approved_scope NULL, approved_until NULL, created_rule_id NULL)
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
decisions_seen(request_id PK, decided_at)   -- ההחלטות שה-snapshot האחרון הביא (48 שעות): על כל אחת מודיעים פעם אחת (§8.1)
hub_identity(hub_id, hub_ca_cert, client_cert, client_key_protected)   -- המפתח מוגן ב-DPAPI (machine)
settings(key PK, value) · schema_version(version, applied_at)
```

Retention של `events`: 90 יום כברירת מחדל — מוחל ב-Hub בהפעלה ופעם ביום, בזיכרון ובמסד (גם מונחי חיפוש, שהם אירועי `search`). התראות להורים (`notifications`, [PROTECTION-SPEC §10](PROTECTION-SPEC.md)): 30 יום.

---

## 10. פרוטוקולים

### 10.1 Agent (תוסף ↔ Host ↔ שירות)

מסגרת זהה ל-Native Messaging: אורך 4 בתים + JSON UTF-8. לכל הודעה `v` (גרסת פרוטוקול) ו-`id`.

| הודעה | כיוון | תוכן |
|-------|-------|------|
| `hello` | → | `{extVersion, browser}` ← `{user:{accountId,name,role}, mode, rulesVersion, serverVersion}` |
| `getRules` | → | ← `{version, dnrRules[], youtube:{mode, allowed[], blocked[]}, alwaysAllowed[], time:{state, remaining}, features?:{jumpGuard, blockYouTubeShorts, logSearches, canOverrideJump, blockAds}, mode?}` |
| `heartbeat` | → | `{focused, idle, hasWebTab}` ← `{state, remainingMinutes}` |
| `reportBlock` | → | `{url, reason, ruleId}` |
| `reportSearch` | → | `{engine, query, url}` ← `error {code: ok}` — נרשם (`search`) רק כש-`logSearches` |
| `reportOverride` | → | `{url, reason: blocked_jump}` ← `error {code: ok}` — נרשם (`override`) |
| `requestAccess` | → | `{kind, target, minutes?, note}` ← `{result: created\|merged\|rate_limited\|cooldown\|queued, requestId?}` |
| `accessDecided` | ← push | `{requestId, status, until?}` — מסך החסימה נטען מחדש באישור. לכל חיבור של בן המשפחה, בכל מחשב שיש לו בו חשבון (בסוכן — מה-snapshot, §8.1) |
| `resolveYouTube` | → | `{handle\|videoId}` ← `{channelId}` |
| `rulesChanged` | ← push | `{version}` |
| `timeState` | ← push | `{state: ok\|warn\|expired, remainingMinutes}` |

הודעה מה-host לתוסף מעל 1MB מפוצלת ע"י ה-host להודעות `chunk` (חלקי base64 של ה-JSON המקורי), והתוסף מרכיב אותן. שמות סוגי התשובות בפועל: `helloResult`, `rules`, `requestAccessResult`, `resolveYouTubeResult`; ב-`hello` התוסף שולח גם את ה-`extensionId` שלו (לבניית כתובת מסך החסימה). ההגדרה המחייבת: `src/WebFilter.Contracts/Agent/`.

### 10.2 Admin (ממשק ↔ שירות)

אותה מסגרת. CRUD לכל ישות + `getDashboard`, `extendTime`, `pause`, `startHomework`, `getEvents`, `decideAccessRequest {id, decision, scope?, minutes?\|today\|always}`, `createPairingCode`, `getDevices`, `getDiagnostics`. כל שינוי נרשם כ-`admin_change` עם שם המנהל (מה-token).

- `replaceSettings {settings, ifVersion?}` — מחליף את כל ההגדרות (אשף, מסך בן משפחה, ייבוא). עם `ifVersion`: אם ההגדרות השתנו מאז שנקראו (אישור בקשה, צימוד מחשב, ספירת שימוש) התשובה `conflict` ולא משתנה דבר, והממשק קורא שוב ומחיל את השינוי שלו מחדש — כך עריכה של בן משפחה אחד לא מוחקת שינוי שנעשה בינתיים. השירות בודק את ההגדרות כולן לפני שהן נכנסות: הפניה לבן משפחה שלא קיים (חשבון, כלל, שעות, מכסה, ערוץ, אפליקציה, קטגוריה), מזהים כפולים, שעות ומכסות מחוץ ליום; כלל אתר חדש או ששונה עובר גם את `RuleValidator`. סירוב מגיע עם הסיבה.
- `scanAccounts` (Hub בלבד) — קורא את חשבונות ה-Windows המופעלים של המחשב הזה (`NetUserEnum`, קריאה בלבד) ומעדכן מיד את ההתקן שלו בהגדרות. בנוסף, כל מחשב מדווח על החשבונות שלו בעצמו כשהם משתנים (סוכן בהעלאה ל-Hub, ה-Hub ישירות): חשבון חדש נוסף לא מקושר; לחשבון מוכר מתעדכנים השם והאם הוא מנהל, והקישור ותשובת "אורח" של ההורים נשמרים (תרחיש MD-008).
- `getStatus` מחזיר גם את אזור הזמן של המשפחה, ובמחשב סוכן את ה-Hub שמנהל אותו (שם, כתובת, האם הסנכרון האחרון הצליח ומתי) — לתצוגת "מנוהל ע"י Hub". סוכן שעוד לא קיבל הגדרות מחזיר סטטוס בלי בני משפחה.
- `getDiagnostics` — בדיקות קריאה בלבד של המחשב, ב-Hub ובסוכן: השירות רץ כשירות מערכת, יש Chrome או Edge, מדיניות הדפדפנים מנוהלת, מדיניות ההתקנה של התוסף קיימת; וב-Hub גם רשת ש-Windows מסווג Public (כך הסוכן לא מגיע ל-Hub) והפורט של ה-Hub בחומת האש. תשובה: קוד בדיקה, `ok`/`warn`/`fail` ופרט; הממשק מנסח. בדיקת `onedrive` (PROTECTION-SPEC §2): `ok` — מותקן לכל המחשב; `warn` עם `OneDriveSetup.exe /allusers` — רק התקנות למשתמש; `ok` עם `none` — לא מותקן.
- `setMember` מעדכן גם את דגלי ההגנה (`blockImageSearch`, `jumpGuard`, `blockYouTubeShorts`, `logSearches`, `blockAds`); `getEvents` מחזיר גם `search` ו-`override`.

ההגדרה המחייבת: `src/WebFilter.Contracts/Admin/AdminMessages.cs`.

**התראות להורים** (PROTECTION-SPEC §10): `\\.\pipe\WebFilter.Notify` ב-Hub, קריאה בלבד. כל משתמש מחובר יכול לפתוח אותו, והשירות עונה רק לחשבון מנהל (מוגבה או לא, לפי הטוקן). `getNotifications {since?}` ← `notificationsResult {items: [{id, kind: request|tamper|device|account, at, memberId?, deviceId?, detail?}]}` — `at` הוא מתי ה-Hub רשם את ההתראה ועולה תמיד, כך ש-`since` הוא סמן מדויק. ב-`tamper` ה-`detail` הוא סוג האירוע: `policy_repaired`, `network_repaired`, `unclean_stop`, או `extension_missing:chrome`/`extension_missing:edge` (דפדפן שנחסם כי רץ בלי התוסף, PROTECTION-SPEC §14). `account` — חשבון Windows רגיל חדש במחשב (לא בדיווח הראשון של מחשב שצומד, ולא של ה-Hub בהגדרה הראשונית), ולחיצה על ההודעה פותחת את ממשק הניהול במסך "מכשירים" (`--page devices`). ההגדרה המחייבת: `src/WebFilter.Contracts/Notify/NotifyMessages.cs`.

### 10.3 Hub ↔ Agent (HTTPS, TLS הדדי)

| נושא | הגדרה |
|------|-------|
| צימוד | ה-Hub מציג קוד חד-פעמי (12 תווים, ≈60 ביט, תקף 10 דקות). הסוכן יוצר זוג מפתחות, שולח CSR + HMAC(קוד, CSR + טביעת תעודת Hub); ה-Hub מאמת, מנפיק תעודת סוכן חתומה ע"י CA של ה-Hub, ומחזיר HMAC(קוד, תשובה). כך כל צד מוכיח שהוא מכיר את הקוד — אין Hub מזויף ואין סוכן מזויף |
| תעבורה | Kestrel ב-Hub, TLS הדדי; הסוכן מצמיד את ה-CA של ה-Hub; מכשיר שבוטל → התעודה נדחית |
| הגדרות | `GET /v1/bundle?since=<version>` + ערוץ push (SSE) ל-`bundleChanged` |
| החלטות על בקשות | ה-snapshot (`GET /v1/snapshot?since=<version>`) כולל `decisions: [{id, member, device, kind, targetKey, status, decidedAt, until?}]` — הבקשות שהוחלטו ב-48 השעות האחרונות, עד 100; `status`: `approved`\|`denied`; `until` — תוקף הכלל שהאישור יצר. חסר אצל Hub ישן (אין מה להודיע), וסוכן ישן מתעלם ממנו (§8.1) |
| שימוש ואירועים | `POST /v1/usage`, `POST /v1/events` — אצוות עם מזהי idempotency מה-outbox |
| מכסה משותפת | בתשובה ל-usage: `{member, localDate, totalMinutes}` |
| ביטול רישום | `POST /v1/unregister {reason: "uninstall"}` — המכשיר שמאחורי תעודת הלקוח (לא מזהה מגוף ההודעה) מבטל את עצמו בהסרה; ה-Hub מבטל אותו כמו ביטול של הורה ושומר את הסיבה (204; מכשיר שכבר בוטל מקבל 401) |
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
| מתקין | WiX Toolset 5.0.2 כחבילת NuGet (`WixToolset.Sdk`) | 5.0.2 היא הגרסה האחרונה ברישיון MS-RL בלבד; 6.x ו-7.x דורשות אישור של רישיון ה-Open Source Maintenance Fee — החלטה שלך |
| CI (אופציונלי) | GitHub Actions `windows-latest` | build + רמות 1–3 + MSI |

### מבנה תיקיות

```
/src
  WebFilter.Core/            מודלים, נרמול, התאמה, זמן, YouTube, מהדרי חבילה ו-DNR
  WebFilter.Contracts/       DTO של כל הפרוטוקולים (+ JSON Schema של Hub API)
  WebFilter.Service/         Agent + Hub
  WebFilter.Wfp.Native/      C++ (MSBuild של Visual Studio, לא ב-slnx)
  WebFilter.NativeHost/
  WebFilter.Admin/
/extension                   src/, rulesets/, pages/ (blocked, timeup, my-channels)
/installer                   WiX
/tests
  WebFilter.Core.Tests/
  WebFilter.Service.Tests/
  WebFilter.Wfp.LabTool/     wfpctl.exe — מפעיל את מנוע ה-WFP במעבדה בלבד
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
  > **Edge ב-Home — עבר (2026-10-23, `lab-kids`, Edge 155, חשבון `noa`):** התוסף 0.1.0 הותקן מחנות Edge לפי ה-forcelist ב-HKLM (`location=7`, סוג התקנה `admin`, הילד לא יכול לכבות אותו), כ-5 דקות אחרי שהדפדפן קיבל רשת. המדיניות של `noa` נכתבה ב-HKCU 1.6 שניות אחרי הכניסה, ו-Edge אוכף אותה: "חלון InPrivate חדש" אפור עם סמל הניהול. לכן המדיניות נשארת לכל ילד (HKCU), לא HKLM. ב-`edge://policy` כל המדיניות של המשתמש בסטטוס OK (Current user, Platform), חוץ משתיים שנכתבו בשם של Chrome — `BrowserAddPersonEnabled` (ב-Edge: `BrowserAddProfileEnabled`) ו-`AbusiveExperienceInterventionEnforce` (אין כזו ב-Edge); תוקן. `HubsSidebarEnabled=false` במצב OK, אבל כפתור Copilot ("Chat") בסרגל נשאר — לבדוק עם התוסף המלא שהוא נחסם כאתר (`ai-chat`). עוד פתוח: Pro (`lab-hub`, אין בו Chrome). באותה בדיקה נמצא באג: משפחה חדשה בלי `supportedBrowsers` חסמה את Edge ב-WFP — תוקן (ad4e66b).
  > **Chrome ב-Home — עבר (2026-10-23, `lab-kids`, Chrome 155, חשבון `noa`):** התוסף 0.1.0 הותקן מ-Chrome Web Store לפי ה-forcelist ב-HKLM (`location=7`). ב-`chrome://policy` כל המדיניות של המשתמש במצב OK (Current user, Platform), וה-forcelist ו-`NativeMessagingUserLevelHosts` במצב OK (Machine). `ProxyMode` סומן Deprecated, ולכן הוחלף ב-`ProxySettings` (`{"ProxyMode":"direct"}`) בשני הדפדפנים.
- ✅ **S2a — Native Messaging וזהות (לא-מוגבה):** הדפדפן מפעיל את ה-host כמשתמש; השרת מזהה SID ונתיב קובץ; תהליך זר נדחה; port פתוח שומר את ה-service worker חי — 12/12 + 2/2 ([דוח](../spikes/S2/REPORT.md))
- **S2b — במכונה וירטואלית:** רישום ב-HKLM, `NativeMessagingUserLevelHosts=0` חוסם host מזויף ב-HKCU, שרת כ-LocalSystem מול כמה sessions
- ✅ **S3 — DNR:** redirect ל-`blocked.html` עם ה-URL המקורי (`regexSubstitution`), רשימה שחורה/לבנה, מיליון דומיינים בכלל אחד — 27/27 על Edge 156 ([דוח](../spikes/S3-S4/REPORT.md)). "service worker נשאר חי" עבר ל-S2
- ✅ **S4 — YouTube:** זיהוי ערוץ בסרטון, Shorts, handle, SPA והטמעה, ואכיפה — 16/16 מול YouTube האמיתי ([דוח](../spikes/S3-S4/REPORT.md))
- **S5 — WFP:** רכיב C++ מוסיף מסנן persistent לפי משתמש + אפליקציה; אפליקציה של ילד נחסמת, מבוגר לא מושפע, שירותי Windows לא נפגעים, שורד אתחול, הסרה נקייה; אירוע על חיבור שנחסם
- **S6 — Hub:** צימוד + TLS הדדי בין שני VMs; גילוי mDNS; חומת אש

**תנאי סיום:** כל הספייקים עברו, או שיש חלופה מתועדת. **נקודת go/no-go.**

### M1 — ליבה + מריץ סימולציות רמה 1

`Core` + `Contracts` + סכמות. מריץ רמה 1 שטוען את קבצי התרחישים. **תנאי סיום:** כל תרחישי `engine` עוברים, benchmark עומד ביעד.

> **סטטוס (2026-10-19): הושלם.** 71/71 תרחישי `engine` עוברים; p99 של החלטה 57µs עם 10,000 כללים + 100,000 דומייני קטגוריה. מהדר ה-DNR: סדר עדיפויות קבוע לפי סוג הכלל (מזהים יציבים בין עדכונים), נבדק מול 83,697 בדיקות `testMatchOutcome` ב-Edge 156 ומול ה-evaluator של Core על 400 מדיניויות אקראיות. כללים נבדקים בכניסה (`RuleValidator`); כלל שחורג ממגבלות הדפדפן לא מפיל את כל העדכון — חסימה מתרחבת, היתר נשמט. regex של משתמש — ללא תלות ברישיות. פערים ידועים: נקודה בסוף שם המארח מול regex, ונתיבים מקודדים ב-%XX.

### M2 — סוכן (מחשב יחיד)

שירות בתפקיד סוכן עם Hub מקומי פשוט, pipes, זיהוי, DecisionService, UsageTracker, אירועים, ProfileCache. **תנאי סיום:** מותקן כ-LocalSystem; תרחישי `integration` עוברים; admin pipe דוחה תהליך לא מוגבה.

> **סטטוס (2026-10-19):** קיימים — אחסון SQLite, Agent pipe (זיהוי לפי טוקן התהליך, לקוחות מהימנים בלבד, FirstPipeInstance), Admin pipe, ProfileCache, Hub+סוכן באותו תהליך, בדיקות רמה 2 עם pipes אמיתיים. חסר — התקנה ובדיקה כ-LocalSystem (במכונה וירטואלית), מריץ רמה 2 שמריץ את קבצי התרחישים.

### M3 — תוסף + Native Host

במקביל ל-M2 מול שירות מדומה. **תנאי סיום:** תרחישי `browser` (Playwright) עוברים, כולל fail-closed ו-YouTube.

> **סטטוס (2026-10-19):** NativeHost קיים (העברה, פיצול מעל 1MB, תשובה עצמאית כשהשירות כבוי, בדיקה שהשרת רץ כ-SYSTEM) ונבדק כתהליך אמיתי. התוסף (`extension/`, MV3 ב-TypeScript, 0.3.0) קיים: fail-closed רק כשמותקן במדיניות, כללי DNR מהשירות עם נפילה חזרה ל-`isRegexSupported`, מסך חסימה שמקבל את הסיבה מהשירות (`reportBlockResult`/`decideUrl`), מסך "הזמן נגמר", בקשות גישה, זיהוי ערוצי YouTube, ממשק עברי RTL. 139 בדיקות יחידה ו-49 בדיקות ב-Edge אמיתי (Playwright) מול host מדומה. ריצה מקצה לקצה עם ה-NativeHost והשירות האמיתיים (רמה 3, `npm run e2e:real`, 26 בדיקות) עוברת ב-Edge. השירות משקף השהיה ושיעורי בית גם בדפדפן (`ExtensionPolicy`). חסר — התקנה במדיניות מהחנות, שירות מותקן כ-LocalSystem וסשן של ילד (מעבדה), Chrome. NativeAOT דורש את רכיב ה-C++ של Visual Studio.

### M4 — אכיפה והגנה

PolicyManager, WfpManager, ProcessGuard, TamperMonitor. **תנאי סיום:** תרחישי `system` של עקיפה ואפליקציות עוברים על VM, עם התוסף המותקן מהחנות.

> **סטטוס (2026-10-22):** קיימים ונבדקו מול רישום ו-WFP מדומים — מדיניות הדפדפן של סעיף 5 כנתונים (`BrowserPolicies`), `PolicyManager` שכותב, מתקן שינויים ומוחק רק ערכים שהוא כתב (כבוי בהרצות פיתוח: `ManageBrowserPolicies`), מדיניות HKCU שנכתבת ברגע שילד מתחבר — השירות מקבל מ-Windows הודעת כניסה (`OnSessionChange`) ובודק שוב 3 פעמים ב-15 השניות הבאות למקרה שה-hive נטען מאוחר, במקום לחכות עד דקה למעבר הבא (נמצא במעבדה; בדיקה במעבדה: סעיף ב' ב[רשימת הבדיקה](LAB-INSTALLER-CHECKLIST.md)), רשימת האפליקציות המותרות לרשת לכל משתמש מוגבל (`NetworkPolicy`, אותו סדר החלטה כמו `Decider.App`), `WfpManager` שמיישב ומחזיר מסננים שנמחקו, ומסווג דפדפנים ל-ProcessGuard (שם/OriginalFilename + חתימת Authenticode, נבדק על Edge אמיתי).
>
> **WFP (2026-10-22): נבנה ונבדק במעבדה.** `WebFilter.Wfp.Native.dll` (C++, `src/WebFilter.Wfp.Native`, נבנה ב-MSBuild של Visual Studio) — מסננים קבועים (persistent) תחת provider ו-sublayer משלנו בשכבות `ALE_AUTH_CONNECT_V4/V6`. לכל חשבון מוגבל: חסימה, היתר ל-loopback, והיתר לכל תוכנה מותרת (`ALE_APP_ID`) ולכל חבילת Store (`ALE_PACKAGE_ID`, לפי שם משפחת החבילה) — כולם בתנאי המשתמש (`ALE_USER_ID`), והחלפת המסננים של חשבון היא טרנזקציה אחת. ה-SID נשמר במסנן, כך שרשימת החשבונות נקראת מ-WFP גם אחרי אתחול. תנאי המשתמש מתיר גם את ALL APPLICATION PACKAGES: בלעדיו אפליקציות Store של הילד לא נחסמות כלל (נמדד במעבדה). תוכנה מותרת שעוד לא מותקנת מדולגת ונרשמת ב-log, ומקבלת מסנן ב-tick שאחרי שהיא מופיעה. `WfpService` מיישב אחרי כל שינוי הגדרות ובכל tick (חלונות זמן ומכסה), בסוכן מחכה להגדרות הראשונות מה-Hub, ושגיאה נרשמת ומנוסה שוב ב-tick הבא. פועל רק כש-`ManageNetwork` מופעל — המתקין מפעיל אותו, בהרצות פיתוח הוא כבוי. ההסרה (`cleanup`) מוחקת את ה-provider, ה-sublayer והמסננים. **במעבדה (`lab-kids`):** מנוע — 36/36 (`Test-LabWfp.ps1`): כשרק `curl.exe` מותר, `powershell.exe` נחסם ב-IPv4 וב-IPv6 (שגיאה 10013) ו-`curl.exe` מתחבר; עותק של `curl.exe` בתיקייה אחרת נחסם; loopback עובד; SYSTEM לא מושפע; תהליך AppContainer (במקום אפליקציית Store) נחסם, ומותר לפי שם החבילה; אחרי אתחול המסננים והחשבון עדיין שם; `RemoveAll` לא משאיר כלום (גם ב-`netsh wfp show state`). שירות — 13/13 (`Test-LabWfpService.ps1`): השירות האמיתי עם `ManageNetwork=true` מסנן כל חשבון שאינו מנהל ולא את המנהלים, מחזיר מסננים שנמחקו, השהיה דרך ה-admin pipe מסירה את המסננים תוך פחות מ-3 שניות, והמסננים נשארים כשהשירות נעצר. תוצאות: `tests/simulation/lab/results/`.
>
> **הגנות (2026-10-22, [PROTECTION-SPEC](PROTECTION-SPEC.md), Core ושירות):** דגלי בן משפחה (`blockImageSearch`, `jumpGuard`, `blockYouTubeShorts`, `logSearches`) בחבילה, ב-`setMember` וב-`features` של `rules`; חסימת חיפוש תמונות מובנית (`builtin-image-search-*`, `blocked_image_search`) ב-`EffectiveUrlPolicy`, זהה ב-evaluator וב-DNR (תרחישי `IMG-001`–`IMG-004`, בדיקה דיפרנציאלית); `windowsFeatures` — svchost וה-OneDrive שמותקן לכל המחשב ב-WFP גם כשהזמן נגמר, `DisableSearchBoxSuggestions`, ובדיקת `onedrive`; מדיניות §5 (חלונות קופצים, פרסומות פולשניות, `ProxyMode=direct`, Tracking Prevention ב-Edge); §11 — AI Mode של Google חסום כש-`ai-chat` חסומה (תרחישי `AIM-*`), ואז גם `HubsSidebarEnabled=0` ב-Edge (שם Copilot) ו-`GeminiSettings=1` ב-Chrome; `accounts.youtube.com` מותר בכל מצב YouTube (`YT-007`); כללי ההיתר לכניסה של משרד החינוך (`www.google.com/a/`, `www.google.com/recaptcha/`) נבדקו לילד במצב "רק מאושרים"; אירועי `search`/`override`; ותיקוני מדיניות ומסננים נרשמים כ-`tamper` (פעם בשעה לאותו חשבון). המדיניות נאכפת מ-HKCU ב-Edge (S1, למעלה).
>
> **במעבדה, 2026-10-23 (0.7.1–0.7.4):** שדרוג שומר תפקיד, צימוד והגדרות ולא מריץ `cleanup`. Edge גולש לילד גם בלי `supportedBrowsers`, דרך הזיהוי של Chrome ו-Edge שמותקנים לכל המחשב. מדיניות שנמחקה או שונתה בחשבון ילד חוזרת תוך 6–43 שניות ונרשמת כ-`tamper`, וההתראה מגיעה ל-Hub תוך שניות. אפליקציית ההתראות מציגה אותה כהודעת Windows בשם WebFilter, ההודעה על ניסיון עקיפה נשארת על המסך עד שלוחצים, ולחיצה פותחת את ממשק הניהול דרך UAC. שדרוג שקט כשהורה מחובר: הגרסה הראשונה של אפליקציית ההתראות החזיקה קבצים, ו-msiexec הפעיל מחדש את ה-Hub (1641). תוקן: `CloseNotifiers` לפני `InstallValidate`, ועכשיו קוד 0 בלי הפעלה מחדש. Chrome ב-Home קיבל את התוסף מהחנות לפי המדיניות (`location=7`). כרטיס "נמצא חשבון Windows חדש" הופיע בממשק הניהול האמיתי, והורה הבין אותו. חשבון חדש (`ariel`) שנוצר ב-`lab-kids` אחרי השדרוג ל-0.7.5 הביא להורה ב-Hub התראה "נמצא חשבון Windows חדש", ולחיצה עליה פתחה את ממשק הניהול דרך UAC עם `--page devices`. השדרוג ל-0.7.5 כשממשק הניהול היה פתוח הסתיים ב-3010 (`ReplacedInUseFiles`), בלי הפעלה מחדש. ב-0.7.6 (ההגנה הבסיסית) חשבונות המנהל בשני המחשבים קיבלו `ForceGoogleSafeSearch=1` ו-`ForceBingSafeSearch=1`, בלי שום מדיניות אחרת (InPrivate זמין להם), והילדים נשארו עם הנעילות שלהם. שאר ההגנה הבסיסית תלויה בתוסף המלא בחנויות.
>
> **במעבדה, 2026-10-24/25 (0.7.7–0.7.11), עם התוסף המלא 0.3.0 מחנות Edge:** Edge אישר את 0.3.0, והוא הותקן בחשבון ילד ב-lab-hub (Windows Pro) תוך 11 שניות מפתיחת הדפדפן; S1 עבר גם על Pro. נמצאו שלושה באגים שאף בדיקה במחשב הפיתוח לא תפסה, כי שם המשתמש מנהל והשירות לא רץ כ-SYSTEM: (1) השירות דחה כל חשבון רגיל ב-`ClientIdentity` (‏`IsInRole` דרש `TOKEN_DUPLICATE`); (2) ה-native host לא הצליח לוודא שהשרת הוא SYSTEM (חשבון רגיל לא פותח token של תהליך SYSTEM), ענה בעצמו מהמטמון ולא דיווח דבר — עכשיו נבדק בעל ה-pipe; (3) החלטה על בקשה לא הגיעה למחשבי הסוכן — עכשיו הסנכרון נושא החלטות אחרונות. אחרי התיקונים, ב-`lab-kids`: חסימה, בקשת גישה, התראה להורה, אישור, ודף החסימה חזר לאתר לבד פחות משנייה אחרי האישור (`ar-3`). שומר התוסף (§14) חסם ב-`lab-hub` את Chrome (עדיין 0.1.0) ואת Edge (native host ישן) בחשבון ילד 3 דקות אחרי עליית השירות, ושני אירועי `extension_missing` הגיעו להורים. עוד לא נבדק: התוסף המלא (בחנויות יש רק 0.1.0).
>
> **חסימת פרסומות (2026-10-23, [PROTECTION-SPEC §12](PROTECTION-SPEC.md)):** דגל בן משפחה `blockAds` (ברירת מחדל באשף: ילד ומתבגר — כן) בחבילה, ב-`setMember` וב-`features` של `rules`; כבוי בהשהיה. בתוסף ruleset סטטי `ads` (כבוי ב-manifest) שנוצר מ-EasyList בקומיט נעוץ (`tools/starter-lists/ad-ruleset.mjs`): רק כללי דומיין שלם, חוסם כל סוג בקשה חוץ מטעינת דף, בעדיפות 1, ומופעל לפי `features.blockAds` (ובמצב fail-closed). אין בו דומיינים של `educational`, `school-tools` וחברות הליבה. נבדק ב-Edge (`npm run verify:edge`, ו-`npm run e2e:real` מול השירות האמיתי); לא נבדק במעבדה.
>
> **הגנה בסיסית להורים ולמבוגרים (2026-10-23, [PROTECTION-SPEC §13](PROTECTION-SPEC.md)):** לבקשת ההורה, גם חשבונות המנהלים (מקושרים למבוגר או לא מקושרים) ובני משפחה ללא הגבלה מקבלים הגנה משפחתית קלה: `baseline` בראש החבילה (`blockAdult`, `jumpGuard`, `blockAds`, `safeSearch`; כולם פועלים כשאין הגדרה, כך שגם משפחות קיימות מקבלות אותה). אתרי קטגוריית `adult` חסומים להם (`blocked_baseline_adult`, דרך ה-Core ומהדר ה-DNR ככל חסימת קטגוריה — לא ruleset סטטי חדש; כלל היתר של ההורים גובר; בלי חסימת כתובות IP, YouTube וזמן), שומר הקפיצות עם "פתח בכל זאת", ה-ruleset של הפרסומות, ו-SafeSearch של Google ושל Bing (מתון) כמדיניות HKCU — ושום מדיניות אחרת, בלי WFP, בלי ספירת זמן ובלי השהיה. שום דבר מהגלישה שלהם לא נרשם (חסימות, "פתח בכל זאת", חיפושים, בקשות). בפרוטוקול: מצב `baseline` ב-`helloResult` וב-`mode` החדש של `rules` (כך קישור מחדש או שינוי מתג חלים עם `rulesChanged`); כשהכול כבוי — `unrestricted` כמו קודם; כשהשירות לא זמין ה-host עונה `unrestricted` כמו קודם. ממשק הניהול: סעיף "הגנה בסיסית להורים ולמבוגרים" בהגדרות (ארבעה מתגים; שמירה מוסיפה את רשימת `adult` לקטגוריות המשפחה) ושורה בסיכום האשף. תרחישי `BASE-001`–`BASE-008` ו-`FC-005`; נבדק ב-Edge (`npm run verify:edge`) ומקצה לקצה מול השירות וה-host האמיתיים (`npm run e2e:real`); לא נבדק במעבדה. משפחה קיימת מקבלת את חסימת האתרים למבוגרים רק אחרי שמירה של הסעיף (הרשימה לא מתווספת לבד).
>
> **שומר התוסף (2026-10-24, [PROTECTION-SPEC §14](PROTECTION-SPEC.md)) — החלק "התוסף שותק בזמן שדפדפן רץ אצל ילד" של TamperMonitor, נבנה ונבדק מול תהליכים ו-WFP מדומים.** במעבדה נמצא ש-Chrome גולש אצל ילד בלי סינון כשהתוסף שלו לא עובד (ב-Chrome עדיין 0.1.0, שלא מתחבר ל-host), כי WFP מתיר את Chrome ו-Edge שמותקנים לכל המחשב. ההורה בחר: התראה **וגם** חסימת הרשת של הדפדפן עד שהתוסף מתחבר, עם חלון להורדת התוסף. בכל חשבון ש-`NetworkPolicy` מסנן (ילד/נער שלא בהשהיה, חשבון לא מקושר; לא מנהלים, לא מבוגרים, לא בהשהיה) ולכל דפדפן: השירות מוצא כל 10 שניות את תהליכי Chrome ו-Edge (נתיב מלא — רק העותקים שמותקנים לכל המחשב ו-`supportedBrowsers` —, זמן התחלה, ומשתמש מה-token; קריאה בלבד) ואת החיבורים של התוסף מה-agent pipe (החשבון מה-token, הדפדפן והמזהה מה-`hello`, וההודעה האחרונה). חיבור "חי" = הודעה ב-90 השניות האחרונות (שלוש פעימות של 30 שניות), ממזהה החנות של אותו דפדפן כשהוא ידוע. דפדפן שמתחיל לרוץ בלי חיבור חי מקבל **חלון הורדה של 3 דקות** (`ExtensionGrace`), ואחריו — אם הוא עדיין רץ בלי חיבור — הנתיבים שלו יוצאים מרשימת ה-WFP של החשבון (שאר הרשימה לא משתנה), ונרשם אירוע `tamper` עם `extension_missing:chrome`/`edge` (התראה להורים, פעם בשעה לכל דפדפן וחשבון). חיבור חי מחזיר את הרשת מיד (ה-`hello` מעיר את השומר ואת WFP); דפדפן שנסגר נשכח, והפעלה חדשה שלו (כל התהליכים נסגרו) פותחת חלון חדש — כך הורדה שנתקעה מקבלת עוד הזדמנות. אחרי הפסקה של יותר מ-90 שניות (שינה) אין חסימה חדשה ב-90 השניות הבאות. הזמן שנגמר לא משנה את השומר. ניסוח בממשק הניהול ("Chrome פעל אצל ‹שם› בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר") ובאפליקציית ההתראות (שם המחשב והדפדפן, לא הילד; נשארת על המסך). הילד רואה רק שגיאת רשת של הדפדפן — אין לשירות ממשק אצל הילד (מגבלה). גרסה חדשה שמחכה לאישור בחנות משאירה את הדפדפן הזה חסום לילדים עד האישור, וההורים מקבלים התראה. בדיקות: Core (חלון, חסימה, החזרה, הפעלה מחדש, לכל דפדפן בנפרד, מנהלים/מבוגרים/השהיה, חיבור ישן, מזהה התוסף, שינה), שירות (עם WFP מדומה: הרשימה בלי הדפדפן וחזרה, אירוע והגבלה לשעה, pipe אמיתי שרושם `hello`), ממשק הניהול ואפליקציית ההתראות. לא נבדק במעבדה — סעיף ב' ב[רשימת הבדיקה](LAB-INSTALLER-CHECKLIST.md).
>
> חסר ב-M4 — **האזנה להפעלת תהליכים (ProcessGuard) — הצעד הבא**, אירוע "אפליקציה ניסתה להתחבר" (`FwpmNetEventSubscribe`; אירועי החסימה של המסננים שלנו כבר מופיעים ב-`netsh wfp show netevents`), שאר TamperMonitor (קפיצות שעון; "התוסף שותק" נבנה — שומר התוסף, למעלה), ובדיקת אפליקציות Store אמיתיות ו-WebView2, ותרחישי `system` על VM עם התוסף מהחנות. `ALE_AUTH_RECV_ACCEPT` לא מסונן (חיבורים נכנסים — חומת האש של Windows חוסמת אותם למשתמש רגיל).

### M5 — ממשק ניהול

אחרי M2, במקביל ל-M3/M4. **תנאי סיום:** הורה מגדיר משפחה מאפס ורואה את הגלישה של הילד בלוח הבקרה; שינוי נאכף תוך פחות מ-5 שניות.

> **סטטוס (2026-10-19): גרסה שלישית.** `src/WebFilter.Admin` (WPF, MVVM, עברית RTL): לוח בקרה, בקשות (אישור לזמן/עד סוף היום/תמיד, אתר או דף), בני משפחה, כללים, פעילות, מכשירים (קוד צימוד עם ספירה לאחור ופקודה להעתקה, רשימת מחשבים, ביטול, קישור חשבונות Windows של כל מחשב), הגדרות, אבחון וחיבור, ואשף הגדרה ראשונית. דורש הרשאת מנהל (Release). השירות רושם מי ביצע כל שינוי.
>
> - **אשף הגדרה ראשונית** — נכנס לניווט ונפתח לבד במחשב Hub שאין בו עדיין בני משפחה: בני משפחה (שם, תפקיד, גיל, אופן סינון, YouTube; התפקיד ממלא תבנית — ילד/ה: רק אתרים וערוצים מאושרים, 15:00–19:00, 120 דקות; נער/ה: חסימת אתרים לא מתאימים ו-YouTube מוגבל, 14:00–21:30, 240 דקות; מבוגר/ת: ללא סינון), קישור חשבונות ה-Windows של המחשב הזה (`scanAccounts`, אזהרה על חשבון מנהל שמקושר לבן משפחה מסונן), שעות ומכסה יומית, קטגוריות מוכנות (`data/starter-lists`: לימודים, רשתות חברתיות, משחקים — ולכל בן משפחה מסונן: לפי אופן הסינון / מותרת / חסומה; ילד/ה מתחיל/ה עם הלימודים מותרים), סיכום עם **בדיקת דרישות** (`getDiagnostics`; אפשר לשמור גם עם אזהרות), והכל ב-`replaceSettings` אחד. בסוף — הסבר על צימוד מחשבי הילדים.
> - **מכשירים — חשבונות Windows:** לכל מחשב (המחשב הזה קודם) החשבונות שלו ולמי כל אחד שייך; בחירה נשמרת מיד (`replaceSettings` עם `ifVersion`), וסירוב מחזיר את הבחירה הקודמת. חשבון רגיל שלא קושר מקבל כרטיס "נמצא חשבון Windows חדש" (גם בראש לוח הבקרה) עם ארבע תשובות: בן משפחה קיים; ילד/ה או נער/ה חדש/ה מהתבנית של האשף (שעות, מכסה, רשימות מוכנות, הגנות וכללי הכניסה של בית הספר), והחשבון משויך אליו; מבוגר/ת בלי סינון (קיים/ת או חדש/ה); או "להשאיר חסום (אורח)" — `guest` בחשבון, הסינון לא משתנה והכרטיס לא חוזר (אפשר לשנות בשורה של החשבון). חשבון מנהל לא מקבל רשימת ילדים: "מנהל — לא מסונן", ורק — לבחירה — לאיזה מבוגר הוא שייך (בלי השפעה על הסינון; האשף מקשר כך את החשבונות של ההורים). חשבון מנהל שקושר לבן משפחה מסונן מקבל אזהרה וכפתור לביטול הקישור. בניווט — כמה חשבונות מחכים לתשובה (בלי אורחים ובלי מחשבים שהצימוד שלהם בוטל).
> - **מחשב סוכן:** רק לוח בקרה והגדרות. "המחשב הזה מנוהל על ידי מחשב ההורים ‹שם›" עם הקשר האחרון (אזהרה כשאין קשר או כשהמחשב לא צומד), והשהיית חירום מקומית עד סוף היום — חידוש רק ממחשב ההורים. בהגדרות: אזור הזמן, ה-Hub והבדיקות.
> - **בן משפחה** (גם מכפתור "פרטים והגדרות" בלוח הבקרה ובהגדרות): פרטים ואופן סינון (`setMember`), לוח שבועי — כמה חלונות שעות ומכסה לכל יום, "העתקה לכל הימים" —, כללי אתרים אישיים, ערוצי YouTube (מזהה UC או כתובת ערוץ, handle לא חובה, היתר/חסימה), אפליקציות מאושרות (נתיב מלא ל-exe או שם חבילה של אפליקציה מהחנות, "מותרת תמיד"), קטגוריות (לפי אופן הסינון / מותרת / חסומה). השמירות עוברות ב-`replaceSettings` עם `ifVersion` ומשנות רק את החלק של בן המשפחה; אם ההגדרות השתנו בינתיים — קריאה מחדש וניסיון נוסף. עריכה שלא נשמרה באזור אחד נשארת כששומרים אזור אחר.
> - **תוקף לכלל חדש** — קבוע, לשעה או עד סוף היום (לפי השעון של השירות ואזור הזמן של המשפחה), במסך הכללים ובמסך בן משפחה; כלל שפג תוקפו לא חוסם הוספה מחדש.
> - דפוסי אתרים נבדקים ב-`RulePatternValidator`, ערוצים ואפליקציות בבדיקות משלהם; סירוב של השירות (למשל `RuleValidator`) מוצג ליד הטופס שממנו נשלח.
> - 350 בדיקות לממשק, כולל רינדור מחוץ למסך של החלון האמיתי בכל המסכים, בכל שלבי האשף ובמחשב סוכן — בלי שגיאות binding.
>
> חסר — עריכת קטגוריות ורשימות גלובליות וייבוא/ייצוא JSON (מסך "רשימות"), התראת Windows על בקשה חדשה, ובדיקה ידנית של המסכים החדשים מול שירות מותקן במעבדה. הרשימות המוכנות נבחרו ביד ודורשות עיון לפני הפצה.
>
> **התראות להורים — צד השירות (2026-10-22):** ה-Hub מגיש `\\.\pipe\WebFilter.Notify` (קריאה בלבד, רק לחשבון מנהל; נבדק עם pipes אמיתיים) עם התראות `request` (בקשה חדשה), `tamper` (תיקון מדיניות או מסננים, ועצירה לא נקייה של השירות שמתגלה בהפעלה — בכל מחשב) ו-`device` (מחשב שלא דיווח יום). חסר — אפליקציית ההתראות (`src/WebFilter.Notifier`) והפעלתה מהמתקין, וניסוח דגלי ההגנה, `onedrive` ו-`windowsFeatures` בממשק הניהול.

### M6 — ריבוי מחשבים

HubServer, צימוד, HubSync, מכסה משותפת, מסך מכשירים, השהיית חירום מקומית. **תנאי סיום:** תרחישי `multi-device` עוברים במעבדה של 3 VMs (Hub + 2 סוכנים), כולל Hub כבוי וחזרה.

> **סטטוס (2026-10-19):** קיימים — HubServer (Kestrel, HTTPS עם mutual TLS, CA לכל Hub, מפתחות מוגנים ב-DPAPI), צימוד בקוד של 12 תווים ל-10 דקות עם הוכחה הדדית (HMAC שקושר את הקוד ל-CSR ולתעודת השרת), סנכרון snapshot, העלאת שימוש/אירועים, בקשות גישה ישירות או בתור כשה-Hub כבוי, דחיפת שינויים ב-SSE (פחות מ-5 שניות), ביטול מכשיר. נבדק בשני שירותים על loopback (8 בדיקות). מסך מכשירים בממשק הניהול; במחשב סוכן ממשק הניהול מציג סטטוס, את ה-Hub שמנהל אותו ומאפשר השהיית חירום מקומית. שינויי מנהל נשמרים מיד. כל מחשב מדווח על חשבונות ה-Windows שלו (כולל שינוי בהרשאות מנהל), וההורה מקשר אותם במסך המכשירים. אירועי `search`, `override` ו-`tamper` של סוכן מגיעים ל-Hub בהעלאה (נבדק על loopback); סיבה שה-Hub לא מכיר (סוכן חדש יותר) נשמטת ולא מכשילה את ההעלאה. חסר — מעבדת 3 VMs, גרסת התוכנה בכל מחשב.
>
> **החלטות על בקשות לסוכנים (2026-10-24):** במעבדה ילדה ב-`lab-kids` ביקשה אתר, ההורה אישר ב-Hub, והכלל הגיע לסוכן תוך שניות — אבל מסך החסימה לא חזר לאתר לבד והיא לא קיבלה הודעה: `accessDecided` נשלח רק מהתהליך של ה-Hub. עכשיו ה-snapshot לסוכנים מביא את ההחלטות של 48 השעות האחרונות, והסוכן מודיע פעם אחת על כל החלטה של בן משפחה שיש לו חשבון אצלו, גם אחרי הפעלה מחדש (§8.1, §10.3). נבדק: Core (רשימת ההחלטות, חלון 48 השעות והגבלה ל-100, פעם אחת, רק לבני משפחה במחשב, החלטה חוזרת, snapshot בלי הרשימה, שחזור), שירות (JSON, הפעלה מחדש עם SQLite, ה-Hub לא מודיע פעמיים), ושני שירותים על loopback עם ה-agent pipe האמיתי: אישור ב-Hub מגיע לחיבור של הילד בסוכן אחרי `rulesChanged`, פעם אחת, ובקשה מהתור חוזרת עם המזהה שלה; בתוסף — החלטה שמגיעה אחרי שהכלל כבר הותקן (`npm test`, ו-T22b ב-`npm run verify:edge`). לא נבדק במעבדה — סעיף ב' ב[רשימת הבדיקה](LAB-INSTALLER-CHECKLIST.md).

### M7 — מתקין ותיעוד

MSI עם בחירת תפקיד, cleanup בהסרה, מדריכים בעברית. **תנאי סיום:** VM נקי → התקנה + צימוד < 10 דקות; הסרה מחזירה דפדפנים ורשת למצב רגיל.

> **סטטוס (2026-10-19): ה-MSI נבנה והותקן במעבדה** (Hub ב-`lab-hub`, סוכן ב-`lab-kids`: התקנה שקטה כ-30–40 שניות, צימוד וסנכרון ראשון תוך 15 שניות, הגדרות המשפחה הגיעו לסוכן ומדיניות המכונה נכתבה). `installer/` ‏(`dotnet build installer/WebFilter.Installer.wixproj -c Release`) בונה MSI בעברית ובאנגלית, כ-60MB, self-contained — אין צורך ב-.NET במחשב היעד. **התקנה:** דיאלוג תפקיד (Hub או סוכן, ולסוכן כתובת Hub וקוד צימוד — אופציונלי, ו-`pair` שנכשל לא מכשיל את ההתקנה); שירות LocalSystem אוטומטי, הפעלה מחדש בכשל — גם כשאחד הרכיבים שלו נכשל: השירות מסיים אז את התהליך בקוד 1 במקום לעצור "נקי", כי Windows מפעיל מחדש רק שירות שלא נעצר "נקי" (`FaultExit`, 6 בדיקות; הרצה רגילה מהמסוף נעצרת כמו קודם), רישום ל-Safe Mode עם רשת; `WebFilter.Service configure` כותב `appsettings.json` (תפקיד, `ManageBrowserPolicies=true`, `ManageNetwork=true`, מזהי החנויות) ואת ה-manifest של ה-host; מפתחות ה-host ב-HKLM ל-Chrome ול-Edge; `%ProgramData%\WebFilter` נוצרת עם הרשאות ל-SYSTEM ולמנהלים בלבד; ב-Hub — כלל חומת אש ל-TCP 47800, רשת פרטית בלבד. התפקיד ומזהי התוסף נזכרים לתיקון ולשדרוג. **הסרה:** `WebFilter.Service cleanup` (אחרי עצירת השירות, לא בשדרוג) מוחק כל ערך מדיניות שהשירות כתב — כולל HKCU של משתמשים שלא מחוברים, על ידי טעינת ה-hive שלהם — את ה-provider, ה-sublayer ומסנני ה-WFP ואת `%ProgramData%\WebFilter`; בסוכן מצומד, לפני מחיקת התיקייה, הוא מבטל את הרישום ב-Hub (`POST /v1/unregister`, עד 5 שניות — Hub כבוי לא עוצר את ההסרה), וה-Hub מציג את המחשב בין המחשבים שהצימוד שלהם בוטל במצב "הסינון הוסר" ורושם אירוע ביומן (ביטול ולא מחיקה — ראו §4.6); ה-MSI מוחק את השירות, הקבצים, המפתחות וכלל חומת האש. 42 בדיקות ללא הרשאות: הקבצים שנוצרים כפי שהשירות קורא אותם, cleanup מול רישום בזיכרון (כולל משתמש לא מחובר ו-hive שלא נטען), והתאמה בין קובץ ה-WiX לקוד (שם השירות, פורט, מפתחות, שורות הפקודה של ה-custom actions, הרשאות התיקייה). ביטול הרישום נבדק מול קישור מדומה (ה-Hub מאשר, כבר ביטל, כבוי, לא עונה — ההסרה לא מחכה יותר מהזמן שנקבע) ומול Hub אמיתי בשני שירותים על loopback (הסרה מלאה עם Hub פעיל ועם Hub כבוי, צימוד מחדש מחזיר את המחשב, ביטול של הורה נשאר ביטול של הורה), ומסך המכשירים מציג "הסינון הוסר". ההרצה במעבדה מצאה ותוקנו: סוכן שעוד לא קיבל הגדרות מה-Hub עצר את כל השירות; `pair` מול Hub לא זמין קרס במקום לדווח; קוד הצימוד הופיע ב-log של ה-MSI. **החלטה (2026-10-22):** כלל חומת האש של ה-Hub נשאר לרשת פרטית בלבד. כשרשת ה-Hub מסווגת Public, ממשק הניהול מציג אזהרה בראש כל מסך עם כפתור "הגדר כרשת פרטית" (אחרי אישור שזו רשת הבית); השירות משנה את הסיווג דרך Network List Manager (`SetNetworksPrivate`). חסר — החלקים האינטראקטיביים של [רשימת הבדיקה](LAB-INSTALLER-CHECKLIST.md) (התקנה דרך הממשק, כניסת ילד ומדיניות משתמש, הסרה וביטול הרישום ב-Hub), בדיקה במעבדה שהשירות עולה מחדש אחרי כשל של רכיב (סעיף א' ברשימת הבדיקה), חתימת קוד, NativeAOT ל-host, דיאלוגים מימין לשמאל, מדריכים בעברית.

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
