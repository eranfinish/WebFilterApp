# Protection features — shared spec (2026-10-22)

Decided with the user after the first lab install. Implemented in parallel by several workstreams; this file is the contract
between them. Wire names are exact. Code, identifiers and comments in English; user-facing text in Hebrew (`Strings.resx`)
and English (`Strings.en.resx`), and the extension's `_locales`.

## 1. Member settings (per family member)

New per-member flags. Bundle: optional properties on `MemberDto` (default `false` when missing, so older bundles load).
Core: properties on `FamilyMember` with the same defaults. Admin: toggles on the member page; the setup wizard sets the
role defaults below.

| Wire name | Meaning | Child default | Teen default | Adult |
|---|---|---|---|---|
| `blockImageSearch` | Image search pages are blocked (§4) | true | false | — |
| `jumpGuard` | A site may not open another site in a new tab/window or by an automatic redirect (§7) | true | true | — |
| `blockYouTubeShorts` | YouTube `/shorts/` is blocked (§8) | true | false | — |
| `logSearches` | Search terms are recorded for the parents (§9). The member is told it is on | false | false | — |

Adults (unrestricted) ignore all four.

## 2. Family-wide Windows features for children

Bundle top level: `windowsFeatures: { "notificationsAndSync": bool, "oneDrive": bool, "webSearchInStart": bool }`.
When missing: `notificationsAndSync = true`, `oneDrive = true`, `webSearchInStart = false`. Applies to every restricted
or unlinked account.

- `notificationsAndSync` → `NetworkPolicy` adds `C:\Windows\System32\svchost.exe` (per-user services such as push
  notifications and settings sync run there as the user). Allowed also when time is up.
- `oneDrive` → adds the **machine-wide** OneDrive only: `C:\Program Files\Microsoft OneDrive\OneDrive.exe` (and the
  `Program Files (x86)` path if it exists). Never a per-user path (`%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe` is
  writable by the child, so allowing it would let any program renamed to it reach the network). Allowed also when time
  is up. The requirement checks (`GetDiagnostics`) gain a `onedrive` check: `ok` (machine-wide), `warn` (only per-user
  installs found — detail: how to switch, `OneDriveSetup.exe /allusers`), `ok` with detail `none` when not installed.
- `webSearchInStart = false` → per-user policy `SOFTWARE\Policies\Microsoft\Windows\Explorer\DisableSearchBoxSuggestions = 1`
  (no Bing results in Start search; local search keeps working).

## 3. New starter lists (categories)

Files in `data/starter-lists/` in the existing format `{ "category": ..., "domains": [...] }`, each with its source and
licence in `data/starter-lists/README.md`.

| Category | Content | Default |
|---|---|---|
| `school-tools` | Sign-in and school platforms: Google Classroom/Docs/Drive/Meet and Google sign-in, Microsoft 365/Teams and Microsoft sign-in, Zoom, and the Israeli school platforms not already in `educational` | **allow** for children (allowlist mode), with `educational` |
| `adult` | Adult content, from an open list with a licence that allows redistribution in this product (no non-commercial clauses; permissive preferred; share-alike acceptable for the data file only, with attribution) | **block** for teens |
| `gambling` | Same sourcing rules | **block** for teens |
| `proxies` | Web proxies and filter-bypass sites, and `translate.goog` (Google Translate serves any site as `*.translate.goog`) | **block** for teens |
| `ai-chat` | Chatbots (ChatGPT, Gemini, Copilot, Claude, Character.AI, …) | offered, not checked, for teens |
| `unfiltered-search` | Search engines where SafeSearch cannot be enforced by policy | **block** for teens |
| `ads-popups` | Ad networks and popunder/redirect networks (click-through and iframe domains), curated by hand | **block** for teens |

