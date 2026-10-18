// Spike S2 (unelevated part) — native messaging host launched by Edge as the
// user, relaying to a pipe server that identifies the caller by the OS; host
// restart; fallback when the service is down; rogue pipe clients; service
// worker lifetime with an open native port.
// Reuses the Playwright install and helpers from spikes/S3-S4/harness.
import { spawn, execFileSync } from "node:child_process";
import { createInterface } from "node:readline";
import { mkdirSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import net from "node:net";
import { launch, recorder } from "../../S3-S4/harness/common.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const S2 = resolve(here, "..");
const HOST_EXE = join(S2, "host", "bin", "Debug", "net10.0-windows", "WebFilter.Spike.Host.exe");
const SERVER_EXE = join(S2, "server", "bin", "Debug", "net10.0-windows", "WebFilter.Spike.Server.exe");
const HOST_NAME = "com.webfilter.spike";
const REG_KEY = `HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\${HOST_NAME}`;
const SYS = join(process.env.SystemRoot ?? "C:\Windows", "System32");
const PIPE = "\\\\.\\pipe\\WebFilter.Spike.Agent";
const KEEPALIVE_WAIT_MS = 75_000; // MV3 idle timeout is 30 s

const r = recorder("s2-native-messaging", join(S2, "results"));
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const userSid = execFileSync(join(SYS, "whoami.exe"), ["/user", "/fo", "csv", "/nh"], { encoding: "utf8" }).trim().split(",")[1].replaceAll('"', "");
const isAdminAccount = /S-1-5-32-544/.test(execFileSync(join(SYS, "whoami.exe"), ["/groups", "/fo", "csv", "/nh"], { encoding: "utf8" }));

// --- pipe server process
let server = null;
const events = [];
function startServer() {
  events.length = 0;
  server = spawn(SERVER_EXE, ["--trusted", HOST_EXE], { stdio: ["ignore", "pipe", "inherit"] });
  createInterface({ input: server.stdout }).on("line", (line) => { try { events.push(JSON.parse(line)); } catch { /* ignore */ } });
  return waitFor(() => events.some((e) => e.evt === "listening"), 10_000);
}
function stopServer() { server?.kill(); server = null; }

async function waitFor(predicate, timeout = 15_000) {
  const t0 = Date.now();
  while (Date.now() - t0 < timeout) {
    const value = await predicate();
    if (value) return value;
    await sleep(200);
  }
  return null;
}

// --- browser + extension
const b = await launch({ extensionDir: join(S2, "extension") });
const manifestDir = join(tmpdir(), "wf-spike-s2");
mkdirSync(manifestDir, { recursive: true });
const manifestPath = join(manifestDir, `${HOST_NAME}.json`);
writeFileSync(manifestPath, JSON.stringify({
  name: HOST_NAME, description: "WebFilter spike host", path: HOST_EXE, type: "stdio",
  allowed_origins: [`chrome-extension://${b.extensionId}/`],
}, null, 2));
execFileSync(join(SYS, "reg.exe"), ["add", REG_KEY, "/ve", "/t", "REG_SZ", "/d", manifestPath, "/f"], { stdio: "ignore" });

async function liveWorker() {
  try { await b.sw.evaluate(() => 1); return b.sw; } catch { /* terminated */ }
  const wake = await b.context.newPage();
  await wake.goto(`chrome-extension://${b.extensionId}/wake.html`).catch(() => {});
  const sw = b.context.serviceWorkers().at(-1) ?? await b.context.waitForEvent("serviceworker");
  await wake.close();
  b.sw = sw;
  return sw;
}
const call = async (fn, ...args) => (await liveWorker()).evaluate(({ fn, args }) => globalThis[fn](...args), { fn, args });
const nextMessage = (type, after) => waitFor(async () => (await call("messages")).slice(after).find((m) => m.type === type));
const messageCount = async () => (await call("messages")).length;

try {
  await startServer();

  // T1: Edge launches the host as the user; the server identifies the caller by the OS
  let n = await messageCount();
  await call("connect");
  await call("send", { type: "hello" });
  const hello = await nextMessage("hello", n);
  r.check("T1.1 hello reaches the service through the host", hello?.source === "service", { note: JSON.stringify(hello?.client) });
  r.check("T1.2 service sees the browser user's SID (impersonation)", hello?.user?.sid === userSid, { note: `${hello?.user?.name} ${hello?.user?.sid}` });
  r.check("T1.3 service sees the host's image path", hello?.client?.image?.toLowerCase() === HOST_EXE.toLowerCase(), { note: hello?.client?.image });
  r.check("T1.4 client is trusted", hello?.client?.trusted === true);
  r.check("T1.5 process-token SID (no impersonation) agrees", hello?.client?.processTokenSid === userSid, { note: hello?.client?.processTokenSid });
  r.metric("host.coldStartToHello.ms", hello?.sinceConnect);
  r.metric("client.sessionId", hello?.client?.sessionId);

  // T2: the service can push to the extension
  n = await messageCount();
  await call("send", { type: "set-ticks", intervalSec: 2 });
  r.check("T2.1 service push reaches the extension", Boolean(await nextMessage("tick", n)));
  await call("send", { type: "set-ticks", intervalSec: 0 });

  // T3: service worker lifetime with an open native port and no traffic
  const before = await call("info");
  await sleep(KEEPALIVE_WAIT_MS);
  const after = await call("info");
  const aliveWithPort = after.boot === before.boot && after.connected && after.disconnects.length === 0;
  r.check(`T3.1 worker and port survive ${KEEPALIVE_WAIT_MS / 1000}s idle with a native port open`, aliveWithPort, { note: JSON.stringify({ before: before.boot.slice(0, 8), after: after.boot.slice(0, 8), connected: after.connected }) });

  // T3 control: same wait without a port — if the worker also survives, Playwright is keeping it alive and T3.1 proves nothing
  await call("disconnect");
  const controlBefore = await call("info");
  await sleep(KEEPALIVE_WAIT_MS);
  const controlAfter = await call("info");
  const controlRestarted = controlAfter.boot !== controlBefore.boot;
  r.metric("T3.control.workerRestartedWithoutPort", controlRestarted);
  r.metric("T3.conclusive", controlRestarted ? "yes" : "no — the worker survived without a port too (automation keeps it alive); verify manually in S1/M3");

  // T4: killing the host disconnects the port; reconnecting starts a new host
  n = await messageCount();
  await call("connect");
  await call("send", { type: "hello" });
  const firstHost = (await nextMessage("hello", n))?.client?.pid;
  const disconnectsBefore = (await call("info")).disconnects.length;
  execFileSync(join(SYS, "taskkill.exe"), ["/F", "/IM", "WebFilter.Spike.Host.exe"], { stdio: "ignore" });
  const sawDisconnect = await waitFor(async () => (await call("info")).disconnects.length > disconnectsBefore, 10_000);
  r.check("T4.1 extension notices the host died", Boolean(sawDisconnect));
  n = await messageCount();
  await call("connect");
  await call("send", { type: "hello" });
  const secondHost = (await nextMessage("hello", n))?.client?.pid;
  r.check("T4.2 reconnect starts a new host", Boolean(secondHost) && secondHost !== firstHost, { note: `${firstHost} → ${secondHost}` });

  // T5: service down — the host answers by itself
  await call("disconnect");
  stopServer();
  await sleep(500);
  n = await messageCount();
  await call("connect");
  await call("send", { type: "hello" });
  const fallback = await nextMessage("hello", n);
  r.check("T5.1 host answers when the service is down", fallback?.source === "host" && fallback?.serviceAvailable === false, { note: JSON.stringify(fallback?.account) });
  r.check("T5.2 host detects an admin account running unelevated", fallback?.account?.isAdmin === isAdminAccount, { note: `elevation=${fallback?.account?.elevation}, inAdminRole=${fallback?.account?.inAdminRole}, expected admin=${isAdminAccount}` });
  await call("disconnect");

  // T6: a rogue process speaking the same protocol is refused by image path
  await startServer();
  const rogue = await new Promise((res) => {
    const sock = net.connect(PIPE, () => {
      const body = Buffer.from(JSON.stringify({ type: "hello" }));
      const header = Buffer.alloc(4);
      header.writeInt32LE(body.length);
      sock.write(Buffer.concat([header, body]));
    });
    let buf = Buffer.alloc(0);
    sock.on("data", (d) => {
      buf = Buffer.concat([buf, d]);
      if (buf.length >= 4 && buf.length >= 4 + buf.readInt32LE(0)) { res(JSON.parse(buf.subarray(4, 4 + buf.readInt32LE(0)))); sock.destroy(); }
    });
    sock.on("error", (e) => res({ error: e.message }));
    setTimeout(() => res({ error: "timeout" }), 5000);
  });
  r.check("T6.1 rogue client is rejected", rogue?.type === "rejected", { note: JSON.stringify(rogue) });
} finally {
  stopServer();
  execFileSync(join(SYS, "reg.exe"), ["delete", REG_KEY, "/f"], { stdio: "ignore" });
  rmSync(manifestDir, { recursive: true, force: true });
  await b.close();
}
process.exitCode = r.save() ? 1 : 0;
