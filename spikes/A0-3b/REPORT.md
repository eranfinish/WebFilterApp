# ספייק A0.3b — Chrome 145 מול WebView משלנו (Android 17, API 37)

**מטרה.** להריץ מחדש את החלקים הפתוחים של A0.3 על Chrome עדכני ו-WebView עדכני, ולהשוות לתוצאות Chrome 91 של A0.3. הספייק **לא מחליט** על הדפדפן. הוא רושם עובדות כשורות מטריצה (יכולת × יצרן × גרסה).

## 0. מה חשוב לדעת לפני הקריאה

- **המכשיר:** אמולטור Google עם תמונת Play (`sdk_gphone64_x86_64`), לא AOSP טהור. טביעת האצבע: `google/sdk_gphone64_x86_64/emu64xa:17/CE2A.260420.051.A2/16418371:user/release-keys`. בעמודת "יצרן" בטבלה: **Google, אמולטור (לא מכשיר אמיתי)**. ראיה: [00-baseline.txt](evidence/00-baseline.txt).
- **גרסאות:** Chrome `145.0.7632.218` ו-WebView `145.0.7632.218` (אותה גרסה). ראיה: [00-baseline.txt](evidence/00-baseline.txt). הגרסה **בסוף** הריצה נרשמת ב-[90-final-state.txt](evidence/90-final-state.txt) (סעיף 8).
- **Device Owner** הוגדר על `emulator-5580` בלבד (אפליקציית הבדיקה `com.webfilter.spike.a03b`). לא נגענו במכשירים אמיתיים, ב-`emulator-5590` (סשן אחר) או ב-`Tablet_A12`.
- **Chrome קרס בעצמו פעם אחת** באמצע ניסוי: Play Services רשמה `DynamiteLoaderV2Impl: Module config changed, forcing restart`, ואחריה `Process: Sending signal SIG 9` מתוך ה-pid של Chrome. ראיה: [11-logcat-after-chrome-exit.txt](evidence/11-logcat-after-chrome-exit.txt). הריצה ההיא פסולה. כל מקרה בודק את ה-pid של Chrome לפני ואחרי, ומסומן פסול אם השתנה.
- **פלט הריצה הראשונה של מקרה Q3 נדרס** בריצה חוזרת, ונשאר רק ה-logcat שלה. לכן הריצה התקפה הראשונה היא ה-[case-q3-block-8130-embed-run2-valid.txt](evidence/case-q3-block-8130-embed-run2-valid.txt).
- **התמדה של המדיניות אחרי הפעלה מחדש של Chrome: לא נבדק.** כל מקרה הגדיר מחדש את המדיניות (`chrome_clear` ואז `chrome_set`).
- **סטייה מ-Chrome רגיל:** הופעלו `set-debug-app --persistent com.android.chrome` וקובץ `/data/local/tmp/chrome-command-line` (`--disable-fre --no-first-run --disable-sync`) כדי לדלג על מסכי ההפעלה הראשונה. **לא אומת** שהדגלים נקלטו; מסך ההתראות הופיע והוכרע ידנית ("No thanks"), ולא הופיע מסך תנאים.
- **שרת הבדיקה** (`tools/serve.js`) רץ על `127.0.0.1` בשני פורטים (8130 = origin "חסום", 8131 = origin "מותר"), והאמולטור מגיע אליו דרך `10.0.2.2`. כל בקשה נרשמה ב-[serve-a03b.log](evidence/serve-a03b.log) עם נתיב, שאילתה ו-`Referer`. שדה `Sec-Fetch-*` נצפה ריק; **הסיבה לא נבדקה**, ולכן הבחנו בין מקרים לפי תגי `k=` בשאילתה.

## 1. מטריצת תוצאות (Chrome 145 / WebView 145)

