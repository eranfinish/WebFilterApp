# Starter lists

Category lists shipped with the product and offered in the setup wizard. The admin app embeds every `*.json` here
(`WebFilter.Admin.csproj`, `StarterLists`); the wizard adds the ones the parent keeps to the family's categories, without
replacing a category the family already has. `educational` is also what homework mode allows
(`FamilyConfig.EducationalCategory`).

Not to be confused with the synthetic categories in `tests/simulation/fixtures/family-base.json`, which exist only for tests.

## Format

```json
{ "category": "adult", "description": "...", "source": "...", "license": "...", "domains": ["example.com", "..."] }
```

- `category` equals the file name. `domains` are lowercase ASCII host names (IDN in punycode), one registrable domain or
  one site's own host (`scratch.mit.edu`, `classroom.google.com`) per entry, with no duplicates in or across lists.
- `source` and `license` (the protection lists) say where the entries come from and carry the notices their licences
  require, so the attribution travels with the embedded file. The app reads only `category` and `domains`.
- An entry is matched with `domain` semantics: the host itself and every subdomain. So a list never repeats a subdomain
  of a domain it already has, and the protection lists are sorted.
- When several rules and categories match, the most specific domain (more labels) wins, and a tie goes to block
  (`DnrPriorities`, `RuleEvaluator`). A member rule beats every category.
- Rules filter only page loads (`main_frame`) and frames (`sub_frame`). Scripts, images and API calls are never
  filtered, so CDNs and API hosts need not be listed to keep sites working, and listing them would change nothing.

Checked by `tests/WebFilter.Core.Tests/StarterListFilesTests.cs` (every file, every domain, the compiled policies, spot
checks) and, for the lists the wizard offers, `tests/WebFilter.Admin.Tests/StarterListsTests.cs`.

## The lists

| File | Hebrew title (`Category_<name>`; new ones suggested) | Default (PROTECTION-SPEC §3) | Domains | Source | Licence |
|---|---|---|---:|---|---|
| `educational.json` | לימודים | allow (children), homework mode | 23 | hand-picked | — |
| `social.json` | רשתות חברתיות | — | 17 | hand-picked | — |
| `gaming.json` | משחקים | — | 19 | hand-picked | — |
| `school-tools.json` | כלי בית ספר | **allow** for children, with `educational` | 49 | hand-curated | — |
| `adult.json` | תוכן למבוגרים | **block** for teens | 15,691 | ShadowWhisperer + Sinfonietta, ranked by CrUX | Unlicense + MIT |
| `gambling.json` | הימורים | **block** for teens | 10,985 | ShadowWhisperer + Sinfonietta + hand-picked | Unlicense + MIT |
| `proxies.json` | פרוקסי ועוקפי חסימות | **block** for teens | 905 | hand-picked + ShadowWhisperer | Unlicense |
| `ai-chat.json` | צ'אטבוטים | offered, not checked, for teens | 32 | hand-curated | — |
| `unfiltered-search.json` | מנועי חיפוש לא מסוננים | **block** for teens | 33 | hand-curated | — |
| `ads-popups.json` | פרסומות וחלונות קופצים | **block** for teens | 2,426 | EasyList (popup servers) + hand-picked | CC BY-SA 4.0 |

All lists together: 30,180 domains, about 700 KB of JSON. A teen policy that blocks every blocking list and allows
`educational` and `school-tools` compiles into about ten DNR rules (one per action and label count, plus the fixed
ones), far inside `DnrLimits`; the compiler and Edge were tested with 100,000 domains in one rule (spike S3).

## `school-tools` (allow)

What children must reach to sign in and study. Because an entry allows the host **and all its subdomains**, Google and
Microsoft are listed host by host: `google.com` would allow all of Google — Search, Images, Gemini, Gmail, Maps, News —
and `microsoft.com` would allow Copilot, the Store and the rest. The more-specific-wins rule also lets the lists
overlap safely: `copilot.cloud.microsoft` (in `ai-chat`) stays blocked for a teen even though `cloud.microsoft` (Microsoft
365) is allowed here.

- **Google sign-in:** `accounts.google.com`, `accounts.google.co.il` (sign-in can pass through it to sign in on the
  Israeli Google domain), `myaccount.google.com` (password and recovery).
