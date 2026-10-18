// Spike S3 — declarativeNetRequest: fail-closed ruleset, redirect that keeps
// the original URL, blocklist/allowlist semantics, frames vs subresources,
// and large requestDomains lists.
import { launch, visit, blockInfo, chromiumMemoryMB, recorder } from "./common.mjs";
import { startServer } from "./server.mjs";

const S = (host, path = "/") => `http://${host}:8080${path}`;
const r = recorder("s3-dnr");
const server = await startServer(8080);
const b = await launch({ hostRules: "MAP * 127.0.0.1, EXCLUDE localhost" });
const page = await b.context.newPage();
const call = (fn, ...args) => b.sw.evaluate(({ fn, args }) => globalThis[fn](...args), { fn, args });
const isBlocked = (v) => v.url.startsWith(b.blockedPage);

try {
  // --- T1: the static fail-closed ruleset blocks everything until the worker says hello
  {
    const v = await visit(page, S("allowed.test"));
    const info = isBlocked(v) ? await blockInfo(page) : {};
    r.check("T1.1 fail-closed before hello", isBlocked(v) && info.reason === "failclosed", { got: v.url });
    await call("hello");
    const v2 = await visit(page, S("allowed.test"));
    r.check("T1.2 open after hello", v2.title === "SITE allowed.test/", { got: v2.title });
  }

  // --- T2: blocklist; the block page receives the exact original URL; the request never leaves the browser
  {
    await call("applyBlocklist", ["blocked.test"]);
    server.clearHits();
    const original = S("blocked.test", "/path/x?q=1&lang=he");
    const v = await visit(page, original);
    const info = isBlocked(v) ? await blockInfo(page) : {};
    r.check("T2.1 blocked domain → block page", isBlocked(v), { got: v.url });
    r.check("T2.2 block page shows the original URL", info.url === original, { got: info.url, note: info.url });
    r.check("T2.3 blocked request never reached the server", !server.hits.some((h) => h.host === "blocked.test"));
    r.check("T2.4 subdomain blocked", isBlocked(await visit(page, S("www.blocked.test"))));
    r.check("T2.5 look-alike domain not blocked", (await visit(page, S("notblocked.test"))).title === "SITE notblocked.test/");
    r.check("T2.6 suffix-extended domain not blocked", (await visit(page, S("blocked.test.other.test"))).title === "SITE blocked.test.other.test/");

    const v3 = await visit(page, S("allowed.test", `/with-iframe?src=${encodeURIComponent(S("blocked.test", "/inner"))}`));
    await page.waitForTimeout(500);
    const frame = page.frames().find((f) => f !== page.mainFrame());
    r.check("T2.7 iframe to blocked domain is replaced by the block page", v3.title.startsWith("SITE allowed.test") && frame?.url().startsWith(b.blockedPage), { got: frame?.url() });
  }

  // --- T3: allowlist; frames are filtered, subresources are not
  {
    await call("applyAllowlist", ["allowed.test"]);
    server.clearHits();
    const v = await visit(page, S("allowed.test", `/with-img?src=${encodeURIComponent(S("cdn.test", "/pixel.png"))}`));
    await page.waitForTimeout(300);
    r.check("T3.1 allowed site loads", v.title.startsWith("SITE allowed.test"), { got: v.title });
    r.check("T3.2 subresource from another domain still loads", server.hits.some((h) => h.host === "cdn.test" && h.path === "/pixel.png"));
    r.check("T3.3 subdomain of allowed site loads", (await visit(page, S("sub.allowed.test"))).title === "SITE sub.allowed.test/");
    r.check("T3.4 other site blocked", isBlocked(await visit(page, S("other.test"))));
    await visit(page, S("allowed.test", `/with-iframe?src=${encodeURIComponent(S("other.test", "/x"))}`));
    await page.waitForTimeout(500);
    const frame = page.frames().find((f) => f !== page.mainFrame());
    r.check("T3.5 iframe to non-allowed site blocked", frame?.url().startsWith(b.blockedPage), { got: frame?.url() });
  }

  // --- T4: scale — one rule with N domains in requestDomains
  {
    await call("clearRules");
    await page.goto("about:blank");
    await page.waitForTimeout(1000);
    const baseMem = chromiumMemoryMB();
    r.metric("memory.baselineMB", baseMem);
    const baselineNav = [];
    for (let i = 0; i < 5; i++) baselineNav.push((await visit(page, S(`free${i}.test`))).ms);
    r.metric("nav.allowedNoRules.ms", baselineNav);

    for (const n of [100_000, 300_000, 1_000_000]) {
      let ms;
      try {
        ms = await call("applySyntheticBlocklist", n, ["target-last.test"]);
      } catch (e) {
        r.check(`T4.${n} install ${n} domains`, false, { note: e.message.split("\n")[0] });
        continue;
      }
      const summary = await call("ruleSummary");
      r.check(`T4.${n}.1 installed ${n.toLocaleString()} domains in one rule`, summary[0]?.domains === n + 1, { note: `${ms} ms` });
      r.metric(`install.${n}.ms`, ms);
      r.check(`T4.${n}.2 first domain blocked`, isBlocked(await visit(page, S("d0000000.example"))));
      r.check(`T4.${n}.3 last domain blocked`, isBlocked(await visit(page, S("target-last.test"))));
      const navAllowed = [], navBlocked = [];
      for (let i = 0; i < 5; i++) {
        navAllowed.push((await visit(page, S(`free${i}.test`))).ms);
        navBlocked.push((await visit(page, S(`d${String(n - 1 - i).padStart(7, "0")}.example`))).ms);
      }
      r.check(`T4.${n}.4 unlisted domain still allowed`, (await visit(page, S("free9.test"))).title === "SITE free9.test/");
      r.metric(`nav.${n}.allowed.ms`, navAllowed);
      r.metric(`nav.${n}.blocked.ms`, navBlocked);
      await page.goto("about:blank");
      await page.waitForTimeout(1000);
      r.metric(`memory.${n}.MB`, chromiumMemoryMB());
    }
  }

  // --- T5: turning fail-closed back on blocks again
  {
    await call("clearRules");
    await call("failClosed");
    const v = await visit(page, S("allowed.test"));
    r.check("T5.1 fail-closed can be re-enabled", isBlocked(v) && (await blockInfo(page)).reason === "failclosed");
    await call("hello");
  }
} finally {
  await b.close();
  await server.close();
}
process.exitCode = r.save() ? 1 : 0;
