# ספייק A0.3 — Chrome מנוהל מול דפדפן WebView משלנו (אמולטור Android 12)

**מטרה.** לאסוף עובדות להחלטה בין שתי דרכים לדפדפן המותר היחיד בטאבלט: (א) Chrome מנוהל, עם מדיניות שה-DPC קובע דרך `setApplicationRestrictions`, או (ב) דפדפן WebView משלנו שמריץ את מנוע ההחלטות שלנו. הספייק **לא מחליט**. התוצאות נרשמות כשורות מטריצה (יכולת × יצרן × Android × גרסת דפדפן), לפי `docs/ANDROID-PLAN.md`.

## 0. מה חשוב לדעת לפני הקריאה

- **גרסת Chrome ו-WebView: 91.0.4472.114 (מ-2021)** בכל השורות. זו הגרסה שהאמולטור מציע. Chrome הנוכחי עשוי להתנהג אחרת, במיוחד במדיניות. לא עדכנו (בלי התחברות ל-Play ובלי APK), לפי החלטת הסשן המתזמר. שאלות **1–5** (מדיניות Chrome) ו-**6** (תלוי בשרת Google ובמחרוזת ה-User-Agent) רגישות לגרסה. השורות האלה יורצו על Android 17 עם Chrome עדכני, אחרי ששורר A0.2 ישחרר אותו.
- **המכשיר:** `emulator-5554`, AVD `Tablet_A12`, Android 12 (API 31), image של Google APIs עם Play (`sdk_gphone64_x86_64`). לא נגענו בטאבלט Lenovo, ב-A07, ב-Poco, ב-S8, וגם לא ב-`emulator-5556` (של A0-2).
- **האמולטור נעלם באמצע הריצה** (סעיף 7). בדיקות שנשארו פתוחות מסומנות "לא נבדק".
- **כל טענה כאן מגובה בפלט שמור ב-[evidence/](evidence/)**, ואם לא נבדק זה כתוב.

## 1. מטריצת תוצאות

גרסת הדפדפן בכל השורות: **Chrome/WebView 91.0.4472.114**. היצרן: אמולטור Google APIs (`sdk_gphone64_x86_64`).