| יכולת | יצרן / Android | גרסה | תוצאה | ראיה |
|---|---|---|---|---|
| **Q1 Chrome:** זמן מ-`setApplicationRestrictions` עד חסימת ניווט subframe (דף פתוח, 3 חזרות, שעון המארח) | Google emu / 17 (37) | 145 | החסימה נכנסה תוך **≤0.49 s / ≤0.64 s / ≤0.56 s** מתחילת הקריאה (הגבול העליון של כל ריצה). ההערכה כוללת את ה-round trip של adb. | [q1-run1.txt](evidence/q1-run1.txt), [q1-run2.txt](evidence/q1-run2.txt), [q1-run3.txt](evidence/q1-run3.txt), סקריפט: [tools/q1-run.sh](tools/q1-run.sh) |
| **Q2 Chrome, סדר עדיפויות A:** `*` ב-blocklist, מארח ב-allowlist | 17 | 145 | `8131` מותר, `8130` חסום ("is blocked") | [case-q2-A-star-allow-host-8131.txt](evidence/case-q2-A-star-allow-host-8131.txt), [case-q2-A-star-allow-host-8130.txt](evidence/case-q2-A-star-allow-host-8130.txt) |
| **Q2 B:** חסימת מארח + חריג נתיב ב-allowlist | 17 | 145 | `index.html` חסום, `path.html` מותר (החריג מנצח) | [case-q2-B-host-block-path-allow-index.txt](evidence/case-q2-B-host-block-path-allow-index.txt), [case-q2-B-host-block-path-allow-path.txt](evidence/case-q2-B-host-block-path-allow-path.txt) |
| **Q2 C:** כלל נתיב מדויק | 17 | 145 | `path.html` חסום; `pathX.html` ו-`path/sub.html` מותרים | [case-q2-C-path-rule-path.html.txt](evidence/case-q2-C-path-rule-path.html.txt), [case-q2-C-path-rule-pathX.html.txt](evidence/case-q2-C-path-rule-pathX.html.txt), [case-q2-C-path-rule-path_sub.html.txt](evidence/case-q2-C-path-rule-path_sub.html.txt) |
| **Q2 D:** אותו כלל ב-blocklist וב-allowlist | 17 | 145 | מותר (allowlist מנצחת) | [case-q2-D-same-rule-both-lists.txt](evidence/case-q2-D-same-rule-both-lists.txt) |
| **Q2 W:** כלל דומיין חוסם גם תת-דומיין | 17 | 145 | **לא נבדק** (המארח הוא IP, אין תת-דומיין) | — |
| **Port scope:** כלל ל-`10.0.2.2:8130` | 17 | 145 | חוסם את 8130, מאפשר את 8131 | [case-q5-portscope-8130-blocked.txt](evidence/case-q5-portscope-8130-blocked.txt), [case-q5-portscope-8131-allowed.txt](evidence/case-q5-portscope-8131-allowed.txt) |
| **Q3 Chrome: משאבים בתוך דף מותר** (iframe, img, script מ-origin חסום) | 17 | 145 | **iframe נחסם** (placeholder של Chrome בצילום המסך; אין שורת שרת ל-`frame.html`). **img ו-script נטענו** (שורות שרת `img.svg`, `s.js`). חזר על אותה תוצאה בריצה שנייה. **fetch/XHR לא נבדק.** | [case-q3-block-8130-embed-run2-valid.txt](evidence/case-q3-block-8130-embed-run2-valid.txt), [case-q3-block-8130-embed.txt](evidence/case-q3-block-8130-embed.txt) (ריצה שנייה), [12-q3-block-screen-full.png](evidence/12-q3-block-screen-full.png), [case-q3-baseline-control.txt](evidence/case-q3-baseline-control.txt) (בקרה: הכול נטען) |
| **Q4 Chrome: שאילתה, כלל `?udm=2`** | 17 | 145 | חסום: `?udm=2`, `?udm=2&q=x`, `?q=x&udm=2`. מותר: `?q=x`, בלי שאילתה, ו-**`?udm=21`** (התאמה מדויקת של key=value, לא קידומת) | [q4-udm-rule-udm21.txt](evidence/case-q4-udm-rule-udm21.txt), [q4-udm-rule-control-search.html_q_x.txt](evidence/case-q4-udm-rule-control-search.html_q_x.txt) |
| **Q4 google.com:** כלל `www.google.com/search?udm=2` | 17 | 145 | חסום: `search?q=test&udm=2`, `search?udm=2&q=test`. מותר: `search?q=test` (תוצאות רגילות). **הלשונית "תמונות" מנווטת ל-`search?sca_esv=…&udm=2&fbs=…`, והכלל חוסם אותה.** | [case-q4-google-udm-rule-q-test-udm2.txt](evidence/case-q4-google-udm-rule-q-test-udm2.txt), [case-q4-google-udm-first-q-test.txt](evidence/case-q4-google-udm-first-q-test.txt), [41-google-images-tap-result.txt](evidence/41-google-images-tap-result.txt) |
| **Q5 Chrome: מה רשום ב-`chrome://policy`** | 17 | 145 | `URLBlocklist` ו-`URLAllowlist` מופיעים, Source **Platform**, Level **Mandatory**, Applies to **Device**. **270 שמות מדיניות נתמכות** (אחרי הסרת 7 תוויות ממשק: Device, Level, Mandatory, Platform, Policies, Source, Value). | [09-chrome-policy-ui.xml](evidence/09-chrome-policy-ui.xml), [10-chrome-policy-expanded.xml](evidence/10-chrome-policy-expanded.xml), [35-q2-policy-expanded.xml](evidence/35-q2-policy-expanded.xml), [38b-q5-policy-names-clean.txt](evidence/38b-q5-policy-names-clean.txt) |
| **Q5 Chrome: האם אפשר טקסט מותאם או כפתור "בקש גישה" בדף החסימה?** | 17 | 145 | **לא נמצאה מדיניות כזו ברשימה של 270 השמות.** בדיקה: נבדקו גם שמות שמופיעים בשתי ההצעות הקרובות ביותר (`AlternateErrorPagesEnabled`, `SSLErrorOverrideAllowed`). הרשימה מכסה את האלפבית (ספירה לפי אות בקובץ), ו-5 מדיניות סינון ידועות נמצאו בה. **זו הוכחה לרשימה של הבנייה הזו, לא לתיעוד.** דף החסימה: "10.0.2.2 is blocked" / "Your organization doesn’t allow you to view this site", בלי כפתור. **קוד השגיאה וכפתור "Details" לא נראו** (לא בעץ הנגישות ולא בצילום). | [case-q5-block-page-8130.png](evidence/case-q5-block-page-8130.png), [38-q5-policy-names.txt](evidence/38-q5-policy-names.txt), [38b-q5-policy-names-clean.txt](evidence/38b-q5-policy-names-clean.txt) |
| **Chrome: `*` ב-blocklist חוסם גם `chrome://policy`** | 17 | 145 | "policy is blocked" במקום העמוד | [32-q2-star-blocks-chrome-policy-page.xml](evidence/32-q2-star-blocks-chrome-policy-page.xml) |
| **Chrome: כלל `m.youtube.com/watch`, ניווט ישיר** | 17 | 145 | **נחסם**: "m.youtube.com is blocked", URL `…/watch?v=…` בשורת הכתובת | [case-q6yt-direct-watch-rule.txt](evidence/case-q6yt-direct-watch-rule.txt) |
| **Chrome: כלל `m.youtube.com/watch`, לחיצה על סרטון מתוך תוצאות** | 17 | 145 | **לא נחסם.** שורת הכתובת הציגה `…/watch?v=EnkWqUk2aA8…`, והעמוד נטען: הכותרת "Wonder Shore … - YouTube" ונגן הווידאו נראה. **כלומר ניווט SPA פנימי עבר את הכלל.** | [47-q6yt-results-tap-result-2.txt](evidence/47-q6yt-results-tap-result-2.txt), [46-yt-after-tap-chrome-2.xml](evidence/46-yt-after-tap-chrome-2.xml) |
| **Q7 WebView: callbacks בניווט YouTube SPA** (לחיצה בסקריפט על `a[href*=watch]`) | 17 | WV 145 | ב-logcat הלא מסונן (`24-…`): `shouldOverrideUrlLoading` = **0** (גם ל-`/watch`); `shouldInterceptRequest` עם `main=true` = **1** (טעינת תוצאות) ו-**0** ל-`/watch`; `doUpdateVisitedHistory` ל-`/watch` = **1**; `GT pushState` = **1**. כלומר ה-SPA נראה רק דרך `doUpdateVisitedHistory` וה-hook. | [24-yt-q7-click-logcat-full.txt](evidence/24-yt-q7-click-logcat-full.txt) |
| **Q8 WebView: דף חסימה מקומי עם "בקש גישה"** | 17 | WV 145 | מוצג; הלחיצה נרשמה כ-`REQUEST` | [14-webview-q8-screen.xml](evidence/14-webview-q8-screen.xml), [15-webview-q8-logcat-full.txt](evidence/15-webview-q8-logcat-full.txt), [16-webview-q8-block-page.png](evidence/16-webview-q8-block-page.png) |
| **Q8 WebView: `deny=/watch` בתוך YouTube** | 17 | WV 145 | `DENY_IN_HISTORY` (ב-`doUpdateVisitedHistory`) כ-**20 ms** אחרי `pushState`; דף החסימה עם הכפתור החליף את דף הצפייה. **לא נבדק אם ההשמעה המשיכה** (ראו סעיף 4). | [27-yt-q8-deny-watch-retry-logcat-full.txt](evidence/27-yt-q8-deny-watch-retry-logcat-full.txt), [28-yt-q8-retry-screen.xml](evidence/28-yt-q8-retry-screen.xml) |
| **Q8 WebView: `shouldInterceptRequest` ב-subframe** | 17 | WV 145 | רואה את ה-iframe, ה-img וה-script מ-origin אחר (`main=false`) | [13-webview-w1-embed-logcat-full.txt](evidence/13-webview-w1-embed-logcat-full.txt) |
| **Q6 כניסה לגוגל** | — | — | **מחוץ לתחום** (לא נכנס חשבון) | — |

