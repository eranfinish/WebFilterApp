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
| `blockAds` | Requests to EasyList's ad servers are blocked in the browser (§12) | true | true | — |

Adults (unrestricted) ignore all five; administrators and adults get the family's baseline protection instead (§13).

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

Added to `BrowserPolicies`: `DefaultPopupsSetting = 2`, `AbusiveExperienceInterventionEnforce = 1` (Chrome only; Edge has no such policy),
`AdsSettingForIntrusiveAdsSites = 2`, `ProxySettings = {"ProxyMode":"direct"}` (not the deprecated `ProxyMode`), and in Edge `TrackingPrevention = 3` (strict) and
`NewTabPageContentEnabled = 0` (no Microsoft News feed on the new tab page, seen in the lab on 2026-10-23). Plus §2's
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
  "canOverrideJump": bool, "blockAds": bool }` (`canOverrideJump` = teen; false for children and unlinked accounts;
  `blockAds` from §12; for administrators and adults the baseline's switches, §13). Absent → all false.

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
  day), `account` (a new standard Windows account on the hub or a paired computer: browsing there is almost fully blocked
  until a parent answers the admin app's "נמצא חשבון Windows חדש" card; `deviceId` is the computer, `detail` the
  account's name, id `account:{deviceId}:{accountId}`, so one per account however often it is reported). No `account`
  for an administrator's account (never filtered), for one already linked or kept as a guest, or for what a computer
  reports first — the accounts it already had when it was paired, or the hub's own at first setup: a device with no
  accounts in the settings yet (`HubCore.AccountsAdded`, `FirstReport`). A computer paired again keeps its accounts, so
  one created meanwhile is notified. Kinds may be added: the notifier shows one it does not know with generic wording.
- A small notifier app `src/WebFilter.Notifier` (asInvoker, tray icon with "פתח את ניהול המשפחה" and "יציאה"), started
  at logon on the hub computer for every user by the installer, exits at once for non-admin users, polls the pipe, and
  shows what arrives as Windows toast notifications: one toast per poll (a summary when it brings several, one line per
  kind and one for kinds it does not know), Hebrew wording that names the computer (and a new account's Windows name),
  never the family member. A new account reads "נמצא חשבון Windows חדש" / "החשבון ‹name› במחשב ‹computer›: הגלישה בו
  חסומה עד שתבחרו לאיזה בן משפחה הוא שייך."
  - Toasts, not tray balloons: a balloon is gone after a few seconds and is not kept in the notification center. The
    notifier uses `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 (MIT; `ToastContentBuilder`, `ToastNotificationManagerCompat`)
    as an app without a package, built for `net10.0-windows10.0.17763.0`. Its app id is `WebFilter.Notifier`, shown as
    "WebFilter" with the shield icon. It registers in the user's HKCU (the app id with name, icon and a COM activator,
    and that activator's `LocalServer32`) the first time the service answers it as a parent, so children's accounts
    register nothing.
  - Every toast stays in the notification center (Win+N) until opened or cleared. Tamper (alone or in a summary) uses
    the reminder scenario and stays on the screen until the parent clicks "פתח את ניהול המשפחה" or "סגור" (a system
    dismissal); requests, silent computers and new accounts show for the normal few seconds.
  - A click on the toast or on "פתח את ניהול המשפחה" opens the admin app through the shell (UAC); the notifier never
    elevates. A running notifier gets the click through COM. With none running (after "יציאה", or after a reboot before
    the Run value starts it) Windows starts the notifier with `-ToastActivated`; it opens the admin app and stays as the
    session's notifier. If one runs but has not registered yet, the new copy opens the admin app and exits.
  - Where it opens: a toast of new accounts alone carries `action=open;page=devices` and opens the devices page, with the
    accounts' cards (`WebFilter.Admin.exe --page devices`; the shell keeps the arguments through UAC). Any other toast
    carries `action=open` and opens the dashboard, which shows the pending requests and the new-account cards too. So do
    toasts of older notifiers, an `account` an older notifier shows generically, and the two cases where the click does
    not reach a registered notifier (a copy that opens the admin app by itself).
  - Uninstall on the hub runs `WebFilter.Notifier.exe --unregister` as the uninstalling user (their toasts and
    registration). Other users' keys stay: harmless, they only name the deleted exe (`installer/README.md`).
- Tamper sources that do not exist yet are added where cheap (the WFP and policy services already report repairs).

## 11. Additions after the lists work (2026-10-22)

- **List titles** (admin app, in this order): `school-tools` "כלי בית ספר", `adult` "תוכן למבוגרים", `gambling` "הימורים",
  `proxies` "פרוקסי ועוקפי חסימות", `ai-chat` "צ'אטבוטים", `unfiltered-search` "מנועי חיפוש לא מסוננים",
  `ads-popups` "פרסומות וחלונות קופצים". The files carry `source` and `license` fields; the notices ship in the embedded files,
  so copying a list into a bundle's categories needs no notice of its own.
