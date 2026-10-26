# The full extension (0.3.0, then 0.3.1): texts for the store updates

> For the update of the **existing** items (Chrome `lkbhmgdhaalikcmpmefffohinafbjfgh`, Edge `labgagpndiahmbeohjikogfdigebeidl`), not a new item.
> The step-by-step guide is in [../README.md](../README.md), "עדכון לגרסה המלאה". This file holds what to paste, field by field.
> Written against the real manifest of 0.3.0 (`store/build-full.ps1` fails if the permissions change, so this file and the package stay in step).
> Fields only reviewers read: paste the English text. Store listing (what parents see): English, and Hebrew in the Hebrew listing.

Privacy policy URL (both stores, now required): <https://gilded-zabaione-076cae.netlify.app/>, after deploying the updated `store/privacy-policy/index.html`.

---

## 1. Store listing: description

**English** (Chrome: Store listing → Description; Edge: Store listings → English → Description; 1,369 characters, Edge needs at least 250)

```text
WebFilter is the browser part of WebFilter, a family web filter for Windows home computers. Parents set the rules in the WebFilter app on the family's computer, and the extension applies them in Microsoft Edge and Google Chrome in each child's Windows account:

• Websites: only approved sites for young children, or blocked categories (adult content, gambling, proxies and more) for older children
• YouTube: Restricted Mode, approved channels only, and no Shorts if the parents choose; older children can search for channels and ask a parent to approve one
• Browsing time: a daily limit and allowed hours, with a warning before the time ends
• Jumps: a site cannot open another site in a new tab or send the child there by an automatic redirect
• Ads: requests to known ad servers are blocked (EasyList)
• Requests: a child can ask a parent to allow a blocked site or channel, and the parent answers from the WebFilter app

Parents get a baseline protection in their own accounts too: adult sites, jumps and ads are blocked and SafeSearch is on, and nothing about their browsing is recorded.

The extension works only together with the WebFilter program for Windows, which installs it through browser policy on the family's computers. Installed by hand, it stays inactive. All decisions are made on the computer; nothing is sent to the developer or to third parties.
```

**Hebrew** (Chrome: the Hebrew listing → Description; Edge: Store listings → Hebrew → Description; 1,104 characters)

```text
WebFilter הוא החלק של WebFilter בדפדפן. WebFilter הוא מסנן גלישה משפחתי למחשבי Windows ביתיים: ההורים קובעים את הכללים בתוכנת WebFilter במחשב של המשפחה, והתוסף מחיל אותם ב-Microsoft Edge וב-Google Chrome, בחשבון ה-Windows של כל ילד:

• אתרים: רק אתרים מאושרים לילדים צעירים, או קטגוריות חסומות (תוכן למבוגרים, הימורים, פרוקסי ועוד) לילדים גדולים יותר
• YouTube: מצב מוגבל, רק ערוצים מאושרים, ובלי Shorts אם ההורים בוחרים; ילדים גדולים יותר יכולים לחפש ערוצים ולבקש מההורה לאשר אותם
• זמן גלישה: מכסה יומית ושעות מותרות, עם אזהרה לפני שהזמן נגמר
• קפיצות: אתר לא יכול לפתוח אתר אחר בלשונית חדשה או להעביר את הילד אליו בהפניה אוטומטית
• פרסומות: בקשות לשרתי פרסום מוכרים נחסמות (EasyList)
• בקשות: ילד יכול לבקש מההורה להתיר אתר או ערוץ חסום, וההורה עונה מתוכנת WebFilter

גם ההורים מקבלים הגנה בסיסית בחשבונות שלהם: אתרים למבוגרים, קפיצות ופרסומות נחסמים ו-SafeSearch פעיל, ושום דבר על הגלישה שלהם לא נרשם.

התוסף פועל רק יחד עם תוכנת WebFilter ל-Windows, שמתקינה אותו דרך מדיניות הדפדפן במחשבי המשפחה. אם מתקינים אותו ידנית, הוא לא פעיל. כל ההחלטות מתקבלות במחשב עצמו; שום דבר לא נשלח למפתח או לצד שלישי.
```

The short summary under the name comes from the package (`_locales/*/messages.json`, `extDescription`) and is not edited in the dashboard.

---

## 2. Single purpose (Chrome: Privacy practices → Single purpose)

```text
WebFilter enforces in the browser the website, YouTube, browsing-time, ad-blocking and pop-up rules that parents set in the WebFilter program on the family's Windows computers, for their children and, as a baseline protection, for themselves. All filtering decisions are made on the computer, with the rules received from the WebFilter service installed on the same computer.
```