## 2. השוואה: A0.3 (Chrome/WebView 91) מול A0.3b (145)

| שאלה | A0.3 (91, API 31) | A0.3b (145, API 37) | שינוי |
|---|---|---|---|
| Q1 זמן אכיפה | ≤5.5 s, ניווט top-level, לולאת `am start` | ≤0.64 s, subframe, שעון המארח | **לא השוואה ישירה** (סוג ניווט שונה, וכל ריצה כוללת adb) |
| Q2 סדר עדיפויות | allowlist מנצחת `*`; חריג נתיב מנצח; נתיב מדויק | **זהה** | אין שינוי |
| Q3 משאבים בתוך דף | לא הושלם | iframe נחסם; img/script נטענים | **חדש** |
| Q4 שאילתה בפרמטר אחד | `?udm=2` חסום, `?q=x` מותר | זהה, ו-`?udm=21` מותר | **זהה** + התאמה מדויקת |
| Q4 שני פרמטרים | לא נבדק (ה-`&` אבד) | חסום בשני הסדרים | **חדש** |
| Q4 google.com | לא נבדק | חסום ל-`udm=2`; הלשונית "תמונות" נחסמת גם | **חדש** |
| Q5 דף החסימה | טקסט קבוע, "The person who set up this computer has chosen to block this site", "Try contacting the system admin", קוד `ERR_BLOCKED_BY_ADMINISTRATOR`, כפתור "Details" | טקסט אחר: "Your organization doesn’t allow you to view this site", בלי קוד שגיאה ובלי "Details" שנראו; בלי כפתור בקשה | **שונה** |
| Q5 `chrome://policy` | **לא נבדק** (ה-intent נדחה; A0.3 לא הגיע לדף המדיניות) | מופיעות שורות URLBlocklist/URLAllowlist, Platform | **חדש**. ב-91 לא נבדק, לא "לא נמצא" |
| Q5 מדיניות לטקסט/כפתור | **לא נבדק** | לא נמצאה ברשימה של 270 השמות | **חדש** |
| Q7 SPA ב-WebView | `doUpdateVisitedHistory` ו-hook בלבד (סינון ספירות) | זהה; ספירות מהלוג הלא מסונן | אין שינוי |
| Q8 דף חסימה מקומי | מוצג, `REQUEST` נרשם | זהה; גם חסימת `/watch` ב-YouTube עובדת | **נוסף** |
| Chrome SPA ב-YouTube | **לא נבדק** | כלל `/watch` חסם ניווט ישיר אבל **לא** לחיצה פנימית | **חדש** |
| example.com HTTPS | "Privacy error" ב-91 | לא נבדק ישירות; **google.com נטען תקין** | לא ברור אם ההבדל נובע מהגרסה או מהסביבה |

