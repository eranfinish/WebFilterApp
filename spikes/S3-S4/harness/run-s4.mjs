// Spike S4 — YouTube channel detection against the live site: handle and
// channel pages, watch, shorts, embeds, SPA navigation (incl. stale-data
// checks), and enforcement in allowlist / restricted modes.
import { launch, visit, blockInfo, recorder } from "./common.mjs";
import { startServer } from "./server.mjs";

const HANDLE = "@GoogleDevelopers";
const KNOWN_ID = "UC_x5XG1OV2P6uZZ5FSM9Ttw"; // Google for Developers (also used in Prompt.md)
const YT = "https://www.youtube.com";

const r = recorder("s4-youtube");
const server = await startServer(8080);
const b = await launch();
const page = await b.context.newPage();
const call = (fn, ...args) => b.sw.evaluate(({ fn, args }) => globalThis[fn](...args), { fn, args });
const vOf = (href) => new URL(href, YT).searchParams.get("v") ?? href.match(/\/(shorts|embed)\/([^/?#]+)/)?.[2] ?? null;

async function dismissConsent() {
  if (!page.url().includes("consent.")) return;
  const reject = page.getByRole("button", { name: /reject all/i }).first();
  if (await reject.isVisible().catch(() => false)) await reject.click();
  await page.waitForURL(/youtube\.com/, { timeout: 15000 }).catch(() => {});
}

// Waits for the extension's final detection for a page whose URL satisfies `match`.
async function detection(match, { frame = "main", after = 0, timeout = 20000 } = {}) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeout) {
    const log = await call("getYtLog");
    const hit = log.slice(after).find((d) => d.frame === frame && match(d.url));
    if (hit) return hit;
    await page.waitForTimeout(250);
  }
  return null;
}
const logSize = async () => (await call("getYtLog")).length;

// Which sources agreed with the expected channel (for the report).
function sourceAgreement(d, expected) {
  const out = {};
  for (const [k, v] of Object.entries(d.sources ?? {})) {
    const id = typeof v === "string" ? v : v?.channelId;
    if (/^UC[A-Za-z0-9_-]{22}$/.test(id ?? "")) out[k] = id === expected ? "match" : `other:${id}`;
    else if (id) out[k] = `not-a-channel-id:${id}`;
  }
  return out;
}

function report(id, d, expected, extra = {}) {
  const ok = d && d.channelId === expected;
  r.check(id, ok, { note: d ? `${d.channelId} via ${d.via}, ${d.ms} ms, trigger=${d.trigger}` : "no detection", expected, ...extra });
  if (d) r.metric(`${id}.sources`, sourceAgreement(d, expected));
  return d;
}

const VIDEO_HREF = /^\/(watch\?v=|shorts\/)[A-Za-z0-9_-]{11}/;

async function firstHref(selector, exclude = null) {
  const visible = page.locator(`${selector}:visible`);
  await visible.first().waitFor({ timeout: 20000 }).catch(() => {});
  const hrefs = await visible.evaluateAll((as) => as.map((a) => a.getAttribute("href")).filter(Boolean));
  return hrefs.find((h) => VIDEO_HREF.test(h) && (!exclude || vOf(h) !== exclude)) ?? null;
}

try {
  await call("hello");
  await call("setYouTube", "observe");

  // --- handle and channel pages (full load)
  let n = await logSize();
  await visit(page, `${YT}/${HANDLE}`);
  await dismissConsent();
  const handleDet = await detection((u) => u.includes(`/${HANDLE}`), { after: n });
  const truth = handleDet?.sources?.canonical ?? KNOWN_ID;
  r.metric("truth.channelId", { canonical: handleDet?.sources?.canonical ?? null, known: KNOWN_ID });
  report("S4.1 handle page, full load", handleDet, truth);

  n = await logSize();
  await visit(page, `${YT}/channel/${truth}`);
  report("S4.2 /channel/ page, full load", await detection((u) => u.includes(`/channel/${truth}`), { after: n }), truth);

  // --- watch page (full load)
  await visit(page, `${YT}/${HANDLE}/videos`);
  const videoHref = await firstHref('a[href^="/watch?v="]');
  r.metric("sample.video", videoHref);
  const videoId = videoHref ? vOf(videoHref) : null;
  n = await logSize();
  await visit(page, `${YT}/watch?v=${videoId}`);
  report("S4.3 watch page, full load", await detection((u) => vOf(u) === videoId, { after: n }), truth);

  // --- SPA: channel videos tab → click a video
  await visit(page, `${YT}/${HANDLE}/videos`);
  n = await logSize();
  await page.locator(`a[href^="/watch?v="]:visible`).first().click();
  await page.waitForURL(/\/watch\?v=/, { timeout: 15000 }).catch(() => {});
  const spaVideoId = vOf(page.url());
  report("S4.4 watch page via SPA click", await detection((u) => vOf(u) === spaVideoId, { after: n }), truth);

  // --- SPA: watch → related video (possibly another channel); verify against a full load of the same URL
  const relatedHref = await firstHref('a[href^="/watch?v="]', spaVideoId);
  let otherChannel = null, relatedId = null;
  if (relatedHref) {
    relatedId = vOf(relatedHref);
    n = await logSize();
    await page.locator(`a[href^="/watch?v=${relatedId}"]:visible`).first().click();
    await page.waitForURL(new RegExp(`v=${relatedId}`), { timeout: 15000 }).catch(() => {});
    const spaDet = await detection((u) => vOf(u) === relatedId, { after: n });

    // --- SPA: watch → that video's channel page, while the earlier channel's page data is still in the DOM.
    // The visible owner name is not an anchor in the current layout, so the description header link is clicked via script.
    await page.waitForTimeout(2500);
    const ownerHref = await page.evaluate(() => document.querySelector('ytd-video-description-header-renderer a[href^="/@"]')?.getAttribute("href"));
    let channelSpaDet = null;
    if (ownerHref) {
      n = await logSize();
      await page.evaluate((href) => document.querySelector(`ytd-video-description-header-renderer a[href="${href}"]`).click(), ownerHref);
      await page.waitForURL((u) => u.pathname.startsWith(ownerHref), { timeout: 15000 }).catch(() => {});
      channelSpaDet = await detection((u) => new URL(u).pathname.startsWith(ownerHref), { after: n });
    }

    // Ground truth for the related video: a full page load.
    n = await logSize();
    await visit(page, `${YT}/watch?v=${relatedId}`);
    const fullDet = await detection((u) => vOf(u) === relatedId, { after: n });
    otherChannel = fullDet?.channelId ?? null;
    report("S4.5 watch→watch SPA matches a full load (no stale data)", spaDet, otherChannel, { note2: `related ${relatedId}` });
    r.metric("sample.relatedChannelIsDifferent", otherChannel !== truth);
    if (ownerHref) report(`S4.6 watch→channel SPA (${ownerHref})`, channelSpaDet, otherChannel);
    else r.check("S4.6 watch→channel SPA", false, { note: "description header channel link not found" });
  } else r.check("S4.5 watch→watch SPA", false, { note: "no related video link found" });

  // --- shorts (full load, then next short via SPA)
  await visit(page, `${YT}/${HANDLE}/shorts`);
  const shortHref = await firstHref('a[href^="/shorts/"]');
  r.metric("sample.short", shortHref);
  if (shortHref) {
    const shortId = vOf(shortHref);
    n = await logSize();
    await visit(page, `${YT}/shorts/${shortId}`);
    report("S4.7 shorts, full load", await detection((u) => vOf(u) === shortId, { after: n }), truth);

    n = await logSize();
    // Headless focus varies: try the keyboard, then the "next" button, then the mouse wheel.
    const advanced = () => page.waitForURL((u) => u.pathname.startsWith("/shorts/") && !u.pathname.includes(shortId), { timeout: 5000 }).then(() => true, () => false);
    await page.locator("#shorts-player, ytd-reel-video-renderer").first().click({ trial: false }).catch(() => {});
    await page.keyboard.press("ArrowDown");
    let how = "keyboard";
    if (!(await advanced())) { how = "button"; await page.locator('#navigation-button-down button:visible, button[aria-label*="Next" i]:visible').first().click().catch(() => {}); }
    if (!(await advanced())) { how = "wheel"; await page.mouse.move(400, 400); await page.mouse.wheel(0, 900); await advanced(); }
    r.metric("shorts.advance", how);
    const nextId = vOf(page.url());
    if (nextId && nextId !== shortId) {
      const spaDet = await detection((u) => vOf(u) === nextId, { after: n });
      n = await logSize();
      await visit(page, `${YT}/shorts/${nextId}`);
      const fullDet = await detection((u) => vOf(u) === nextId, { after: n });
      report("S4.8 next short via SPA matches a full load", spaDet, fullDet?.channelId ?? null);
    } else r.check("S4.8 next short via SPA", false, { note: "did not advance to another short" });
  } else r.check("S4.7 shorts", false, { note: "no shorts link found" });

  // --- embeds on a third-party page
  for (const nocookie of [false, true]) {
    n = await logSize();
    await visit(page, `http://localhost:8080/embed?v=${videoId}${nocookie ? "&nocookie" : ""}`);
    report(`S4.9 embed${nocookie ? " (youtube-nocookie)" : ""}`, await detection((u) => vOf(u) === videoId, { frame: "sub", after: n }), truth);
  }

  // --- enforcement: allowlist mode
  await call("setYouTube", "allowlist", [truth]);
  const blockedReason = async () => (page.url().startsWith(b.blockedPage) ? (await blockInfo(page)).reason : null);

  await visit(page, `${YT}/watch?v=${videoId}`);
  await page.waitForTimeout(4000);
  r.check("S4.10 allowlist: approved channel plays", !page.url().startsWith(b.blockedPage) && vOf(page.url()) === videoId, { got: page.url() });
  r.metric("S4.10 page visible", await page.evaluate(() => getComputedStyle(document.documentElement).visibility).catch(() => "n/a"));

  if (relatedId && otherChannel && otherChannel !== truth) {
    await visit(page, `${YT}/watch?v=${relatedId}`);
    await page.waitForURL((u) => u.href.startsWith(b.blockedPage), { timeout: 15000 }).catch(() => {});
    r.check("S4.11 allowlist: other channel blocked (full load)", (await blockedReason()) === "blocked_youtube_channel", { got: page.url() });

  } else r.metric("S4.11", "related video was from the same channel; skipped");

  // SPA click on whatever related video is shown: it must be blocked exactly when its channel is not approved.
  await visit(page, `${YT}/watch?v=${videoId}`);
  await page.waitForTimeout(3000);
  const spaHref = await firstHref('a[href^="/watch?v="]', videoId);
  if (spaHref) {
    const spaId = vOf(spaHref);
    const n12 = await logSize();
    await page.locator(`a[href^="/watch?v=${spaId}"]:visible`).first().click();
    const det = await detection((u) => vOf(u) === spaId, { after: n12 });
    await page.waitForTimeout(1500);
    const blocked = (await blockedReason()) === "blocked_youtube_channel";
    const shouldBlock = det?.channelId !== truth;
    r.check("S4.12 allowlist: SPA click enforced per channel", det && blocked === shouldBlock, { note: `${det?.channelId} → ${blocked ? "blocked" : "allowed"}` });
  } else r.check("S4.12 allowlist: SPA click enforced per channel", false, { note: "no related video link" });

  await visit(page, `${YT}/`);
  await page.waitForURL((u) => u.href.startsWith(b.blockedPage), { timeout: 15000 }).catch(() => {});
  r.check("S4.13 allowlist: home redirected", (await blockedReason()) === "redirect_youtube_home", { got: page.url() });

  // --- enforcement: restricted mode blocks a listed channel only
  await call("setYouTube", "restricted", [], [truth]);
  await visit(page, `${YT}/watch?v=${videoId}`);
  await page.waitForURL((u) => u.href.startsWith(b.blockedPage), { timeout: 15000 }).catch(() => {});
  r.check("S4.14 restricted: blocked channel blocked", (await blockedReason()) === "blocked_youtube_channel", { got: page.url() });
  if (relatedId && otherChannel && otherChannel !== truth) {
    await visit(page, `${YT}/watch?v=${relatedId}`);
    await page.waitForTimeout(4000);
    r.check("S4.15 restricted: other channel plays", vOf(page.url()) === relatedId, { got: page.url() });
  }

  // --- detection latency summary
  const log = await call("getYtLog");
  const times = log.filter((d) => d.channelId).map((d) => d.ms).sort((a, b2) => a - b2);
  r.metric("detection.ms", { count: times.length, median: times[Math.floor(times.length / 2)], max: times.at(-1) });
  r.metric("detection.timeouts", log.filter((d) => d.timedOut).map((d) => ({ url: d.url, trigger: d.trigger })));
  r.metric("detection.triggers", Object.fromEntries([...new Set(log.map((d) => d.trigger))].map((t) => [t, log.filter((d) => d.trigger === t).length])));
} finally {
  await b.close();
  await server.close();
}
process.exitCode = r.save() ? 1 : 0;
