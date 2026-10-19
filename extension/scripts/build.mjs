// Builds the unpacked extension.
//   node scripts/build.mjs               → dist/       (production: no test hooks)
//   node scripts/build.mjs --test-hooks  → dist-test/  (adds globalThis.wfTest in the service worker for Playwright)
import { build } from "esbuild";
import { cpSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = join(dirname(fileURLToPath(import.meta.url)), "..");
const testHooks = process.argv.includes("--test-hooks");
const outdir = join(root, testHooks ? "dist-test" : "dist");
const pkg = JSON.parse(readFileSync(join(root, "package.json"), "utf8"));

rmSync(outdir, { recursive: true, force: true });
mkdirSync(outdir, { recursive: true });

await build({
  entryPoints: {
    background: join(root, "src/background", testHooks ? "main-test.ts" : "main.ts"),
    "yt-main": join(root, "src/content/yt-main.ts"),
    "yt-bridge": join(root, "src/content/yt-bridge.ts"),
    blocked: join(root, "src/pages/blocked.ts"),
    timeup: join(root, "src/pages/timeup.ts"),
    "my-channels": join(root, "src/pages/my-channels.ts"),
    popup: join(root, "src/pages/popup.ts"),
  },
  outdir,
  bundle: true,
  format: "iife",
  target: ["chrome120"],
  charset: "utf8",
  legalComments: "none",
  // Readable output: store reviewers read it, and it has nothing to hide.
  minify: false,
  sourcemap: testHooks ? "inline" : false,
  logLevel: "warning",
});

// Static files.
const manifest = JSON.parse(readFileSync(join(root, "manifest.json"), "utf8"));
manifest.version = pkg.version;
writeFileSync(join(outdir, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);
for (const dir of ["_locales", "icons", "rulesets"]) cpSync(join(root, dir), join(outdir, dir), { recursive: true });
for (const file of readdirSync(join(root, "pages"))) cpSync(join(root, "pages", file), join(outdir, file));

// Sanity checks: every file the manifest names exists, and the production bundle has no test hooks.
const referenced = [
  manifest.background.service_worker,
  manifest.action.default_popup,
  ...Object.values(manifest.icons),
  ...manifest.content_scripts.flatMap((c) => c.js),
  ...manifest.declarative_net_request.rule_resources.map((r) => r.path),
  ...manifest.web_accessible_resources.flatMap((w) => w.resources).filter((r) => !r.includes("*")),
];
const missing = referenced.filter((f) => !existsSync(join(outdir, f)));
if (missing.length > 0) throw new Error(`Missing files in ${outdir}: ${missing.join(", ")}`);
const background = readFileSync(join(outdir, "background.js"), "utf8");
if (!testHooks && (background.includes("wfTest") || background.includes("wf-test-hooks") || background.includes("FakeHost"))) {
  throw new Error("The production background.js contains test hooks");
}
if (testHooks && !background.includes("wf-test-hooks")) throw new Error("The test build is missing its hooks");

console.log(`built ${testHooks ? "test" : "production"} extension ${pkg.version} → ${outdir}`);