- **Ministry of Education Google sign-in** for children in allowlist mode goes through `www.google.com/a/<domain>/...`:
  when a parent picks `school-tools`, the admin app also adds global prefix allow rules `www.google.com/a/` and
  `www.google.com/recaptcha/` (ids `builtin-school-signin-*`).
- **YouTube off** exempts `accounts.youtube.com` (a Google sign-in cookie endpoint) from the YouTube-mode rule.
- **AI chat:** when a member's categories block `ai-chat`, built-in rules also block Google's AI Mode
  (`google.com/search?...udm=50`), and per-user browser policies turn off Copilot in Edge's sidebar and Gemini in Chrome
  (exact policy names verified against the official lists by the core work).
- **Blocked frames** (e.g. ad iframes from `ads-popups`): the block page inside a frame is a quiet placeholder and sends
  no `reportBlock`, so ads do not flood the activity log.
- **Sign-in is not a jump** (decided with the user): a jump to `accounts.google.com`, `accounts.google.co.il`,
  `login.microsoftonline.com`, `login.microsoft.com`, `login.live.com`, `login.windows.net` or `appleid.apple.com`
  (exact hosts), and the provider's return to the site afterwards, are not blocked by the jump guard. The URL rules
  still decide whether those hosts are allowed (children get them through `school-tools`).

## 12. Ad blocking (`blockAds`, 2026-10-23)

Decided with the user: ad blocking with EasyList, **on by default for children and teens**, off for adults; a parent can
turn it off per child.

- **Flag** `blockAds`, end to end like `jumpGuard`: optional on `MemberDto` (false when missing), `FamilyMember`,
  `SetMember(blockAds)`, `RulesResponse.features.blockAds`. The wizard's child and teen templates set it; adults are
  stored without it. Admin: the switch "חסימת פרסומות" among the member's protection switches, with a hint that ads a
  site serves from its own address are not blocked and that the switch is the way out when a site refuses to work.