## 3. השוואה: Chrome מנוהל מול WebView משלנו, לפי הסינון שאנחנו צריכים

השאלה: איזו דרך מכסה את הסינון שאנחנו צריכים. **הספייק לא מכריע.** להלן מה שנצפה בלבד; מה שלא נבדק מסומן כך.

| צורך | Chrome מנוהל (145) | WebView משלנו (145) |
|---|---|---|
| חסימת ניווט לפי URL, כולל נתיב ושאילתה | **נצפה:** התאמה מדויקת לנתיב ולפרמטר (כולל `udm=2` בכל סדר, וגם בתוך ה-URL של הלשונית "תמונות" בגוגל), מדיניות Platform, אכיפה תוך כחצי שנייה | **לפי הקוד שלנו (לא נבדק כאן).** ה-callbacks רואים את ה-URL |
| ניווט SPA (YouTube, לחיצה פנימית) | **לא נחסם.** הכלל נאכף רק לניווט מלא | **נראה** ב-`doUpdateVisitedHistory` ו-hook JS, **אחרי** שהתחיל. חסימה הצליחה תוך ~20 ms, לא לפני |
| משאבים בתוך דף (iframe, img, script) | **נצפה:** iframe מ-origin חסום נחסם; img ו-script מאותו origin **נטענים** | **נצפה:** ה-callbacks רואים את כל המשאבים (`main=false`). **חסימה לפי משאב לא נבדקה** |
| דף חסימה עם טקסט וכפתור "בקש גישה" | **לא נמצאה מדיניות לכך** ברשימה של 270 השמות (Q5). דף החסימה קבוע | **נבדק** (כפתור נרשם ב-bridge) |
| מי מכריע על הכלל | מדיניות של המערכת (Device Owner); השרת על Chrome | האפליקציה שלנו |
| תלות בגרסה | **לא נבדק** (אין השוואה מסודרת לרשימת 91) | **לא נבדק** |
| הוכחה שהכלל נאכף | מסך `chrome://policy` מראה שהמדיניות מוגדרת. האכיפה נמדדה במקרי הניווט עצמם, לא בדף המדיניות | לוג משלנו |
| דפדפן אחר | **לא נבדק** | **לא נבדק** |

