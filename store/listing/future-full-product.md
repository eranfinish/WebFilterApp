# FUTURE — for the full product later (not for 0.1.0)

> **Do not paste anything from this file into the 0.1.0 submission.** Reviewers reject listings and justifications for permissions the package does not request.
> These are drafts for the version that adds filtering (planned for M3, see [MVP-PLAN §4.4](../../docs/MVP-PLAN.md)). Review them against the real manifest before use.

## How the full product reaches the stores

- It is uploaded as a **new version of the same item** in each store (e.g. `1.0.0`), never as a new item — so the two extension IDs, which the native host manifest and the policies depend on, stay the same.
- Broad host permissions (`<all_urls>`) and `nativeMessaging` mean an in-depth review: expect days to weeks, and keep the extension stable (logic lives in the service, MVP-PLAN §14).
- With the update, also change: the store description and images (new taglines; re-render with `store/tools`), the single-purpose text, the data-usage answers (below), the Edge "personal information" answer (**Yes**), and the privacy policy (remove the "version 0.1.0" section). The privacy policy URL becomes required in both stores.
- Extensions force-installed by policy normally receive new permissions without a prompt or being disabled. Verify this in the lab when the update ships.
- The package includes `rulesets/ads.json`, the ad-server ruleset generated from EasyList (CC BY-SA 4.0), with its notice `rulesets/ads-notice.txt`; both must stay in the zip. Build the zip from a fresh `npm run build`: a browser that loaded `dist/` unpacked writes its ruleset index to `dist/_metadata/`.
- Review risk: reviewers install the extension without the Windows program. If the static fail-closed ruleset then blocks every page, the item may be rejected as "breaks browsing". Worth deciding in M3 whether fail-closed applies only when `installType === "admin"` (a store install by hand can never be a child's managed install), and saying so in the notes below.

---

## Single purpose (draft)

**English**

```text
WebFilter enforces the website, YouTube, ad-blocking and browsing-time rules that parents set for their children on the family's Windows computers. All filtering decisions are made on the computer, with the rules received from the WebFilter service installed on the same computer.
```

**Hebrew**

```text
WebFilter אוכף את הכללים לאתרים, ל-YouTube, לחסימת פרסומות ולזמן הגלישה שההורים קבעו לילדיהם במחשבי Windows של המשפחה. כל החלטות הסינון מתקבלות במחשב עצמו, לפי הכללים שמגיעים משירות WebFilter שמותקן באותו מחשב.
```

---

## Permission justifications (draft)

### nativeMessaging

```text
Connects to com.webfilter.host, the native messaging host installed by the WebFilter Windows program on the same computer. Through it the extension receives the rules that the parents set for the signed-in Windows user, and sends block events, access requests and active browsing minutes to the local WebFilter service. The open port also keeps the service worker running so that rules are enforced continuously. This channel stays on the computer; the extension itself sends nothing over the network.
```

```text
מתחבר ל-com.webfilter.host, ה-native messaging host שתוכנת WebFilter ל-Windows מתקינה באותו מחשב. דרכו התוסף מקבל את הכללים שההורים קבעו למשתמש Windows המחובר, ושולח לשירות WebFilter המקומי אירועי חסימה, בקשות גישה ודקות גלישה פעילות. ה-port הפתוח גם שומר את ה-service worker פעיל, כך שהכללים נאכפים ברציפות. הערוץ הזה נשאר בתוך המחשב; התוסף עצמו לא שולח דבר ברשת.
```

### declarativeNetRequest

```text
Enforces the parents' rules: navigations of top-level pages and frames to sites that are not allowed for this child are blocked and redirected to the extension's own block page, which shows the reason and a "request access" button. A static ruleset blocks navigation until the local WebFilter service confirms the user's profile (fail-closed), and session rules block browsing when the child's time is over. These rules filter only main_frame and sub_frame requests. When the parents turn on ad blocking for a child (on by default for children and teens), a second static ruleset, disabled by default and generated from EasyList, blocks the requests that pages make to known ad servers (scripts, images, frames and other resources); it never blocks a page itself and reports nothing.
```

```text
אוכף את כללי ההורים: ניווט של דפים ומסגרות (frames) לאתרים שאינם מותרים לילד נחסם ומופנה לדף החסימה של התוסף, שמציג את הסיבה וכפתור "בקש גישה". ruleset סטטי חוסם ניווט עד ששירות WebFilter המקומי מאשר את פרופיל המשתמש (fail-closed), וכללי session חוסמים גלישה כשהזמן של הילד נגמר. הכללים האלה מסננים רק בקשות main_frame ו-sub_frame. כשההורים מפעילים חסימת פרסומות לילד (פעילה כברירת מחדל לילדים ולמתבגרים), ruleset סטטי שני, כבוי כברירת מחדל ונוצר מ-EasyList, חוסם בקשות שדפים שולחים לשרתי פרסום מוכרים (סקריפטים, תמונות, מסגרות ומשאבים אחרים); הוא אף פעם לא חוסם דף עצמו ולא מדווח על דבר.
```

### Host permissions: `<all_urls>`

```text
Parents choose which sites are allowed or blocked for each child, so the rules must be able to apply to any site, and redirecting a blocked navigation to the block page requires host access to that address. The extension does not read or change the content of web pages, except on YouTube (see content scripts).
```

```text
ההורים בוחרים אילו אתרים מותרים או חסומים לכל ילד, ולכן הכללים צריכים לחול על כל אתר, והפניית ניווט חסום לדף החסימה דורשת הרשאה לכתובת הזו. התוסף לא קורא ולא משנה את תוכן דפי האינטרנט, חוץ מ-YouTube (ראו content scripts).
```

### storage

```text
Keeps small working state in extension storage: which time warnings were already shown today, the YouTube mode and channel lists the YouTube page script needs as the page starts, and (for the browser session only) the address a blocked tab was trying to open, so the block page can offer a request for it. Filtering rules are not kept: after the browser or the service worker restarts, browsing stays blocked until the local service answers. Nothing in storage leaves the computer.
```

```text
שומר מצב עבודה קטן באחסון של התוסף: אילו אזהרות זמן כבר הוצגו היום, מצב YouTube ורשימות הערוצים שסקריפט הדף של YouTube צריך כשהדף נטען, ו(לזמן ההפעלה של הדפדפן בלבד) הכתובת שלשונית חסומה ניסתה לפתוח, כדי שמסך החסימה יוכל להציע בקשה עבורה. כללי הסינון לא נשמרים: אחרי הפעלה מחדש של הדפדפן או של ה-service worker הגלישה חסומה עד שהשירות המקומי עונה. דבר מהאחסון לא יוצא מהמחשב.
```

### notifications

```text
Warns the child 10, 5 and 1 minutes before the daily browsing time ends, and tells the child when a parent has answered an access request.
```

```text
מזהיר את הילד 10, 5 ו-1 דקות לפני שזמן הגלישה היומי נגמר, ומודיע לו כשההורה ענה על בקשת גישה.
```

### idle

```text
Detects whether the user is active or idle, so that only active browsing minutes count toward the daily limit that the parents set.
```

```text
מזהה אם המשתמש פעיל או לא פעיל, כדי שרק דקות גלישה פעילות ייספרו במכסה היומית שההורים קבעו.
```

### tabs

```text
Checks whether a website tab is open in the focused window, for counting active browsing time, and re-evaluates open tabs when the rules change: for example, it reloads the block page after a parent approves a request, and blocks open tabs when the browsing time ends.
```

```text
בודק אם לשונית של אתר פתוחה בחלון הפעיל, לצורך ספירת זמן גלישה פעיל, ובודק מחדש לשוניות פתוחות כשהכללים משתנים: למשל, טוען מחדש את דף החסימה אחרי שההורה אישר בקשה, וחוסם לשוניות פתוחות כשזמן הגלישה נגמר.
```

### webNavigation

```text
Detects navigations that change the address without loading a new page (history API navigations in single-page sites), which network rules do not see, so that a blocked address is blocked even when the site navigates to it in place, and block events record the address the child actually opened.
```

```text
מזהה ניווטים שמשנים את הכתובת בלי לטעון דף חדש (ניווט history API באתרי single-page), שכללי הרשת לא רואים, כדי שכתובת חסומה תיחסם גם כשהאתר מנווט אליה בלי טעינה, ושאירועי החסימה ירשמו את הכתובת שהילד פתח בפועל.
```

### Content scripts on YouTube

Matches: `*://*.youtube.com/*`, `*://*.youtube-nocookie.com/*` (`run_at: document_start`, `all_frames: true`, one script in the `MAIN` world).

```text
On YouTube pages only (youtube.com, and YouTube players embedded in other sites through youtube.com or youtube-nocookie.com), a content script identifies the channel of the video, Short, channel page or embedded player being shown and applies the parents' YouTube rules: allow it, block it, or send the home page and search to the child's page of approved channels. It hides the page until the decision is made and hides recommendations on allowed videos. A script in the page's main world is needed to read the player's own data, which identifies the channel reliably after in-page navigation. The channel ID is compared with the list stored on the computer; the decision is local.
```

```text
רק בדפי YouTube (youtube.com, ונגני YouTube שמוטמעים באתרים אחרים דרך youtube.com או youtube-nocookie.com), content script מזהה את הערוץ של הסרטון, ה-Short, דף הערוץ או הנגן המוטמע שמוצג, ומחיל את כללי ה-YouTube של ההורים: מתיר, חוסם, או מפנה את דף הבית והחיפוש לדף הערוצים המאושרים של הילד. הוא מסתיר את הדף עד שמתקבלת החלטה, ומסתיר המלצות בסרטונים מותרים. סקריפט ב-main world של הדף נחוץ כדי לקרוא את הנתונים של הנגן עצמו, שמזהים את הערוץ באופן אמין גם אחרי ניווט בתוך האתר. מזהה הערוץ מושווה לרשימה ששמורה במחשב; ההחלטה מקומית.
```

### Remote code

**No.** All code ships in the package; rules are data (declarativeNetRequest rules and lists, including the static ad-server ruleset, generated from EasyList ahead of time and shipped in the package), not code.

---

## Data usage answers (draft, Chrome Web Store)

The extension handles data only to enforce the parents' rules; it goes to the WebFilter service on the same computer and, from there, to the family's own hub computer on the home network — never to the developer or third parties. Disclose conservatively:

| Category | Check? | Why |
|---|---|---|
| Web history | **Yes** | Addresses are evaluated locally; addresses of **blocked** pages and of access requests are sent to the local service and synced to the family's hub PC |
| Website content | **Yes** | The YouTube channel ID of the page being shown is read to apply channel rules |
| User activity | Decide at that time | Only focus / idle / "a web tab is open" signals for counting minutes — no clicks, keystrokes or mouse tracking. Check it if in doubt |
| Personally identifiable information, health, financial, authentication, personal communications, location | No | Not handled by the extension |

All three certifications (no sale/transfer, no unrelated use, no creditworthiness) remain true.

Edge "Does your extension access, collect or transmit personal information?" → **Yes**, with the privacy policy URL.

---

## Notes for certification (draft, Edge) / test instructions (Chrome)

```text
WebFilter enforces parents' rules on the family's Windows computers and works together with the WebFilter Windows program, which installs this extension through browser policy and provides the rules through native messaging (host com.webfilter.host). Without the program the extension cannot receive rules. [State what a reviewer will see in that case, according to the M3 fail-closed decision above, and how a reviewer can see the extension working: e.g. a test build of the Windows program and a test child account, or a video.]
```