- **Classroom, Docs, Drive, Meet:** `classroom.google.com`, `docs.google.com` (Docs, Sheets, Slides, Forms, Drawings,
  the file picker), `sheets.google.com`, `slides.google.com`, `forms.google.com` (entry points that redirect to Docs),
  `forms.gle` (Forms short links), `drive.google.com`, `drive.usercontent.google.com` (Drive downloads),
  `meet.google.com`.
- **Frames these apps open:** `ogs.google.com` (the app launcher and account switcher), `apis.google.com`,
  `clients6.google.com` and `content.googleapis.com` (API proxy frames).
- **Microsoft sign-in:** `login.microsoftonline.com` (work and school accounts), `login.microsoft.com`,
  `login.windows.net` (older apps), `login.live.com` and `account.live.com` (personal accounts), `mysignins.microsoft.com`
  (security info), `myaccount.microsoft.com`, `myapps.microsoft.com` and `myapplications.microsoft.com` (the Ministry of
  Education's district sign-in links use `myapps.microsoft.com?whr=…`), `aka.ms` (Microsoft's short links; the target is
  filtered on its own).
- **Microsoft 365:** `office.com`, `microsoft365.com`, `office365.com` (Outlook web for school accounts),
  `cloud.microsoft` (the new home of Microsoft 365: `m365.`, `teams.`, `outlook.`, `word.`…), `static.microsoft` and
  `cdn.office.net` (static hosts the Teams and Office web apps load from), `officeapps.live.com` (the Word, Excel and
  PowerPoint editor frames), `sharepoint.com` (school OneDrive and SharePoint), `onedrive.live.com`, `1drv.ms`,
  `onenote.com`.
