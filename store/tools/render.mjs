// Renders the extension icons and the store images from the HTML sources in ./html, at exact pixel sizes.
//
//   cd store/tools && npm install && npm run capture && npm run render
//
// The screenshots frame the real extension pages that capture.mjs takes (tools/captures/; needs extension/dist-test).
// Uses the installed Edge (Playwright's bundled Chromium does not start on this Windows build).
// Pages are served from store/ under a fake origin through request interception; every other request is
// aborted, so the sources cannot depend on anything outside the repo and nothing goes to the network.

import { chromium } from "playwright";
import { readFile, writeFile, mkdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const storeDir = path.resolve(here, "..");
const ORIGIN = "http://render.test";

const icon = (size, art, out) => ({ out, page: `tools/html/icon.html?size=${size}&art=${art}`, width: size, height: size, transparent: true });

// Order matters: the screenshots load extension-s1/icons/*, so the icons are rendered first.
const targets = [
  // Extension icons (in the package). 128: 96 px mark + 16 px transparent padding, per Chrome Web Store guidance.
  icon(16, 16, "extension-s1/icons/icon-16.png"),
  icon(32, 30, "extension-s1/icons/icon-32.png"),
  icon(48, 44, "extension-s1/icons/icon-48.png"),
  icon(128, 96, "extension-s1/icons/icon-128.png"),
  // Store images.
  icon(128, 96, "assets/cws-icon-128x128.png"),
  icon(300, 264, "assets/edge-logo-300x300.png"),
  { out: "assets/promo-small-440x280-en.png", page: "tools/html/promo-small.html?lang=en", width: 440, height: 280 },
  { out: "assets/promo-small-440x280-he.png", page: "tools/html/promo-small.html?lang=he", width: 440, height: 280 },
  ...["en", "he"].flatMap((lang) =>
    ["blocked", "channels", "popup"].map((shot, i) => ({
      out: `assets/screenshot-${i + 1}-${shot}-1280x800-${lang}.png`,
      page: `tools/html/screenshot.html?lang=${lang}&shot=${shot}`,
      width: 1280,
      height: 800,
      lang,
    })),
  ),
];

const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".png": "image/png",
  ".svg": "image/svg+xml",
};

async function serveStore(route) {
  const url = new URL(route.request().url());
  if (url.origin !== ORIGIN) return route.abort("blockedbyclient");
  const file = path.resolve(storeDir, "." + decodeURIComponent(url.pathname));
  if (!file.startsWith(storeDir + path.sep)) return route.fulfill({ status: 403, body: "outside store/" });
  try {
    const body = await readFile(file);
    return route.fulfill({ status: 200, body, contentType: contentTypes[path.extname(file)] ?? "application/octet-stream" });
  } catch {
    return route.fulfill({ status: 404, body: `not found: ${url.pathname}` });
  }
}

function pngSize(buffer) {
  const signature = "89504e470d0a1a0a";
  if (buffer.subarray(0, 8).toString("hex") !== signature) throw new Error("not a PNG");
  return { width: buffer.readUInt32BE(16), height: buffer.readUInt32BE(20) };
}

const browser = await chromium.launch({ channel: "msedge" });
try {
  for (const target of targets) {
    const context = await browser.newContext({
      viewport: { width: target.width, height: target.height },
      deviceScaleFactor: 1,
      colorScheme: "light",
      locale: target.lang === "he" ? "he-IL" : "en-US",
    });
    await context.route("**/*", serveStore);
    const page = await context.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(error.message));
    page.on("console", (message) => { if (message.type() === "error") errors.push(message.text()); });

    await page.goto(`${ORIGIN}/${target.page}`);
    await page.waitForSelector("html[data-ready='1']", { timeout: 15000 });
    if (errors.length) throw new Error(`${target.page}: ${errors.join("; ")}`);

    const buffer = await page.screenshot({ omitBackground: Boolean(target.transparent), animations: "disabled" });
    const size = pngSize(buffer);
    if (size.width !== target.width || size.height !== target.height) {
      throw new Error(`${target.out}: rendered ${size.width}x${size.height}, expected ${target.width}x${target.height}`);
    }
    const outFile = path.join(storeDir, target.out);
    await mkdir(path.dirname(outFile), { recursive: true });
    await writeFile(outFile, buffer);
    console.log(`${target.out}  ${size.width}x${size.height}  ${buffer.length} bytes`);
    await context.close();
  }
} finally {
  await browser.close();
}