---

## 3. Permission justifications (Chrome: Privacy practices → one field per permission)

### nativeMessaging

```text
Connects to com.webfilter.host, the native messaging host installed by the WebFilter Windows program on the same computer. Through it the extension receives the rules the parents set for the signed-in Windows user, and, in children's accounts only, sends block events, access requests, active browsing minutes, "open anyway" choices and, only if the parents turned on the search log for that child, search terms, to the local WebFilter service. In the parents' own accounts it only receives the baseline rules and sends nothing about their browsing. The open port also keeps the service worker running so that rules are enforced continuously. This channel stays on the computer; the extension itself sends nothing over the network.
```

### declarativeNetRequest

```text
Enforces the parents' rules. Navigations of pages and frames to sites that are not allowed are redirected to the extension's own block page, which shows the reason and, for children, a "request access" button. A static ruleset blocks navigation until the local WebFilter service confirms the user's rules (fail-closed); session rules block browsing when a child's time is over, block YouTube Shorts when the parents chose so, and keep a tab that a site opened to another site on the block page (jump protection). These rules filter only main_frame and sub_frame requests. A second static ruleset, disabled by default and generated ahead of time from EasyList, is enabled when the parents turn on ad blocking: it blocks the requests pages make to known ad servers (scripts, images, frames and other resources); it never blocks a page itself and reports nothing.
```

### Host permission justification (the dashboard's single field, 897 characters)

Chrome's dashboard has **one** "Host permission justification" field (at most 1,000 characters), and it covers both
`<all_urls>` and the content scripts' match patterns. Paste this combined text there:

```text
<all_urls>: parents choose which sites are allowed or blocked for each child, and the adult-content and jump protections apply to any site, so the rules must be able to apply to every address, and redirecting a blocked navigation to the extension's block page requires host access to that address. The extension does not read or change the content of web pages, except on YouTube.

YouTube content scripts (youtube.com, youtube-nocookie.com): they identify the channel of the video, Short, channel page or embedded player being shown and apply the parents' YouTube rules: allow it, block it, block Shorts if the parents chose so, or send the home page and search to the child's approved channels. When the parents allow a child to search for channels, a search shows YouTube's channel results only (the scripts hide every other result) with an "approved" mark or an "ask for approval" button next to each channel. A script in the page's main world reads the player's own data, which identifies the channel reliably after in-page navigation, and the channel's title and handle, which name the channel in a request the child sends to the parents. The page is hidden until the decision is made, and the decision is local.
```

The two longer texts below are the same explanations in full, for reviewers' questions and the Edge notes.

### Host permission: `<all_urls>`

```text
Parents choose which sites are allowed or blocked for each child, and the adult-content and jump protections apply to any site, so the rules must be able to apply to every address, and redirecting a blocked navigation to the block page requires host access to that address. The extension does not read or change the content of web pages, except on YouTube (see the content scripts).
```

### storage

```text
Keeps a small working state in extension storage: which time warnings were already shown today, the YouTube mode and channel lists the YouTube page script needs as the page starts, and, for the browser session only, the address a blocked tab or frame was trying to open, so the block page can offer a request for it and go back there once allowed. Filtering rules are not kept: after the browser or the service worker restarts, browsing stays blocked until the local service answers. Nothing in storage leaves the computer.
```

### alarms

```text
A 30-second alarm keeps the extension connected to the local WebFilter service: if the browser stopped the service worker or the service restarted, it reconnects, and it sends the heartbeat that counts a child's active browsing minutes.
```

### notifications

```text
Warns the child 10, 5 and 1 minutes before the daily browsing time ends, and tells the child when a parent has answered an access request.
```

### idle

```text
Detects whether the user is active or idle, so that only active browsing minutes count toward the daily limit the parents set.
```

### tabs

```text
Checks whether a website tab is open in the focused window, for counting active browsing time, and acts on open tabs when the rules change: for example, it reloads the block page after a parent approves a request, moves open tabs to the time-up page when the browsing time ends, and sends a tab that a site opened to another site to the block page (jump protection).
```

### webNavigation

```text
Sees navigations that network rules do not: addresses changed in place by single-page sites (history API), so that a blocked address is blocked there too; a new tab or window opened by a page (onCreatedNavigationTarget) and automatic client-side redirects (onCommitted transition qualifiers), so that a site cannot send the child to another site by itself while the child's own link clicks keep working; the address a frame was about to load when the fail-closed rule blocked it, so it can be reopened once the rules arrive; and, only when the parents turned on the search log, the search results address from which the search terms are read.
```