- **Teams and Outlook:** `teams.microsoft.com`, `teams.live.com`, `outlook.com`, `outlook.live.com`.
- **Zoom:** `zoom.us` (including schools' `*.zoom.us` hosts such as `edu-il.zoom.us`), `zoom.com`.
- **Israeli school platforms not in `educational`:** `edu.gov.il` (the Ministry of Education's unified sign-in
  `lgn.edu.gov.il`, which Google and Microsoft school accounts federate to, and the virtual school `my.edu.gov.il`;
  `education.gov.il` is already in `educational`), `galim.org.il` (Galim), `wizdi.school` (Wizdi), `classoos.com`
  (Classoos digital textbooks), `classit.co.il` (Classit maths and physics), `schooly.co.il` (Schooly). Chosen from the
  Chrome UX Report's Israel list: each is among the 10,000 sites Israeli Chrome users load most.

Known gaps — they need a URL rule, because a category holds domains only:

- Ministry of Education **Google** accounts (`@educ.org.il`, `@taded.org.il`, …) sign in through SAML, which passes
  through `www.google.com/a/<domain>/ServiceLogin` and posts back to `www.google.com/a/<domain>/acs`. In allowlist mode
  that is blocked (`www.google.com` is Search). A global prefix allow rule `www.google.com/a/` fixes it without opening
  Search. Likewise `www.google.com/recaptcha/` if a sign-in shows a captcha frame.
- Google sign-in also visits `accounts.youtube.com` to sign in to YouTube. YouTube hosts follow the member's YouTube
  mode, above every category, so with YouTube **off** that step is blocked. To check in the lab.
- Shared hosts cannot be split: `docs.google.com` and `sharepoint.com` serve any user's shared files, Gemini runs inside
  Docs and Classroom, Microsoft 365 Copilot Chat at `m365.cloud.microsoft/chat`, Teams and Zoom chat with anyone.
- Not included (add them where a school uses them): Gmail, Calendar and Sites (`mail.`, `calendar.`, `sites.google.com`),
  Kahoot, Wordwall, Padlet, Quizlet, Canva, Genially, Nearpod, Edpuzzle, Liveworksheets, and school websites
  (`*.k12.il` and others).

## `adult` (block)

- **Sources:** ShadowWhisperer `Lists/Adult` (234,046 entries) and Sinfonietta `pornography-hosts` (61,197 lines) —
  295,241 entries, about 265,000 distinct.
- **Selection — the most visited:** only domains that Chrome users actually load, by the Chrome UX Report top lists of
  September 2026: the domain or one of its subdomains is in the global top million or in the Israel list
  (`zakird/crux-top-lists`). That keeps 15,691. The top million covers about 95% of Chrome page loads; the 250,000 left
  out are mostly tiny, dead, parked or spam domains, and shipping them would make this one list ~6 MB and 90% of all the
  data for little protection. SafeSearch (forced by policy), `unfiltered-search`, image-search blocking (§4) and the jump
  guard (§7) cover discovery; new mirrors of the big sites appear in the next regeneration.
- **Removed** (`tools/starter-lists/curation.mjs`): false positives found by reviewing the ~2,000 most-visited entries and
  scanning the rest for general-purpose names — news and media (ekstrabladet.dk, fark.com, marieclaire.it, virginradio.fr,
  blogs.icrc.org), stores and services (hmv.co.jp, suruga-ya.jp, booth.pm, quoka.de, a Tokyo railway, sperm banks),
  general platforms (fc2.com, my.mail.ru, kinopoisk.ru, dmm.com — its adult store dmm.co.jp stays), general file, image
  and video hosts, and piracy sites that are not adult (torrent indexes, game and sports-stream piracy). A proxy service
  and three ad networks moved to `proxies` and `ads-popups`. `vk.com` and public suffixes in the sources are dropped by
  the guards (below).
- **Added:** `janitorai.com` and `chub.ai` — NSFW AI role-play chat, like `crushon.ai`, `spicychat.ai` and `candy.ai`,
  which the sources already list.
- Kept as the sources have them: imageboards with adult boards (`4chan.org`, `2chan.net`), shock and gore sites, adult
  stores. Report other false positives by adding them to `ADULT.remove` and regenerating.

## `gambling` (block)

- **Sources:** ShadowWhisperer `Lists/Gambling` (9,662) and Sinfonietta `gambling-hosts` (2,694). The list is small, so
  every entry is kept (no ranking); 731 of them are in the CrUX lists.
- Casinos, sportsbooks, lotteries (national lotteries included), poker, skin gambling (CS2 case sites), sweepstakes and
  social casinos, prediction markets (Polymarket, Kalshi), money fantasy sports, and betting tips and odds sites.
- **Added:** Israel's legal betting and lottery — `winner.co.il` (Winner, Sport Toto), `pais.co.il` (Mifal HaPais) — and
  lottery-ticket services (`lottosheli.co.il`, `lottoishi.co.il`, `lottoplus.co.il`, `lottonline.co.il`), plus large
  international sites both sources miss (PokerStars, GGPoker, Roobet, Shuffle, 1win, Mostbet, Kalshi, …).
- **Removed:** live scores and sports data or news (`livescore.in`, `sportradar.com`, `cricketweb.net`), corporate
  sites of game and software companies (`playtika.com`, `playtech.com`), a regulator (`pagcor.ph`), a game store
  (`taptap.io`).
- Casino-resort companies (`caesars.com`, `mgmresorts.com`) stay, although they also sell hotel rooms.

## `proxies` (block)

- **Hand-picked:** `translate.goog` — Google Translate serves any translated site as `<site>.translate.goog`, a full
  proxy; Google Translate's older proxy and cache hosts (`translate.googleusercontent.com`,
  `webcache.googleusercontent.com`); Microsoft Translator's page translation (`translatetheweb.com`,
  `translatoruser-int.com`); Yandex Translate's proxies (`turbopages.org`, `z5h64q92x9.net`); archive.today's
  on-demand snapshots of any page (`archive.ph`, `.is`, `.today`, `.li`, `.vn`, `.fo`, `.md`); remote browsers
  (`browserling.com`, `hyperbeam.com`); well-known web proxies (CroxyProxy, BlockAway, Proxyium, Titanium Network, 12ft).
- **Source:** ShadowWhisperer `Lists/Tunnels` (VPNs and proxies, 1,025): web proxies and unblockers, Tor gateways and
  VPN services' sites — a VPN app itself is stopped by the per-account program allowlist (WFP), so its website is
  where a teen would get one.
