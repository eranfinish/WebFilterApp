# ספייק S2 — Native Messaging וזיהוי המשתמש (חלק לא-מוגבה)

> 2026-10-18 · Microsoft Edge 156.0.4314.30 · .NET 10 · Windows 11 Pro 10.0.26220 · משתמש: מנהל עם UAC (טוקן מפוצל)
> תוצאות גולמיות: [results/s2-native-messaging.json](results/s2-native-messaging.json), [results/s2-keepalive.json](results/s2-keepalive.json)

## שורה תחתונה

| חלק | תוצאה |
|-----|:---:|
| Edge מפעיל את ה-host כמשתמש, ה-host מעביר ל-pipe, השרת מזהה את המשתמש ואת הקובץ שמתחבר | ✅ 12/12 |
| port פתוח שומר את ה-service worker חי (בלי אוטומציה) | ✅ 2/2 |

**ההחלטה: go**, עם שני כללי מימוש חדשים (סעיף "השלכות").

## איך הרצנו

- `host/` — native host ב-.NET 10 (framework-dependent, Debug). רשום **זמנית** ב-`HKCU\Software\Microsoft\Edge\NativeMessagingHosts`, והרישום נמחק בסוף כל ריצה
- `server/` — שרת pipe (`\\.\pipe\WebFilter.Spike.Agent`, ACL: Authenticated Users) שרץ כמשתמש הנוכחי, במקום השירות האמיתי
- `extension/` — תוסף מינימלי שמופעל דרך Playwright; `extension-keepalive/` — תוסף שרץ ב-Edge **בלי** Playwright

```bash
dotnet build spikes/S2/host/WebFilter.Spike.Host.csproj
dotnet build spikes/S2/server/WebFilter.Spike.Server.csproj
node spikes/S2/harness/run-s2.mjs
node spikes/S2/harness/run-s2-keepalive.mjs
```

## תוצאות

| # | נבדק | תוצאה |
|---|------|-------|
| T1.1 | `hello` מהתוסף מגיע לשרת דרך ה-host וחוזר | ✅ |
| T1.2 | השרת רואה את ה-SID של המשתמש בדפדפן (impersonation של לקוח ה-pipe) | ✅ |
| T1.3–4 | השרת רואה את נתיב הקובץ של ה-host (`GetNamedPipeClientProcessId` + `QueryFullProcessImageName`) ומסמן אותו כמהימן | ✅ |
| T1.5 | אותו SID גם מטוקן **התהליך** של הלקוח — בלי impersonation | ✅ |
| T2.1 | השרת דוחף הודעה לתוסף | ✅ |
| T4.1–2 | הריגת ה-host → התוסף מקבל `onDisconnect`; חיבור מחדש מפעיל host חדש | ✅ |
| T5.1 | השרת כבוי → ה-host עונה בעצמו (`serviceAvailable: false`) | ✅ |
| T5.2 | ה-host מזהה חשבון **מנהל שרץ לא-מוגבה** | ✅ `elevation=limited`, `IsInRole(Administrator)=false` |
| T6.1 | תהליך זר (`node.exe`) שמדבר באותו פרוטוקול נדחה לפי נתיב הקובץ | ✅ |
| K1 | Edge בלי אוטומציה, port פתוח: ה-worker חי 131 שניות (מעבר ל-30 שניות של idle), אותו boot לכל אורך הזמן | ✅ |
| K2 | בקרה: בלי port, ה-worker נסגר — הדיווח המתוזמן ל-90 שניות לא הגיע | ✅ |

**מדדים:** הפעלה קרה של ה-host עד תשובת `hello`: 280–380ms (Debug, לא AOT). ה-ruleset של fail-closed מכסה את הזמן הזה, ו-NativeAOT ב-M3 יקצר אותו.

## ממצאים חשובים

1. **impersonation "דולף".** אחרי `NamedPipeServerStream.RunAsClient`, טוקן ה-identification של הלקוח עבר ל-`Timer` שנוצר מאוחר יותר באותו זרם async. הקריאה של `TimeZoneInfo.Local` מה-registry זרקה `SecurityException`, והשרת קרס. פתרון שנבדק: impersonation ב-thread ייעודי עם `ExecutionContext.SuppressFlow()`. **עדיף בכלל לא להשתמש ב-impersonation:** קריאת ה-SID מטוקן התהליך של הלקוח (T1.5) נותנת את אותה תוצאה בלי הסיכון.
2. **`IsInRole(Administrator)` לא מזהה הורה שגולש לא-מוגבה.** עם UAC, הטוקן הרגיל של מנהל מסומן "deny only" לקבוצת Administrators. הזיהוי הנכון ל-fallback של ה-NativeHost: `TokenElevationType` = `limited` (טוקן מפוצל) או `full`.
3. **Playwright משאיר service workers חיים.** בבדיקת הבקרה דרך Playwright ה-worker לא נסגר גם בלי port. לכן **אי אפשר לבדוק את אורך החיים של ה-worker ברמה 3 של הסימולציות**. צריך הפעלה ישירה של Edge, כמו ב-K1/K2.
4. ב-K1 ה-worker שלח הודעה כל 10 שניות (מטיימר). לפי התיעוד, port פתוח לבדו מספיק. הבקרה (K2) מוכיחה שבלי port ה-worker כן נסגר.

## השלכות על המימוש

| רכיב | כלל |
|------|-----|
| `AgentPipeServer` | זיהוי: `GetNamedPipeClientProcessId` → פתיחת handle לתהליך **מיד** בחיבור (מונע שימוש חוזר ב-PID) → נתיב קובץ + SID מטוקן התהליך. **בלי `RunAsClient`.** |
| `NativeHost` fallback | "מנהל" = `TokenElevationType` ∈ {`limited`, `full`}, לא `IsInRole` |
| תוסף | להשאיר port של Native Messaging פתוח כל הזמן; ב-`onDisconnect` → fail-closed ומיד חיבור מחדש |
| סימולציות רמה 3 | בדיקות אורך חיים של ה-worker — בהפעלה ישירה של Edge, לא דרך Playwright |

## מה נשאר ל-S2b (במכונה וירטואלית, עם הרשאות מנהל)

- רישום ה-host ב-**HKLM**, ומדיניות `NativeMessagingUserLevelHosts=0` שחוסמת host מזויף ב-HKCU
- שרת pipe שרץ כ-**LocalSystem** (שירות אמיתי) ומשרת דפדפנים של כמה משתמשים בכמה sessions (החלפת משתמש מהירה)
- הרשאות לפתיחת טוקן התהליך מ-SYSTEM מול תהליכים מוגנים
