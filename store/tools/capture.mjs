// Captures the real extension pages for the store screenshots: the block page, "my channels" with the channel search,
// and the popup, in English and in Hebrew. render.mjs then frames them (tools/html/screenshot.html).
//
//   cd extension && npm run build:test        (dist-test/: the build with test hooks)
//   cd store/tools && npm run capture         (writes tools/captures/<lang>-<shot>.png; not kept in git)
//
// Loads extension/dist-test unpacked into the installed Edge and drives it through its test hooks, as
// extension/harness/verify-edge.mjs does: a simulated policy install and an in-worker fake host that answers like the
// service. Every host resolves to 127.0.0.1 and the block rule redirects before any request, so nothing goes to the
// network. The names and channels are made up for the pictures.

import { chromium } from "playwright";
import { mkdirSync, mkdtempSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const extensionDir = path.resolve(here, "..", "..", "extension", "dist-test");
const outDir = path.join(here, "captures");
try {
  statSync(path.join(extensionDir, "manifest.json"));
} catch {
  throw new Error(`${extensionDir} is missing: run "npm run build:test" in extension/ first.`);
}

const BLOCKED_URL = "https://play.example-games.com/";
const FRAMES = ["main_frame", "sub_frame"];
const CHANNELS = [
  { channelId: "UCspaceExplorersKids0001", handle: "@spaceexplorers", title: "Space Explorers" },
  { channelId: "UCnatureLabForKids000002", handle: "@naturelab", title: "Nature Lab" },
  { channelId: "UCmathAdventures00000003", handle: "@mathadventures", title: "Math Adventures" },
];

const text = {
  en: { name: "Noa", note: "For my science project" },
  he: { name: "נועה", note: "בשביל עבודה במדעים" },
};

function script(id, lang) {
  const redirect = { type: "redirect", redirect: { regexSubstitution: `chrome-extension://${id}/blocked.html#\\0` } };
  return {
    hello: { user: { accountId: "S-1-5-21-store", name: text[lang].name, role: "child", isAdmin: false }, mode: "allowlist" },
    rules: {
      rulesVersion: 1,
      // "Only approved sites": everything is redirected to the block page except YouTube (filtered by channel) and the
      // approved sites — the shape the service's compiler emits.
      dynamicRules: [
        { id: 1, priority: 1, action: redirect, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
        { id: 3, priority: 196608, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["youtube.com", "youtube-nocookie.com"] } },
        { id: 4321, priority: 65544, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["wikipedia.org"] } },
      ],
      timeUpSessionRules: [],
      youTube: { mode: "allowlist", allowed: CHANNELS, blocked: [] },
      time: { state: "ok", remainingMinutes: 75, windowEndsAt: "19:00" },
      features: { jumpGuard: true, blockYouTubeShorts: true, logSearches: false, canOverrideJump: false, blockAds: true, youTubeChannelSearch: true },
    },
    decide: { [BLOCKED_URL]: { reason: "blocked_category", category: "games" } },
  };
}

async function waitFor(fn, what, timeout = 15000) {
  const until = Date.now() + timeout;
  while (Date.now() < until) {
    try {
      if (await fn()) return;
    } catch {
      // navigating
    }
    await new Promise((r) => setTimeout(r, 100));
  }
  throw new Error(`timed out waiting for ${what}`);
}

async function capture(lang) {
  const profile = mkdtempSync(path.join(tmpdir(), "wf-store-"));
  const context = await chromium.launchPersistentContext(profile, {
    channel: "msedge",
    headless: true,
    locale: lang === "he" ? "he-IL" : "en-US",
    viewport: { width: 800, height: 600 },
    deviceScaleFactor: 2,
    colorScheme: "light",
    args: [
      `--disable-extensions-except=${extensionDir}`,
      `--load-extension=${extensionDir}`,
      "--host-resolver-rules=MAP * 127.0.0.1, EXCLUDE localhost",
      `--lang=${lang}`,
    ],
  });
  try {
    let [sw] = context.serviceWorkers();
    if (!sw) sw = await context.waitForEvent("serviceworker", { timeout: 15000 });
    const id = new URL(sw.url()).host;
    const page = (file) => `chrome-extension://${id}/${file}`;

    await sw.evaluate(() => globalThis.wfTest.simulatePolicyInstall("admin"));
    await sw.evaluate((s) => globalThis.wfTest.useFakeHost(s), script(id, lang));
    if (!(await sw.evaluate(() => globalThis.wfTest.whenReady(5000)))) throw new Error("the extension did not become ready");

    const tab = await context.newPage();
    const shot = async (name, options = {}) => {
      const file = path.join(outDir, `${lang}-${name}.png`);
      await tab.screenshot({ path: file, animations: "disabled", ...options });
      console.log(file);
    };

    // 1. A site outside the approved list → the block page, with the reason from the service and a request being written.
    await tab.goto(BLOCKED_URL).catch(() => {}); // Chromium reports an abort when DNR redirects to an extension page
    await waitFor(() => tab.evaluate(() => document.documentElement.dataset.ready === "true" && document.documentElement.dataset.reason === "blocked_category"), "the block page")
      .catch(async (e) => {
        const state = await tab.evaluate(() => ({ url: location.href, data: { ...document.documentElement.dataset } })).catch((x) => String(x));
        throw new Error(`${e.message}: ${JSON.stringify(state)}`);
      });
    await waitFor(() => tab.isVisible("#ask-url"), "the request button");
    await tab.fill("#note", text[lang].note);
    await tab.locator("#note").blur();
    await shot("blocked");

    // 2. YouTube in "approved channels only", with the channel search on.
    await tab.goto(page("my-channels.html"));
    await waitFor(() => tab.evaluate((n) => document.querySelectorAll("#channels li").length === n && !document.getElementById("search").hidden, CHANNELS.length), "my channels");
    await shot("channels");

    // 3. The popup, connected to the service.
    await tab.setViewportSize({ width: 360, height: 600 });
    await tab.goto(page("popup.html"));
    await waitFor(() => tab.evaluate(() => document.documentElement.dataset.connection === "service" && !document.getElementById("profile").hidden), "the popup");
    // The browser sizes a popup to its body; do the same, in either direction: the body's width with its margins, and the
    // content's height plus the body's top margin below it.
    const box = await tab.evaluate(() => {
      const style = getComputedStyle(document.body);
      const body = document.body.getBoundingClientRect();
      const bottom = Math.max(...[...document.body.children].filter((e) => e.getClientRects().length).map((e) => e.getBoundingClientRect().bottom));
      return {
        width: Math.ceil(body.width + parseFloat(style.marginLeft) + parseFloat(style.marginRight)),
        height: Math.ceil(bottom + parseFloat(style.paddingBottom) + parseFloat(style.marginTop)),
      };
    });
    await tab.setViewportSize({ width: box.width, height: box.height });
    await shot("popup");
  } finally {
    await context.close();
    rmSync(profile, { recursive: true, force: true });
  }
}

mkdirSync(outDir, { recursive: true });
for (const lang of ["en", "he"]) await capture(lang);
