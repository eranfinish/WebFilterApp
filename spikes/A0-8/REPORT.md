# ספייק A0.8: HTTPS עם תעודת לקוח, SSE וצימוד מול מחשב הורים

**תאריך:** 2026-11-19 · **תוצאה: go** לתעודת לקוח, ל-SSE ולהוכחת הצימוד, **עם דרישה ל-A2**: בדיקת EKU של תעודת שרת (ראו "ממצאים") · **קוד:** זורק, לפי CLAUDE.md. A2 כותב את `:hub-client` מחדש ומשתמש בממצאים בלבד.

## מה נבדק

ארבעת קריטריוני היציאה מתוכנית האנדרואיד (ANDROID-PLAN, A0 סעיף 8), כל אחד עם בדיקות שרצות:

| # | קריטריון | בדיקות | תוצאה |
|---|---|---|---|
| 1 | mutual TLS: לקוח מציג תעודה, מאמת שרת מול CA מוצמד. בלי תעודה או עם תעודה מ-CA אחר: דחייה | `MutualTlsTest` (6), `TrustTest` (7) | 13/13 |
| 2 | SSE: אירוע שנדחף מהשרת מגיע תוך כמה שניות | `SseTest` (1) | 1/1 |
| 3 | הוכחת צימוד: `HMAC-SHA256(code, CSR ‖ SHA-256(העלה של השרת))` זהה ל-C#, והשרת דוחה הוכחה שגויה | `ProofVectorsTest` (5), `PairingTest` (4) | 9/9 |
| 4 | שרת: הקוד האמיתי של מחשב ההורים, לא הדמיה | ה-harness (ראו להלן) | כל הבדיקות החיות רצות מולו |

**סה"כ: 23 בדיקות, 23 עוברות, 0 נכשלות, 0 מדולגות** (`build/test-results/test/*.xml`).

## בחירת השרת

השרת הוא **הקוד האמיתי**: `FamilyService` ו-`HubServer` מ-`WebFilter.Service`, בדיוק כמו ש-`HubSyncTests` מפעיל אותם. הם רצים ב-[spikes/A0-8/server](server/Program.cs), תוכנית קונסול קטנה שמפנה לפרויקט `WebFilter.Service` ולא מעתיקה ממנו דבר. היא מדפיסה `READY port=… code=…`, ומקבלת פקודות מ-stdin: `newcode` (קוד צימוד חדש), `push` (שינוי הגדרות, שמעלה את הגרסה ודוחף אירוע SSE), `quit`. בחרנו בה כי נתיב קטן יותר (שרת Kestrel עצמאי עם `HubCertificates`) היה משכפל את לוגיקת הצימוד ולא מוכיח אותה. מצב `vectors` מייצר ווקטורים מ-`HubPairing` וממחלקת `HubCertificates` האמיתיות, כך שההשוואה לקוטלין היא בין שני מימושים ולא בין מימוש לעצמו.

## מדידות

- **TLS שנמשך:** TLS 1.3 כברירת מחדל מול ה-harness. כשכופים TLS 1.2 בלבד (המקרה של S8 עם Android 9), ה-mTLS עובר ו-`TLS_1_2` נמשך.
- **SSE:** אירוע `version` הגיע **37 ms** אחרי `push` (ריצה אחת, loopback). ריצה קודמת נתנה 43 ms. אלה מספרים מבודדים ולא מדידה סטטיסטית.
- **צימוד:** ווקטורי ההוכחה של C# ו-Kotlin זהים (שלושה ווקטורים: הוכחת הסוכן, הוכחת ההאב, ונגטיב עם hash של ה-CA).

## ממצאים

