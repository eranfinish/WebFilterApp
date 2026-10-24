// Level-3 end-to-end check of the real chain in the installed Edge:
//   MV3 extension (dist-test, simulated policy install) → real native host process (Simulation build)
//   → real WebFilter service process (Simulation build, hub role, temp data, unique pipes).
//
//   npm run e2e:real               builds the service, the host and dist-test, then runs every check
//   npm run e2e:real -- --no-build uses the existing builds
//   npm run e2e:real -- --cleanup  only removes the HKCU native messaging host key this harness creates
//   HEADED=1                       shows the browser
//
// The service maps the current Windows user to the fixture's noa (allowlist child, pc-kids). Simulation-only knobs:
// the service clock (admin setClock), and WEBFILTER_SIMULATE_STANDARD_USER so the host's service-down fallback treats
// the (usually administrator) developer account as the standard account a child has.
//
// It changes exactly one thing outside its temp folder: the key
//   HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.webfilter.host
// whose default value points at a manifest in the temp folder. The key must not exist before the run (the harness
// stops otherwise), and it is deleted in a finally block that also runs on Ctrl+C and on errors. Nothing under HKLM,
// no browser policies, no service install, no firewall change (the hub listens on 127.0.0.1 with a random port).
import { chromium } from "playwright";
import { execFileSync, spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { copyFileSync, createWriteStream, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { createServer as createHttpServer } from "node:http";
import { createServer as createHttpsServer } from "node:https";
import { createConnection } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const EXT_ROOT = resolve(here, "..");
const REPO = resolve(EXT_ROOT, "..");
const DIST_TEST = join(EXT_ROOT, "dist-test");
const RESULTS = join(here, "results");
const SERVICE_DIR = join(REPO, "src", "WebFilter.Service", "bin", "Simulation", "net10.0-windows");
const SERVICE_EXE = join(SERVICE_DIR, "WebFilter.Service.exe");
const HOST_EXE = join(REPO, "src", "WebFilter.NativeHost", "bin", "Simulation", "net10.0-windows", "WebFilter.NativeHost.exe");
const FIXTURE = join(REPO, "tests", "simulation", "fixtures", "family-base.json");

const REG_EXE = "C:\\Windows\\System32\\reg.exe";
const WHOAMI_EXE = "C:\\Windows\\System32\\whoami.exe";
const HOST_NAME = "com.webfilter.host";
const HOST_KEY = `HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\${HOST_NAME}`;
const TEMP_PREFIX = "wf-e2e-";

const BLOCKED_TITLE = "WebFilter · חסום";
const TIMEUP_TITLE = "WebFilter · הזמן נגמר";

// Simulated wall clock (Asia/Jerusalem, the fixture's zone; standard time +02:00 after 2026-10-25). The Simulation
// service clock does not advance by itself, so no minutes are ever counted against noa's quota.
const SUNDAY_IN_WINDOW = "2026-11-01T16:00:00+02:00"; // noa: Sun–Thu 15:00–19:00, 120 min
const MONDAY_OUTSIDE = "2026-11-02T10:00:00+02:00";
const MONDAY_IN_WINDOW = "2026-11-02T16:00:00+02:00";
const TUESDAY_IN_WINDOW = "2026-11-03T16:00:00+02:00"; // after the service restart

const args = new Set(process.argv.slice(2));

// ---- registry: exactly one HKCU key ---------------------------------------------------------------------------------

function reg(argv) {
  return execFileSync(REG_EXE, argv, { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
}

/** { exists, value } of the host key (value = its default value, the manifest path). */
function readHostKey() {
  try {
    reg(["query", HOST_KEY]);
  } catch {
    return { exists: false, value: null };
  }
  try {
    const out = reg(["query", HOST_KEY, "/ve"]);
    const m = out.match(/REG_(?:EXPAND_)?SZ\s+(.*)$/m);
    return { exists: true, value: m ? m[1].trim() : null };
  } catch {
    return { exists: true, value: null };
  }
}

const isOurManifest = (value) => typeof value === "string" && value.toLowerCase().includes(`\\${TEMP_PREFIX}`) && value.toLowerCase().endsWith(`\\${HOST_NAME}.json`);

let createdKey = false;

function createHostKey(manifestPath) {
  if (cleanupPromise) throw new Error("aborted");
  if (readHostKey().exists) throw new Error(`${HOST_KEY} appeared during the run; not overwriting it`);
  reg(["add", HOST_KEY, "/ve", "/t", "REG_SZ", "/d", manifestPath, "/f"]);
  createdKey = true;
}

/** Deletes the key if this run created it; returns whether it is gone. Synchronous (also used from process 'exit'). */
function removeHostKey() {
  if (!createdKey) return !readHostKey().exists;
  try {
    reg(["delete", HOST_KEY, "/f"]);
  } catch {
    // verified below
  }
  const gone = !readHostKey().exists;
  if (gone) createdKey = false;
  return gone;
}

if (args.has("--cleanup")) {
  const key = readHostKey();
  if (!key.exists) {
    console.log(`${HOST_KEY} does not exist; nothing to do.`);
    process.exit(0);
  }
  if (!isOurManifest(key.value)) {
    console.error(`${HOST_KEY} points at ${key.value ?? "(no value)"}, which is not a manifest this harness writes. Not touching it.`);
    process.exit(2);
  }
  reg(["delete", HOST_KEY, "/f"]);
  const gone = !readHostKey().exists;
  console.log(gone ? `Removed ${HOST_KEY} (was ${key.value}).` : `Could not remove ${HOST_KEY}.`);
  process.exit(gone ? 0 : 1);
}

// ---- results --------------------------------------------------------------------------------------------------------

const checks = [];
function check(id, ok, evidence = {}) {
  checks.push({ id, ok: Boolean(ok), ...evidence });
  console.log(`${ok ? "PASS" : "FAIL"}  ${id}${evidence.note ? `  — ${evidence.note}` : ""}`);
  return Boolean(ok);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function waitFor(fn, timeout = 8000, step = 100) {
  const until = Date.now() + timeout;
  let last;
  while (Date.now() < until) {
    try {
      last = await fn();
      if (last) return last;
    } catch {
      // page navigating, worker busy
    }
    await sleep(step);
  }
  try {
    return await fn();
  } catch {
    return last;
  }
}

// ---- preconditions --------------------------------------------------------------------------------------------------

const before = readHostKey();
if (before.exists) {
  console.error(`STOP: ${HOST_KEY} already exists (default value: ${before.value ?? "(none)"}).`);
  console.error(isOurManifest(before.value)
    ? "It looks like a leftover of an interrupted run of this harness: run `npm run e2e:real -- --cleanup`."
    : "It is not from this harness. Remove or rename it yourself if it is safe; the harness will not overwrite it.");
  process.exit(2);
}

function currentUserSid() {
  const out = execFileSync(WHOAMI_EXE, ["/user", "/fo", "csv", "/nh"], { encoding: "utf8", windowsHide: true });
  const sid = out.match(/"(S-1-[0-9-]+)"/)?.[1];
  if (!sid) throw new Error(`Cannot read the current user's SID from: ${out}`);
  return sid;
}

function run(cmd, argv, cwd) {
  console.log(`> ${cmd} ${argv.join(" ")}`);
  execFileSync(cmd, argv, { cwd, stdio: "inherit", windowsHide: true, shell: false });
}

if (!args.has("--no-build")) {
  run("dotnet", ["build", "src/WebFilter.Service", "-c", "Simulation", "-nologo", "-v", "q"], REPO);
  run("dotnet", ["build", "src/WebFilter.NativeHost", "-c", "Simulation", "-nologo", "-v", "q"], REPO);
  run(process.execPath, ["scripts/build.mjs", "--test-hooks"], EXT_ROOT);
}
for (const f of [SERVICE_EXE, HOST_EXE, join(DIST_TEST, "manifest.json")]) {
  if (!existsSync(f)) {
    console.error(`Missing ${f}. Run without --no-build.`);
    process.exit(2);
  }
}

// ---- temp folder, settings, names -----------------------------------------------------------------------------------

const suffix = randomBytes(4).toString("hex");
const scratch = mkdtempSync(join(tmpdir(), TEMP_PREFIX));
const dataDir = join(scratch, "data");
const profileDir = join(scratch, "profiles");
const edgeProfile = join(scratch, "edge-profile");
const manifestPath = join(scratch, `${HOST_NAME}.json`);
const bundlePath = join(scratch, "bootstrap.json");
const serviceLogPath = join(scratch, "service.log");
const extensionLogPath = join(scratch, "extension.log");
const agentPipe = `WebFilter.E2E.Agent.${suffix}`;
const adminPipe = `WebFilter.E2E.Admin.${suffix}`;
const userSid = currentUserSid();

// The family fixture, with noa's account on pc-kids belonging to whoever runs the harness.
const bundle = JSON.parse(readFileSync(FIXTURE, "utf8"));
for (const account of bundle.devices.find((d) => d.id === "pc-kids").accounts) if (account.member === "noa") account.accountId = userSid;
writeFileSync(bundlePath, JSON.stringify(bundle, null, 2));

const serviceLog = createWriteStream(serviceLogPath, { flags: "a" });
const extensionLog = createWriteStream(extensionLogPath, { flags: "a" });

// ---- the WebFilter service process ----------------------------------------------------------------------------------

let service = null;
let serviceStarts = 0;

function startService() {
  if (cleanupPromise) throw new Error("aborted");
  serviceStarts += 1;
  serviceLog.write(`\n===== service start #${serviceStarts} ${new Date().toISOString()} =====\n`);
  const child = spawn(SERVICE_EXE, [
    "--WebFilter:Role=hub",
    "--WebFilter:DeviceId=pc-kids",
    `--WebFilter:DataDirectory=${dataDir}`,
    `--WebFilter:ProfileDirectory=${profileDir}`,
    `--WebFilter:AgentPipeName=${agentPipe}`,
    `--WebFilter:AdminPipeName=${adminPipe}`,
    `--WebFilter:TrustedClientPaths:0=${HOST_EXE}`,
    `--WebFilter:BootstrapBundlePath=${bundlePath}`,
    "--WebFilter:HubBindAddress=127.0.0.1",
    "--WebFilter:HubPort=0",
    // Time pushes ride on the tick: 1 s makes an admin setClock reach the extension within a second.
    "--WebFilter:TickInterval=00:00:01",
  ], { cwd: SERVICE_DIR, stdio: ["ignore", "pipe", "pipe"], windowsHide: true });
  child.stdout.on("data", (d) => serviceLog.write(d));
  child.stderr.on("data", (d) => serviceLog.write(d));
  child.exited = new Promise((r) => child.once("exit", (code, signal) => r({ code, signal })));
  child.alive = true;
  child.once("exit", () => {
    child.alive = false;
  });
  service = child;
  return child;
}

async function stopService() {
  const child = service;
  if (!child?.alive) return;
  child.kill(); // TerminateProcess: an abrupt stop, like a crash
  await Promise.race([child.exited, sleep(5000)]);
}

// ---- admin pipe (4-byte little-endian length + UTF-8 JSON, "type" first) ------------------------------------------

let adminSeq = 0;
function admin(body, timeoutMs = 10000) {
  const id = `e2e-${++adminSeq}`;
  const { type, ...rest } = body;
  return new Promise((resolvePromise, reject) => {
    const socket = createConnection(`\\\\.\\pipe\\${adminPipe}`);
    let buffer = Buffer.alloc(0);
    let done = false;
    const finish = (error, value) => {
      if (done) return;
      done = true;
      clearTimeout(timer);
      socket.destroy();
      if (error) reject(error);
      else resolvePromise(value);
    };
    const timer = setTimeout(() => finish(new Error(`admin ${type}: no reply within ${timeoutMs} ms`)), timeoutMs);
    socket.on("connect", () => {
      const payload = Buffer.from(JSON.stringify({ type, v: 1, id, ...rest }), "utf8");
      const header = Buffer.alloc(4);
      header.writeInt32LE(payload.length);
      socket.write(Buffer.concat([header, payload]));
    });
    socket.on("data", (chunk) => {
      buffer = Buffer.concat([buffer, chunk]);
      while (buffer.length >= 4) {
        const length = buffer.readInt32LE(0);
        if (buffer.length < 4 + length) break;
        const message = JSON.parse(buffer.subarray(4, 4 + length).toString("utf8"));
        buffer = buffer.subarray(4 + length);
        if (message.id === id) finish(null, message);
      }
    });
    socket.on("error", (error) => finish(error));
    socket.on("close", () => finish(new Error(`admin ${type}: pipe closed without a reply`)));
  });
}

async function adminOk(body) {
  const reply = await admin(body);
  if (reply.type === "error") throw new Error(`admin ${body.type} failed: ${reply.code}: ${reply.message}`);
  return reply;
}

async function waitForService(timeout = 30000) {
  const until = Date.now() + timeout;
  let lastError = null;
  while (Date.now() < until) {
    if (!service?.alive) throw new Error(`The service exited during startup (see ${serviceLogPath})`);
    try {
      const status = await admin({ type: "getStatus" }, 2000);
      if (status.type === "statusResult") return status;
      lastError = new Error(JSON.stringify(status));
    } catch (error) {
      lastError = error;
    }
    await sleep(200);
  }
  throw new Error(`The service did not answer on its admin pipe within ${timeout} ms: ${lastError?.message}`);
}

// ---- local web server: every host name resolves to it (--host-resolver-rules) -------------------------------------

const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const hits = [];
// An AdSense host of the extension's ad-server ruleset (PROTECTION-SPEC §12); it resolves here like every host.
const AD_HOST = "pagead2.googlesyndication.com";
function handler(req, res) {
  const url = new URL(req.url, `http://${req.headers.host}`);
  hits.push(`${url.hostname}${url.pathname}`);
  res.writeHead(200, { "content-type": "text/html; charset=utf-8", "cache-control": "no-store" });
  // /with-ads on any host: a page that loads an ad script and an ad image (same scheme and port, protocol-relative).
  const ads = url.pathname === "/with-ads" ? `<script src="//${AD_HOST}:${url.port}/pagead/js/adsbygoogle.js"></script><img src="//${AD_HOST}:${url.port}/ad.gif">` : "";
  res.end(`<!doctype html><html><head><title>${esc(`SITE ${url.hostname}${url.pathname}`)}</title></head><body><h1>${esc(url.hostname)}</h1>${ads}</body></html>`);
}

function makeCert() {
  try {
    const key = join(scratch, "key.pem");
    const cert = join(scratch, "cert.pem");
    execFileSync("openssl", ["req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", key, "-out", cert, "-days", "2",
      "-subj", "/CN=site.test", "-addext", "subjectAltName=DNS:site.test,DNS:*.test,DNS:khanacademy.org,DNS:*.khanacademy.org,DNS:mashov.info"],
    { stdio: "ignore", windowsHide: true });
    return { key: readFileSync(key), cert: readFileSync(cert) };
  } catch {
    return null;
  }
}

function listen(server) {
  return new Promise((r, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => r(server.address().port));
  });
}

// ---- browser --------------------------------------------------------------------------------------------------------

let context = null;
let extensionId = null;

async function launchEdge() {
  if (cleanupPromise) throw new Error("aborted");
  context = await chromium.launchPersistentContext(edgeProfile, {
    channel: "msedge",
    headless: !process.env.HEADED,
    locale: "he-IL",
    ignoreHTTPSErrors: true,
    // Edge starts the native host with its own environment: the Simulation host reads its pipe and profile folder here.
    // The developer account is usually an administrator, whose service-down fallback is "unrestricted" (README §9);
    // WEBFILTER_SIMULATE_STANDARD_USER makes the host's fallback treat it as the standard (child) account it stands for.
    env: { ...process.env, WEBFILTER_AGENT_PIPE: agentPipe, WEBFILTER_PROFILE_DIR: profileDir, WEBFILTER_SIMULATE_STANDARD_USER: "1" },
    // The harness handles Ctrl+C itself, so the registry key is removed before the process exits.
    handleSIGINT: false,
    handleSIGTERM: false,
    handleSIGHUP: false,
    args: [
      `--disable-extensions-except=${DIST_TEST}`,
      `--load-extension=${DIST_TEST}`,
      "--host-resolver-rules=MAP * 127.0.0.1, EXCLUDE localhost",
      "--ignore-certificate-errors",
      "--lang=he",
    ],
  });
  let [sw] = context.serviceWorkers();
  if (!sw) sw = await context.waitForEvent("serviceworker", { timeout: 20000 });
  extensionId = new URL(sw.url()).host;
  return sw;
}

function attachWorkerLog(sw) {
  sw.on?.("console", (m) => extensionLog.write(`${new Date().toISOString()} [${m.type()}] ${m.text()}\n`));
}

/** The extension's current service worker (it is replaced if the browser restarts it). */
function worker() {
  const sw = context.serviceWorkers().find((w) => w.url().startsWith(`chrome-extension://${extensionId}/`));
  if (!sw) throw new Error("extension service worker not running");
  return sw;
}

async function swEval(fn, arg) {
  for (let attempt = 0; ; attempt++) {
    try {
      return await worker().evaluate(fn, arg);
    } catch (error) {
      if (attempt >= 2) throw error;
      await sleep(300);
    }
  }
}

const status = () => swEval(() => globalThis.wfTest.status());
const dynamicRules = () => swEval(() => chrome.declarativeNetRequest.getDynamicRules());
const sessionRules = () => swEval(() => chrome.declarativeNetRequest.getSessionRules());
const enabledRulesets = () => swEval(() => chrome.declarativeNetRequest.getEnabledRulesets());
const extPage = (path) => `chrome-extension://${extensionId}/${path}`;

async function visit(page, url, timeout = 15000) {
  try {
    await page.goto(url, { waitUntil: "load", timeout });
  } catch {
    // Chromium reports an abort when a navigation is redirected to an extension page.
    await page.waitForLoadState("load", { timeout: 5000 }).catch(() => {});
  }
  return page.url();
}

const pageInfo = (page) =>
  page.evaluate(() => ({
    title: document.title,
    ready: document.documentElement.dataset.ready ?? null,
    reason: document.documentElement.dataset.reason ?? null,
    reasonSource: document.documentElement.dataset.reasonSource ?? null,
    url: document.documentElement.dataset.url ?? null,
    canRequest: document.documentElement.dataset.canRequest ?? null,
    requestResult: document.documentElement.dataset.requestResult ?? null,
    timeState: document.documentElement.dataset.timeState ?? null,
  }));

/** Waits for an extension page (blocked/timeup) to finish its setup; null if the tab is not one. */
const readyInfo = (page, timeout = 10000) =>
  waitFor(async () => {
    if (!page.url().startsWith(`chrome-extension://${extensionId}/`)) return null;
    const i = await pageInfo(page);
    return i.ready ? i : null;
  }, timeout);

const titleIs = (page, title, timeout = 8000) => waitFor(async () => ((await page.title()) === title ? title : null), timeout);

let visitNo = 0;
const fresh = (path) => `${path}${path.includes("?") ? "&" : "?"}n=${++visitNo}`;

// ---- cleanup (finally, Ctrl+C, errors) ------------------------------------------------------------------------------

const servers = [];
let cleanupPromise = null;
const cleanupReport = {};

/** Native host processes Edge started for this extension (this build's exe, launched with our extension's origin). */
function leftoverHosts() {
  if (!extensionId) return [];
  const exe = HOST_EXE.replace(/'/g, "''");
  try {
    const out = execFileSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
      `Get-CimInstance Win32_Process -Filter "Name='WebFilter.NativeHost.exe'" | Where-Object { $_.ExecutablePath -eq '${exe}' -and $_.CommandLine -like '*chrome-extension://${extensionId}/*' } | ForEach-Object { $_.ProcessId }`],
    { encoding: "utf8", windowsHide: true });
    return out.split(/\s+/).filter(Boolean).map(Number);
  } catch {
    return [];
  }
}

function cleanup(reason) {
  if (cleanupPromise) return cleanupPromise;
  const step = async (name, fn) => {
    try {
      await fn();
    } catch (error) {
      cleanupReport.errors = [...(cleanupReport.errors ?? []), `${name}: ${error?.message ?? error}`];
    }
  };
  cleanupPromise = (async () => {
    cleanupReport.reason = reason;
    await step("close Edge", async () => {
      const closing = context;
      context = null;
      if (closing) await Promise.race([closing.close(), sleep(15000)]);
    });
    await step("stop service", stopService);
    // Edge closes the hosts' stdin when it exits, which ends them; stop any that are still there after a moment.
    await step("stop hosts", async () => {
      await waitFor(async () => leftoverHosts().length === 0, 5000, 250);
      const hosts = leftoverHosts();
      for (const pid of hosts) {
        try {
          process.kill(pid);
        } catch {
          // gone
        }
      }
      cleanupReport.leftoverHostsKilled = hosts;
    });
    await step("registry", async () => {
      cleanupReport.registryKeyRemoved = removeHostKey();
      cleanupReport.registryKeyAbsentAfterRun = !readHostKey().exists;
    });
    await step("web server", async () => {
      for (const s of servers) s.close();
    });
    await step("logs", async () => {
      await new Promise((r) => serviceLog.end(r));
      await new Promise((r) => extensionLog.end(r));
      mkdirSync(RESULTS, { recursive: true });
      copyFileSync(serviceLogPath, join(RESULTS, "e2e-real-service.log"));
      copyFileSync(extensionLogPath, join(RESULTS, "e2e-real-extension.log"));
    });
    await step("temp folder", async () => rmSync(scratch, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 }));
  })();
  return cleanupPromise;
}

// Backstop for any exit path: never leave the key or the service behind.
process.on("exit", () => {
  if (service?.alive) {
    try {
      service.kill();
    } catch {
      // gone
    }
  }
  if (createdKey) removeHostKey();
});
for (const signal of ["SIGINT", "SIGTERM", "SIGHUP", "SIGBREAK"]) {
  process.on(signal, () => {
    console.log(`\n${signal}: cleaning up…`);
    const force = setTimeout(() => process.exit(130), 30000);
    cleanup(signal).finally(() => {
      clearTimeout(force);
      console.log(`registry key removed: ${!readHostKey().exists}`);
      process.exit(130);
    });
  });
}

// ---- run ------------------------------------------------------------------------------------------------------------

const tls = makeCert();
const scheme = tls ? "https" : "http";
const webServer = tls ? createHttpsServer(tls, handler) : createHttpServer(handler);
servers.push(webServer);
let failures = 0;
const meta = { ranAt: new Date().toISOString(), scheme, agentPipe, adminPipe, userSid, scratch };

try {
  const port = await listen(webServer);
  const U = (host, path = "/") => `${scheme}://${host}:${port}${path}`;

  startService();
  const firstStatus = await waitForService();
  meta.settingsVersion = firstStatus.settingsVersion;
  await adminOk({ type: "setClock", now: SUNDAY_IN_WINDOW });

  createHostKey(manifestPath);
  const sw = await launchEdge();
  attachWorkerLog(sw);
  meta.extensionId = extensionId;
  meta.edgeVersion = (await swEval(() => navigator.userAgent)).match(/Edg\/([\d.]+)/)?.[1] ?? null;
  writeFileSync(manifestPath, JSON.stringify({
    name: HOST_NAME,
    description: "WebFilter native host (level-3 e2e test, Simulation build)",
    path: HOST_EXE,
    type: "stdio",
    allowed_origins: [`chrome-extension://${extensionId}/`],
  }, null, 2));
  await swEval(() => globalThis.wfTest.simulatePolicyInstall());

  // ===== 1. Connection ===============================================================================================
  {
    const connected = await waitFor(async () => {
      const s = await status();
      return s.connection === "service" ? s : null;
    }, 30000, 200);
    const s = connected ?? (await status());
    const rules = await dynamicRules();
    const rulesets = await enabledRulesets();
    const redirectsToUs = rules.some((r) => r.action?.redirect?.regexSubstitution?.startsWith(`chrome-extension://${extensionId}/blocked.html#`));
    check("1a handshake reaches the real service (controller connection = service)", s.connection === "service",
      { note: `${s.connection}; user ${s.user?.name} role ${s.user?.role}; mode ${s.mode}; rulesVersion ${s.rulesVersion}; ${s.lastError ?? ""}`, status: s });
    check("1b service identifies the Windows user as noa (child, allowlist), time ok", s.user?.role === "child" && s.mode === "allowlist" && s.time?.state === "ok",
      { note: `role ${s.user?.role}, mode ${s.mode}, time ${JSON.stringify(s.time)}` });
    check("1c dynamic DNR rules from the service installed, failclosed off", rules.length > 0 && rules.some((r) => r.id === 1) && redirectsToUs && !rulesets.includes("failclosed"),
      { note: `${rules.length} dynamic rules (ids ${rules.map((r) => r.id).join(",")}); enabled rulesets ${JSON.stringify(rulesets)}` });
    const popup = await context.newPage();
    await visit(popup, extPage("popup.html"));
    const popupState = await waitFor(() => popup.evaluate(() => document.documentElement.dataset.connection), 5000);
    check("1d popup shows connection = service", popupState === "service", { note: popupState });
    await popup.close();
  }

  const page = await context.newPage();

  // ===== 2. Blocked site =============================================================================================
  {
    hits.length = 0;
    const url = U("site.test", "/page?q=1");
    const landed = await visit(page, url);
    const info = await readyInfo(page);
    check("2a site not on noa's allowlist → blocked.html#<url>", landed === `${extPage("blocked.html")}#${url}` && info?.title === BLOCKED_TITLE,
      { note: landed });
    check("2b reason blocked_not_in_allowlist from the service (reportBlock → reportBlockResult)",
      info?.reason === "blocked_not_in_allowlist" && info.reasonSource === "service", { note: JSON.stringify(info) });
    check("2c the blocked request never reached the web server", !hits.some((h) => h.startsWith("site.test")), { note: hits.join(", ") || "no hits" });
    const events = await waitFor(async () => {
      const r = await adminOk({ type: "getEvents", eventType: "blocked", memberId: "noa" });
      return r.events.some((e) => e.url === url) ? r.events : null;
    }, 5000, 250);
    const event = events?.find((e) => e.url === url);
    check("2d admin getEvents shows the blocked event", event?.reason === "blocked_not_in_allowlist" && event.deviceId === "pc-kids",
      { note: JSON.stringify(event ?? null) });
  }

  // ===== 3. Allowed site =============================================================================================
  {
    hits.length = 0;
    await visit(page, U("www.khanacademy.org", "/math"));
    const title = await page.title();
    check("3 educational site (khanacademy.org) loads", title === "SITE www.khanacademy.org/math" && hits.includes("www.khanacademy.org/math"), { note: title });
  }

  // ===== 4. Live rule change =========================================================================================
  {
    const t0 = Date.now();
    await adminOk({ type: "addRule", rule: { id: "e2e-allow-site", member: "noa", pattern: "site.test", type: "domain", action: "allow" } });
    const installed = await waitFor(async () => (await dynamicRules()).some((r) => r.action?.type === "allow" && r.condition?.requestDomains?.includes("site.test")), 5000, 50);
    const tRules = Date.now() - t0;
    await visit(page, U("site.test", fresh("/after-rule")));
    const title = await titleIs(page, "SITE site.test/after-rule", 5000 - Math.min(5000, Date.now() - t0) + 500);
    const elapsed = Date.now() - t0;
    check("4 admin addRule (allow site.test for noa) → site.test loads within 5 s, no restart", !!installed && !!title && elapsed <= 5000,
      { note: `rules updated after ${tRules} ms, page loaded after ${elapsed} ms (${await page.title()})` });
  }

  // ===== 5. Access request ===========================================================================================
  {
    const url = U("other.test", "/lesson?id=7");
    const landed = await visit(page, url);
    const info = await readyInfo(page);
    const button = info ? await page.isVisible("#ask-url") : false;
    check("5a block page of another site offers a request (canRequest from the service)",
      landed === `${extPage("blocked.html")}#${url}` && info?.reasonSource === "service" && info.canRequest === "true" && button,
      { note: JSON.stringify(info) });
    await page.fill("#note", "e2e: needed for homework");
    await page.click("#ask-url");
    const created = await waitFor(async () => ((await pageInfo(page)).requestResult === "created" ? true : null), 8000);
    const pending = await adminOk({ type: "getRequests", status: "pending", memberId: "noa" });
    const request = pending.requests.find((r) => r.targetKey === "other.test");
    check("5b child sends the request; admin getRequests shows it pending",
      !!created && request?.kind === "url" && request.target === url && request.message === "e2e: needed for homework",
      { note: JSON.stringify(request ?? pending.requests) });
    const t0 = Date.now();
    if (request) await adminOk({ type: "decideRequest", requestId: request.id, decision: "approve", scope: "host", minutes: 15 });
    const back = await titleIs(page, "SITE other.test/lesson", 10000);
    const approved = (await adminOk({ type: "getRequests", status: "approved", memberId: "noa" })).requests.find((r) => r.id === request?.id);
    check("5c admin approves (host, 15 min) → the block page returns to the site by itself",
      !!back && approved?.status === "approved",
      { note: `returned after ${Date.now() - t0} ms; now ${page.url()}` });
  }

  // ===== 5d. YouTube channel search and channel names (PROTECTION-SPEC §15) =========================================
  {
    // The parent allows noa to search for channels (a one-flag setMember, as the member page sends it).
    await adminOk({ type: "setMember", memberId: "noa", youTubeChannelSearch: true });
    const searchOn = await waitFor(async () => ((await status()).features?.youTubeChannelSearch === true ? true : null), 8000, 100);
    // A channel's block page as the YouTube bridge opens it from a channel search (every host is local here).
    const natGeo = "UCnatgeoKids000000000004";
    const channelPage = U("www.youtube.com", "/@natgeokids-sim");
    await visit(page, `${extPage("blocked.html")}?reason=blocked_youtube_channel&channel=${natGeo}&title=NatGeo+Kids+%28sim%29&handle=%40natgeokids-sim&q=space#${channelPage}`);
    const info = await readyInfo(page);
    const button = info ? await page.isVisible("#ask-channel") : false;
    await page.click("#ask-channel");
    const created = await waitFor(async () => ((await pageInfo(page)).requestResult === "created" ? true : null), 8000);
    const asked = (await adminOk({ type: "getRequests", status: "pending", memberId: "noa" })).requests.find((r) => r.targetKey === natGeo);
    check("5d channel search on from the service; a channel request carries its title, handle and search to the parent",
      !!searchOn && info?.canRequest === "true" && button && !!created && asked?.title === "NatGeo Kids (sim)" && asked.handle === "@natgeokids-sim" && asked.searchQuery === "space",
      { note: JSON.stringify(asked ?? info) });
    if (asked) await adminOk({ type: "decideRequest", requestId: asked.id, decision: "approve", duration: "always" });
    const stored = (await adminOk({ type: "getSettings" })).settings.youTubeChannels.find((c) => c.channelId === natGeo && c.member === "noa");
    const mine = await context.newPage();
    await visit(mine, extPage("my-channels.html"));
    const named = await waitFor(() => mine.evaluate(() => [...document.querySelectorAll("#channels .channel-title")].map((e) => e.textContent).find((t) => t === "NatGeo Kids (sim)") ?? null), 8000);
    const box = await mine.evaluate(() => document.getElementById("search")?.hidden === false);
    check("5e the approval stores the channel with its names; my channels lists it by name and offers the channel search",
      stored?.title === "NatGeo Kids (sim)" && stored.handle === "@natgeokids-sim" && stored.action === "allow" && !!named && box,
      { note: JSON.stringify({ stored, named, box }) });
    await mine.close();
    await adminOk({ type: "setMember", memberId: "noa", youTubeChannelSearch: false });
  }

  // ===== 6. Time up ==================================================================================================
  {
    const lessonUrl = U("www.khanacademy.org", "/lesson");
    await visit(page, lessonUrl);
    const school = await context.newPage();
    await visit(school, U("mashov.info", "/grades"));
    const okBefore = (await page.title()) === "SITE www.khanacademy.org/lesson" && (await school.title()) === "SITE mashov.info/grades";

    await adminOk({ type: "setClock", now: MONDAY_OUTSIDE });
    const outside = await waitFor(async () => {
      const s = await status();
      return s.time?.state === "outside_window" ? s : null;
    }, 5000, 100);
    const moved = await titleIs(page, TIMEUP_TITLE, 5000);
    const sRules = await sessionRules();
    check("6a setClock Monday 10:00 → time state outside_window pushed, time-up session rules installed",
      okBefore && !!outside && sRules.some((r) => r.id === 10),
      { note: `state ${outside?.time?.state ?? (await status()).time?.state}; session rule ids ${sRules.map((r) => r.id).join(",")}` });
    check("6b the open tab moved to timeup.html", !!moved && page.url().startsWith(`${extPage("timeup.html")}#${lessonUrl}`), { note: page.url() });
    check("6c the always-allowed tab (mashov.info) stays", (await school.title()) === "SITE mashov.info/grades", { note: await school.title() });

    const probe = await context.newPage();
    const newNav = await visit(probe, U("www.khanacademy.org", fresh("/new")));
    const info = await readyInfo(probe);
    check("6d a new navigation → block page with blocked_time_window (from the service)",
      newNav.startsWith(extPage("blocked.html")) && info?.reason === "blocked_time_window" && info.reasonSource === "service",
      { note: `${newNav} ${JSON.stringify(info)}` });
    await visit(probe, U("mashov.info", fresh("/homework")));
    check("6e always-allowed mashov.info still loads in a new navigation", (await probe.title()) === "SITE mashov.info/homework", { note: await probe.title() });

    await adminOk({ type: "setClock", now: MONDAY_IN_WINDOW });
    const okAgain = await waitFor(async () => {
      const s = await status();
      return s.time?.state === "ok" ? s : null;
    }, 5000, 100);
    const returned = await titleIs(page, "SITE www.khanacademy.org/lesson", 8000);
    const sAfter = await sessionRules();
    await visit(probe, U("www.khanacademy.org", fresh("/again")));
    check("6f setClock back into the window → time ok, time-up tab returns by itself, session rules removed, browsing restored",
      !!okAgain && !!returned && sAfter.length === 0 && (await probe.title()) === "SITE www.khanacademy.org/again",
      { note: `time ${JSON.stringify(okAgain?.time)}; tab ${page.url()}; session rules ${sAfter.length}; probe ${await probe.title()}` });
    await school.close();
    await probe.close();
  }

  // ===== 7. Pause ====================================================================================================
  {
    const baseline = await visit(page, U("games.test", fresh("/play")));
    const bInfo = await readyInfo(page);
    const blockedBefore = baseline.startsWith(extPage("blocked.html")) && bInfo?.reason === "blocked_not_in_allowlist";

    await adminOk({ type: "pause", memberId: "noa", until: "2026-11-04T12:00:00+02:00" });
    const lifted = await waitFor(async () => {
      const [rules, s] = await Promise.all([dynamicRules(), status()]);
      return rules.length === 0 && s.time?.state === "unlimited" ? true : null;
    }, 5000, 100);
    await visit(page, U("games.test", fresh("/play")));
    const pausedTitle = await page.title();
    check("7a admin pause → all rules lifted, the blocked site loads",
      blockedBefore && !!lifted && pausedTitle === "SITE games.test/play", { note: `before: ${bInfo?.reason}; during pause: ${pausedTitle}` });

    await adminOk({ type: "resume", memberId: "noa" });
    const back = await waitFor(async () => ((await dynamicRules()).length > 0 ? true : null), 5000, 100);
    const after = await visit(page, U("games.test", fresh("/play")));
    const aInfo = await readyInfo(page);
    check("7b admin resume → rules return, the site is blocked again",
      !!back && after.startsWith(extPage("blocked.html")) && aInfo?.reason === "blocked_not_in_allowlist" && aInfo.reasonSource === "service",
      { note: `${after} ${JSON.stringify(aInfo)}` });
  }

  // ===== 7c. Ad blocking (PROTECTION-SPEC §12) ===================================================================
  {
    const adHits = () => hits.filter((h) => h.startsWith(AD_HOST));
    // The fixture's noa has no blockAds: the parent turns it on (a one-flag setMember, as the member page sends it).
    await adminOk({ type: "setMember", memberId: "noa", blockAds: true });
    const on = await waitFor(async () => ((await enabledRulesets()).includes("ads") && (await status()).features?.blockAds === true ? true : null), 8000, 100);
    hits.length = 0;
    await visit(page, U("www.khanacademy.org", fresh("/with-ads")));
    await sleep(1000);
    check("7c setMember blockAds → features.blockAds from the service, the ads ruleset on; an allowed page loads without its ads",
      !!on && (await page.title()) === "SITE www.khanacademy.org/with-ads" && adHits().length === 0,
      { note: `${await page.title()}; rulesets ${JSON.stringify(await enabledRulesets())}; ad hits ${adHits().join(",") || "none"}` });

    await adminOk({ type: "setMember", memberId: "noa", blockAds: false });
    const off = await waitFor(async () => (!(await enabledRulesets()).includes("ads") ? true : null), 8000, 100);
    hits.length = 0;
    await visit(page, U("www.khanacademy.org", fresh("/with-ads")));
    const reached = await waitFor(() => (adHits().length >= 2 ? true : null), 5000, 100);
    check("7d switched off by the parent → the ruleset is off and the ad requests reach the server", !!off && !!reached,
      { note: `ad hits ${adHits().join(",") || "none"}` });
  }

  // ===== 8. Service down =============================================================================================
  {
    const seen = new Set();
    let failClosedSeen = false;
    // The service writes its state to SQLite on each tick (1 s here), not on every change: let one tick pass so the
    // restart below starts from the latest settings. (An abrupt stop loses up to one TickInterval of changes.)
    await sleep(1500);
    const killedAt = Date.now();
    await stopService();
    const onHost = await waitFor(async () => {
      const [s, rulesets] = await Promise.all([status(), enabledRulesets()]);
      seen.add(s.connection);
      if (rulesets.includes("failclosed")) failClosedSeen = true;
      return s.connection === "host" ? s : null;
    }, 20000, 50);
    const s = onHost ?? (await status());
    check("8a service killed → port closes, extension goes fail-closed, then the host answers by itself",
      failClosedSeen && s.connection === "host",
      { note: `states seen ${[...seen].join(" → ")}; failclosed seen ${failClosedSeen}; now ${s.connection} mode ${s.mode} after ${Date.now() - killedAt} ms` });

    hits.length = 0;
    const down = await visit(page, U("games.test", fresh("/down")));
    const dInfo = await readyInfo(page);
    check("8b service down → block page says the service is not running (blocked_service_unavailable from the host)",
      s.mode === "allowlist" && down.startsWith(extPage("blocked.html")) && dInfo?.reason === "blocked_service_unavailable" && dInfo.reasonSource === "host"
        && !hits.some((h) => h.startsWith("games.test")),
      { note: `mode ${s.mode}: ${down} ${JSON.stringify(dInfo) ?? (await page.title())}` });
    await visit(page, U("mashov.info", fresh("/down")));
    const school = await page.title();
    check("8c service down → always-allowed mashov.info still loads (host fallback, profile cache)", school === "SITE mashov.info/down", { note: school });

    const restartedAt = Date.now();
    startService();
    await waitForService();
    await adminOk({ type: "setClock", now: TUESDAY_IN_WINDOW });
    const kept = (await adminOk({ type: "getSettings" })).settings.rules;
    const noa = (await adminOk({ type: "getStatus" })).members.find((m) => m.memberId === "noa");
    check("8d the restarted service kept what the admin pipe changed (added rule, approval, resume)",
      kept.some((r) => r.id === "e2e-allow-site") && kept.some((r) => r.pattern === "other.test" && r.member === "noa" && r.expiresAt) && !noa?.pausedUntil,
      { note: `rules ${kept.map((r) => `${r.id}:${r.pattern}`).join(", ")}; noa paused until ${noa?.pausedUntil ?? "-"}` });
    const recovered = await waitFor(async () => {
      const st = await status();
      return st.connection === "service" && st.time?.state === "ok" ? st : null;
    }, 30000, 200);
    const recoveredMs = Date.now() - restartedAt;
    const again = await visit(page, U("games.test", fresh("/back")));
    const rInfo = await readyInfo(page);
    await visit(page, U("www.khanacademy.org", fresh("/back")));
    const khan = await page.title();
    check("8e service restarted → extension back on the service without restarting Edge, normal filtering",
      !!recovered && again.startsWith(extPage("blocked.html")) && rInfo?.reason === "blocked_not_in_allowlist" && rInfo.reasonSource === "service"
        && khan === "SITE www.khanacademy.org/back",
      { note: `connection ${(recovered ?? (await status())).connection} after ${recoveredMs} ms; games.test → ${rInfo?.reason}/${rInfo?.reasonSource}; khan → ${khan}` });
  }

  // ===== 9. The parents' baseline protection (PROTECTION-SPEC §13) ===================================================
  {
    // The parent links this Windows account to dana (an unrestricted adult) and the family has an adult list. The service
    // runs without ManageBrowserPolicies, so the baseline's SafeSearch policies are not written anywhere.
    const read = await adminOk({ type: "getSettings" });
    const settings = read.settings;
    for (const account of settings.devices.find((d) => d.id === "pc-kids").accounts) if (account.accountId === userSid) account.member = "dana";
    settings.categories = { ...settings.categories, adult: ["adult-site.test"] };
    await adminOk({ type: "replaceSettings", settings, ifVersion: settings.version });
    const parent = await waitFor(async () => {
      const s = await status();
      return s.mode === "baseline" ? s : null;
    }, 8000, 100);
    const rulesets = await enabledRulesets();
    check("9a re-linked to an adult → mode baseline from the real rules reply, without a reconnect: ads ruleset on, unlimited time, no request",
      parent?.connection === "service" && parent.time?.state === "unlimited" && parent.canRequest === false && parent.features?.jumpGuard === true
        && parent.features.canOverrideJump === true && rulesets.includes("ads") && (await sessionRules()).length === 0,
      { note: JSON.stringify({ mode: parent?.mode ?? (await status()).mode, time: parent?.time, features: parent?.features, rulesets }) });

    hits.length = 0;
    const adultUrl = U("www.adult-site.test", fresh("/video"));
    const landed = await visit(page, adultUrl);
    const info = await readyInfo(page);
    const hintShown = info ? await page.isVisible("#baseline-hint") : false;
    check("9b a site of the adult list → blocked_baseline_adult from the service, no request button, the hint shown",
      landed.startsWith(extPage("blocked.html")) && info?.reason === "blocked_baseline_adult" && info.reasonSource === "service" && info.canRequest === "false"
        && !(await page.isVisible("#ask-url")) && hintShown && !hits.some((h) => h.startsWith("www.adult-site.test")),
      { note: `${landed} ${JSON.stringify(info)} hint ${hintShown}` });
    await visit(page, U("games.test", fresh("/play")));
    const games = await page.title();
    check("9c a site noa could not open loads for the parent", games === "SITE games.test/play", { note: games });
    const blocked = (await adminOk({ type: "getEvents", eventType: "blocked" })).events.filter((e) => e.memberId === "dana" || String(e.url).includes("adult-site.test"));
    check("9d nothing of the parent's browsing is recorded", blocked.length === 0, { note: JSON.stringify(blocked) });
  }
} catch (error) {
  check("harness error", false, { note: error?.stack ?? String(error) });
} finally {
  await cleanup("finished");
  failures = checks.filter((c) => !c.ok).length;
  const keyAbsent = !readHostKey().exists;
  console.log(`\nregistry: ${HOST_KEY} ${keyAbsent ? "absent (removed)" : "STILL PRESENT"}`);
  if (!keyAbsent) failures += 1;
  mkdirSync(RESULTS, { recursive: true });
  writeFileSync(join(RESULTS, "e2e-real.json"), JSON.stringify({
    ...meta,
    finishedAt: new Date().toISOString(),
    passed: checks.filter((c) => c.ok).length,
    failed: checks.filter((c) => !c.ok).length,
    cleanup: cleanupReport,
    checks,
  }, null, 2));
  console.log(`${checks.length - checks.filter((c) => !c.ok).length}/${checks.length} passed  (results: ${join(RESULTS, "e2e-real.json")})`);
  process.exit(failures ? 1 : 0);
}