**לאן הראיות נוטות (לא החלטה):**
- **בעד Chrome:** ניווט מלא לפי URL ושאילתה נאכף מהר ומדויק, בלי קוד מאיתנו. אבל הוא **לא עוצר ניווט SPA פנימי** כמו לחיצה על סרטון, וזה הסינון העיקרי ב-YouTube.
- **בעד WebView משלנו:** רואים את ה-SPA ואת כל המשאבים, ויש דף חסימה עם בקשה. אבל ה-SPA נחסם **אחרי** שהתחיל (20 ms אחרי `pushState`), וה-WebView שלנו הוא הדפדפן היחיד רק אם החלטנו כך.
- כלומר: אף אחת מהדרכים לא מכסה לבד את כל הצרכים בנתונים האלה. ההכרעה תלויה במה שחשוב יותר לכם (דף חסימה עם בקשה ומשאבים, או אכיפה מלאה על הניווטים של הדפדפן עצמו).

## 4. סייגים לנתונים

- **Q1:** מדד על שעון המארח, כולל round trip של adb (לא מדידה של הזמן הפנימי של המכשיר). ניווט subframe בדף פתוח, לא ניווט top-level ולא מצב cold, והמצב cold לא חזר. חישוב הגבול: הניסוי רושם את ההיענות האחרונה לפני החסימה. בריצות v2 לא נלכד מונה `n=` (הגרפ חיפש `n=` והדף הציג `stopped n=…`), לכן "הפולינג נמשך אחרי n=11" הוא **הסקה** מקצב של 500 ms ומהריצה הראשונה (v1, n עד 30), לא נתון ישיר.
- **Q3:** נצפה בתצפית (שורות שרת, צילום מסך). **fetch/XHR לא נבדק.** שתי ריצות תקפות זהות. תוצאת ה-iframe מסתמכת על צילום מסך ועל היעדר שורת שרת, לא על `iframe=load` (ה-load נורה גם לדף השגיאה).
- **Q5 (דף חסימה):** ב-91 היו קוד השגיאה, הכפתור והניסוח האחר. ב-145 לא נראו קוד שגיאה או כפתור "Details" בעץ הנגישות או בצילום; לכן "לא נראו", לא "אין".
- **Q5 (רשימת המדיניות):** הרשימה נאספה בגלילה של 300 px בכל פעם, ולא נבדק חפיפה בין מסכים. הכיסוי נבדק בשתי דרכים: (א) חמש מדיניות סינון ידועות נמצאו בשמותיהן, (ב) ההתפלגות לפי אות נראית סבירה. זו הוכחה **לרשימה שנאספה**, לא לאיסוף מלא ב-100%.
- **Q7:** הקליק הוא `element.click()` מתוך הסקריפט, **לא מחווה של משתמש**. ה-hook הוזרק ב-`onPageStarted`/`onPageFinished` (לא ב-document start), ולכן `pushState` שנקרא לפני ההזרקה לא נלכד (לא נראה כזה במקרה הזה).
- **Q8 (YouTube):** רק נראה שדף החסימה הוחלף. **לא נבדק** אם הווידאו המשיך להתנגן מאחורי דף החסימה. בלוג לא נרשמה שום בקשת `videoplayback` בחלון (ספירה: 0), ולכן זה לא נתון על ההשמעה.
- **Chrome/YouTube:** המקרה הישיר והמקרה של לחיצה נבדקו פעם אחת כל אחד. לא נבדקו ערוצים/סרטונים נוספים, ולא השמעה.
- **`force-stop`:** נבדק על אפליקציית ה-DO: `am force-stop` מחזיר 0, וה-pid נשאר (ראו [49-force-stop-with-do.txt](evidence/49-force-stop-with-do.txt)). בדיקה חוזרת אחרי הסרת ה-DO מופיעה בסעיף 8.

