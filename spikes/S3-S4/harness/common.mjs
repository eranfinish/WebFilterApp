import { chromium } from "playwright";
import { mkdtempSync, rmSync, mkdirSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";

const here = dirname(fileURLToPath(import.meta.url));
export const EXTENSION_DIR = resolve(here, "..", "extension");
const RESULTS_DIR = resolve(here, "..", "results");

// Fresh profile per run; the extension is loaded unpacked into the installed Edge
// (Playwright's bundled Chromium fails to start on this Windows build: SxS manifest error).
// BROWSER=chrome|chromium overrides; HEADED=1 shows the browser window.
export const CHANNEL = process.env.BROWSER ?? "msedge";

export async function launch({ hostRules, extensionDir = EXTENSION_DIR } = {}) {
  const userDataDir = mkdtempSync(join(tmpdir(), "wf-spike-"));
  const args = [`--disable-extensions-except=${extensionDir}`, `--load-extension=${extensionDir}`];
  if (hostRules) args.push(`--host-resolver-rules=${hostRules}`);
  const context = await chromium.launchPersistentContext(userDataDir, {
    channel: CHANNEL,
    headless: !process.env.HEADED,
    args,
    locale: "en-US",
  });
  let [sw] = context.serviceWorkers();
  if (!sw) sw = await context.waitForEvent("serviceworker");
  const extensionId = new URL(sw.url()).host;
  return {
    context,
    sw,
    extensionId,
    blockedPage: `chrome-extension://${extensionId}/blocked.html`,
    async close() {
      await context.close();
      rmSync(userDataDir, { recursive: true, force: true });
    },
  };
}

// Navigates and tolerates the abort Chromium reports when a request is redirected to an extension page.
export async function visit(page, url, timeout = 20000) {
  const t0 = performance.now();
  let error = null;
  try {
    await page.goto(url, { waitUntil: "load", timeout });
  } catch (e) {
    error = e.message.split("\n")[0];
    await page.waitForLoadState("load", { timeout: 5000 }).catch(() => {});
  }
  return { url: page.url(), title: await page.title().catch(() => ""), ms: Math.round(performance.now() - t0), error };
}

export const blockInfo = (pageOrFrame) =>
  pageOrFrame.evaluate(() => ({ reason: document.documentElement.dataset.reason, url: document.documentElement.dataset.url }));

// Sum of working sets of the Playwright Chromium processes, in MB.
export function chromiumMemoryMB() {
  const ps = "(Get-CimInstance Win32_Process -Filter \"Name='msedge.exe' or Name='chrome.exe'\" | Where-Object { $_.CommandLine -like '*wf-spike-*' } | Measure-Object WorkingSetSize -Sum).Sum";
  const bytes = Number(execFileSync("powershell.exe", ["-NoProfile", "-Command", ps], { encoding: "utf8" }).trim() || 0);
  return Math.round(bytes / 1024 / 1024);
}

export function recorder(name, resultsDir = RESULTS_DIR) {
  const checks = [];
  const metrics = {};
  return {
    check(id, ok, detail = {}) {
      checks.push({ id, ok: Boolean(ok), ...detail });
      console.log(`${ok ? "PASS" : "FAIL"}  ${id}${detail.note ? `  — ${detail.note}` : ""}`);
    },
    metric(key, value) {
      metrics[key] = value;
      console.log(`      ${key}: ${JSON.stringify(value)}`);
    },
    save() {
      mkdirSync(resultsDir, { recursive: true });
      const passed = checks.filter((c) => c.ok).length;
      writeFileSync(join(resultsDir, `${name}.json`), JSON.stringify({ name, ranAt: new Date().toISOString(), passed, failed: checks.length - passed, checks, metrics }, null, 2));
      console.log(`\n${name}: ${passed}/${checks.length} passed`);
      return checks.length - passed;
    },
  };
}
