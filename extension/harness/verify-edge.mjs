// Loads the built extension unpacked into the installed Edge (Playwright, channel "msedge") and checks it end to end.
//   npm run verify:edge        (builds dist/ and dist-test/ first)
// No native messaging host is registered and nothing is installed system-wide: the production build is checked as an
// unpacked (installType "development") extension, and the test build simulates a policy install and an in-worker fake
// host through its test hooks. HEADED=1 shows the browser.
import { chromium } from "playwright";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer as createHttpServer } from "node:http";
import { createServer as createHttpsServer } from "node:https";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(here, "..");
const DIST = join(ROOT, "dist");
const DIST_TEST = join(ROOT, "dist-test");
const RESULTS = join(here, "results");
const HTTP_PORT = 18080;
const HTTPS_PORT = 18443;
const BLOCKED_TITLE = "WebFilter · חסום";
const TIMEUP_TITLE = "WebFilter · הזמן נגמר";
const KHAN = "UCkhanKids00000000000001";
const GAMER = "UCgamerX0000000000000003";

// ---- results --------------------------------------------------------------------------------------------------------
const checks = [];
function check(id, ok, detail = {}) {
  checks.push({ id, ok: Boolean(ok), ...detail });
  console.log(`${ok ? "PASS" : "FAIL"}  ${id}${detail.note ? `  — ${detail.note}` : ""}`);
}

// ---- local web server: every host resolves here (--host-resolver-rules) -------------------------------------------
const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const hits = [];
// Jump-guard fixtures on site A (PROTECTION-SPEC §7). Site B is another registrable domain; www.site-a.test is the same site.
const B = (path) => `http://site-b.test:${HTTP_PORT}${path}`;
const JUMP_PAGES = {
  "/opener": `<a id="same-tab" href="${B("/landing")}">same tab</a>
    <a id="blank" target="_blank" href="${B("/blank")}">new tab</a>
    <button id="open" onclick="window.open('${B("/popup")}')">window.open</button>
    <button id="open-same" onclick="window.open('http://www.site-a.test:${HTTP_PORT}/same')">same site</button>
    <button id="open-later" onclick="const w = window.open('about:blank'); setTimeout(() => { w.location = '${B("/later")}'; }, 300)">later</button>`,
  "/meta": `<meta http-equiv="refresh" content="0;url=${B("/frommeta")}"><p>meta</p>`,
  "/js": `<script>location.replace('${B("/fromjs")}')</script><p>js</p>`,
  "/meta-same": `<meta http-equiv="refresh" content="0;url=http://www.site-a.test:${HTTP_PORT}/landing"><p>meta</p>`,
  // A news page with an ad slot from a blocked ad network (the ads-popups list blocks sub_frames).
  "/with-ad": `<h1>news</h1><iframe id="ad" src="http://ads.test:${HTTP_PORT}/banner?slot=1" width="300" height="250"></iframe>`,
};
// Ad blocking (PROTECTION-SPEC §12) with real hosts of the shipped ruleset: they resolve here like every other host, so
// nothing leaves the PC. AdSense's loader host is blocked for any party; the first third-party domain only from other sites.
const AD_RULESET = JSON.parse(readFileSync(join(ROOT, "rulesets", "ads.json"), "utf8"));
const AD_HOST = "pagead2.googlesyndication.com";
const AD_3P = AD_RULESET.find((r) => r.condition.domainType === "thirdParty").condition.requestDomains[0];
const A = (host, path) => `http://${host}:${HTTP_PORT}${path}`;
const AD_PAGE = `<h1>news</h1><img id="own" src="/own.png" width="20" height="20">
  <script src="${A(AD_HOST, "/pagead/js/adsbygoogle.js")}"></script>
  <img id="ad-img" src="${A(AD_HOST, "/ad.gif")}" width="300" height="50">
  <iframe id="ad-frame" src="${A(AD_HOST, "/frame")}" width="300" height="250"></iframe>
  <script src="${A(AD_3P, "/tag.js")}"></script>
  <script>
    fetch("${A(AD_HOST, "/xhr")}", { mode: "no-cors" }).then(() => "loaded", () => "blocked").then((r) => { document.documentElement.dataset.xhr = r; });
    navigator.sendBeacon("${A(AD_HOST, "/beacon")}", "x");
  </script>`;
function handler(req, res) {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const host = url.hostname;
  hits.push(`${host}${url.pathname}`);
  const send = (title, body) => {
    res.writeHead(200, { "content-type": "text/html; charset=utf-8" });
    res.end(`<!doctype html><html><head><title>${esc(title)}</title></head><body>${body}</body></html>`);
  };
  if (host.endsWith("youtube.com")) {
    if (url.pathname === "/") return send("YT home", "<h1>home</h1>");
    if (url.pathname === "/watch") {
      // Mimics the trusted source of spike S4: #movie_player.getPlayerResponse().videoDetails for the current URL.
      // Plus a Shorts shelf and the side menu's Shorts entry, which the bridge hides while Shorts are blocked.
      return send(
        "YT watch",
        `<ytd-guide-entry-renderer id="guide-shorts"><a href="/shorts" title="Shorts">Shorts</a></ytd-guide-entry-renderer>
        <ytd-guide-entry-renderer id="guide-home"><a href="/" title="Home">Home</a></ytd-guide-entry-renderer>
        <ytd-reel-shelf-renderer id="shelf">shorts shelf</ytd-reel-shelf-renderer>
        <div id="movie_player">player</div><script>
          const owners = { vidKhan0001: "${KHAN}", vidGamr0001: "${GAMER}" };
          document.getElementById("movie_player").getPlayerResponse = () => {
            const v = new URLSearchParams(location.search).get("v");
            return { videoDetails: { videoId: v, channelId: owners[v] } };
          };
        </script>`,
      );
    }
    return send(`YT ${url.pathname}`, "<h1>yt</h1>");
  }
  const jump = host === "site-a.test" ? JUMP_PAGES[url.pathname] : undefined;
  if (jump) return send(`SITE ${host}${url.pathname}`, jump);
  if (host === "site-a.test" && url.pathname === "/with-ads") return send(`SITE ${host}${url.pathname}`, AD_PAGE);
  if (host === AD_3P && url.pathname === "/home") return send(`SITE ${host}${url.pathname}`, `<script src="/own.js"></script><h1>own page</h1>`);
  return send(`SITE ${host}${url.pathname}`, `<h1>${esc(host)}</h1>`);
}

function makeCert(dir) {
  try {
    const key = join(dir, "key.pem");
    const cert = join(dir, "cert.pem");
    execFileSync("openssl", ["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", key, "-out", cert, "-days", "2",
      "-subj", "/CN=www.youtube.com", "-addext", "subjectAltName=DNS:www.youtube.com,DNS:*.youtube.com"], { stdio: "ignore" });
    return { key: readFileSync(key), cert: readFileSync(cert) };
  } catch {
    return null;
  }
}