## 5. מה לא נבדק ומה נשאר

- W (תת-דומיין): נדרש שם מארח, לא IP.
- fetch/XHR מתוך דף מותר; חסימה לפי משאב (סוג משאב מסוים) ב-WebView.
- השמעה מאחורי דף החסימה (Q8).
- מדיניות אחרי הפעלה מחדש של Chrome.
- מכשיר אמיתי ו-Chrome/WebView בגרסאות אחרות.
- דפדפן אחר (לא נבדק בשתי הדרכים).
- Q6 (כניסה לגוגל) מחוץ לתחום.

## 6. דלתא ותיקונים בזמן הניסוי (לא לציטוט)

- `*` לא מוגש ל-adb בלי גרשיים: הריצה נפסלה ונרשמה ב-[invalid/](evidence/invalid/) (`adb_keys`).
- ריצות Q1 v1 נפסלו כי לשוניות ישנות המשיכו לפנות לשרת; מוגדר פרוטוקול v2 עם מזהה ריצה.
- ה-selector של YouTube עם גרשיים נשבר בשורת הפקודה של המכשיר: הוחלף ב-`a[href*=watch]`.
- הקובצים [14](evidence/14-webview-q8-screen.xml), [15](evidence/15-webview-q8-logcat-full.txt) ו-[25](evidence/25-yt-q8-deny-watch-logcat-full.txt) נדרסו בריצות חוזרות אחרי כשלי כלים (לא תוצאות). התוצאות שמצוטטות הן מהריצות החוזרות.