| יכולת | Android | תוצאה | ראיה |
|---|---|---|---|
| Q1 Chrome: זמן מ-`setApplicationRestrictions` עד חסימה, Chrome רץ (3 חזרות) | 12 (31) | נחסם בבדיקה הראשונה בכל 3 החזרות: 5.47 / 5.54 / 5.50 שניות. **חסם עליון: הזמן הוא ≤ ~5.5 שניות ולא נפתר יותר** (כל בדיקה כוללת `am start`, המתנה של 3 שניות ו-`uiautomator dump`) | [q1-warm-run1.txt](evidence/q1-warm-run1.txt), [run2](evidence/q1-warm-run2.txt), [run3](evidence/q1-warm-run3.txt) |
| Q1 Chrome: אותו מדד, Chrome הופעל מחדש 5 שניות לפני השינוי (3 חזרות) | 12 (31) | נחסם בבדיקה הראשונה בכל 3 החזרות: 5.68 / 5.43 / 5.46 שניות (חסם עליון) | [q1-cold-run1.txt](evidence/q1-cold-run1.txt), [run2](evidence/q1-cold-run2.txt), [run3](evidence/q1-cold-run3.txt) |
| Q2 Chrome: `*` חוסם, `allowlist` של מארח מנצחת | 12 (31) | `index` ו-`path` מותרים | [rules-A-star-blocks-allow-host.txt](evidence/rules-A-star-blocks-allow-host.txt) |
| Q2 Chrome: חסימת מארח + חריג נתיב ב-allowlist | 12 (31) | `index` חסום, `path.html` מותר (החריג מנצח) | [rules-B-host-block-path-allow.txt](evidence/rules-B-host-block-path-allow.txt) |
| Q2 Chrome: כלל נתיב מדויק (לא קידומת ולא תיקייה) | 12 (31) | `path.html` חסום; `pathX.html` ו-`path/sub.html` מותרים | [rules-C-path-prefix-vs-segment.txt](evidence/rules-C-path-prefix-vs-segment.txt) |
| Q2 Chrome: אותו כלל ב-blocklist וב-allowlist | 12 (31) | מותר (allowlist מנצחת) | [rules-D-same-rule-both-lists.txt](evidence/rules-D-same-rule-both-lists.txt) |
| Q2 Chrome: כלל דומיין חוסם גם תת-דומיין | 12 (31) | `example.com` ו-`www.example.com` חסומים | [rules-W-domain-vs-subdomain.txt](evidence/rules-W-domain-vs-subdomain.txt) |
| Q3 Chrome: משאבים בתוך דף (iframe, img, script) | 12 (31) | **נדחה לריצה חוזרת על API 37** (ראו 3.1) | [q3-embed-no-block.txt](evidence/q3-embed-no-block.txt) (ריק) |
| Q4 Chrome: כלל נתיב בלי פרמטרים, שאילתה בפרמטר אחד | 12 (31) | `search.html?udm=2` ו-`search.html?q=x` נחסמים; `search.html` מותר (ראו 4.6) | [rules-Q4b-path-rule-vs-query.txt](evidence/rules-Q4b-path-rule-vs-query.txt) |
| Q4 Chrome: כלל `?udm=2`, שאילתה בפרמטר אחד | 12 (31) | `?udm=2` נחסם; `?q=x` מותר | [rules-Q4a-query-rule.txt](evidence/rules-Q4a-query-rule.txt) |
| Q4 Chrome: שני פרמטרים (`?udm=2&q=x`, `?q=x&udm=2`) | 12 (31) | **נדחה לריצה חוזרת על API 37**: ה-shell קטע את ה-`&` (ראו 4.6) | — |
| Q4 Chrome: `udm=2` ב-google.com | 12 (31) | **לא נבדק** | — |
| Q5 Chrome: מה הילד רואה בחסימה | 12 (31) | דף קבוע: "10.0.2.2 is blocked", "The person who set up this computer has chosen to block this site.", "Try contacting the system admin.", `ERR_BLOCKED_BY_ADMINISTRATOR`, וכפתור "Details". **אין כפתור בקשה ואין טקסט מותאם.** כותרת הלשונית = מארח בלבד | [21-chrome-blocked-demo.png](evidence/21-chrome-blocked-demo.png) |
| Q5 Chrome: רשימת המדיניות הנתמכת (`chrome://policy`) | 12 (31) | **נדחה לריצה חוזרת על API 37** (ראו 4.4) | [20-chrome-policy-page.png](evidence/20-chrome-policy-page.png) (ריק) |
| Q6 WebView: Google sign-in עד שדה הדוא"ל | 12 (31) | מוצג "Sign in / Use your Google Account / Email or phone". **נעצר לפני כל קלט** | [q6-google-signin-screen.png](evidence/q6-google-signin-screen.png) |
| Q7 WebView: callbacks בניווט YouTube | 12 (31) | ראו 3.2 (המספרים מסומנים כמסוננים) | [q7-youtube-video-1-logcat.txt](evidence/q7-youtube-video-1-logcat.txt), [q7-youtube-logcat-1.txt](evidence/q7-youtube-logcat-1.txt) |
| Q8 WebView: דף חסימה מקומי עם כפתור "בקש גישה" | 12 (31) | מוצג; הלחיצה נרשמה ב-bridge כ-`REQUEST`. **ניסיון חסימה ב-`deny=/watch` בתוך YouTube לא נבדק** | [q8-block-page-screen-2.png](evidence/q8-block-page-screen-2.png), [q8-request-logcat.txt](evidence/q8-request-logcat.txt) |

## 2. ה-DPC והכלים

- האפליקציה: `kotlin/` (`com.webfilter.spike.a03`), `minSdk`=`targetSdk`=31, `compileSdk` 36, `buildToolsVersion` 36.0.0. בנייה דרך ה-wrapper: `BUILD SUCCESSFUL` ([06-gradle-assemble-startdeny.txt](evidence/06-gradle-assemble-startdeny.txt); היו כשלי בנייה קודמים, ראו 4).
- Device Owner: `dpm set-device-owner` הצליח אחרי בדיקה מוקדמת נקייה (אין חשבונות, משתמש 0 בלבד, אין VPN פעיל, אין חבילת ספייק אחרת): [10-precheck-before-do.txt](evidence/10-precheck-before-do.txt), [11-set-device-owner.txt](evidence/11-set-device-owner.txt).
- `setApplicationRestrictions` במצב מערך (`String[]`) עובד: Chrome 91 חסם לפיו ([14-chrome-set-array.txt](evidence/14-chrome-set-array.txt), [15-chrome-read.txt](evidence/15-chrome-read.txt)).
- מצב JSON (מחרוזת עם מערך JSON) **לא נבדק**: מצב המערך עבד, אז לא היה צורך.
- `getManifestRestrictions("com.android.chrome")` **לא נבדק**: נחסם ב-package visibility של Android 11+ (`No such package`, [13-manifest-types.txt](evidence/13-manifest-types.txt)). לא הוסף `<queries>`.