1. **נדרש ב-A2: בדיקת EKU של תעודת שרת.** `PinnedCaTrustManager` ו-`PinnedCaHostnameVerifier` מקבלים כל תעודת end-entity שנחתמה על ידי ה-CA של המחשב. כל תעודת מכשיר נחתמת על ידי אותו CA, לכן בלי בדיקה נוספת מכשיר אחד עם המפתח שלו יכול להתחזות להאב מול כל הסוכנים האחרים. הבדיקה נכתבה **קודם** (`rejectsADeviceClientCertificatePresentedAsTheServer`) ונכשלה, ואז נוסף בדיקת `serverAuth` ב-`Trust.isServerLeaf`. בדיקת ההשפעה: ביטול `cert.verify(...)` מפיל את `rejectsALeafForgedUnderACaWithTheSameNameThatIsNotThePinnedKey`, כלומר הבדיקה מכסה את נתיב החתימה.
2. **פתוח, לא תוקן (ב-`src/`):** `HubCertificates.ChainsTo`, שמשמש את `HubLink` לאימות תעודת ההאב ב-C#, לא בודק EKU. הווקטור `csharpChainsToAcceptsDeviceCertAsHub` מראה שהוא מקבל תעודת מכשיר כתעודת האב (`True`), ובדיקת `theHubsOwnChainCheckAcceptsADeviceCertificateAsTheHub` מתעדת זאת. זה שייך ל-A2 ולהאב, לא לספייק.
3. **`HubApi.cs` מטעה, כפי שצפינו.** ההערה שם אומרת שההוכחה נעשית על ה-CA. הקוד (`HubPairing.AgentProofData`, `HubLink.PairAsync`) מגבב את **העלה של השרת**, והבדיקה `proofOverTheHubCaInsteadOfTheLeafIsRefusedWith403` מראה שההאב דוחה הוכחה שמגבבת את ה-CA. ההערה לא תוקנה בספייק, כמבוקש.
4. **TLS של ההאב מקבל כל תעודת לקוח.** `ClientCertificateMode.AllowCertificate` ו-`AllowAnyClientCertificate()` מאפשרים לחיבור להתבצע בלי תעודה או עם תעודה זרה. הדחייה היא **401 מבדיקת האפליקציה** (`Authorize`), לא כשל ב-handshake. הבדיקות מוכיחות 401 לתעודה חסרה ולתעודה מ-CA זר.
5. **אימות השם מבוסס CA מקבל כל שם מארח, בכוונה.** `hub.test` עובר עם `PinnedCaHostnameVerifier` וכושל עם בדיקת השם של ברירת המחדל (`SSLPeerUnverifiedException`). זה תואם את ההחלטה שההאב נגיש בכל כתובת ברשת הביתית.
6. **CA לא נכון מוצמד:** הבדיקה `pinnedWrongCaFailsTheHandshake` מוודאת שהחיבור נכשל ב-`SSLHandshakeException` (לא בשגיאת רשת כללית), כלומר ההכשל נובע מהצמדת ה-CA.

## מה לא נמדד ולא הוכח

- **Android KeyStore:** מפתח לא ניתן לייצוא מאחורי `X509KeyManager`, וחתימת CSR בו. כאן המפתח בזיכרון.
- **Conscrypt ו-OEM:** כאן רץ SunJSSE על JVM 17. מחסנית ה-TLS של Android לא נבדקה.
- **BouncyCastle על Android:** האנדרואיד מגיע עם BC משלו. כאן נעשה שימוש בתלות של הפרויקט.
- **המכשיר עצמו:** S8, Android 9, ו-OEM לא נבדקו. ספייק זה לא נוגע במכשיר.
- **רשת אמיתית:** Wi-Fi, Doze, ושחזור של SSE אחרי הפעלה מחדש של ההאב לא נבדקו.
- **Keep-alive של 25 שניות:** לא נבדק; הזמן שלו (25 שניות) קטן מ-`readTimeout` של 60 שניות, אבל זה לא הוכח בריצה.
- **ביטול (revocation):** לא נבדק מצד קוטלין. בדיקת ביטול קיימת רק ב-C#.
- **מקרים נוספים של EKU:** רק `serverAuth` ו-`clientAuth` נבדקו.

## איך מריצים מחדש

מתוך שורש הריפו, בטרמינל של PowerShell:

```powershell
dotnet build spikes\A0-8\server\A0_8Hub.csproj -c Debug
dotnet spikes\A0-8\server\bin\Debug\net10.0-windows\A0_8Hub.dll vectors spikes\A0-8\vectors\proof-vectors.json
```

ואז מתוך `spikes\A0-8\kotlin` (ב-Gradle רק בטרמינל של המשתמש, לא ב-shell של הסוכן):

```powershell
.\gradlew.bat test --console=plain
```

תוצאות ב-`spikes\A0-8\kotlin\build\test-results\test\*.xml`. ה-harness נשמר ב-`C:\Users\...\Temp\a0-8-hub-*` (`harness-stderr.log`).
