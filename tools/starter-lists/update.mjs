#!/usr/bin/env node
// Regenerates the sourced starter lists in data/starter-lists (adult, gambling, proxies, ads-popups) from pinned
// upstream files, with the filtering described in data/starter-lists/README.md. Run by hand, then review the diff:
//
//   node tools/starter-lists/update.mjs [--cache-dir <dir>] [--offline] [--report <file>]
//
// Downloads go to a cache directory (default: <os tmp>/webfilter-starter-lists) and are only parsed as text.
// To move to newer sources, change the pinned commits in sources.mjs, run this, and review the report and the diff.
// No dependencies; Node 20 or later.

import fs from "node:fs";
import path from "node:path";
import url from "node:url";
import * as curation from "./curation.mjs";
import {
  CRUX, CRUX_MONTH, CRUX_SOURCES, EASYLIST, PSL_SOURCE, SHADOW, SINFONIETTA, commandLine, download, easylist, gh, lines,
  parseCrux, parsePsl,
} from "./sources.mjs";

const here = path.dirname(url.fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..", "..");
const listsDir = path.join(repoRoot, "data", "starter-lists");

// ---- pinned sources ------------------------------------------------------------------------------------------------

const SOURCES = {
  swAdult: gh("ShadowWhisperer/BlockLists", SHADOW, "Lists/Adult"),
  swGambling: gh("ShadowWhisperer/BlockLists", SHADOW, "Lists/Gambling"),
  swTunnels: gh("ShadowWhisperer/BlockLists", SHADOW, "Lists/Tunnels"),
  sinPorn: gh("Sinfonietta/hostfiles", SINFONIETTA, "pornography-hosts"),
  sinGambling: gh("Sinfonietta/hostfiles", SINFONIETTA, "gambling-hosts"),
  easylistPopup: easylist("easylist_adservers_popup.txt"),
  ...CRUX_SOURCES,
  psl: PSL_SOURCE,
};

/** Most domains a sourced list may ship (a safety net; see README for the actual sizes). */
const MAX_DOMAINS = { adult: 60_000, gambling: 20_000, proxies: 5_000, "ads-popups": 10_000 };

const SHADOW_NOTICE = "ShadowWhisperer/BlockLists (https://github.com/ShadowWhisperer/BlockLists), Unlicense (https://unlicense.org)";
const SINFONIETTA_NOTICE =
  "Sinfonietta/hostfiles (https://github.com/Sinfonietta/hostfiles), MIT License, Copyright (c) 2016 Sinfonietta. " +
  "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated " +
  "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the " +
  "rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit " +
  "persons to whom the Software is furnished to do so, subject to the following conditions: The above copyright notice " +
  "and this permission notice shall be included in all copies or substantial portions of the Software. THE SOFTWARE IS " +
  "PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF " +
  "MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT " +
  "HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, " +
  "ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.";

// ---- command line -------------------------------------------------------------------------------------------------

const cli = commandLine(process.argv.slice(2), "review.txt");
const { reportFile } = cli;

// ---- parsing ------------------------------------------------------------------------------------------------------

/** ShadowWhisperer: one domain per line, '#' comments. */
const parsePlain = (buffer) => lines(buffer).map((l) => l.trim()).filter((l) => l && !l.startsWith("#"));

/** Hosts files: "0.0.0.0 domain". */
const parseHosts = (buffer) =>
  lines(buffer)
    .map((l) => l.replace(/#.*/, "").trim())
    .filter(Boolean)
    .map((l) => l.split(/\s+/))
    .filter((parts) => parts.length >= 2 && (parts[0] === "0.0.0.0" || parts[0] === "127.0.0.1"))
    .map((parts) => parts[1]);

/** EasyList: only whole-domain popup rules ("||domain^$popup"), not ones limited to third-party or a path. */
const parseEasyListPopup = (buffer) =>
  lines(buffer)
    .map((l) => /^\|\|([a-z0-9.-]+)\^\$popup$/i.exec(l.trim()))
    .filter(Boolean)
    .map((m) => m[1]);

// ---- normalisation ------------------------------------------------------------------------------------------------

const LABEL = /^(?!-)[a-z0-9-]{1,63}(?<!-)$/;

/** A lowercase ASCII (punycode) host, without a trailing dot or a www./m./mobile. prefix; null if not a domain name. */
function normalize(input) {
  let host = String(input).trim().toLowerCase().replace(/\.$/, "");
  if (!host || host.includes("*") || host.includes("/")) return null;
  host = url.domainToASCII(host);
  if (!host) return null;
  const labels = host.split(".");
  if (labels.length < 2 || !labels.every((l) => LABEL.test(l))) return null;
  if (/^[0-9]+$/.test(labels.at(-1)) || labels.at(-1).length < 2) return null; // IP addresses and junk
  while (labels.length > 2 && ["www", "m", "mobile"].includes(labels[0])) labels.shift();
  return labels.join(".");
}

/** The host and each parent domain above it ("a.b.c" → "a.b.c", "b.c"). */
function selfAndParents(host) {
  const labels = host.split(".");
  const result = [];
  for (let i = 0; i < labels.length - 1; i++) result.push(labels.slice(i).join("."));
  return result;
}

/** Drops entries whose parent domain is also listed (a domain matches its subdomains). */
function withoutCoveredSubdomains(domains) {
  const set = new Set(domains);
  return [...set].filter((d) => !selfAndParents(d).slice(1).some((p) => set.has(p)));
}

// ---- building -----------------------------------------------------------------------------------------------------

const report = [];
const note = (text) => report.push(text);

/**
 * Normalises, applies the guards and the hand-curated changes, and returns the sorted list.
 * @param {string} name list name
 * @param {string[]} raw source entries
 * @param {{ add?: string[], remove?: string[], dropUnder?: string[] }} edits from curation.mjs (DROP_UNDER applies to all)
 * @param {{ taken: Set<string>, protectedDomains: Set<string>, protectedParents: Set<string>,
 *           isPublicSuffix: (h: string) => boolean, ranks: Map<string, number> }} ctx
 * @param {(domain: string, rank: number | undefined) => boolean} keep selection beyond the guards
 */
function build(name, raw, edits, ctx, keep) {
  const removed = new Set((edits.remove ?? []).map(normalize));
  const dropUnder = [...curation.DROP_UNDER, ...(edits.dropUnder ?? [])].map(normalize);
  const dropped = { invalid: 0, publicSuffix: [], protected: [], curated: [], under: 0, otherList: [], unselected: 0 };
  const candidates = new Map();

  for (const entry of raw) {
    const domain = normalize(entry);
    if (!domain) {
      dropped.invalid++;
      continue;
    }
    if (candidates.has(domain)) continue;
    if (ctx.isPublicSuffix(domain)) {
      dropped.publicSuffix.push(domain);
      continue;
    }
    // Neither a protected domain nor a parent of one (blocking "google.com" would also block "classroom.google.com").
    if (ctx.protectedDomains.has(domain) || ctx.protectedParents.has(domain)) {
      dropped.protected.push(domain);
      continue;
    }
    if (selfAndParents(domain).some((d) => removed.has(d))) {
      dropped.curated.push(domain);
      continue;
    }
    if (dropUnder.some((suffix) => domain.endsWith("." + suffix))) {
      dropped.under++;
      continue;
    }
    const rank = ctx.ranks.get(domain);
    if (!keep(domain, rank)) {
      dropped.unselected++;
      continue;
    }
    candidates.set(domain, rank);
  }

  for (const entry of edits.add ?? []) {
    const domain = normalize(entry);
    if (!domain) throw new Error(`${name}: curated entry "${entry}" is not a domain.`);
    candidates.set(domain, ctx.ranks.get(domain) ?? 0);
  }

  // Most visited first when the list must be cut, then alphabetical for the file.
  let domains = [...candidates.entries()]
    .sort((a, b) => (a[1] ?? Infinity) - (b[1] ?? Infinity) || (a[0] < b[0] ? -1 : 1))
    .map(([d]) => d);
  if (domains.length > MAX_DOMAINS[name]) {
    note(`${name}: cut from ${domains.length} to ${MAX_DOMAINS[name]} by CrUX rank.`);
    domains = domains.slice(0, MAX_DOMAINS[name]);
  }

  domains = withoutCoveredSubdomains(domains).filter((d) => {
    if (ctx.taken.has(d)) {
      dropped.otherList.push(d);
      return false;
    }
    return true;
  });
  domains.sort();
  domains.forEach((d) => ctx.taken.add(d));

  note(
    `\n== ${name}: ${domains.length} domains (from ${raw.length} source entries; ` +
      `${dropped.invalid} not domains, ${dropped.unselected} not selected, ${dropped.under} under CDN/cloud hosts)`,
  );
  note(`   public suffixes dropped: ${dropped.publicSuffix.join(" ") || "none"}`);
  note(`   protected domains dropped: ${dropped.protected.join(" ") || "none"}`);
  note(`   curated removals hit: ${dropped.curated.join(" ") || "none"}`);
  note(`   already in another list: ${dropped.otherList.join(" ") || "none"}`);
  const ranked = domains.filter((d) => ctx.ranks.has(d)).sort((a, b) => ctx.ranks.get(a) - ctx.ranks.get(b));
  note(`   ranked in CrUX (review these for false positives, most visited first): ${ranked.length}`);
  for (let i = 0; i < ranked.length; i += 8)
    note("     " + ranked.slice(i, i + 8).map((d) => `${d}(${ctx.ranks.get(d) / 1000}k)`).join(" "));
  return domains;
}

function writeList(name, description, source, license, domains) {
  const file = path.join(listsDir, `${name}.json`);
  const json = { category: name, description, source, license, domains };
  fs.writeFileSync(file, JSON.stringify(json, null, 2) + "\n");
  console.log(`wrote ${path.relative(repoRoot, file)}: ${domains.length} domains`);
}

// ---- main ---------------------------------------------------------------------------------------------------------

const data = {};
for (const [name, source] of Object.entries(SOURCES)) data[name] = await download(name, source, cli);

const ranks = new Map();
parseCrux(data.cruxGlobal, ranks);
parseCrux(data.cruxIsrael, ranks);
const isPublicSuffix = parsePsl(data.psl);

// Lists written by hand (every JSON here that this tool does not write) keep their domains; allow lists are also
// protected, with their parents, from every blocking list.
const generated = new Set(["adult", "gambling", "proxies", "ads-popups"]);
const taken = new Set();
const protectedDomains = new Set(curation.NEVER_BLOCK.map(normalize));
for (const file of fs.readdirSync(listsDir).filter((f) => f.endsWith(".json"))) {
  const name = path.basename(file, ".json");
  if (generated.has(name)) continue;
  const list = JSON.parse(fs.readFileSync(path.join(listsDir, file), "utf8"));
  for (const d of list.domains) {
    taken.add(d);
    if (curation.ALLOW_LISTS.includes(name)) protectedDomains.add(d);
  }
}
const protectedParents = new Set([...protectedDomains].flatMap((d) => selfAndParents(d).slice(1)));
const ctx = { taken, protectedDomains, protectedParents, isPublicSuffix, ranks };

const ranked = (_domain, rank) => rank !== undefined;
const all = () => true;

const adult = build("adult", [...parsePlain(data.swAdult), ...parseHosts(data.sinPorn)], curation.ADULT, ctx, ranked);
const gambling = build("gambling", [...parsePlain(data.swGambling), ...parseHosts(data.sinGambling)], curation.GAMBLING, ctx, all);
const proxies = build("proxies", parsePlain(data.swTunnels), curation.PROXIES, ctx, all);
const ads = build("ads-popups", parseEasyListPopup(data.easylistPopup), curation.ADS, ctx, all);

const crux = `ranked with the Chrome UX Report top lists (global and Israel, ${CRUX_MONTH}) via zakird/crux-top-lists@${CRUX.slice(0, 7)}`;
writeList(
  "adult",
  "Adult and pornographic sites: the most visited ones from two open lists, plus hand-picked additions.",
  `ShadowWhisperer/BlockLists@${SHADOW.slice(0, 7)} Lists/Adult and Sinfonietta/hostfiles@${SINFONIETTA.slice(0, 7)} pornography-hosts, ${crux}; see README`,
  `Unlicense (ShadowWhisperer) and MIT (Sinfonietta). ${SHADOW_NOTICE}. ${SINFONIETTA_NOTICE}`,
  adult,
);
writeList(
  "gambling",
  "Gambling, betting, casino, lottery and skin-gambling sites.",
  `ShadowWhisperer/BlockLists@${SHADOW.slice(0, 7)} Lists/Gambling and Sinfonietta/hostfiles@${SINFONIETTA.slice(0, 7)} gambling-hosts, plus hand-picked additions; see README`,
  `Unlicense (ShadowWhisperer) and MIT (Sinfonietta). ${SHADOW_NOTICE}. ${SINFONIETTA_NOTICE}`,
  gambling,
);
writeList(
  "proxies",
  "Web proxies, filter-bypass and unblocker sites, page-translation proxies (translate.goog), and VPN services.",
  `Hand-curated additions and ShadowWhisperer/BlockLists@${SHADOW.slice(0, 7)} Lists/Tunnels; see README`,
  `Unlicense (ShadowWhisperer). ${SHADOW_NOTICE}`,
  proxies,
);
writeList(
  "ads-popups",
  "Ad, popunder and redirect networks: blocking their pages and frames stops ad click-throughs and popunders.",
  `Hand-curated ad networks and EasyList easylist_adservers_popup.txt (easylist/easylist@${EASYLIST.slice(0, 7)}); see README`,
  "CC BY-SA 4.0 (https://creativecommons.org/licenses/by-sa/4.0/). Contains material from EasyList (https://easylist.to/), " +
    "dual licensed GPL-3.0-or-later or CC BY-SA 3.0-or-later; used here under CC BY-SA 4.0. Attribution: The EasyList authors (https://easylist.to/). " +
    "Changes: only whole-domain popup rules, filtered and extended with hand-picked ad networks.",
  ads,
);

const total = adult.length + gambling.length + proxies.length + ads.length;
note(`\nTotal in the sourced lists: ${total}`);
fs.mkdirSync(path.dirname(reportFile), { recursive: true });
fs.writeFileSync(reportFile, report.join("\n") + "\n");
console.log(`total ${total}; review report: ${reportFile}`);