### Content scripts on YouTube

Matches: `*://*.youtube.com/*`, `*://*.youtube-nocookie.com/*` (`run_at: document_start`, `all_frames: true`, one script in the `MAIN` world). If the dashboard asks about content scripts, or in the test instructions:

```text
On YouTube pages only (youtube.com, and YouTube players embedded in other sites through youtube.com or youtube-nocookie.com), a content script identifies the channel of the video, Short, channel page or embedded player being shown and applies the parents' YouTube rules: allow it, block it, or send the home page and search to the child's page of approved channels. When the parents allow a child to search for channels, a search shows YouTube's channel results only — the script hides every other result — and marks each channel "approved" or offers "ask for approval" next to it. When the parents blocked Shorts, it blocks Shorts pages and hides the Shorts shelf. It hides the page until the decision is made and hides recommendations on allowed videos. A script in the page's main world is needed to read the player's own data, which identifies the channel reliably after in-page navigation. The channel ID is compared with the list stored on the computer; the decision is local. The channel's title and handle are read only to name the channel in a request the child sends to the parents.
```

---

## 4. Remote code (Chrome: Privacy practices)

**No, I am not using remote code.** All code ships in the package. The rules are data (declarativeNetRequest rules and domain lists, including the static ad-server ruleset generated from EasyList and shipped in the package), not code.

---

## 5. Data usage (Chrome: Privacy practices → Data usage)

The extension handles data only to enforce the parents' rules. It goes to the WebFilter service on the same computer and, from there, to the family's own hub computer on the home network, never to the developer or to third parties. Disclose conservatively:

| Category | Check | Why |
|---|---|---|
| Web history | **Yes** | Addresses are checked on the computer; in children's accounts, the addresses of **blocked** pages and of access requests, and, only when the parents turned on the search log, search terms and the search page address, go to the local service and the family's hub |
| User activity | **Yes** | Focus, active/idle and "a website tab is open" signals, to count a child's active minutes. No clicks, keystrokes or mouse tracking |
| Website content | **Yes** | The YouTube channel ID of the page being shown is read to apply the channel rules; its title and handle, to name the channel in a request the child sends to the parents (through the local service) |
| Personally identifiable information, Health, Financial and payment, Authentication, Personal communications, Location | No | Not handled by the extension |

Tick all three certifications: not sold or transferred to third parties; not used or transferred for purposes unrelated to the item's single purpose; not used or transferred to determine creditworthiness or for lending.

---

## 6. Edge Partner Center: Properties

- Category: Productivity (unchanged)
- "Does your extension access, collect or transmit personal information?" → **Yes**
- Privacy policy URL → the URL above
- Website, Support contact → unchanged (`finish.software@gmail.com`)
- Mature content → No

---

## 7. Test instructions (Chrome: Test instructions) / Notes for certification (Edge: Submit)

```text
WebFilter is the browser part of a parental-control product for Windows. The WebFilter Windows program installs this extension on the family's computers through browser policy (ExtensionInstallForcelist) and gives it the parents' rules through native messaging (host com.webfilter.host, registered by the program).

Installed by hand from the store, the extension checks chrome.management.getSelf(): because installType is not "admin", it stays inactive. It blocks nothing, connects to nothing, and its popup says that it was not installed by policy. This is deliberate, so that a manual install can never break anyone's browsing.

When installed by policy on a child's account, it blocks sites according to the parents' rules and shows its own block page (blocked.html) with a "request access" button, blocks YouTube by channel on youtube.com (and, when the parents allow it, lets the child search for channels: YouTube's results page is restricted to its Channels filter and every video, Shorts and playlist result is hidden, so a child can find a channel and ask a parent to approve it), counts active browsing time, blocks a site from opening another site in a new tab or by an automatic redirect, and, when the parents turn it on, blocks requests to ad servers with a static ruleset generated from EasyList (rulesets/ads.json, notice in rulesets/ads-notice.txt). Nothing is sent over the network by the extension: all messages go to the local program through native messaging.

The code is not minified. We can provide a screen recording of the extension working with the Windows program on request.
```

---

## 8. Images (optional)

The screenshots and tiles in `store/assets` show the 0.1.0 popup. They can stay for this update; new ones of the block page and the popup can be made later with `store/tools`.