Children are in allowlist mode, where anything not allowed is already blocked, so blocking lists matter for teens.
Lists are shipped embedded (the admin app copies a chosen list into the bundle's `categories`); automatic updates from
the internet are a follow-up. Size must stay compatible with the DNR limits (domains pack into a few rules; the compiler
was tested with 100,000 domains) and with hub→agent sync of the whole bundle.

## 4. Image search (`blockImageSearch`)

Built-in block rules added to the member's effective URL policy (ids `builtin-image-search-*`), so the DNR rules and the
Core evaluator agree, with reason `blocked_image_search`:

- Google: regex on `/search?...` with `udm=2` or `tbm=isch` (query part, so regex, not prefix); `images.google.*`;
  `lens.google.com`.
- Bing: `bing.com/images/`.
- DuckDuckGo: `duckduckgo.com` with `ia=images` or `iax=images`.
- Yandex: `yandex.*/images`.

Images inside ordinary result pages are not blocked (SafeSearch, already forced by policy, covers explicit content).

## 5. Browser policies for restricted accounts (per user, MVP-PLAN §5.2)

Added to `BrowserPolicies`: `DefaultPopupsSetting = 2`, `AbusiveExperienceInterventionEnforce = 1`,
`AdsSettingForIntrusiveAdsSites = 2`, `ProxyMode = "direct"`, and in Edge `TrackingPrevention = 3` (strict). Plus §2's
`DisableSearchBoxSuggestions`. Whether each is honoured from HKCU on an unmanaged PC is checked in the lab (S1).

## 6. Reason codes and events

- New reason codes (Core `ReasonCode` + wire names): `blocked_image_search`, `blocked_jump`, `blocked_youtube_shorts`.
  Hebrew/English wording in the admin app and the extension.
- New event types: `search` (detail: engine, query; url) and `override` (a teen chose "open anyway" on a jump block).
- New agent-protocol messages (extension → host → service):
  - `reportSearch { "engine": "google|bing|youtube|duckduckgo", "query": string, "url": string }` — acknowledged with
    `error { code: "ok" }`. Recorded only when the account's member has `logSearches`; otherwise acknowledged and
    dropped.
  - `reportOverride { "url": string, "reason": "blocked_jump" }` — acknowledged with `error { code: "ok" }`; recorded as
    an `override` event.
- `reportBlock` with `blocked_jump`, `blocked_youtube_shorts` keeps the extension's reason (the service cannot judge
  them from the URL alone), like the YouTube channel reasons.
- `RulesResponse` gains `features: { "jumpGuard": bool, "blockYouTubeShorts": bool, "logSearches": bool,
  "canOverrideJump": bool }` (`canOverrideJump` = teen; false for children and unlinked accounts). Absent → all false.

## 7. Jump guard (extension, `features.jumpGuard`)

- A page opens a new tab or window (`webNavigation.onCreatedNavigationTarget`) whose registrable domain differs from the
  source tab's → the new tab shows the block page with `blocked_jump` ("האתר ניסה לפתוח אתר אחר").
- An automatic client-side redirect (`transitionQualifiers` contains `client_redirect`) to a different registrable
  domain → same.
- A user's own link click in the same tab is **not** blocked (search results must work). Typed addresses, bookmarks and
  the browser's own pages are not affected.
- With `canOverrideJump`, the block page has "פתח בכל זאת": the extension sends `reportOverride` and opens the target once.
- The decision is reported with `reportBlock { reason: "blocked_jump" }`.

## 8. YouTube Shorts (extension, `features.blockYouTubeShorts`)

In the YouTube logic (the host rule for YouTube has its own priority, so a URL rule cannot do this): any `/shorts/`
page, including SPA navigations inside YouTube, is blocked with `blocked_youtube_shorts`; the Shorts shelf and the
Shorts entry in the side menu are hidden where practical.

## 9. Search-term log (extension, `features.logSearches`)

The extension recognises searches from the URL (Google `/search?q=`, Bing `/search?q=`, YouTube `/results?search_query=`,
DuckDuckGo `?q=`) and sends `reportSearch`. When on, the popup says "החיפושים שלך גלויים להורים" so the member knows.
The admin app's activity page shows searches (filter "חיפושים").

## 10. Parent notifications

- The hub service exposes a read-only pipe `\\.\pipe\WebFilter.Notify` (DACL: Authenticated Users; the server accepts
  only clients whose token is an administrator's, elevated or not — `ClientIdentity.IsAdminAccount`). Messages:
  `getNotifications { since }` → `notificationsResult { items: [{ id, kind, at, memberId?, deviceId?, detail? }] }`.
  Kinds: `request` (new access request), `tamper` (filters or policies repaired, extension silent while a browser runs
  in a child's session, the service found it had stopped uncleanly), `device` (a paired computer stopped reporting for a
  day).
- A small notifier app `src/WebFilter.Notifier` (asInvoker, tray icon with balloon notifications), started at logon on
  the hub computer for every user by the installer, exits at once for non-admin users, polls the pipe, and opens the
  admin app (UAC) when clicked.
- Tamper sources that do not exist yet are added where cheap (the WFP and policy services already report repairs).