// ---- browser -----------------------------------------------------------------------------------------------------
async function launch(extensionDir) {
  const profile = mkdtempSync(join(tmpdir(), "wf-m3-"));
  const context = await chromium.launchPersistentContext(profile, {
    channel: "msedge",
    headless: !process.env.HEADED,
    locale: "he-IL",
    ignoreHTTPSErrors: true,
    args: [
      `--disable-extensions-except=${extensionDir}`,
      `--load-extension=${extensionDir}`,
      `--host-resolver-rules=MAP * 127.0.0.1, EXCLUDE localhost`,
      "--ignore-certificate-errors",
      "--lang=he",
    ],
  });
  let [sw] = context.serviceWorkers();
  if (!sw) sw = await context.waitForEvent("serviceworker", { timeout: 15000 });
  const id = new URL(sw.url()).host;
  return {
    context,
    sw,
    id,
    page: (path) => `chrome-extension://${id}/${path}`,
    async close() {
      await context.close();
      rmSync(profile, { recursive: true, force: true });
    },
  };
}

async function visit(page, url, timeout = 15000) {
  try {
    await page.goto(url, { waitUntil: "load", timeout });
  } catch {
    // Chromium reports an abort when a navigation is redirected to an extension page.
    await page.waitForLoadState("load", { timeout: 5000 }).catch(() => {});
  }
  return page.url();
}

async function waitFor(fn, timeout = 8000, step = 100) {
  const until = Date.now() + timeout;
  let last;
  while (Date.now() < until) {
    try {
      last = await fn();
      if (last) return last;
    } catch {
      // page navigating
    }
    await new Promise((r) => setTimeout(r, step));
  }
  return last;
}

const pageInfo = (page) =>
  page.evaluate(() => ({
    title: document.title,
    ready: document.documentElement.dataset.ready ?? null,
    reason: document.documentElement.dataset.reason ?? null,
    reasonSource: document.documentElement.dataset.reasonSource ?? null,
    url: document.documentElement.dataset.url ?? null,
    text: document.getElementById("reason")?.textContent ?? "",
    category: document.getElementById("category-row")?.hidden === false ? document.getElementById("category")?.textContent : null,
    canRequest: document.documentElement.dataset.canRequest ?? null,
    requestResult: document.documentElement.dataset.requestResult ?? null,
    lang: document.documentElement.lang,
    dir: document.documentElement.dir,
  }));

const enabledRulesets = (b) => b.sw.evaluate(() => chrome.declarativeNetRequest.getEnabledRulesets());
const dynamicRules = (b) => b.sw.evaluate(() => chrome.declarativeNetRequest.getDynamicRules());
const sessionRules = (b) => b.sw.evaluate(() => chrome.declarativeNetRequest.getSessionRules());

// ---- rules in the shape of the v2 compiler (DnrPriorities / DnrRuleIds) -------------------------------------------
const FRAMES = ["main_frame", "sub_frame"];
function compiledRules(id, { allowed = [], blocked = [], bigList = 0, prefixBlock = null } = {}) {
  const redirect = { type: "redirect", redirect: { regexSubstitution: `chrome-extension://${id}/blocked.html#\\0` } };
  const rules = [
    { id: 1, priority: 1, action: redirect, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
    { id: 3, priority: 196608, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["youtube-nocookie.com", "youtube.com"] } },
  ];
  if (allowed.length) rules.push({ id: 4321, priority: 65544, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: allowed, isUrlFilterCaseSensitive: false } });
  // Redirect rules need a regexFilter for regexSubstitution (the compiler adds one to every block rule).
  if (blocked.length) rules.push({ id: 5555, priority: 131083, action: redirect, condition: { resourceTypes: FRAMES, requestDomains: blocked, regexFilter: "^(.*)$" } });
  // A member prefix block (e.g. host/secret) above the allowed host, as the compiler emits it.
  if (prefixBlock) rules.push({ id: 6000, priority: 131072 + 2 * 400 + 1, action: redirect, condition: { resourceTypes: FRAMES, requestDomains: [prefixBlock.host], regexFilter: `^[^:]+://[^/]+${prefixBlock.path}(?:[/?#].*)?$` } });
  if (bigList) {
    const domains = Array.from({ length: bigList }, (_, i) => `d${String(i).padStart(6, "0")}.example`);
    rules.push({ id: 7777, priority: 65545, action: redirect, condition: { resourceTypes: FRAMES, requestDomains: domains, regexFilter: "^(.*)$" } });
  }
  const timeUp = [
    { id: 10, priority: 262144, action: redirect, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
    { id: 9001, priority: 262145, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["mashov.test"] } },
  ];
  return { rules, timeUp };
}

const S = (host, path = "/") => `http://${host}:${HTTP_PORT}${path}`;
const Y = (path) => `https://www.youtube.com:${HTTPS_PORT}${path}`;

// ---- run ---------------------------------------------------------------------------------------------------------
const scratch = mkdtempSync(join(tmpdir(), "wf-m3-cert-"));
const tls = makeCert(scratch);
const http = createHttpServer(handler).listen(HTTP_PORT, "127.0.0.1");
const https = tls ? createHttpsServer(tls, handler).listen(HTTPS_PORT, "127.0.0.1") : null;
let failures = 0;