- **When it is on:** a restricted member with the flag, while filtered — normal, homework and time up alike. Off for
  unlinked accounts (like every feature; administrators and unrestricted members get the baseline's switch, §13), and **while the member is paused**
  (the hub's pause or this computer's own): a pause means no filtering. The native host's own answers (service down)
  carry no features, so it is off then; only always-allowed sites load in that mode.
- **Extension:** a static ruleset `ads` (`rulesets/ads.json`, `"enabled": false` in the manifest), enabled with
  `updateEnabledRulesets` when `features.blockAds` is true and disabled otherwise. While **fail-closed** (no profile
  yet, or disconnected) it is **on**, the strictest state: new pages and frames are blocked anyway, and pages already
  open stop loading ads. Inert (not policy-installed) and unrestricted: off. A browser that refuses the change is logged
  and asked again with the next rules. The popup says "חסימת פרסומות פעילה" while it is on.
- **The ruleset** is generated by `tools/starter-lists/ad-ruleset.mjs` from EasyList at the commit pinned in
  `tools/starter-lists/sources.mjs` (reproducible, committed): the whole-domain blocking rules of
  `easylist_adservers.txt` and `easylist_thirdparty.txt` — `||domain^`, and `||domain^$third-party` as
  `domainType: "thirdParty"`. Rules with a path or any other option, regular expressions and cosmetic rules are skipped.
  Each rule blocks every request type except `main_frame` (`excludedResourceTypes: ["main_frame"]`: sub_frame, script,
  image, xmlhttprequest, ping, media, font, stylesheet, websocket, object, other, …); page loads stay with the URL rules
  and the `ads-popups` list. Domains are packed with `requestDomains` into one rule for any party and one for third
  party. EasyList's exceptions (`easylist_allowlist.txt`) are honoured per host: a host spared on every site is left out
  (`excludedRequestDomains` when it is below a listed domain); a host spared on some sites gets a rule of its own that
  blocks it everywhere else (`excludedInitiatorDomains`). The path and types of an exception are ignored, so it is
  honoured more widely than written, except that a path exception on every site for a shared host above listed domains
  (`cloudfront.net/js/common/invoke.js`) does not spare all of them. Block rules only, priority 1.
- **Never in it:** the `educational` and `school-tools` domains with every host below them and their parents; the
  essentials `AD_ESSENTIALS` in `curation.mjs` (Google, YouTube, Microsoft, Apple core hosts, `gov.il`) with every host
  below them and their parents; the platforms of `NEVER_BLOCK` and their parents (one ad host below a platform, such as an
  ad distribution on `cloudfront.net`, may stay); public suffixes; and `AD_SERVERS.remove` (curated, empty so far).
- **Priorities:** no compiled rule uses `allowAllRequests`, and every compiled rule and extension rule (fail-closed,
  time up, Shorts, jump-guard tabs) filters `main_frame`/`sub_frame` only (pinned by `DnrCompilerTests`). So for
  scripts, images and requests the ad rules are the only rules that match: ad scripts are blocked on an allowed site.
  For a frame, a URL rule that matches it (priority 2 and up: an explicit allow of the ad host, the `ads-popups`
  category, time up) still decides; the allowlist default (priority 1, a redirect) loses to the ad block at equal
  priority (block beats redirect), so a blocked ad frame collapses instead of showing the block-page placeholder. Static
  rule ids are per ruleset and never meet the compiled or extension ids.
- **Quiet:** a blocked ad request never loads the block page, so there is no `reportBlock` and no activity event.
- **Limits:** ads a site serves from its own address (YouTube, Google Search, social feeds) are not blocked, nor what
  EasyList blocks only by path or by cosmetic rules (an empty ad box can stay). A site that detects the blocker may
  refuse to work; the parent turns the switch off for that child (per-site exceptions are a follow-up). Updates come
  with a regenerated ruleset in a new extension version; nothing is downloaded at run time.
- **Licence:** EasyList is GPL-3.0-or-later or CC BY-SA 3.0-or-later; `rulesets/ads.json` is an adaptation under CC
  BY-SA 4.0, with `rulesets/ads-notice.txt` shipped next to it and the attribution in `data/starter-lists/README.md`.
- **Size (EasyList 48ea829):** 51,874 domains in 65 rules (two main rules, 63 for spared hosts), 1.17 MB, 449 KB
  gzipped. The extension's zip grows from about 60 KB to 520 KB (unpacked 0.18 → 1.36 MB); the browser also keeps its
  own index of the static rulesets, disabled ones included (about 1.5 MB per profile, `_metadata` in Edge 156). It uses
  65 of the 330,000 static rules Chrome shares between extensions.

## 13. Baseline protection for parents and adults (`baseline`, 2026-10-23)

Decided with the user ("I want filtering for the administrator too: I don't want all kinds of sites popping up at me, and
adult content blocked by default for everyone"). Until now administrators and unrestricted members got no filtering at
all. They now get a family-wide **baseline**, much lighter than a child's, and nothing of their browsing is recorded.

- **Setting:** bundle top level `baseline: { "blockAdult": bool, "jumpGuard": bool, "blockAds": bool, "safeSearch": bool }`,
  like `windowsFeatures`. A missing section, or a missing switch in it, is `true`: existing families get the baseline.
  Core `FamilyConfig.Baseline` (`BaselineProtection`), Contracts `BaselineDto`; the service writes the section out in
  full. Admin: the settings section "הגנה בסיסית להורים ולמבוגרים" (below).
- **Who it applies to** (`BaselineProtection.AppliesTo`): an administrator account linked to nobody or to an
  unrestricted member, and any account linked to an unrestricted member — the accounts no member settings filter. An
  administrator account linked to a filtered member is filtered as that member (the admin app warns about such a link).
  Children, teens, unlinked standard accounts and guests are unchanged: they follow their own settings, which are
  stronger. For the browser policies (below), every administrator account, whoever it is linked to, and every
  unrestricted member's account get the baseline's SafeSearch and nothing else.
- **`blockAdult`:** the bundle's `adult` category (`data/starter-lists/adult.json`) is blocked with reason
  **`blocked_baseline_adult`** (a new code: Core `ReasonCode.BlockedBaselineAdult`, wire, scenario schema, MVP-PLAN §6,
  scenarios `BASE-*`, admin and extension wording). The list is not a new static ruleset: it goes through the Core and the
  DNR compiler like any category block. `EffectiveUrlPolicy.ForBaseline` holds the category's domains as blocks (global
  band) and the parents' active **allow** rules — the member's own and the global ones — as member rules above them, so
  a parent's explicit allow wins whatever its specificity (scenario BASE-003, an expiring one BASE-004). The parents'
  block rules and the family's global block rules are not part of it. Nothing else is filtered: no default block, no
  IP-literal block (a router's page opens), no YouTube rule, no built-in blocks, no time-up rules
  (`EffectiveUrlPolicy.Baseline`, read the same way by `RuleEvaluator` and `DnrCompiler`; random differential tests and
  the scenario cross-check keep them equal). The service's `Decider.Baseline` answers `allowed_unrestricted` outside
  the category, and inside it the allow rule's reason and id or `blocked_baseline_adult`. **The adult domains must be
  in the bundle:** the admin app copies the shipped list into the family's categories when the section is saved with
  `blockAdult` on, and in the setup wizard (a family's own `adult` category is kept). A family without the category
  blocks nothing (BASE-006).
- **`jumpGuard`:** the extension's jump guard (§7) for these accounts, always with "פתח בכל זאת"
  (`canOverrideJump: true`). The override is not reported.
- **`blockAds`:** the extension's `ads` static ruleset (§12).
- **`safeSearch`:** per-user policies `ForceGoogleSafeSearch = 1` (Chrome and Edge) and `ForceBingSafeSearch = 1` (Edge,
  moderate) for these accounts (`BrowserPolicies`, `PolicyService`). **No other policy for them:** InPrivate, guest,
  profiles, developer mode and DevTools, extensions, proxy, pop-ups, tracking prevention, the new tab page, the sidebar,
  Gemini, YouTube Restricted Mode and Start search stay as they are. Restoring one of these values is not reported as
  tamper (they are the parent's own settings). Turned off, the values the service wrote are removed.
- **Wire:** `helloResult.mode` is **`baseline`** for these accounts while any switch is on, and `unrestricted` — exactly
  the old answer, no rules, no features — when all four are off. The `rules` reply gains an optional **`mode`** with the
  same values as the hello's (`unrestricted | blocklist | allowlist | baseline`), so a re-linked account or a changed
  switch applies with `rulesChanged`, without a new hello; absent (an older service, the native host's fallback) or
  unknown, the hello's mode stands. For a parent the reply carries the compiled baseline rules, `timeUpSessionRules: []`,
  `youtube.mode: "unrestricted"`, `time: unlimited` and `features: { jumpGuard, blockAds, canOverrideJump: true,
  logSearches: false, blockYouTubeShorts: false }` (with only SafeSearch on: mode `baseline`, no rules, jump guard and
  ads off).
- **Nothing is reported:** no `blocked`, `override` or `search` event and no access request for these accounts, whatever
  the switches. The service answers `reportBlock` with its reason and records nothing, answers `reportOverride` with
  "not recorded", drops `reportSearch` (no `logSearches`), answers `requestAccess` with `unavailable`, and gives
  `canRequest: false`; heartbeats count no time. The extension in mode `baseline` never sends `reportBlock` (the block
  page asks with `decideUrl`, which records nothing; a jump block asks nothing), `reportOverride`, `reportSearch` or
  `requestAccess`, and shows no request button.
- **A pause** does not apply to parents: they have none, and a pause set for an adult changes nothing (BASE-008).
- **Block page:** "אתר למבוגרים — חסום בהגנה הבסיסית של המשפחה." with no request button and the hint "אפשר לכבות את
  ההגנה הזו בממשק הניהול של WebFilter, בדף ההגדרות (הגנה בסיסית להורים ולמבוגרים)." The popup shows the mode
  "הגנה בסיסית להורים" and "שום דבר מהגלישה שלך לא נרשם".
- **Service down:** the native host's own answers are unchanged: an administrator, or an account whose profile cache says
  unrestricted, is `unrestricted` with no rules and no features, so the baseline is off until the service is back and a
  stopped service never blocks a parent more than before (scenario FC-005). With no host at all the extension is
  fail-closed for everyone, as before.
- **An older extension** with a newer service gets `baseline` in the hello and handles it as a filtered mode: it installs
  the rules and the features; the service records nothing of its reports and offers no request, and its block page shows
  the generic "הדף הזה חסום".
- **Admin app:** Settings, section "הגנה בסיסית להורים ולמבוגרים" (hub only): the four switches with a line each, saved
  together through `ReplaceSettings` like the Windows features, and a note that it applies to the administrators and the
  unrestricted members, that children and teens follow their own settings, that an administrator can turn it off, that
  blocking does not work in InPrivate/incognito windows, and that nothing is recorded. While `blockAdult` is on and the
  family has no adult list, the section says so and saving adds it. The setup wizard's summary names the baseline and
  adds the adult list. Texts that said administrators and adults are "not filtered" say "apart from the baseline".
- **Not done (limits):** the extension does not run in the parents' InPrivate/incognito windows (they are not disabled
  for parents), so the adult list, the jump guard and ad blocking do not apply there; SafeSearch, a policy, does. Other
  browsers are not covered for parents; no WFP filters, no time counting and no browser lockdown for them. The switches
  are family-wide, not per parent. An existing family gets the jump guard, ad blocking and SafeSearch at once, but the
  adult list only once a parent saves the section (or the wizard runs): nothing adds it by itself. A Windows
  administrator can undo any of it (policies, the extension's state); the service restores the policies silently. Not
  checked in the lab.
