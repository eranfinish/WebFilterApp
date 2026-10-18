// Spike S2, part 2 — service worker lifetime without automation attached.
// Playwright keeps service workers alive (see run-s2 T3 control), so Edge is
// started directly here and the extension reports through the native host to
// the pipe server, whose stdout we read.
import { spawn, execFileSync } from "node:child_process";
import { createInterface } from "node:readline";
import { mkdirSync, mkdtempSync, writeFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { launch, recorder } from "../../S3-S4/harness/common.mjs";

const here = dirname(fileURLToPath(import.meta.url));
const S2 = resolve(here, "..");
const EXT = join(S2, "extension-keepalive");
const HOST_EXE = join(S2, "host", "bin", "Debug", "net10.0-windows", "WebFilter.Spike.Host.exe");
const SERVER_EXE = join(S2, "server", "bin", "Debug", "net10.0-windows", "WebFilter.Spike.Server.exe");
const EDGE = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const SYS = join(process.env.SystemRoot ?? "C:\\Windows", "System32");
const HOST_NAME = "com.webfilter.spike";
const REG_KEY = `HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\${HOST_NAME}`;
const sleep = (ms) => new Promise((res) => setTimeout(res, ms));
const r = recorder("s2-keepalive", join(S2, "results"));

// The unpacked extension id depends only on its path: learn it once through Playwright.
writeFileSync(join(EXT, "mode.json"), JSON.stringify({ mode: "control" }));
const probe = await launch({ extensionDir: EXT });
const extensionId = probe.extensionId;
await probe.close();

const manifestDir = join(tmpdir(), "wf-spike-s2-keepalive");
mkdirSync(manifestDir, { recursive: true });
const manifestPath = join(manifestDir, `${HOST_NAME}.json`);
writeFileSync(manifestPath, JSON.stringify({ name: HOST_NAME, description: "spike", path: HOST_EXE, type: "stdio", allowed_origins: [`chrome-extension://${extensionId}/`] }));
execFileSync(join(SYS, "reg.exe"), ["add", REG_KEY, "/ve", "/t", "REG_SZ", "/d", manifestPath, "/f"], { stdio: "ignore" });

const events = [];
const server = spawn(SERVER_EXE, ["--trusted", HOST_EXE], { stdio: ["ignore", "pipe", "inherit"] });
createInterface({ input: server.stdout }).on("line", (l) => { try { events.push({ ...JSON.parse(l), t: Date.now() }); } catch { /* ignore */ } });
await sleep(1500);

async function runEdge(mode, seconds) {
  writeFileSync(join(EXT, "mode.json"), JSON.stringify({ mode }));
  const profile = mkdtempSync(join(tmpdir(), "wf-spike-direct-"));
  const startedAt = Date.now();
  const edge = spawn(EDGE, ["--headless=new", `--user-data-dir=${profile}`, "--no-first-run", "--no-default-browser-check",
    `--disable-extensions-except=${EXT}`, `--load-extension=${EXT}`, "about:blank"], { stdio: "ignore" });
  await sleep(seconds * 1000);
  try { execFileSync(join(SYS, "taskkill.exe"), ["/PID", String(edge.pid), "/T", "/F"], { stdio: "ignore" }); } catch { /* already gone */ }
  await sleep(1500);
  rmSync(profile, { recursive: true, force: true });
  return events.filter((e) => e.evt === "beat" && e.t >= startedAt).map((e) => ({ ...e, sec: Math.round((e.t - startedAt) / 1000) }));
}

try {
  const keep = await runEdge("keepalive", 130);
  const boots = new Set(keep.map((b) => b.boot));
  const last = keep.filter((b) => b.phase === "keepalive").at(-1)?.sec ?? 0;
  r.metric("keepalive.beats", keep.map((b) => `${b.sec}s:${b.boot}:${b.phase}`));
  r.check("K1 with a native port open the worker keeps running past the 30 s idle limit", boots.size === 1 && last >= 110, { note: `last beat at ${last}s, worker boots=${boots.size}` });

  const control = await runEdge("control", 105);
  r.metric("control.beats", control.map((b) => `${b.sec}s:${b.boot}:${b.phase}`));
  const reportedAfterIdle = control.some((b) => b.phase === "control-after-idle");
  r.check("K2 control: without a port the idle worker is terminated (the 90 s report never arrives)", !reportedAfterIdle, { note: reportedAfterIdle ? "worker was NOT terminated — K1 would be inconclusive" : "terminated as expected" });
} finally {
  server.kill();
  execFileSync(join(SYS, "reg.exe"), ["delete", REG_KEY, "/f"], { stdio: "ignore" });
  rmSync(manifestDir, { recursive: true, force: true });
  rmSync(join(EXT, "mode.json"), { force: true });
}
process.exitCode = r.save() ? 1 : 0;