## 3. ממצאים לפי שאלה

### 3.1 Part A: Chrome מנוהל

**Q1 — זמן אכיפה.** שש חזרות: 3 עם Chrome רץ (`warm`), 3 עם Chrome שהופעל מחדש 5 שניות לפני השינוי (`cold`). בכל חזרה: ניקוי, המתנה, קריאת `setApplicationRestrictions` עם t0 מהמכשיר, ואז בדיקה חוזרת של הכתובת החסומה עד שטקסט החסימה מופיע. **בכל ששת החזרות החסימה נראתה כבר בבדיקה הראשונה**, כ-5.4–5.7 שניות מ-t0. מכיוון שכל בדיקה לוקחת כ-5.5 שניות, הזמן האמיתי הוא **בין 0 לכ-5.5 שניות**, ולא ידוע יותר מזה. **לא נבדק:** מצב "policy set while Chrome is stopped" (כלומר Chrome עצור לחלוטין בזמן השינוי), דף שכבר פתוח בלשונית שייחסם בלי ניווט, ומצב JSON.

**Q2 — סדר עדיפויות.** ארבעה מקרים שנבדקו עם intent מלא ומשתנה נתיב רגיל (ללא `&`):
- allowlist של מארח מנצחת blocklist של `*` (A), וגם כשאותו כלל נמצא בשתי הרשימות (D).
- חריג נתיב ב-allowlist מנצח חסימת מארח (B).
- כלל נתיב `host/path.html` מתאים רק לנתיב המדויק: `pathX.html` ו-`path/sub.html` לא נחסמו (C). כלומר לא קידומת תווים ולא מודל תיקיות.
- כלל דומיין `example.com` חוסם גם `www.example.com` (W).
- **לא נבדק:** wildcard חלקי כמו `*.example.com`, regex, וכללי JSON.

**Q3 — משאבים בתוך דף: לא הושלם.** דף "מותר" (`embed.html`) אמור לטעון iframe, תמונה וסקריפט ממארח אחר שחסום. ניסיון ראשון השתמש בכתובת LAN (`192.168.150.185`): הבקשות לא הגיעו ללוג השרת, כלומר האמולטור לא הגיע לכתובת הזו. ניסיון שני השתמש ביציאה 8001 על אותו מארח (`10.0.2.2:8001`), שנגישה מהאמולטור (נבדקה עם `index.html`, ראו `serve-8001.log`), אבל הדף נתקע בספינר ולא נשלחה אף בקשה ל-`/sub/`. ניסיון לנקות את Chrome נכשל כי האמולטור נעלם (סעיף 7). **התשובה לשאלה 3 לא ידועה.**

**Q4 — פרמטרים בשאילתה: רק פרמטר אחד נבדק.** כלל נתיב בלי פרמטרים (`search.html`) חסם את `search.html?udm=2` ו-`search.html?q=x`, והשאיר מותר את `search.html` עצמו. כלל `search.html?udm=2` חסם את `?udm=2` ולא את `?q=x`. שני פרמטרים **לא נבדקו**, ראו 4.6. google.com לא נבדק.

**Q5 — מה הילד רואה.** דף החסימה של Chrome 91 הוא טקסט קבוע של מערכת ([21-chrome-blocked-demo.png](evidence/21-chrome-blocked-demo.png)). **אין דרך מוכחת להוסיף כפתור "בקש גישה" או טקסט מותאם לדף החסימה של Chrome**, אבל גם לא בדקנו את רשימת המדיניות הנתמכת (`chrome://policy`), ולכן אי אפשר לומר בוודאות שאין מדיניות שמאפשרת זאת (ראו 4.4).

### 3.2 Part B: WebView משלנו

