# חומרי חנות — WebFilter 0.1.0 (ספייק S1)

> המטרה: לפרסם בשתי החנויות **תוסף מינימלי** (בלי הרשאות, כדי שהביקורת תהיה מהירה), לקבל **שני מזהי תוסף קבועים**, ואיתם להריץ את S1 במעבדה. הגרסה המלאה תעלה בהמשך **לאותו פריט** כעדכון, כך שהמזהים לא ישתנו ([LAB-AND-ACCOUNTS.md](../docs/LAB-AND-ACCOUNTS.md), חלק ב').
> כל ההתחברויות, ההעלאות והפרסום — שלך. לא העליתי ולא התחברתי לשום מקום.

## מה יש בתיקייה

| נתיב | מה זה |
|------|-------|
| `extension-s1/` | התוסף עצמו (גרסה 0.1.0): חלון קופץ שמציג אם התוסף הותקן על ידי מדיניות, את המזהה ואת הגרסה. עברית/אנגלית לפי שפת הדפדפן |
| `build-s1.ps1` | בונה את קובץ ה-ZIP להעלאה |
| `dist/webfilter-s1-0.1.0.zip` | הקובץ להעלאה (נוצר ע"י הסקריפט; לא נשמר ב-git) |
| `assets/` | התמונות לחנויות (אייקון, לוגו, צילומי מסך, אריח קידום) |
| `listing/chrome-web-store.md` | כל הטקסטים ל-Chrome Web Store, לפי סדר הלשוניות, באנגלית ובעברית |
| `listing/edge-add-ons.md` | כל הטקסטים ל-Edge Add-ons, לפי סדר הדפים, באנגלית ובעברית |
| `listing/future-full-product.md` | **לעתיד בלבד** — טיוטות להצדקת ההרשאות של הגרסה המלאה. לא להדביק עכשיו |
| `privacy-policy/index.html` | מדיניות פרטיות דו-לשונית, קובץ אחד בלי תלות חיצונית — אפשר לפרסם כמו שהוא |
| `tools/` | המקור של התמונות (HTML) והסקריפטים שמייצרים אותן ובודקים את החלון הקופץ |

## התמונות — איזה קובץ לאן

| קובץ | גודל | Chrome Web Store | Edge Add-ons |
|------|------|------------------|--------------|
| `assets/cws-icon-128x128.png` | 128×128 | Store icon | — |
| `assets/edge-logo-300x300.png` | 300×300 | — | Extension Store logo |
| `assets/screenshot-1280x800-en.png` | 1280×800 | Screenshot (רישום באנגלית) | Screenshot (רישום באנגלית) |
| `assets/screenshot-1280x800-he.png` | 1280×800 | Screenshot (רישום בעברית) | Screenshot (רישום בעברית) |
| `assets/promo-small-440x280-en.png` | 440×280 | Small promo tile (אנגלית) | Small promotional tile (אנגלית) |
| `assets/promo-small-440x280-he.png` | 440×280 | Small promo tile (עברית) | Small promotional tile (עברית) |

צילומי המסך מציגים את החלון הקופץ האמיתי במצב "הותקן על ידי מדיניות", עם מזהה **לדוגמה** (`kpml…`). אפשר לייצר אותם מחדש עם המזהה האמיתי (סעיף "עדכון תמונות" למטה) — לא חובה.

---

## שלב 0 — לבנות את קובץ ה-ZIP

ב-PowerShell, מתיקיית ה-repo (כרגע: `C:\Projests\WebFilterApp\wf-m1-store`):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File store\build-s1.ps1
```

התוצאה: `store\dist\webfilter-s1-0.1.0.zip`. כבר בניתי אותו פעם אחת בתיקייה הזו. הסקריפט נכשל אם מישהו הוסיף לתוסף הרשאה, `key` או קובץ חסר.

**בדיקה מהירה לפני העלאה (לא חובה):** ב-Edge ‏`edge://extensions` → Developer mode → **Load unpacked** → בחר את `store\extension-s1` → לחץ על כפתור התוסף. צפוי: "לא הותקן על ידי מדיניות", סוג התקנה `development`. אחר כך הסר אותו.

## שלב 1 — מדיניות הפרטיות

1. פתח את `store\privacy-policy\index.html` והחלף את `CONTACT_EMAIL_PLACEHOLDER` (מופיע **פעמיים** — בעברית ובאנגלית) בכתובת המייל ליצירת קשר. כדאי אותה כתובת שתזין כמייל תמיכה בשתי החנויות
2. החנויות צריכות **כתובת ציבורית (https)**. אפשרויות — לא ביצעתי אף אחת:
   - **GitHub Pages** — מאגר ציבורי **חדש וקטן** (למשל `webfilter-privacy`) שמכיל רק את `index.html` → ‏Settings → Pages → Deploy from a branch → ‏`main` / root. הכתובת: `https://<שם-משתמש>.github.io/webfilter-privacy/`. לא לפרסם דרך המאגר הראשי — Pages ממאגר פרטי דורש תוכנית בתשלום, וממילא אין סיבה לחשוף את הקוד
   - **Netlify Drop** (`app.netlify.com/drop`) או **Cloudflare Pages** — גוררים את התיקייה `privacy-policy` ומקבלים כתובת `*.netlify.app` / `*.pages.dev`
   - **Google Sites** — דף חדש עם הטקסט מודבק (העיצוב הולך לאיבוד, התוכן מספיק)
   - דומיין או אחסון סטטי שכבר יש לך
3. ל-0.1.0 השדה **אופציונלי** בשתי החנויות, כי התוסף לא אוסף מידע. אם עוד אין כתובת — אפשר להגיש בלעדיה ולהוסיף אחר כך. בגרסה המלאה הוא **חובה**

## שלב 2 — Chrome Web Store

הטקסטים: [`listing/chrome-web-store.md`](listing/chrome-web-store.md). בשדות שרק הבודקים קוראים — להדביק את הגרסה האנגלית.

1. [Developer Dashboard](https://chrome.google.com/webstore/devconsole) → **New item** → העלה את `store\dist\webfilter-s1-0.1.0.zip`
2. **רשום את מזהה הפריט** (32 אותיות a–p) — הוא מופיע מיד אחרי ההעלאה, בדף הפריט ובכתובת הדשבורד
3. **Store listing:**
   - Description ← "Description / English" (הסיכום הקצר נלקח מהחבילה ואינו ניתן לעריכה)
   - Category ← Lifestyle → Well-being · Language ← English
   - Store icon ← `assets\cws-icon-128x128.png` · Screenshots ← `assets\screenshot-1280x800-en.png` · Small promo tile ← `assets\promo-small-440x280-en.png`
   - Mature content ← No. שאר השדות — ריקים
4. **(לא חובה) רישום בעברית:** בבורר השפה של Store listing → הוסף **Hebrew** → Description ← "Description / Hebrew", ותמונות ה-`-he`
5. **Privacy practices:**
   - Single purpose ← הטקסט האנגלי
   - Permission justification ← לא אמור להופיע אף שדה (אין הרשאות)
   - Remote code ← **No, I am not using remote code**
   - Data usage ← **לא לסמן אף סוג מידע**, ולסמן את שלוש ההצהרות
   - Privacy policy URL ← הכתובת משלב 1, אם יש
6. **Distribution:** Free of charge · Visibility ← **Unlisted** · Regions ← All regions
7. **Test instructions** (לא חובה) ← הטקסט האנגלי
8. **Account:** ודא שמייל יצירת הקשר מאומת — בלי זה אי אפשר להגיש
9. **Submit for review** (השאר "לפרסם אוטומטית אחרי אישור" — הפריט ממילא לא מופיע בחיפוש)

## שלב 3 — Microsoft Edge Add-ons

הטקסטים: [`listing/edge-add-ons.md`](listing/edge-add-ons.md).

1. [Partner Center → Microsoft Edge](https://partner.microsoft.com/dashboard/microsoftedge/overview) → **Create new extension** → העלה את אותו `webfilter-s1-0.1.0.zip`
2. **Availability:** Visibility ← **Hidden** · Markets ← כל השווקים
3. **Properties:** Category ← Productivity · "Does your extension access, collect or transmit personal information?" ← **No** · Privacy policy URL ← אם יש · Support contact ← המייל שלך · Mature content ← No
4. **Store listings → English:** Description ← "Description / English" (חייב 250 תווים לפחות — הטקסט 756) · Extension Store logo ← `assets\edge-logo-300x300.png` · Small promotional tile ← `assets\promo-small-440x280-en.png` · Screenshots ← `assets\screenshot-1280x800-en.png` · Search terms ← ריק
5. **(לא חובה) Store listings → Hebrew:** אותו דבר עם הטקסט העברי (573 תווים) ותמונות ה-`-he`
6. **Submit:** Notes for certification ← הטקסט האנגלי → **Publish**. האישור לוקח עד 7 ימי עבודה

## שלב 4 — מה לשלוח לי

| מה | איפה מוצאים |
|----|-------------|
| **מזהה התוסף ב-Chrome** (32 אותיות a–p) | בדשבורד, בדף הפריט |
| **מזהה התוסף ב-Edge** (32 אותיות a–p) | Partner Center → התוסף → Extension overview. **שים לב:** יש שם גם **Product ID** (מזהה חנות, אותיות גדולות וספרות) — אני צריך את ה-**Extension ID** / CRX ID בן 32 האותיות. אם הוא מופיע רק אחרי האישור — שלח כשיופיע |
| **קישור לדף התוסף** בכל חנות | Chrome: ‏`https://chromewebstore.google.com/detail/<id>` · Edge: הקישור שמוצג ב-Partner Center אחרי הפרסום |
| מצב הביקורת בכל חנות | אושר / ממתין / נדחה — ואם נדחה, **העתק את נוסח ההודעה** |
| כתובת מדיניות הפרטיות | אם פרסמת |

**לא** סיסמאות ולא קודי אימות.

עם המזהים נגדיר במעבדה את `ExtensionInstallForcelist` — ‏`<id>;https://clients2.google.com/service/update2/crx` ל-Chrome ו-`<id>;https://edge.microsoft.com/extensionwebstorebase/v1/crx` ל-Edge — ואת `allowed_origins` של ה-native host.

## דגשים

- **לא** לשנות את הנראות ל-Public
- **לא** למחוק פריט ולפתוח חדש — המזהה ישתנה, והוא כבר יהיה רשום במדיניות וב-native host. הגרסה המלאה תעלה כ-**חבילה חדשה לאותו פריט** (Chrome: ‏Package → Upload new package; Edge: ‏Packages → Replace)
- אם יש דחייה — שלח לי את הנוסח, ואל תוסיף הרשאות או טקסטים על דעת עצמך
- שמות השדות בדשבורדים משתנים מדי פעם; אם משהו נראה אחרת מהמתואר כאן — התוכן הוא מה שחשוב

## עדכון תמונות (לא חובה)

```powershell
cd store\tools
npm install
npm run render
```

מייצר מחדש את אייקוני התוסף (`extension-s1\icons`) ואת כל התמונות ב-`assets` מקובצי ה-HTML ב-`tools\html`, בגדלים מדויקים, דרך Edge המותקן. לצילומי מסך עם המזהה האמיתי: `$env:WF_EXT_ID = "<id>"` לפני `npm run render`. אם האייקונים השתנו — לבנות שוב את ה-ZIP (שלב 0).

`npm run check` טוען את התוסף ל-Edge (בעברית ובאנגלית) ובודק שהחלון הקופץ עובד עם ה-API האמיתי.
