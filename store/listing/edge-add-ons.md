# Microsoft Edge Add-ons — WebFilter 0.1.0 (spike S1)

Copy-paste text for [Partner Center → Microsoft Edge](https://partner.microsoft.com/dashboard/microsoftedge/overview), in the order of its pages.
Every text is given in English and Hebrew. The primary listing language is **English** (the package's `default_locale`); Hebrew is an optional second listing.

Fields that only the certification team reads (notes for certification): paste the **English** text.

This file describes **only version 0.1.0** (the first submission, kept for the record). The texts for the full version are in [full-product.md](full-product.md).

---

## 1. Packages

| | |
|---|---|
| File to upload | `store/dist/webfilter-s1-0.1.0.zip` (build: `store/build-s1.ps1`) |
| Name | `WebFilter` — read from the manifest |
| Version | `0.1.0` |

---

## 2. Availability

| Field | Value |
|---|---|
| Visibility | **Hidden** — not discoverable in the store; installed only by its direct link or by policy |
| Markets | All markets |

---

## 3. Properties

| Field | Value |
|---|---|
| Category | **Productivity** |
| Does your extension access, collect or transmit personal information? | **No** (0.1.0 processes no data) |
| Privacy policy URL | Optional for 0.1.0. If `store/privacy-policy/index.html` is already hosted, fill in its public address |
| Website URL | Optional — leave empty, or the address where you host the privacy policy |
| Support contact details | Your support email (the same one you put in the privacy policy) |
| Mature content | **No** |

---

## 4. Store listings

Add a listing for **English**; optionally also for **Hebrew** (Edge offers the languages declared in the package: `en`, `he`).

### Description (required, 250–10,000 characters)

**English** (756 characters)

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

**Hebrew** (573 characters)

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

### Short description (if the form shows this field)

Same text as the manifest description:

- **en**

  ```text
  Shows whether WebFilter was installed by your computer's browser policy. Part of the WebFilter family web filter for home PCs.
  ```

- **he**

  ```text
  מציג אם WebFilter הותקן על ידי מדיניות הדפדפן במחשב. חלק ממסנן הגלישה המשפחתי WebFilter למחשבים ביתיים.
  ```

### Images

| Field | File (en listing) | File (he listing) | Required |
|---|---|---|---|
| Extension Store logo (300×300) | `store/assets/edge-logo-300x300.png` | same | yes |
| Small promotional tile (440×280) | `store/assets/promo-small-440x280-en.png` | `store/assets/promo-small-440x280-he.png` | no, but recommended |
| Large promotional tile (1400×560) | — | — | no, leave empty |
| Screenshots (1280×800) | `store/assets/screenshot-1280x800-en.png` | `store/assets/screenshot-1280x800-he.png` | no, but recommended |
| YouTube video URL | — | — | no |

### Search terms

Leave empty for 0.1.0: the item is hidden, and terms like "parental control" would describe features this version does not have.

---

## 5. Submit → Notes for certification

**English** (paste this one)

```text
This is a minimal build (0.1.0) of the browser component of WebFilter, a family web filtering product for Windows home computers.

Purpose of this build: to obtain a stable extension ID and to verify that the extension can be force-installed through browser policy (ExtensionInstallForcelist) on home computers that are not joined to a domain. That is why the visibility is Hidden: the extension is meant to be installed by the family's own WebFilter installation, not found in search.

What it does: the toolbar popup shows whether the extension was installed by policy (chrome.management.getSelf().installType === "admin"), the extension ID, the version and whether the user can turn it off. It declares no permissions and no host permissions, has no background script and no content scripts, makes no network requests, contains no remote code and collects no data.

How to test (no account needed): install the extension and click the toolbar button. A normal install shows "Not installed by policy" (install type "normal"); an install through ExtensionInstallForcelist shows "Installed by policy" (install type "admin"). The popup is in Hebrew when the browser language is Hebrew, and in English otherwise.

Later versions of this same item will add the filtering features (declarativeNetRequest rules and native messaging to the local WebFilter service), with an updated listing, privacy policy and justifications at that time.
```

**Hebrew** (for reference)

```text
זוהי גרסה מינימלית (0.1.0) של רכיב הדפדפן של WebFilter, מוצר לסינון גלישה משפחתי למחשבי Windows ביתיים.

מטרת הגרסה: לקבל מזהה תוסף קבוע ולבדוק שאפשר להתקין את התוסף בכפייה דרך מדיניות דפדפן (ExtensionInstallForcelist) במחשבים ביתיים שאינם מצורפים לדומיין. לכן הנראות היא Hidden: התוסף מיועד להתקנה על ידי התקנת WebFilter של המשפחה, ולא לחיפוש בחנות.

מה היא עושה: החלון הקופץ בסרגל הכלים מציג אם התוסף הותקן על ידי מדיניות (chrome.management.getSelf().installType === "admin"), את מזהה התוסף, את הגרסה ואם המשתמש יכול לכבות אותו. היא לא מבקשת הרשאות או הרשאות לאתרים, אין לה סקריפט רקע או content scripts, היא לא שולחת בקשות רשת, אין בה קוד מרוחק והיא לא אוספת מידע.

איך בודקים (בלי חשבון): מתקינים את התוסף ולוחצים על הכפתור בסרגל הכלים. בהתקנה רגילה מוצג "לא הותקן על ידי מדיניות" (סוג התקנה "normal"); בהתקנה דרך ExtensionInstallForcelist מוצג "הותקן על ידי מדיניות" (סוג התקנה "admin"). החלון בעברית כששפת הדפדפן עברית, ובאנגלית בכל שפה אחרת.

גרסאות מאוחרות יותר של אותו פריט יוסיפו את יכולות הסינון (כללי declarativeNetRequest ו-native messaging לשירות WebFilter המקומי), עם תיאור, מדיניות פרטיות והצדקות מעודכנים באותו שלב.
```

Then **Publish**. Certification can take up to 7 business days.