try {
  // ===== Production build: unpacked → installType "development" → inert ==========================================
  {
    const b = await launch(DIST);
    check("P1 service worker starts (production build)", !!b.sw && /^[a-p]{32}$/.test(b.id), { note: b.id });
    const self = await b.sw.evaluate(() => chrome.management.getSelf().then((s) => s.installType));
    check("P2 unpacked install type is not admin", self === "development", { note: self });
    const rulesets = await waitFor(async () => {
      const r = await enabledRulesets(b);
      return r.includes("failclosed") ? null : r;
    });
    check("P3 not policy-installed → failclosed disabled, no dynamic rules", Array.isArray(rulesets) && (await dynamicRules(b)).length === 0, { note: JSON.stringify(rulesets) });
    check("P3b … and the ads ruleset stays off (manifest: enabled false)", Array.isArray(rulesets) && !rulesets.includes("ads"), { note: JSON.stringify(rulesets) });
    check("P4 no test hooks in the production worker", await b.sw.evaluate(() => typeof globalThis.wfTest === "undefined"));

    const page = await b.context.newPage();
    const pageLog = [];
    page.on("console", (m) => pageLog.push(`${m.type()}: ${m.text()}`));
    page.on("pageerror", (e) => pageLog.push(`pageerror: ${e.message}`));
    await visit(page, S("site.test", "/hello"));
    check("P5 inert extension does not block browsing", (await page.title()) === "SITE site.test/hello", { note: await page.title() });

    await visit(page, b.page("blocked.html?reason=failclosed"));
    const info = await waitFor(async () => {
      const i = await pageInfo(page);
      return i.ready ? i : null;
    }, 20000); // the first extension page after a build can wait on a cold worker
    // On a miss, say where the page is (a cold first extension page after a build has been seen to take long).
    const p6Miss = info?.ready ? "" : ` — missed: ${JSON.stringify({ url: page.url(), now: await pageInfo(page).catch((e) => String(e).split("\n")[0]), log: pageLog })}`;
    check("P6 blocked.html?reason=failclosed: exact window title", info?.title === BLOCKED_TITLE, { note: `${info?.title}${p6Miss}` });
    check("P7 blocked.html shows the fail-closed reason in Hebrew (RTL)", info?.reason === "failclosed" && /שירות הסינון/.test(info.text) && info.dir === "rtl" && info.lang === "he", { note: info?.text });

    await visit(page, b.page("timeup.html"));
    const timeupTitle = await waitFor(async () => ((await page.title()) === TIMEUP_TITLE ? TIMEUP_TITLE : null));
    check("P8 timeup.html: exact window title", timeupTitle === TIMEUP_TITLE, { note: await page.title() });
    await visit(page, b.page("popup.html"));
    const popup = await waitFor(() => page.evaluate(() => (document.documentElement.dataset.connection === "inert" ? document.getElementById("status-title").textContent : null)));
    check("P9 popup shows 'not installed by policy'", !!popup, { note: popup });
    await visit(page, b.page("my-channels.html"));
    check("P10 my-channels.html renders", (await page.title()) === "WebFilter · הערוצים שלי");
    await b.close();
  }

  // ===== Test build: simulated policy install ======================================================================
  {
    const b = await launch(DIST_TEST);
    check("T1 service worker starts (test build, hooks present)", await b.sw.evaluate(() => globalThis.wfTest?.marker === "wf-test-hooks"));

    await b.sw.evaluate(() => globalThis.wfTest.simulatePolicyInstall());
    const status = await waitFor(async () => {
      const s = await b.sw.evaluate(() => globalThis.wfTest.status());
      return s.connection === "disconnected" ? s : null;
    });
    check("T2 policy install + no native host → port disconnects, status disconnected", status?.connection === "disconnected" && status.policyInstalled, { note: status?.lastError });
    check("T3 failclosed enabled while disconnected", (await enabledRulesets(b)).includes("failclosed"));
    const badge = await b.sw.evaluate(() => chrome.action.getBadgeText({}));
    check("T4 badge shows ! while fail-closed", badge === "!", { note: badge });

    const page = await b.context.newPage();
    hits.length = 0;
    const original = S("site.test", "/page?q=1");
    const landed = await visit(page, original);
    const info = await waitFor(async () => {
      const i = await pageInfo(page);
      return i.ready ? i : null;
    });
    check("T5 navigation redirected to blocked.html?reason=failclosed", landed.startsWith(b.page("blocked.html?reason=failclosed")), { note: landed });
    check("T6 fail-closed page: exact title and reason", info?.title === BLOCKED_TITLE && info.reason === "failclosed", { note: info?.text });
    check("T7 fail-closed page knows the original URL (webNavigation)", info?.url === original, { note: info?.url });
    check("T8 the blocked request never left the browser", !hits.some((h) => h.startsWith("site.test")), { note: hits.join(",") });
    const reconnecting = await b.sw.evaluate(async () => {
      await new Promise((r) => setTimeout(r, 3500));
      return globalThis.wfTest.status();
    });
    check("T9 still fail-closed after retries (backoff)", reconnecting.connection === "disconnected" && (await enabledRulesets(b)).includes("failclosed"));

    // ---- in-worker fake host: hello + rules ---------------------------------------------------------------------
    const { rules, timeUp } = compiledRules(b.id, { allowed: ["allowed.test", "mashov.test"], prefixBlock: { host: "allowed.test", path: "/secret" } });
    const youTube = { mode: "allowlist", allowed: [{ channelId: KHAN, handle: "@khankids-sim", title: "Khan Kids (sim)" }], blocked: [] };
    const script = {
      rules: { rulesVersion: 3, dynamicRules: rules, timeUpSessionRules: timeUp, youTube, time: { state: "ok", remainingMinutes: 60, windowEndsAt: "19:00" } },
      decide: { [S("blocked.test", "/x?q=1")]: { reason: "blocked_category", category: "social" } },
    };
    await b.sw.evaluate((s) => globalThis.wfTest.useFakeHost(s), script);
    const ready = await b.sw.evaluate(() => globalThis.wfTest.whenReady(5000));
    check("T10 hello + rules from the (fake) service → ready", ready);
    check("T11 failclosed disabled after the rules were installed", !(await enabledRulesets(b)).includes("failclosed"));
    check("T12 dynamic rules installed", (await dynamicRules(b)).map((r) => r.id).sort((x, y) => x - y).join(",") === "1,3,4321,6000");

    // The fail-closed tab returns to its URL by itself, now under the real rules (not allowed → compiled block).
    const back = await waitFor(async () => {
      const i = await pageInfo(page);
      return i.ready && i.reason !== "failclosed" ? i : null;
    });
    check("T13 fail-closed page retried its URL once connected", back?.url === original && back.reason === "blocked_not_in_allowlist", { note: `${back?.reason} ${back?.url}` });

    await visit(page, S("allowed.test"));
    check("T14 allowed site loads", (await page.title()) === "SITE allowed.test/");
    await page.evaluate(() => history.pushState({}, "", "/secret/page"));
    const inPage = await waitFor(async () => page.url() === `${b.page("blocked.html")}#${S("allowed.test", "/secret/page")}`);
    check("T14b in-page (history API) navigation to a blocked path → block page", !!inPage, { note: page.url() });
    await page.waitForLoadState("load").catch(() => {});

    hits.length = 0;
    const blockedUrl = S("blocked.test", "/x?q=1");
    const blockedLanded = await visit(page, blockedUrl);
    const bi = await waitFor(async () => {
      const i = await pageInfo(page);
      return i.ready ? i : null;
    });
    check("T15 compiled rule redirects to blocked.html#<original URL>", blockedLanded === `${b.page("blocked.html")}#${blockedUrl}`, { note: blockedLanded });
    check("T16 reason comes from the service (reportBlockResult), category shown", bi?.reason === "blocked_category" && bi.reasonSource === "service" && bi.category === "social", { note: JSON.stringify(bi) });
    check("T17 blocked request never left the browser", !hits.some((h) => h.startsWith("blocked.test")));
    const received = await b.sw.evaluate(() => globalThis.wfTest.received());
    check("T18 reportBlock sent with the URL", received.some((m) => m.type === "reportBlock" && m.url === blockedUrl), { note: JSON.stringify(received.filter((m) => m.type === "reportBlock")) });

    // ---- request access → approved → the page returns -----------------------------------------------------------
    check("T19 request button offered", bi?.canRequest === "true" && (await page.isVisible("#ask-url")));
    await page.fill("#note", "לשיעורי בית");
    await page.click("#ask-url");
    const result = await waitFor(async () => (await pageInfo(page)).requestResult === "created");
    check("T20 request sent and result shown (created)", !!result);
    const reqs = (await b.sw.evaluate(() => globalThis.wfTest.received())).filter((m) => m.type === "requestAccess");
    check("T21 requestAccess carries kind, target and note", reqs[0]?.kind === "url" && reqs[0].target === blockedUrl && reqs[0].note === "לשיעורי בית", { note: JSON.stringify(reqs[0]) });
    const approvedRules = compiledRules(b.id, { allowed: ["allowed.test", "mashov.test", "blocked.test"] });
    await b.sw.evaluate((s) => {
      globalThis.wfTest.setScript(s);
      globalThis.wfTest.push({ type: "accessDecided", requestId: "ar-1", status: "approved" });
    }, { ...script, rules: { ...script.rules, rulesVersion: 4, dynamicRules: approvedRules.rules } });
    const returned = await waitFor(async () => (await page.title()) === "SITE blocked.test/x", 8000);
    check("T22 approval refreshes rules and the block page reloads the site", !!returned, { note: await page.title() });

    // ---- time up ---------------------------------------------------------------------------------------------
    const tab2 = await b.context.newPage();
    await visit(tab2, S("mashov.test", "/grades"));
    await visit(page, S("allowed.test", "/lesson"));
    // The fake host answers heartbeats with the same state it pushes (as the service does).
    const expired = { state: "expired", remainingMinutes: 0, windowEndsAt: "19:00" };
    await b.sw.evaluate(({ s, t }) => {
      globalThis.wfTest.setScript({ ...s, heartbeat: t });
      globalThis.wfTest.push({ type: "timeState", ...t });
    }, { s: { ...script, rules: { ...script.rules, dynamicRules: approvedRules.rules } }, t: expired });
    const moved = await waitFor(async () => (await page.title()) === TIMEUP_TITLE);
    check("T23 time up: open tab moved to timeup.html (exact title)", !!moved, { note: page.url() });
    check("T24 time-up session rules installed", (await sessionRules(b)).map((r) => r.id).sort((x, y) => x - y).join(",") === "10,9001");
    check("T25 always-allowed tab stays open", (await tab2.title()) === "SITE mashov.test/grades");
    await visit(tab2, S("allowed.test", "/other"));
    const ti = await waitFor(async () => {
      const i = await pageInfo(tab2);
      return i.ready ? i : null;
    });
    check("T26 new navigation while time is up → block page with blocked_daily_limit", ti?.title === BLOCKED_TITLE && ti.reason === "blocked_daily_limit", { note: ti?.reason });
    const timeReq = await tab2.isVisible("#ask-15");
    check("T27 time block offers 'ask for more time'", timeReq);
    const okAgain = { state: "ok", remainingMinutes: 15, windowEndsAt: "19:00" };
    await b.sw.evaluate(({ s, t }) => {
      globalThis.wfTest.setScript({ ...s, heartbeat: t });
      globalThis.wfTest.push({ type: "timeState", ...t });
    }, { s: { ...script, rules: { ...script.rules, dynamicRules: approvedRules.rules } }, t: okAgain });
    const resumed = await waitFor(async () => (await page.title()) === "SITE allowed.test/lesson");
    check("T28 time back: timeup tab returns to its page, session rules removed", !!resumed && (await sessionRules(b)).length === 0, { note: await page.title() });
    await tab2.close();

    // ---- chunked rules ------------------------------------------------------------------------------------------
    const big = compiledRules(b.id, { allowed: ["allowed.test"], bigList: 120000 });
    await b.sw.evaluate((s) => globalThis.wfTest.useFakeHost(s), { ...script, chunkBytes: 700 * 1024, rules: { ...script.rules, dynamicRules: big.rules } });
    const bigReady = await b.sw.evaluate(() => globalThis.wfTest.whenReady(15000));
    const bigRule = (await dynamicRules(b)).find((r) => r.id === 7777);
    check("T29 a >1 MB rules reply arrives in chunks and is reassembled", bigReady && bigRule?.condition.requestDomains.length === 120000, { note: `${bigRule?.condition.requestDomains.length} domains` });
    await visit(page, S("d000042.example"));
    check("T30 a domain from the big list is blocked", (await page.title()) === BLOCKED_TITLE);

    // ---- disconnect → fail-closed → reconnect -------------------------------------------------------------------
    await b.sw.evaluate((s) => globalThis.wfTest.useFakeHost(s), script);
    await b.sw.evaluate(() => globalThis.wfTest.whenReady(5000));
    await b.sw.evaluate(() => globalThis.wfTest.hangUp());
    const down = await waitFor(async () => (await enabledRulesets(b)).includes("failclosed"));
    check("T31 host exit → failclosed re-enabled and dynamic rules cleared", !!down && (await dynamicRules(b)).length === 0);
    const again = await b.sw.evaluate(async () => {
      const ok = await globalThis.wfTest.whenReady(4000);
      return { ok, connections: globalThis.wfTest.connections() };
    });
    check("T32 reconnects after the backoff and recovers", again.ok && again.connections === 2 && !(await enabledRulesets(b)).includes("failclosed"), { note: JSON.stringify(again) });

    // ---- YouTube ------------------------------------------------------------------------------------------------
    if (https) {
      await visit(page, Y("/watch?v=vidKhan0001"));
      const visible = await waitFor(() => page.evaluate(() => document.title === "YT watch" && getComputedStyle(document.documentElement).visibility === "visible"));
      check("T33 YouTube allowlist: approved channel plays and the page is revealed", !!visible, { note: page.url() });
      await page.evaluate(() => history.pushState({}, "", "/watch?v=vidGamr0001"));
      const spa = await waitFor(async () => {
        const i = await pageInfo(page);
        return i.ready ? i : null;
      });
      check("T34 SPA navigation to another channel → block page (blocked_youtube_channel)", spa?.reason === "blocked_youtube_channel" && page.url().includes(`channel=${GAMER}`), { note: page.url() });
      check("T35 YouTube block offers 'approve this channel'", await page.isVisible("#ask-channel"));
      await visit(page, Y("/watch?v=vidGamr0001"));
      const full = await waitFor(async () => (await pageInfo(page)).reason === "blocked_youtube_channel");
      check("T36 full load of a non-approved channel → blocked", !!full);
      await visit(page, Y("/"));
      const home = await waitFor(async () => page.url().startsWith(b.page("my-channels.html")) && (await page.evaluate(() => document.querySelectorAll("#channels a").length)) === 1);
      check("T37 YouTube home → my-channels.html with the approved channel", !!home, { note: page.url() });
    } else {
      check("T33–T37 YouTube fixtures skipped (openssl not available)", true, { note: "skipped" });
    }

    await visit(page, b.page("popup.html"));
    const popupState = await waitFor(() => page.evaluate(() => document.documentElement.dataset.connection));
    check("T38 popup shows the connection state", popupState === "service", { note: popupState });
    await b.close();
  }

  // ===== Test build: protection features (PROTECTION-SPEC §7–§9) ===============================================
  {
    const b = await launch(DIST_TEST);
    await b.sw.evaluate(() => globalThis.wfTest.simulatePolicyInstall());
    const { rules, timeUp } = compiledRules(b.id, { allowed: ["site-a.test", "site-b.test", "site-c.test", "google.com"], blocked: ["ads.test"] });
    const youTube = { mode: "allowlist", allowed: [{ channelId: KHAN, handle: "@khankids-sim", title: "Khan Kids (sim)" }], blocked: [] };
    const child = { jumpGuard: true, blockYouTubeShorts: true, logSearches: true, canOverrideJump: false, blockAds: false };
    const script = { rules: { rulesVersion: 7, dynamicRules: rules, timeUpSessionRules: timeUp, youTube, time: { state: "ok", remainingMinutes: 60, windowEndsAt: null }, features: child } };
    await b.sw.evaluate((s) => globalThis.wfTest.useFakeHost(s), script);
    const ready = await b.sw.evaluate(() => globalThis.wfTest.whenReady(5000));
    const status = await b.sw.evaluate(() => globalThis.wfTest.status());
    check("F1 features from the rules reply are in force", ready && JSON.stringify(status.features) === JSON.stringify(child), { note: JSON.stringify(status.features) });
    const received = () => b.sw.evaluate(() => globalThis.wfTest.received());
    const jumpRuleIds = async () => (await sessionRules(b)).map((r) => r.id).filter((id) => id >= 1_100_000_000);
    // Generous waits: the dev PC often runs other builds in parallel. On a miss, the note says where the tab is.
    const blockInfo = async (p) => {
      const found = await waitFor(async () => {
        const i = await pageInfo(p);
        return i.ready && i.title === BLOCKED_TITLE ? { ...i, href: p.url(), canOverride: await p.evaluate(() => document.documentElement.dataset.canOverride ?? null) } : null;
      }, 20000);
      if (found) return found;
      const last = await pageInfo(p).catch(() => null);
      return { missed: true, href: p.url(), title: last?.title ?? null, ready: last?.ready ?? null, reason: last?.reason ?? null, url: last?.url ?? null };
    };
    const miss = (info) => (info?.missed ? ` — missed: ${JSON.stringify(info)}` : "");
    const opened = async (page, selector) => {
      const [tab] = await Promise.all([b.context.waitForEvent("page", { timeout: 10000 }), page.click(selector)]);
      return tab;
    };

    // ---- jump guard -----------------------------------------------------------------------------------------------
    const page = await b.context.newPage();
    await visit(page, S("site-a.test", "/opener"));
    await page.click("#same-tab");
    const sameTab = await waitFor(async () => (await page.title()) === "SITE site-b.test/landing", 15000);
    check("J1 same-tab link click to another site loads", !!sameTab, { note: page.url() });

    await visit(page, S("site-a.test", "/opener"));
    hits.length = 0;
    const popup = await opened(page, "#open");
    const popupInfo = await blockInfo(popup);
    check("J2 window.open to another site → block page (blocked_jump)", popupInfo?.reason === "blocked_jump" && popupInfo.url === B("/popup"), {
      note: `${popupInfo?.reason} ${popupInfo?.url}; target requested: ${hits.includes("site-b.test/popup")}${miss(popupInfo)}`,
    });
    check("J2b Hebrew text: the site tried to open another site", /האתר ניסה לפתוח אתר אחר/.test(popupInfo?.text ?? ""), { note: popupInfo?.text });
    check("J2c child: no 'open anyway'", popupInfo?.canOverride === "false" && !(await popup.isVisible("#override")));
    const blankTab = await opened(page, "#blank");
    const blankInfo = await blockInfo(blankTab);
    check("J3 target=_blank link to another site → block page (blocked_jump)", blankInfo?.reason === "blocked_jump" && blankInfo.url === B("/blank"), { note: `${blankInfo?.reason} ${blankInfo?.url}${miss(blankInfo)}` });
    const sameSite = await opened(page, "#open-same");
    const sameLoaded = await waitFor(async () => (await sameSite.title()) === "SITE www.site-a.test/same", 15000);
    check("J4 window.open to the same site (www.) loads", !!sameLoaded, { note: sameSite.url() });
    hits.length = 0;
    const later = await opened(page, "#open-later");
    const laterInfo = await blockInfo(later);
    check("J5 window.open(about:blank), then opened.location = another site → block page", laterInfo?.reason === "blocked_jump" && laterInfo.url === B("/later"), { note: `${laterInfo?.reason} ${laterInfo?.url}${miss(laterInfo)}` });
    check("J5b … stopped by the tab's DNR rule before any request", !hits.includes("site-b.test/later"), { note: hits.join(",") });
    const leftover = await waitFor(async () => ((await jumpRuleIds()).length === 0 ? "none" : null), 5000);
    check("J6 tab rules are gone once the block pages show (a typed address works there)", leftover === "none", { note: JSON.stringify(await jumpRuleIds()) });
    await visit(popup, S("site-c.test", "/typed"));
    check("J7 typed address in a block-page tab loads", (await popup.title()) === "SITE site-c.test/typed", { note: popup.url() });

    hits.length = 0;
    await visit(page, S("site-a.test", "/meta"));
    const metaInfo = await blockInfo(page);
    check("J8 meta-refresh redirect to another site → block page", metaInfo?.reason === "blocked_jump" && metaInfo.url === B("/frommeta"), {
      note: `${metaInfo?.reason} ${metaInfo?.url}; target requested: ${hits.includes("site-b.test/frommeta")}${miss(metaInfo)}`,
    });
    await visit(page, S("site-a.test", "/js"));
    const jsInfo = await blockInfo(page);
    check("J9 script redirect while loading to another site → block page", jsInfo?.reason === "blocked_jump" && jsInfo.url === B("/fromjs"), { note: `${jsInfo?.reason} ${jsInfo?.url}${miss(jsInfo)}` });
    await visit(page, S("site-a.test", "/meta-same"));
    const metaSame = await waitFor(async () => (await page.title()) === "SITE www.site-a.test/landing", 15000);
    check("J10 redirect within the same site loads", !!metaSame, { note: page.url() });
    const jumpReports = (await received()).filter((m) => m.type === "reportBlock" && m.reason === "blocked_jump").map((m) => m.url);
    check("J11 every jump block reported once as reportBlock {reason: blocked_jump}", [B("/popup"), B("/blank"), B("/later"), B("/frommeta"), B("/fromjs")].every((u) => jumpReports.filter((r) => r === u).length === 1), { note: JSON.stringify(jumpReports) });
    for (const tab of [popup, blankTab, sameSite, later]) await tab.close();

    // ---- a blocked ad frame: quiet placeholder, nothing reported ---------------------------------------------------
    hits.length = 0;
    const adUrl = `http://ads.test:${HTTP_PORT}/banner?slot=1`;
    await visit(page, S("site-a.test", "/with-ad"));
    const adFrame = await waitFor(async () => {
      const frame = page.frames().find((f) => f.url().startsWith(b.page("blocked.html")));
      if (!frame) return null;
      const state = await frame.evaluate(() => ({
        quiet: document.documentElement.dataset.frame ?? null,
        ready: document.documentElement.dataset.ready ?? null,
        mainShown: getComputedStyle(document.querySelector("main")).display !== "none",
        visibleButtons: [...document.querySelectorAll("button")].filter((el) => el.offsetParent !== null).length,
        text: document.body.innerText.trim(),
        background: getComputedStyle(document.body).backgroundColor,
      }));
      return state.ready === "true" ? { ...state, url: frame.url() } : null;
    }, 15000);
    await new Promise((r) => setTimeout(r, 1500)); // anything the frame would send has arrived by now
    const frameMessages = (await received()).filter((m) => (m.type === "reportBlock" || m.type === "decideUrl") && String(m.url).includes("ads.test"));
    check("A1 a blocked ad frame shows a quiet empty placeholder (no block page, no buttons)", adFrame?.quiet === "quiet" && !adFrame.mainShown && adFrame.visibleButtons === 0 && adFrame.text === "" && adFrame.url === `${b.page("blocked.html")}#${adUrl}`, {
      note: JSON.stringify(adFrame),
    });
    check("A2 … and sends nothing to the host (no reportBlock, no decideUrl); the ad request never left", frameMessages.length === 0 && !hits.some((h) => h.startsWith("ads.test")) && (await page.title()) === "SITE site-a.test/with-ad", {
      note: `${JSON.stringify(frameMessages)} ${hits.join(",")}`,
    });

    // ---- teen: "open anyway" --------------------------------------------------------------------------------------
    const teen = { ...child, blockYouTubeShorts: false, canOverrideJump: true };
    await b.sw.evaluate((s) => {
      globalThis.wfTest.setScript(s);
      globalThis.wfTest.push({ type: "rulesChanged", rulesVersion: 8 });
    }, { rules: { ...script.rules, rulesVersion: 8, features: teen } });
    const teenOn = await waitFor(() => b.sw.evaluate(() => globalThis.wfTest.status().features.canOverrideJump));
    await visit(page, S("site-a.test", "/opener"));
    const teenTab = await opened(page, "#open");
    const teenInfo = await blockInfo(teenTab);
    const buttonText = await teenTab.textContent("#override").catch(() => null);
    check("O1 teen: the jump block page offers 'פתח בכל זאת'", !!teenOn && teenInfo?.canOverride === "true" && buttonText === "פתח בכל זאת" && (await teenTab.isVisible("#override")), { note: buttonText });
    await teenTab.click("#override");
    const overridden = await waitFor(async () => (await teenTab.title()) === "SITE site-b.test/popup", 15000);
    check("O2 'open anyway' opens the site", !!overridden, { note: teenTab.url() });
    const overrides = (await received()).filter((m) => m.type === "reportOverride");
    check("O3 reportOverride {url, reason: blocked_jump} sent", overrides.length === 1 && overrides[0].url === B("/popup") && overrides[0].reason === "blocked_jump", { note: JSON.stringify(overrides) });
    const again = await opened(page, "#open");
    const againInfo = await blockInfo(again);
    check("O4 once: the same jump again is blocked again", againInfo?.reason === "blocked_jump", { note: againInfo?.reason });
    await teenTab.close();
    await again.close();

    // ---- YouTube Shorts (child: blockYouTubeShorts) -----------------------------------------------------------------
    await b.sw.evaluate((s) => {
      globalThis.wfTest.setScript(s);
      globalThis.wfTest.push({ type: "rulesChanged", rulesVersion: 9 });
    }, { rules: { ...script.rules, rulesVersion: 9, features: child } });
    await waitFor(() => b.sw.evaluate(() => globalThis.wfTest.status().features.blockYouTubeShorts));
    const shortsRule = await waitFor(async () => (await sessionRules(b)).find((r) => r.id === 1_000_000_001) ?? null);
    check("S1 the Shorts session rule is installed", !!shortsRule && shortsRule.priority > 196608 && shortsRule.priority < 262144, { note: JSON.stringify(shortsRule?.condition) });
    if (https) {
      hits.length = 0;
      const short = Y("/shorts/vidKhan0001");
      await visit(page, short);
      const shortInfo = await blockInfo(page);
      check("S2 full load of /shorts/ → block page (blocked_youtube_shorts), never requested", shortInfo?.reason === "blocked_youtube_shorts" && shortInfo.url === short && !hits.some((h) => h.includes("/shorts/")), {
        note: `${shortInfo?.reason} ${shortInfo?.url} ${hits.join(",")}`,
      });
      await visit(page, Y("/watch?v=vidKhan0001"));
      const watching = await waitFor(() => page.evaluate(() => document.title === "YT watch" && getComputedStyle(document.documentElement).visibility === "visible"));
      const hidden = await waitFor(() =>
        page.evaluate(() => {
          const gone = (id) => getComputedStyle(document.getElementById(id)).display === "none";
          return document.documentElement.dataset.wfShorts === "hidden" && gone("shelf") && gone("guide-shorts") && !gone("guide-home") ? true : null;
        }),
      );
      check("S3 approved video plays; the Shorts shelf and menu entry are hidden, the rest is not", !!watching && !!hidden);
      await page.evaluate(() => history.pushState({}, "", "/shorts/vidSpa0001"));
      const spaInfo = await blockInfo(page);
      check("S4 in-site (SPA) navigation to /shorts/ → block page (blocked_youtube_shorts)", spaInfo?.reason === "blocked_youtube_shorts" && spaInfo.url === Y("/shorts/vidSpa0001"), { note: `${spaInfo?.reason} ${spaInfo?.url}` });
      const shortsReports = (await received()).filter((m) => m.type === "reportBlock" && m.reason === "blocked_youtube_shorts");
      check("S5 Shorts blocks reported with their reason", shortsReports.length === 2, { note: JSON.stringify(shortsReports.map((m) => m.url)) });

      // ---- search log ---------------------------------------------------------------------------------------------
      const G = (path) => `https://www.google.com:${HTTPS_PORT}${path}`;
      const searchUrl = G("/search?q=%D7%97%D7%AA%D7%95%D7%9C%D7%99%D7%9D+%D7%95%D7%9B%D7%9C%D7%91%D7%99%D7%9D&hl=he");
      await visit(page, searchUrl);
      await page.reload({ waitUntil: "load" });
      await visit(page, G("/search?q=%D7%97%D7%AA%D7%95%D7%9C%D7%99%D7%9D+%D7%95%D7%9B%D7%9C%D7%91%D7%99%D7%9D&start=10"));
      const searches = await waitFor(async () => {
        const s = (await received()).filter((m) => m.type === "reportSearch");
        return s.length > 0 ? s : null;
      });
      await new Promise((r) => setTimeout(r, 500));
      const allSearches = (await received()).filter((m) => m.type === "reportSearch");
      check("Q1 a Google /search?q= page sends reportSearch {engine, query, url}", searches?.[0]?.engine === "google" && searches[0].query === "חתולים וכלבים" && searches[0].url === searchUrl, { note: JSON.stringify(searches?.[0]) });
      check("Q2 … once (not again for the reload or page 2)", allSearches.length === 1, { note: `${allSearches.length} reports` });
    } else {
      check("S2–Q2 YouTube and search fixtures skipped (openssl not available)", true, { note: "skipped" });
    }
    await visit(page, b.page("popup.html"));
    const notice = await waitFor(() => page.evaluate(() => (document.getElementById("search-notice")?.hidden === false ? document.getElementById("search-notice").textContent : null)));
    check("Q3 popup tells the member: החיפושים שלך גלויים להורים", notice === "החיפושים שלך גלויים להורים", { note: notice });
    await b.close();
  }

  // ===== Test build: ad blocking (PROTECTION-SPEC §12) ===============================================================
  {
    const b = await launch(DIST_TEST);
    await b.sw.evaluate(() => globalThis.wfTest.simulatePolicyInstall());
    check("B0 the fixtures use hosts of the shipped ruleset", AD_RULESET.some((r) => r.condition.requestDomains.includes(AD_HOST)) && !!AD_3P, { note: `${AD_HOST}, ${AD_3P}` });
    // A child in allowlist mode: the news site and the third-party ad domain's own site are allowed by URL rules.
    const { rules, timeUp } = compiledRules(b.id, { allowed: ["site-a.test", AD_3P] });
    const base = { rulesVersion: 11, dynamicRules: rules, timeUpSessionRules: timeUp, youTube: { mode: "restricted", allowed: [], blocked: [] }, time: { state: "ok", remainingMinutes: 60, windowEndsAt: null } };
    const withAds = (blockAds, version) => ({ rules: { ...base, rulesVersion: version, features: { jumpGuard: false, blockYouTubeShorts: false, logSearches: false, canOverrideJump: false, blockAds } } });
    await b.sw.evaluate((s) => globalThis.wfTest.useFakeHost(s), withAds(true, 11));
    const ready = await b.sw.evaluate(() => globalThis.wfTest.whenReady(5000));
    const on = await waitFor(async () => {
      const r = await enabledRulesets(b);
      return r.includes("ads") && !r.includes("failclosed") ? r : null;
    });
    check("B1 features.blockAds → the ads ruleset is on, fail-closed off", ready && !!on, { note: JSON.stringify(await enabledRulesets(b)) });
    const staticRules = await b.sw.evaluate(() => chrome.declarativeNetRequest.getAvailableStaticRuleCount());
    check("B1b Edge accepted the static ruleset (rules left for other extensions)", typeof staticRules === "number" && staticRules > 0, { note: `${staticRules} static rules available` });

    const page = await b.context.newPage();
    const adHits = () => hits.filter((h) => h.startsWith(AD_HOST) || h.startsWith(`${AD_3P}/tag`));
    hits.length = 0;
    await visit(page, S("site-a.test", "/with-ads"));
    const loaded = await waitFor(() => page.evaluate(() => (document.documentElement.dataset.xhr ? { title: document.title, xhr: document.documentElement.dataset.xhr } : null)));
    await new Promise((r) => setTimeout(r, 1000)); // the beacon and the frame have been sent (or not) by now
    const slot = await page.evaluate(() => {
      const frame = document.getElementById("ad-frame").getBoundingClientRect();
      return { width: Math.round(frame.width), height: Math.round(frame.height) };
    });
    check("B2 an allowed page loads with its own image while ads are blocked", loaded?.title === "SITE site-a.test/with-ads" && hits.includes("site-a.test/own.png"), { note: `${loaded?.title} ${hits.join(",")}` });
    check("B3 no request to the ad servers left the browser: script, image, frame, fetch, beacon (and the third-party tag)", adHits().length === 0 && loaded?.xhr === "blocked", {
      note: `xhr ${loaded?.xhr}; ${adHits().join(",") || "none"}`,
    });
    check("B4 the blocked ad frame is collapsed (no block page in the slot)", slot.height === 0 && !page.frames().some((f) => f.url().startsWith(b.page("blocked.html"))), { note: JSON.stringify(slot) });
    const quiet = (await b.sw.evaluate(() => globalThis.wfTest.received())).filter((m) => m.type === "reportBlock" || m.type === "decideUrl");
    check("B5 ad blocks are quiet: no reportBlock, no decideUrl", quiet.length === 0, { note: JSON.stringify(quiet) });

    hits.length = 0;
    await visit(page, A(AD_3P, "/home"));
    const own = await waitFor(async () => ((await page.title()) === `SITE ${AD_3P}/home` && hits.includes(`${AD_3P}/own.js`) ? true : null));
    check("B6 a third-party-only ad domain: its own page (main_frame) loads with its own script", !!own, { note: `${await page.title()} ${hits.join(",")}` });

    await visit(page, b.page("popup.html"));
    const adsNotice = await waitFor(() => page.evaluate(() => (document.getElementById("ads-notice")?.hidden === false ? document.getElementById("ads-notice").textContent : null)));
    check("B7 popup: חסימת פרסומות פעילה", adsNotice === "חסימת פרסומות פעילה", { note: adsNotice });

    // The parent turns it off for this child (and a pause sends blockAds false too): the same page loads its ads.
    await b.sw.evaluate((s) => {
      globalThis.wfTest.setScript(s);
      globalThis.wfTest.push({ type: "rulesChanged", rulesVersion: 12 });
    }, withAds(false, 12));
    const off = await waitFor(async () => (!(await enabledRulesets(b)).includes("ads") ? true : null));
    hits.length = 0;
    await visit(page, S("site-a.test", "/with-ads"));
    // (The ad frame is still blocked there: in allowlist mode the URL rules block frames of sites not allowed.)
    const reached = await waitFor(() => (["/pagead/js/adsbygoogle.js", "/ad.gif", "/xhr"].every((p) => adHits().includes(`${AD_HOST}${p}`)) ? true : null));
    check("B8 switched off → the ads ruleset is off and the same ad requests reach the server", !!off && !!reached, { note: adHits().join(",") });
    await visit(page, b.page("popup.html"));
    const hidden = await waitFor(() => page.evaluate(() => (document.documentElement.dataset.adBlocking === "false" ? true : null)));
    check("B9 popup: no ad blocking line when it is off", !!hidden);
    await b.close();
  }

  // ===== Test build: the parents' baseline protection (PROTECTION-SPEC §13) =========================================
  {
    const b = await launch(DIST_TEST);
    await b.sw.evaluate(() => globalThis.wfTest.simulatePolicyInstall());
    // What the compiler emits for a parent: the family's adult list blocked (global band), the parent's own allow rule above
    // it (member band), and nothing else — no default block, no IP-literal gate, no YouTube rule, no time-up rules.
    const redirect = { type: "redirect", redirect: { regexSubstitution: `chrome-extension://${b.id}/blocked.html#\\0` } };
    const baselineRules = [
      { id: 2001, priority: 65545, action: redirect, condition: { resourceTypes: FRAMES, requestDomains: ["adult-site.test", "nsfw.test"], regexFilter: "^(.*)$" } },
      { id: 2002, priority: 131080, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["ok.nsfw.test"] } },
    ];
    const features = { jumpGuard: true, blockYouTubeShorts: false, logSearches: false, canOverrideJump: true, blockAds: true };
    const adultUrl = S("www.adult-site.test", "/video?id=1");
    const unlimited = { state: "unlimited", remainingMinutes: null, windowEndsAt: null };
    const parent = (version, extra = {}) => ({
      hello: { mode: "baseline", user: { accountId: "S-1-5-21-parent", name: "dana", role: "adult", isAdmin: true } },
      rules: { rulesVersion: version, dynamicRules: baselineRules, timeUpSessionRules: [], youTube: { mode: "unrestricted", allowed: [], blocked: [] }, time: unlimited, features, mode: "baseline", ...extra },
      // The service's answer for a parent: its reason, and never a request.
      decide: { [adultUrl]: { reason: "blocked_baseline_adult", canRequest: false, requestKind: "url" } },
    });
    await b.sw.evaluate((s) => globalThis.wfTest.useFakeHost(s), parent(21));
    const ready = await b.sw.evaluate(() => globalThis.wfTest.whenReady(5000));
    const on = await waitFor(async () => {
      const r = await enabledRulesets(b);
      return r.includes("ads") && !r.includes("failclosed") ? r : null;
    });
    const st = await b.sw.evaluate(() => globalThis.wfTest.status());
    const installed = (await dynamicRules(b)).map((r) => r.id).sort((x, y) => x - y).join(",");
    check("PB1 mode baseline: the adult rules installed, fail-closed off, ads ruleset on, unlimited time, no time-up rules",
      ready && !!on && st.mode === "baseline" && installed === "2001,2002" && (await sessionRules(b)).length === 0 && st.time?.state === "unlimited" && st.canRequest === false,
      { note: JSON.stringify({ mode: st.mode, installed, rulesets: await enabledRulesets(b), time: st.time }) });
    const received = () => b.sw.evaluate(() => globalThis.wfTest.received());
    const blockInfo = (p) =>
      waitFor(async () => {
        const i = await pageInfo(p);
        return i.ready && i.title === BLOCKED_TITLE ? { ...i, canOverride: await p.evaluate(() => document.documentElement.dataset.canOverride ?? null) } : null;
      }, 20000);
    const opened = async (p, selector) => {
      const [tab] = await Promise.all([b.context.waitForEvent("page", { timeout: 10000 }), p.click(selector)]);
      return tab;
    };

    const page = await b.context.newPage();
    hits.length = 0;
    await visit(page, S("news.test", "/today"));
    const ordinary = await page.title();
    await visit(page, `http://127.0.0.1:${HTTP_PORT}/router`);
    const router = await page.title();
    check("PB2 ordinary pages load, an IP address too (no IP-literal block for parents)", ordinary === "SITE news.test/today" && router === "SITE 127.0.0.1/router", { note: `${ordinary} | ${router}` });

    hits.length = 0;
    const landed = await visit(page, adultUrl);
    const info = await blockInfo(page);
    const hint = await page.evaluate(() => {
      const el = document.getElementById("baseline-hint");
      return { shown: el?.hidden === false, text: el?.textContent ?? "", buttons: [...document.querySelectorAll("button")].filter((x) => x.offsetParent !== null).map((x) => x.id) };
    });
    check("PB3 a page of the adult list → block page with blocked_baseline_adult in Hebrew; the request never left the browser",
      landed === `${b.page("blocked.html")}#${adultUrl}` && info?.reason === "blocked_baseline_adult" && info.reasonSource === "service"
        && info.text === "אתר למבוגרים — חסום בהגנה הבסיסית של המשפחה." && !hits.some((h) => h.includes("adult-site.test")),
      { note: `${landed} ${JSON.stringify(info)} ${hits.join(",")}` });
    check("PB4 … no request button, and the hint that the protection is turned off in the admin app's settings",
      info?.canRequest === "false" && !(await page.isVisible("#request")) && !(await page.isVisible("#ask-url")) && hint.shown && /ממשק הניהול/.test(hint.text) && hint.buttons.length === 0,
      { note: JSON.stringify(hint) });
    const asked = (await received()).filter((m) => m.type === "reportBlock" || m.type === "decideUrl");
    check("PB5 nothing reported: the block page asks with decideUrl only, never reportBlock",
      asked.length === 1 && asked[0].type === "decideUrl" && asked[0].url === adultUrl, { note: JSON.stringify(asked) });
    await visit(page, S("ok.nsfw.test", "/"));
    check("PB6 the parent's own allow rule wins over the list", (await page.title()) === "SITE ok.nsfw.test/", { note: await page.title() });

    // Jump guard with "open anyway", and nothing reported for it.
    await visit(page, S("site-a.test", "/opener"));
    const jumped = await opened(page, "#open");
    const jumpInfo = await blockInfo(jumped);
    check("PB7 the jump guard runs for parents: window.open to another site → blocked_jump with 'פתח בכל זאת'",
      jumpInfo?.reason === "blocked_jump" && jumpInfo.canOverride === "true" && (await jumped.isVisible("#override")), { note: JSON.stringify(jumpInfo) });
    await jumped.click("#override");
    const through = await waitFor(async () => (await jumped.title()) === "SITE site-b.test/popup", 15000);
    const reports = (await received()).filter((m) => m.type === "reportOverride" || m.type === "reportBlock" || m.type === "reportSearch");
    check("PB8 'open anyway' opens it, and no reportOverride or reportBlock is sent", !!through && reports.length === 0, { note: JSON.stringify(reports) });
    await jumped.close();

    await visit(page, b.page("popup.html"));
    const popup = await waitFor(() =>
      page.evaluate(() => (document.documentElement.dataset.connection === "service" ? { mode: document.getElementById("mode").textContent, detail: document.getElementById("status-detail").textContent } : null)));
    check("PB9 popup: mode 'הגנה בסיסית להורים', nothing recorded", popup?.mode === "הגנה בסיסית להורים" && /לא נרשם/.test(popup.detail), { note: JSON.stringify(popup) });

    // Every switch off: the rules reply says unrestricted, and the extension removes everything without a new hello.
    await b.sw.evaluate((s) => {
      globalThis.wfTest.setScript(s);
      globalThis.wfTest.push({ type: "rulesChanged", rulesVersion: 22 });
    }, parent(22, { dynamicRules: [], features: { jumpGuard: false, blockYouTubeShorts: false, logSearches: false, canOverrideJump: false, blockAds: false }, mode: "unrestricted" }));
    const off = await waitFor(async () => ((await dynamicRules(b)).length === 0 && !(await enabledRulesets(b)).includes("ads") ? true : null));
    await visit(page, adultUrl);
    const openNow = await page.title();
    check("PB10 all switches off → mode unrestricted from the rules reply: rules and ads ruleset removed, the page loads",
      !!off && openNow === "SITE www.adult-site.test/video" && (await b.sw.evaluate(() => globalThis.wfTest.status().mode)) === "unrestricted", { note: openNow });
    await b.close();
  }
} catch (error) {
  check("harness error", false, { note: error?.stack ?? String(error) });
} finally {
  http.close();
  https?.close();
  rmSync(scratch, { recursive: true, force: true });
  failures = checks.filter((c) => !c.ok).length;
  mkdirSync(RESULTS, { recursive: true });
  writeFileSync(join(RESULTS, "verify-edge.json"), JSON.stringify({ ranAt: new Date().toISOString(), passed: checks.length - failures, failed: failures, checks }, null, 2));
  console.log(`\n${checks.length - failures}/${checks.length} passed`);
  process.exit(failures ? 1 : 0);
}