**Q6 — Google sign-in.** העמוד `accounts.google.com` נטען בתוך ה-WebView ומציג את מסך הכניסה של Google עם שדה "Email or phone" ([q6-google-signin-screen.png](evidence/q6-google-signin-screen.png)). נעצרנו שם: **לא הוקלד דוא"ל או סיסמה**. מה שאחרי זה דורש את המשתמש. מחרוזת ה-User-Agent של ה-WebView **לא נשמרה**. ב-`shouldOverrideUrlLoading` נראו הפניות אוטומטיות של Google (`ServiceLogin`, `signin/identifier`). השאלה "האם Google חוסם WebView" **לא הוכרעה** ותלויה בגרסה.

**Q7 — callbacks בניווט YouTube.** הדף נטען (אחרי תיקון ההרשאה, ראו 4.3), ובחיפוש "nature" ולחיצה על סרטון נרשמו:
- טעינה ראשונה: `shouldInterceptRequest` נקרא לכל משאב, כולל הבקשה הראשית ([q7-youtube-logcat-1.txt](evidence/q7-youtube-logcat-1.txt), לא מסונן).
- **`shouldOverrideUrlLoading` לא נקרא לא לטעינה הראשונה (`loadUrl` של האפליקציה), ולא לניווטי SPA.** בקובץ ה-logcat של הלחיצה על סרטון ([q7-youtube-video-1-logcat.txt](evidence/q7-youtube-video-1-logcat.txt)) נספרו `shouldOverrideUrlLoading`: **0**; שורות `main=true`: **0**; `doUpdateVisitedHistory`: **1**; `GT` (hook JS): **1** (`GT pushState ...watch?v=...`). **הקובץ מסונן:** הוא נשמר אחרי `grep -vE` שמוציא `shouldInterceptRequest` של משאבים סטטיים (fonts, .js, .css, svg, png, generate_204, ServiceLogin, guide, feedback, GenerateIT), כך שהספירה ל-`main=true` תקפה רק לבקשות ראשיות שלא נופלות לתבניות האלה.
- ה-hook JS (`pushState`, `replaceState`, `popstate`) הוזרק ב-`onPageFinished` ולכן `pushState` שנקרא לפני ההזרקה לא נלכד. זו מגבלה של הבדיקה.
- **תשובה לשאלה "האם יש ניווטים שאף קולבק לא רואה":** כן: ניווט SPA לסרטון חדש לא מופיע ב-`shouldOverrideUrlLoading` ולא כבקשה ראשית ב-`shouldInterceptRequest`. רק `doUpdateVisitedHistory` ו-hook JS רואים אותו. לגרסה 91 בלבד.

**Q8 — דף חסימה מקומי.** כלל מקומי קשיח (`deny` מכיל מחרוזת) חוסם ניווט ראשי ומציג דף מקומי עם כפתור "בקש גישה" בעברית. הכפתור שולח `REQUEST <url>` דרך ה-bridge ([q8-request-logcat.txt](evidence/q8-request-logcat.txt)). **לא נבדק** הניסיון שהמשימה ביקשה: `deny=/watch` ב-m.youtube.com ולחיצה על סרטון, כדי לראות אם הדף החסום מופיע בניווט בתוך הדף. הבדיקה שכן נעשתה נכשלה בפעם הראשונה (ראו 4.5), וזה חשוב לתכנון: הניווט הראשון שהאפליקציה טוענת בעצמה **לא עובר** דרך `shouldOverrideUrlLoading`, ולכן צריך לבדוק את ה-URL הראשוני לפני `loadUrl`. זה תוקן ונבדק.

## 4. מה נכשל ולמה

