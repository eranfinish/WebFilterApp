// Loads store/extension-s1 unpacked into the installed Edge and checks the real popup (real chrome.* APIs),
// once with an English and once with a Hebrew browser UI.
//
//   cd store/tools && npm run check [-- <dir for popup screenshots>]
//   WF_EXTENSION_DIR=<folder> npm run check   (e.g. the dist zip extracted)
//
// An unpacked extension reports installType "development", so the expected state here is "not installed by
// policy". The "admin" state can only be seen with the store build installed through ExtensionInstallForcelist (S1).
// To know the ID in advance, the extension is copied to a temp folder and given a throwaway "key";
// the package itself never has a "key".

import { chromium } from "playwright";
import { cp, mkdtemp, readFile, writeFile, rm } from "node:fs/promises";
import { createHash, generateKeyPairSync } from "node:crypto";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
// WF_EXTENSION_DIR checks another copy, e.g. the packaged zip extracted to a folder.
const extensionDir = process.env.WF_EXTENSION_DIR ?? path.resolve(here, "..", "extension-s1");
const outDir = process.argv[2] ?? tmpdir();

const { publicKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
const spki = publicKey.export({ type: "spki", format: "der" });
const expectedId = [...createHash("sha256").update(spki).digest("hex").slice(0, 32)]
  .map((c) => String.fromCharCode(97 + parseInt(c, 16)))
  .join("");

const work = await mkdtemp(path.join(tmpdir(), "wf-s1-check-"));
const unpacked = path.join(work, "extension");
await cp(extensionDir, unpacked, { recursive: true });
const manifestPath = path.join(unpacked, "manifest.json");
const manifest = JSON.parse(await readFile(manifestPath, "utf8"));
await writeFile(manifestPath, JSON.stringify({ ...manifest, key: spki.toString("base64") }));

const expected = {
  en: { lang: "en", dir: "ltr", title: "Not installed by policy", mayDisable: "Yes" },
  he: { lang: "he", dir: "rtl", title: "לא הותקן על ידי מדיניות", mayDisable: "כן" },
};

let failures = 0;
try {
  for (const uiLang of ["en", "he"]) {
    const profile = path.join(work, `profile-${uiLang}`);
    const context = await chromium.launchPersistentContext(profile, {
      channel: "msedge",
      args: [`--disable-extensions-except=${unpacked}`, `--load-extension=${unpacked}`, `--lang=${uiLang}`],
      viewport: { width: 320, height: 420 },
      colorScheme: "light",
    });
    const page = await context.newPage();
    const errors = [];
    page.on("pageerror", (error) => errors.push(error.message));
    page.on("console", (message) => { if (message.type() === "error") errors.push(message.text()); });

    await page.goto(`chrome-extension://${expectedId}/popup.html`);
    await page.waitForFunction(() => document.getElementById("status").dataset.state !== "loading");
    const actual = await page.evaluate(() => ({
      lang: document.documentElement.lang,
      dir: document.documentElement.dir,
      title: document.getElementById("status-title").textContent,
      state: document.getElementById("status").dataset.state,
      id: document.getElementById("ext-id").textContent,
      version: document.getElementById("ext-version").textContent,
      installType: document.getElementById("install-type").textContent,
      mayDisable: document.getElementById("may-disable").textContent,
      emptyLabels: [...document.querySelectorAll("[data-i18n]")].filter((el) => !el.textContent).length,
    }));
    const want = expected[uiLang];
    const checks = {
      "no errors": errors.length === 0,
      "lang/dir": actual.lang === want.lang && actual.dir === want.dir,
      "state unmanaged": actual.state === "unmanaged" && actual.title === want.title,
      "id": actual.id === expectedId,
      "version": actual.version === manifest.version,
      "installType development": actual.installType === "development",
      "mayDisable": actual.mayDisable === want.mayDisable,
      "all labels localized": actual.emptyLabels === 0,
    };
    for (const [name, ok] of Object.entries(checks)) {
      console.log(`${ok ? "PASS" : "FAIL"}  [${uiLang}] ${name}`);
      if (!ok) failures++;
    }
    if (errors.length) console.log(errors.join("\n"));
    const shot = path.join(outDir, `popup-dev-${uiLang}.png`);
    await page.screenshot({ path: shot, fullPage: true });
    console.log(`      screenshot: ${shot}`);
    await context.close();
  }
} finally {
  await rm(work, { recursive: true, force: true });
}

console.log(failures ? `${failures} check(s) failed` : "all checks passed");
process.exitCode = failures ? 1 : 0;
