# Chrome Web Store — WebFilter 0.1.0 (spike S1)

Copy-paste text for the [Developer Dashboard](https://chrome.google.com/webstore/devconsole), in the order of its tabs.
Every text is given in English and Hebrew. The primary listing language is **English** (the package's `default_locale`); Hebrew is an optional localized listing.

Fields that only the reviewers read (single purpose, justifications, test instructions): paste the **English** text.

This file describes **only version 0.1.0** (the first submission, kept for the record). The texts for the full version are in [full-product.md](full-product.md).

---

## 1. Package

| | |
|---|---|
| File to upload | `store/dist/webfilter-s1-0.1.0.zip` (build: `store/build-s1.ps1`) |
| Name | `WebFilter` — read from the manifest, not editable in the dashboard |
| Version | `0.1.0` |

---

## 2. Store listing

### Summary (short description)

Read-only in the dashboard ("from package"): it comes from `extension-s1/_locales/*/messages.json` → `extDescription`. Limit 132 characters. To change it, change the messages file and rebuild the zip.

- **en** (126 characters):

  ```text
  Shows whether WebFilter was installed by your computer's browser policy. Part of the WebFilter family web filter for home PCs.
  ```

- **he** (103 characters):

  ```text
  מציג אם WebFilter הותקן על ידי מדיניות הדפדפן במחשב. חלק ממסנן הגלישה המשפחתי WebFilter למחשבים ביתיים.
  ```

### Description

**English**

```text
WebFilter is the browser part of WebFilter, a family web filter for Windows home computers. It is meant to be installed automatically through browser policy on the family's computers, not installed by hand.

What this version (0.1.0) does
Click the WebFilter button in the toolbar to see:
• whether the extension was installed by the computer's browser policy ("Installed by policy") or in another way
• the extension ID and version number
• whether the user can turn the extension off

That is all it does. This version requests no permissions, does not read or change any website, has no background activity, and does not collect, store or send any data.

Website filtering, browsing-time limits and YouTube channel rules will be added in later versions.
```

**Hebrew**

```text
WebFilter הוא רכיב הדפדפן של WebFilter, מסנן גלישה משפחתי למחשבי Windows ביתיים. התוסף מיועד להתקנה אוטומטית דרך מדיניות הדפדפן במחשבי המשפחה, ולא להתקנה ידנית.

מה הגרסה הזו (0.1.0) עושה
לחיצה על כפתור WebFilter בסרגל הכלים מציגה:
• אם התוסף הותקן על ידי מדיניות הדפדפן במחשב ("הותקן על ידי מדיניות") או בדרך אחרת
• את מזהה התוסף ואת מספר הגרסה
• אם המשתמש יכול לכבות את התוסף

זה כל מה שהיא עושה. הגרסה הזו לא מבקשת הרשאות, לא קוראת ולא משנה אף אתר, לא פועלת ברקע, ולא אוספת, שומרת או שולחת מידע.

סינון אתרים, הגבלת זמן גלישה וכללים לערוצי YouTube יתווספו בגרסאות הבאות.
```

### Category

**Lifestyle → Well-being** (fits the full product too: filtering and browsing-time limits for children, so it will not need to change).
Alternative: **Make Chrome Yours → Privacy & Security**. If the dashboard's list differs, pick the closest; for an unlisted item it has little effect.

### Language

English (primary). Add **Hebrew** as a second listing language if you want the Hebrew texts and images (optional).

### Graphic assets

| Dashboard field | File (en listing) | File (he listing) | Required |
|---|---|---|---|
| Store icon (128×128) | `store/assets/cws-icon-128x128.png` | same | yes |
| Screenshots (1280×800) | `store/assets/screenshot-1280x800-en.png` | `store/assets/screenshot-1280x800-he.png` | at least 1 |
| Small promo tile (440×280) | `store/assets/promo-small-440x280-en.png` | `store/assets/promo-small-440x280-he.png` | upload it |
| Marquee promo tile (1400×560) | — | — | no, leave empty |
| Global promo video | — | — | no |

### Additional fields

| Field | Value |
|---|---|
| Official URL | leave empty (needs a verified site) |
| Homepage URL | leave empty, or the address where you host the privacy policy |
| Support URL | leave empty for now |
| Mature content | **No** |

---

## 3. Privacy practices

### Single purpose description

**English** (paste this one)

```text
WebFilter is the browser component of the WebFilter family web filter for Windows home computers. Version 0.1.0 has one narrow purpose: to show, in its toolbar popup, whether the extension was force-installed by the computer's browser policy, together with its extension ID and version. It declares no permissions and processes no user data.
```

**Hebrew** (for reference)

```text
WebFilter הוא רכיב הדפדפן של מסנן הגלישה המשפחתי WebFilter למחשבי Windows ביתיים. לגרסה 0.1.0 יש מטרה אחת מצומצמת: להציג בחלון הקופץ שלה אם התוסף הותקן בכפייה על ידי מדיניות הדפדפן במחשב, יחד עם מזהה התוסף והגרסה. היא לא מבקשת הרשאות ולא מעבדת מידע של המשתמש.
```

### Permission justification

**None needed.** The manifest declares no permissions, no host permissions, no content scripts and no background script, so the dashboard should not show any justification field. The popup uses `chrome.management.getSelf()`, `chrome.runtime` and `chrome.i18n`, which need no permission.

If the dashboard still asks for a justification anywhere, paste:

```text
No permissions are requested. The popup only calls chrome.management.getSelf(), chrome.runtime and chrome.i18n, which do not require any permission.
```

```text
לא מתבקשות הרשאות. החלון הקופץ משתמש רק ב-chrome.management.getSelf(), ב-chrome.runtime וב-chrome.i18n, שאינם דורשים הרשאה.
```

### Are you using remote code?

**No, I am not using remote code.** (All code is in the package: `popup.js` only.)

### Data usage

| Question | Answer for 0.1.0 |
|---|---|
| What user data do you plan to collect from users now or in the future? | **Check nothing.** 0.1.0 collects nothing. (The full product will need new answers — see the future file.) |
| I do not sell or transfer user data to third parties, outside of the approved use cases | ✔ check |
| I do not use or transfer user data for purposes that are unrelated to my item's single purpose | ✔ check |
| I do not use or transfer user data to determine creditworthiness or for lending purposes | ✔ check |

### Privacy policy URL

Optional for 0.1.0 (it collects no data), required for the full product. If the policy (`store/privacy-policy/index.html`) is already hosted, fill in its public address now; otherwise leave it empty.

---

## 4. Distribution

| Field | Value |
|---|---|
| Payments | Free of charge |
| Visibility | **Unlisted** — not in search; installed only by link or by policy |
| Distribution (regions) | All regions |

---

## 5. Test instructions (optional tab)

Paste the English text; no test account is needed.

**English**

```text
No account or setup is needed.
1. Install the extension and click the WebFilter button in the toolbar.
2. The popup shows "Not installed by policy" (install type "normal"), the extension ID and version 0.1.0.
3. When the same extension is installed through the ExtensionInstallForcelist browser policy, the popup shows "Installed by policy" (install type "admin").
The popup is in Hebrew when the browser language is Hebrew, and in English otherwise.

Context: this minimal build is used to verify policy force-installation on non-domain home computers for a family web filtering product. Filtering features will be submitted later as updates to this item, with their permissions justified at that time.
```

**Hebrew** (for reference)

```text
אין צורך בחשבון או בהגדרה.
1. מתקינים את התוסף ולוחצים על כפתור WebFilter בסרגל הכלים.
2. החלון הקופץ מציג "לא הותקן על ידי מדיניות" (סוג התקנה "normal"), את מזהה התוסף ואת הגרסה 0.1.0.
3. כשאותו תוסף מותקן דרך מדיניות הדפדפן ExtensionInstallForcelist, החלון מציג "הותקן על ידי מדיניות" (סוג התקנה "admin").
החלון בעברית כששפת הדפדפן עברית, ובאנגלית בכל שפה אחרת.

רקע: הגרסה המינימלית הזו משמשת לבדיקת התקנה כפויה דרך מדיניות במחשבים ביתיים שאינם בדומיין, עבור מוצר לסינון גלישה משפחתי. יכולות הסינון יוגשו בהמשך כעדכונים לאותו פריט, עם הצדקה להרשאות שלהן באותו שלב.
```

---

## 6. Submit

**Submit for review**. Leave "publish automatically after it passes review" on — the item is unlisted, so publishing only makes it installable by ID/link.