1. **בנייה ראשונה:** `processDebugMainManifest` נכשל כי הערת XML הכילה `--` (אסור ב-XML). תוקן.
2. **WebView: `net::ERR_CLEARTEXT_NOT_PERMITTED`** לדפי `http://`. תוקן עם `android:usesCleartextTraffic="true"`.
3. **WebView: `net::ERR_CACHE_MISS` לכל טעינה**, גם לדף מקומי וגם ל-`m.youtube.com`, בזמן ש-Chrome טען את אותן כתובות. הסיבה: **חסרה הרשאת `INTERNET`** במניפסט. תוקן. בקבצי ה-logcat הישנים (`q7-local-logcat.txt`, `q7-local-logcat-2.txt`) יש `onPageFinished` גם לדפי שגיאה; הם **לא** מוכיחים טעינה.
4. **Chrome DevTools (`localabstract:chrome_devtools_remote`)** לא הגיב (`Empty reply from server`). פורט 9222 בהוסט השיב לאפליקציה אחרת על המחשב (לא מהאמולטור), לכן לא השתמשנו בו. הבדיקות משתמשות ב-`uiautomator dump`, עם רזולוציה של 2–5 שניות.
5. **Q1, ניסוי ראשון (נפסל):** הניווט הלך לכתובת החוק בלי `http://` (Chrome דחה את ה-intent), והסימון חיפש טקסט שלא תאם. תוקן. בנוסף, ההמתנה לרינדור הייתה שנייה אחת, ולכן הבדיקה פספסה את החסימה. ההרצה החוזרת דרסה את קובץ הניסיון הראשון.
6. **Q2/Q4, ניסויים v1–v3 (נפסלו, לא לציטוט):**
   - (א) `am broadcast` עם `--es allowlist ""` נכשל (`Argument expected after "allowlist"`), כך שהמדיניות לא נשלחה במקרים עם allowlist ריקה (C, Q4a, Q4b, W). תוקן: `--es allowlist` נשלח רק כשהוא לא ריק. רק אחרי התיקון קיבלנו את תוצאות C, Q4a, Q4b, W המוצגות בסעיף 1. (רשומה ישנה: [rules-C-retest-foreground.txt](evidence/rules-C-retest-foreground.txt) **הוחלפה**: היא רצה לפני התיקון ולכן מתעדת מדיניות ריקה.)
   - (ב) בין הגדרת המדיניות לניווט היה פער של 1–2 שניות בלבד, קצר מהזמן הנמדד של כ-5.5 שניות (Q1). תוקן: המתנה של 10 שניות.
   - (ג) ניווט לכתובת שכבר פתוחה בלשונית לא תמיד ביצע טעינה חדשה. תוקן: ניווט ל-`about:blank` לפני כל בדיקה.
   - (ד) intent בלי רכיב מפורש הגיע בפעם אחת ל-`org.chromium.webview_shell` (נראה ב-Q4a). תוקן: הרכיב `ChromeTabbedActivity` מפורש, ונרשם `fg=com.android.chrome` בכל שורה.
   - (ה) זמן האכיפה של 5.5 שניות **לא** הסביר את הכשלים הללו; הסיבה העיקרית היא (א).
   - (ו) הכתובות ב-`rules-probe.sh` נשלחו בלי ציטוט לתוך `sh` של המכשיר, ו-`&` סיים את הפקודה (ראו 4.6). התיקון (ציטוט הכתובת) נכתב, **אבל לא הורץ מחדש**, כי האמולטור נעלם.
7. **`example.com` בלי חסימה:** Chrome 91 מציג "Privacy error" ("Your connection is not private") לאתר בלי חסימה. זה לא קשור למדיניות, וכנראה תעודה שה-Chrome הזה לא מאמת. לכן אתרי HTTPS אמיתיים לא שמישים לבדיקות על הגרסה הזו בלי בדיקה נוספת.
8. **LAN:** הכתובת `192.168.150.185` לא נגישה מהאמולטור (סעיף 3.1, Q3).
9. **`adb kill-server` (הפרה):** הרצתי `adb kill-server` ואחריו `adb start-server`. ה-daemon של adb משותף לכל האמולטורים על המחשב, כולל `emulator-5556` של A0-2, שהסשן שלו היה פעיל באותו זמן. הפעולה לא הייתה בהוראות, ולא נבדק אם היא הפריעה לסשן של A0-2. **תרשם כאן ובסיכום.**

## 5. השוואה: Chrome מנוהל מול WebView משלנו (Chrome/WebView 91)

| שאלה | Chrome מנוהל | WebView משלנו |
|---|---|---|
| זמן אכיפה | בין 0 ל-~5.5 שניות (נמדד: חסם בבדיקה הראשונה) | מיידי **לפי התכנון** (הבדיקה בקולבק); **לא נמדד** |
| כללי נתיב | נתיב מדויק; לא קידומת ולא תיקייה | לפי הקוד שלנו (לא נבדק כאן) |
| פרמטרים בשאילתה | כלל נתיב בלי פרמטרים חוסם שאילתה בפרמטר אחד; שני פרמטרים לא נבדקו | לפי הקוד שלנו (לא נבדק) |
| משאבים בתוך דף | לא נבדק (Q3 לא הושלם) | לפי התכנון: `shouldInterceptRequest` (לא נבדק כאן) |
| ניווטי SPA (YouTube) | לא נבדק | **לא נראים לקולבקי הניווט**; רק `doUpdateVisitedHistory` ו-hook JS |
| דף חסימה עם בקשה | **לא ניתן להוסיף כפתור** לפי מה שנראה (Q5 חלקי) | **ניתן** (Q8, לחיצה נרשמה) |
| כניסת Google | לא נבדק | מוצג עד שדה הדוא"ל (Q6) |
| תלות בגרסה | גבוהה (מדיניות ותעודות) | בינונית |

