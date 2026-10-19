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
      return send(
        "YT watch",
        `<div id="movie_player">player</div><script>
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
    check("P4 no test hooks in the production worker", await b.sw.evaluate(() => typeof globalThis.wfTest === "undefined"));

    const page = await b.context.newPage();
    await visit(page, S("site.test", "/hello"));
    check("P5 inert extension does not block browsing", (await page.title()) === "SITE site.test/hello", { note: await page.title() });

    await visit(page, b.page("blocked.html?reason=failclosed"));
    const info = await waitFor(async () => {
      const i = await pageInfo(page);
      return i.ready ? i : null;
    });
    check("P6 blocked.html?reason=failclosed: exact window title", info?.title === BLOCKED_TITLE, { note: info?.title });
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