- **Removed:** VPN apps' API and server endpoints on CDN and cloud hosts (not pages); corporate VPN, zero-trust and
  remote-access products used by schools and parents' workplaces (Check Point, Fortinet, Palo Alto GlobalProtect, Cisco,
  Zscaler, Tailscale, Twingate, OpenVPN, WireGuard, Azure VPN, eWON); unrelated sites (`spaceship.com`, a registrar;
  `wellpoint.com`, an insurer); browsers' own privacy relays (Chrome IP Protection, Firefox Private Network).
- Not blocked: `translate.google.com` (text translation; the page-translation proxy is `translate.goog`), the browsers'
  built-in page translation (in-page), and the Wayback Machine (`web.archive.org`) — too much legitimate use
  (Wikipedia's citations), though it can show old copies of blocked sites; parents can add it. Self-hosted unblockers
  appear on new domains every day; for children, allowlist mode is the real defence.

## `ai-chat` (offered, not checked)

Chat front-ends only: ChatGPT (`chatgpt.com`, `chat.openai.com`, `chat.com`), Claude (`claude.ai`, `claude.com`),
Gemini (`gemini.google.com`, AI Studio `aistudio.google.com`, NotebookLM `notebooklm.google.com`), Copilot
(`copilot.microsoft.com`, `copilot.com`, Microsoft 365 Copilot Chat `copilot.cloud.microsoft`), Meta AI, Grok, Perplexity,
Poe, Pi, Character.AI, DeepSeek, Mistral Le Chat, Qwen, Kimi, Z.ai, Duck.ai, You.com, DeepAI, LMArena, OpenRouter, and
companion bots (Replika, PolyBuzz, Talkie, Nomi, Kindroid).

Edge cases:

- Not listed, because they would break unrelated services: `google.com` (AI Mode and AI Overviews are Search pages),
  `bing.com` (Copilot in Bing), `microsoft.com`, `openai.com` and `anthropic.com` (company sites and developer
  consoles), `huggingface.co` (HuggingChat is a path on the model hub), `x.com` (Grok inside X).
- `copilot.cloud.microsoft` wins over `school-tools`' `cloud.microsoft` (more specific), but Copilot Chat is also a path
  of `m365.cloud.microsoft`, and Gemini runs inside Docs, Gmail and Classroom — a domain list cannot reach those.
- Assistants built into the browser (Copilot in Edge's sidebar, Gemini in Chrome) are controlled by browser policies,
  not by URL rules.
- NSFW role-play bots are in `adult`, which is blocked for teens by default.

## `unfiltered-search` (block)

Search engines whose SafeSearch Chrome and Edge policies cannot force (`ForceGoogleSafeSearch` covers Google,
`ForceBingSafeSearch` covers Bing; YouTube has its own mode). On all of these SafeSearch is a user setting (a cookie or
URL parameter) or can be forced only by DNS, which this product does not use.

| Entry | Why |
|---|---|
| `duckduckgo.com` | SafeSearch is a cookie/`kp=` setting; forcing needs DNS (`safe.duckduckgo.com`). Covers `html.` and `lite.` |
| `startpage.com` | Google results on Startpage's own domain, beyond Google's policy |
| `ecosia.org`, `qwant.com`, `mojeek.com`, `kagi.com`, `presearch.com`, `yep.com` | Independent or Bing-based engines; SafeSearch is a user setting |
| `search.brave.com` | Brave Search only (not `brave.com`); user setting |
| `metager.org`, `metager.de` | Metasearch; user setting |
| `gibiru.com` | Markets itself as "uncensored" |
| `search.yahoo.com` | Bing results under Yahoo's domain; Edge's Bing policy does not apply. Regional `*.search.yahoo.com` included |
| `search.yahoo.co.jp` | Yahoo Japan search |
| `search.aol.com`, `ask.com`, `dogpile.com`, `metacrawler.com`, `webcrawler.com`, `info.com` | Bing- or Google-fed portals and metasearch; user setting |
| `yandex.ru`, `yandex.com`, `ya.ru`, `yandex.by`, `yandex.kz`, `yandex.uz`, `yandex.com.tr` | Yandex search lives at the root of its main domains, so the whole domain is listed; this also blocks Yandex Mail, Maps and Music (a rule can allow e.g. `mail.yandex.ru`) and Yandex map widgets on other sites |
| `baidu.com`, `sogou.com`, `so.com` | Chinese engines without an enforceable SafeSearch |
| `search.naver.com`, `search.daum.net`, `search.seznam.cz` | The portals' search hosts only; the rest of the portals stays reachable |

Not listed: Google and Bing (enforced by policy), `swisscows.com` (filters adult content by design), `kiddle.co` (a
children's search on Google SafeSearch), Petal Search and Lycos search (did not resolve when checked), self-hosted
SearXNG or Whoogle instances (too many to list).

## `ads-popups` (block)

Listing a domain blocks it as a page — a popunder, a new tab, an ad click-through redirect — and as a frame, which is
where display ads render. Scripts and images from it still load, so a page never loses functionality, only ads and
ad navigations.

- **Source:** EasyList `easylist_adservers_popup.txt` — the domains EasyList blocks when they open as a popup: popunder
  networks and their rotating redirect domains. Only whole-domain rules (`||domain^$popup`); rules limited to
  third-party popups or to a path are real brands that are only blocked in some popups, so they are skipped, and IP
  addresses are skipped (bare-IP pages are already blocked for teens). `s.id`, a URL shortener, is removed. Gambling
  and adult brands from it stay in those lists.
- **Hand-picked — Google:** `googleadservices.com` (the click-through redirector of Search, Shopping and YouTube ads,
  and conversion pixels; blocking it breaks only ad clicks — some search-ad clicks go through `www.google.com/aclk`,
  which is Search and stays), `doubleclick.net` (display-ad frames `googleads.g.`, click trackers `ad.` and
  `adclick.g.`, hidden Floodlight frames), `googlesyndication.com` (AdSense and Ad Manager safeframes),
  `dartsearch.net` (Search Ads 360 click tracking). Not listed: `google.com`, Google's consent dialogs
  (`fundingchoicesmessages.google.com`), `googletagmanager.com` and `googletagservices.com` (scripts only), reCAPTCHA,
  `gstatic.com`, `googleapis.com`.
- **Hand-picked — other networks:** Xandr (`adnxs.com`), Amazon (`amazon-adsystem.com`), Criteo, The Trade Desk
  (`adsrvr.org`); native "around the web" ads: `paid.outbrain.com` (the redirect host of its sponsored links), MGID,
  Revcontent, AdSkeeper; popunder, push and redirect networks (PropellerAds/Monetag, PopAds, PopCash, Adsterra, ExoClick, TrafficJunky,
  JuicyAds, HilltopAds, Clickadu, AdMaven, Galaksion, RichAds, Zeropark, TrafficStars, AdCash, …). Taboola is not listed:
  its widgets also carry the publisher's own article recommendations (Israeli news sites use it heavily), and blocking
  its hosts risks breaking those links.
- Never listed: consent managers, tag managers and analytics, payment frames, social embeds, video players and CDNs.
- Side effects: a blocked ad frame shows the block page inside the ad slot; "watch an ad to continue" offers stop
  working; ad-tracked links in newsletters open the block page.

## Guards applied to every sourced list

`tools/starter-lists/update.mjs` drops, before writing:

- entries that are not domain names (IP addresses, wildcards, paths) or are public suffixes (Public Suffix List);
- entries equal to, or a parent of, a protected domain — `NEVER_BLOCK` in `curation.mjs` (Google, Microsoft, Apple,
  Amazon, the big social and messaging platforms, Wikipedia, code hosting, website builders, CDNs, payments…) and every
  domain of the allow lists (`educational`, `school-tools`); a specific subdomain below a platform (one site's own host)
  may stay;
- hosts below CDN and cloud-storage domains (another site's assets or DNS aliases, not pages);
- entries already in a hand-written list, or in an earlier sourced list (order: adult, gambling, proxies, ads-popups), so
  no domain is in two lists; and subdomains of a domain already in the same list.

## Sources and licences

| Source | Used for | Version | Licence |
|---|---|---|---|
| [ShadowWhisperer/BlockLists](https://github.com/ShadowWhisperer/BlockLists) — `Lists/Adult`, `Lists/Gambling`, `Lists/Tunnels` | adult, gambling, proxies | commit `2b7dd18e51c50ed042a56cd57f0b7c3071fd9eef`, 2026-10-22 | [Unlicense](https://github.com/ShadowWhisperer/BlockLists/blob/master/LICENSE) (public domain dedication; no conditions). The maintainer builds the lists with his own script and manual additions and states he does not merge other lists. |
| [Sinfonietta/hostfiles](https://github.com/Sinfonietta/hostfiles) — `pornography-hosts`, `gambling-hosts` | adult, gambling | commit `46f3097d7bcfc9eea323fe365074dfd771d0d17c`, 2026-09-09 | [MIT](https://github.com/Sinfonietta/hostfiles/blob/master/LICENSE), "Copyright (c) 2016 Sinfonietta". The notice and permission text must accompany substantial portions: it is in the `license` field of `adult.json` and `gambling.json`. |
| [EasyList](https://easylist.to/) — [`easylist_adservers_popup.txt`](https://github.com/easylist/easylist/blob/master/easylist/easylist_adservers_popup.txt) | ads-popups | commit `48ea8294cbb6855914a243e891a3c3b13163fb58`, 2026-10-22 | [Dual licensed](https://easylist.to/pages/licence.html) GPL-3.0-or-later or CC BY-SA 3.0 Unported-or-later; used under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). Share-alike applies to `ads-popups.json` only, which is therefore CC BY-SA 4.0 and says so; attribution "The EasyList authors (https://easylist.to/)", changes noted. |
| [Chrome UX Report](https://developer.chrome.com/docs/crux) top lists via [zakird/crux-top-lists](https://github.com/zakird/crux-top-lists) — global and `il`, 2026-09 | choosing which adult entries ship; review order | commit `35ba243941defcd21882b134d9dd354824922482`, 2026-10-13 | Input only: no CrUX data ships (the files contain only entries from the lists above). |
| [Public Suffix List](https://publicsuffix.org/) | dropping public suffixes | commit `8437e5262f13305f94087abe699b4fe2be1d1de8`, 2026-10-22 | MPL-2.0; input only. |

Rejected:

- **Block List Project** (Unlicense) — its `porn.txt` and `gambling.txt` now merge upstream lists, Hagezi's (GPL-3.0)
  among them (`config/lists.yml`), so the Unlicense cannot be relied on for those files.
- **Hagezi** and **OISD** — GPL-3.0: copyleft for software, unclear for a data file embedded in the admin app.
- **UT1 blacklists** (Université Toulouse Capitole) — CC BY-SA 4.0 per its mirror, and it has a `redirector` category,
  but the official page (dsi.ut-capitole.fr/blacklists) was unreachable, so the licence could not be read at the source;
  the permissive sources above were enough.
- **Bon-Appetit/porn-domains** — CC BY-SA 4.0; acceptable, but not needed with two permissive sources.
- **Shallalist** (non-commercial, discontinued), **Cloudflare Radar** rankings (CC BY-NC), **Tranco** (no licence stated).
- **ShadowWhisperer `Lists/Ads`** — broad marketing list (consent tools, newsletters, survey tools, Google's consent
  page), not chosen for navigation blocking.

## Regenerating

```bash
node tools/starter-lists/update.mjs            # downloads the pinned sources to <tmp>/webfilter-starter-lists
node tools/starter-lists/update.mjs --offline  # reuse the cached downloads
```

It rewrites `adult.json`, `gambling.json`, `proxies.json` and `ads-popups.json` (the other files are edited by hand) and
writes a review report (`review.txt` in the cache folder): per list, what the guards dropped, which curated removals
applied, and the entries in the CrUX lists, most visited first. To update:

1. Pin newer commits in `SOURCES` (`update.mjs`) — ShadowWhisperer occasionally squashes its history, so an old commit
   can disappear; the tool then stops with an HTTP 404.
2. Run it, read the report from the top, and add false positives to `curation.mjs` (with a reason), then run it again.
3. Run `dotnet test tests/WebFilter.Core.Tests` and review the diff. Nothing runs this automatically; the lists ship
   embedded, and updating them from the internet is a follow-up (§3).