**המסקנה הזמנית:** ב-Chrome המנוהל מוותרים על דף חסימה משלנו ועל ניווטי SPA, ומקבלים אכיפה ברמת הדפדפן עם עיכוב של עד ~5.5 שניות. בדפדפן משלנו יש דף חסימה וקולבקים, אבל יש פער ידוע בניווטי SPA שצריך לסגור (למשל hook JS שמעביר URL לפני הניווט), וזה צריך בדיקה חוזרת שלא הושלמה.

## 6. מה לא נבדק ומה נותר

- **Q3 (משאבים בתוך דף):** לא הושלם. דרוש אמולטור פעיל וניסיון נוסף על `10.0.2.2:8001` עם חסימה לפורט הזה.
- **Q5, `chrome://policy`:** לא נבדק. ה-intent נדחה. דרוש דרך אחרת לפתוח את העמוד ב-Chrome 91 (למשל הקלדה בסרגל הכתובות על המסך).
- **Q1:** לא נבדק: דף פתוח שייחסם בלי ניווט; Chrome עצור לחלוטין בזמן השינוי; מצב JSON; זמן מדויק מתחת ל-5.5 שניות.
- **Q2:** wildcard חלקי, regex, כללי JSON.
- **Q4:** שני פרמטרים (ראו 4.6); `udm=2` ב-google.com.
- **Q6:** שום דבר אחרי שדה הדוא"ל. User-Agent לא נשמר.
- **Q7:** hook JS לא לכד `pushState` לפני `onPageFinished`. הספירות הן מהקובץ המסונן (סעיף 3.2).
- **Q8:** ניסיון `deny=/watch` ב-YouTube לא נבדק.
- **גרסאות:** כל התוצאות על Chrome/WebView 91. שאלות 1–5 ו-6 רגישות לגרסה.
- **מכשיר אמיתי:** לא נבדק. האמולטור לא מייצג יצרן.
- **ניקוי:** לא בוצע (סעיף 7).

## 7. מצב האמולטור וניקוי

- ב-`adb devices` נשאר רק `emulator-5556` (A0-2). `emulator-5554` לא הופיע, בלי הודעת שגיאה מהאפליקציה. לא ידוע אם הוא נסגר, קרס, או נסגר על ידי מישהו אחר.
- לא נגענו ב-`emulator-5556`.
- **קריסה (תוצאה, לא הערת שוליים):** האמולטור `emulator-5554` קרס ב-**02:30 שעון מקומי (00:30 UTC; בשעון הסטטוס של האמולטור 12:30)**. נכתב דוח קריסה (minidump של כ-67MB), ולא ידוע מה גרם לה. זה רלוונטי ל-A0.2 (VPN עם lockdown ומדיניות נכשלת-סגורה): קריסה באמצע ריצה, עם Device Owner ו-`URLBlocklist` פעילים, משאירה מכשיר במצב שלא נבדק.
- **הפעלה מחדש:** לפי אישור הסשן המתזמר, `emulator.exe -avd Tablet_A12 -memory 2048` הופעל ב-02:35 (בלי wipe). **לאחר כ-11 דקות לא עלה:** אף פורט קונסולה לא מאזין (5554, 5555, 5556, 5557), ו-`adb devices` ריק. בנוסף הופעל ב-02:39 תהליך אמולטור שני שלא הפעלתי ואינני יודע מי הפעיל אותו.
- **הופסקו הרצות המכשיר** לפי ההוראה. לא הרגתי תהליכים ולא הפעלתי שוב את `adb kill-server`. **Device Owner, האפליקציה ו-`URLBlocklist` לא אומתו אחרי הקריסה** (הבדיקה דורשת את המכשיר). Q3, Q5 והניקוי לא בוצעו.
- ראיה: [30-emulator-crash-and-relaunch.txt](evidence/30-emulator-crash-and-relaunch.txt).
- **החלטה (הסשן המתזמר, אחרי הכישלון):** לא מפעילים את Tablet_A12 שוב. Q3, Q5, Q4 של שני פרמטרים והניקוי **נדחים לריצה חוזרת על Android 17 (Chrome 145.0.7632.218)**, שמתאים יותר לשאלות הרגישות לגרסה.
- **מצב Tablet_A12 הסופי (Device Owner, האפליקציה, מדיניות Chrome): לא ידוע אחרי הקריסה.** לפני שימוש חוזר צריך לנקות או לאפס את ה-AVD.
- **הפעלה מחדש נוספת (לפי בקשת המשתמש, "Yes, relaunch"):** `emulator.exe -avd Tablet_A12 -memory 2048` הופעל ב-11:31 (שעון מקומי), ועלה ב-11:33 כ-`emulator-5554` (הפורט הסטנדרטי). במקביל רץ `emulator-5580` של סשן אחר, שלא נגענו בו.
- **מה נמצא אחרי ההפעלה:**
  - **Device Owner נשמר:** `dpm list-owners` מציג `com.webfilter.spike.a03/.AdminReceiver`, `DeviceOwner`.
  - **האפליקציה מותקנת** (`com.webfilter.spike.a03`).
  - **תצפית אחת, סיבה לא ידועה: מדיניות Chrome לא נמצאה אחרי ההפעלה.** `chrome_read` החזיר `restrictions=` ריק, ו-`URLBlocklist` שנקבע לפני הקריסה לא היה פעיל. ייתכן שהקריסה קרתה לפני שהמדיניות נשמרה, וזה הסבר אחד אפשרי. **לא הוכח** ש-`setApplicationRestrictions` אובד בכל הפעלה מחדש של האמולטור. זה נושא לבדיקה ב-A0.2 (נכשל-סגור) ולא מסקנה.
  - חשבונות: 0. משתמשים: 1 (משתמש 0).