## 7. קבצים ומבנה

- קוד: [kotlin/](kotlin/) (אפליקציית DPC + WebView). ה-APK נבנה עם `--offline`.
- שרת בדיקה: [tools/serve.js](tools/serve.js); מקרה בודד: [tools/run-case.sh](tools/run-case.sh); ספריית עזר: [tools/lib.sh](tools/lib.sh); Q1: [tools/q1-run.sh](tools/q1-run.sh), [tools/q1-analyze.py](tools/q1-analyze.py).
- דפי בדיקה: [testpages/](testpages/).
- ראיות: [evidence/](evidence/) (כל הפלט הגולמי לפי סדר). [STATE.md](STATE.md) הוא מסמך מסירה מהשלב הקודם.

## 8. מצב הסביבה בסוף הריצה

הניקוי בוצע לפי הסדר, והמצב נרשם לפני סגירת האמולטור ([90-final-state.txt](evidence/90-final-state.txt), [50-cleanup-step1-2.txt](evidence/50-cleanup-step1-2.txt), [51-cleanup-step3-4.txt](evidence/51-cleanup-step3-4.txt)):

- **מדיניות Chrome:** `chrome_clear` נשלח בזמן שה-Device Owner היה פעיל, ו-`chrome://policy` הציג "No policies set" ([50-cleanup-policy-after-clear.xml](evidence/50-cleanup-policy-after-clear.xml)).
- **גרסאות בסוף:** Chrome `145.0.7632.218`, WebView `145.0.7632.218`. זהות לתחילת הריצה.
- **Device Owner הוסר** (`dpm remove-active-admin`: Success). `dpm list-owners`: ריק.
- **בדיקת force-stop אחרי הסרת ה-DO:** `am force-stop` **הרג** את התהליך (pid 15953 נעלם). זה משלים את הממצא בסעיף 4: עם Device Owner ה-force-stop החזיר 0 ולא הרג את התהליך ([49-force-stop-with-do.txt](evidence/49-force-stop-with-do.txt)); בלעדיו הוא עובד. לכן ה-DO הוא שמונע את ה-force-stop, וזה נצפה על האפליקציה שלנו בלבד, לא על Chrome.
- **האפליקציה הוסרה** (`adb uninstall`: Success). `pm list packages` מוצא 0 חבילות a03b. `/data/local/tmp/chrome-command-line` נמחק. `am clear-debug-app` נשלח, אך **אין אימות** שההגדרה נוקתה (הפלט ריק).
- **שרתי הבדיקה** נעצרו (PID 21332, אומת כ-`node tools/serve.js` לפני העצירה). הפורטים 8130 ו-8131 פנויים.
- **חשבונות:** 0. **משתמשים:** משתמש 0 בלבד. `device_provisioned=1`, `user_setup_complete=1`.
- **הגדרות שאנחנו שינינו ולא שחזרנו:** `screen_off_timeout` נקבע ל-`2147483647` ו-`svc power stayon` הופעל בתחילת הריצה. **הערך המקורי לא נרשם**, ולכן לא שוחזר; זה אמולטור שנסגר, ולא נשמר שום שינוי מעבר לו.
- **האמולטור `emulator-5580` נסגר** (`emu kill`: OK). **`emulator-5590` נשאר** (סשן אחר; לא נגענו בו).
- **טרמינל:** נסגרו הכרטיסיות c1–c4.
- **לא נבדק אחרי הניקוי:** דבר שדורש את המכשיר (האמולטור נסגר).