- **נדחה לריצה על API 37 (לא בוצע כאן):** Q3, Q5 ו-Q4 של שני פרמטרים. ההנחיה של הסשן המתזמר: לא להריץ אותם על Tablet_A12.
- **ניקוי (בוצע על `emulator-5554` בלבד):**
  - `dpm remove-active-admin com.webfilter.spike.a03/.AdminReceiver`: `Success`.
  - `adb uninstall com.webfilter.spike.a03`: `Success`.
  - מצב סופי: `dpm list-owners` → `no owners`; האפליקציה לא מותקנת; משתמש 0 בלבד; חשבונות 0; `device_provisioned=1`. ראיה: [90-final-state.txt](evidence/90-final-state.txt), [90-cleanup-log.txt](evidence/90-cleanup-log.txt).
  - האמולטור נעצר עם `adb -s emulator-5554 emu kill` ("OK: killing emulator"). לא הופעל מחדש.
  - לא נגענו ב-`emulator-5580` ו-`emulator-5556`.
- **מצב שנשאר על ה-AVD (לא אומת אחרי ההיעלמות, והמכשיר עצמו לא מחובר):**
  - Device Owner: `com.webfilter.spike.a03/.AdminReceiver`.
  - האפליקציה `com.webfilter.spike.a03` מותקנת.
  - `URLBlocklist` של Chrome הוא `10.0.2.2:8000/path.html` (מהבדיקה האחרונה). **צריך לנקות** עם `chrome_clear`.
  - Chrome: נבחרה "No thanks" ב-sync, ולא נוסף חשבון.
  - שרתי ה-node (8000 ו-8001) רצים על המחשב. יש לעצור אותם.
- **ניקוי שלא בוצע:** `chrome_clear`, `dpm remove-active-admin`, הסרת האפליקציה, ושמירת `dpm list-owners` ומשתמשים ל-`evidence/90-final-state.txt`. הניקוי דורש שהאמולטור יחזור.

## 8. מה נשאר עבור ההחלטה

- אם הכיוון הוא Chrome מנוהל: לבדוק על Android 17 (Chrome עדכני) את שאלות 1–5, כולל `chrome://policy`, מדיניות משאבים ורשימת המדיניות הנתמכת.
- אם הכיוון הוא WebView משלנו: לסגור את פער ניווטי ה-SPA לפני החלטה, ולבדוק ש-`shouldInterceptRequest` רואה משאבים בתוך iframe.
- בשני המקרים: לאשר בטאבלט האמיתי (אחרי הסרת רימון) את הגרסה שנבחרת, כי אמולטור לא מספיק.
