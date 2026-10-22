// Pinned upstream sources and the download cache shared by update.mjs (the starter lists) and ad-ruleset.mjs (the
// extension's ad-server ruleset). To move to newer sources, change a commit here, run the tools, and review the diffs.

import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import zlib from "node:zlib";
import url from "node:url";
import crypto from "node:crypto";

export const SHADOW = "2b7dd18e51c50ed042a56cd57f0b7c3071fd9eef"; // ShadowWhisperer/BlockLists, 2026-10-22 (Unlicense)
export const SINFONIETTA = "46f3097d7bcfc9eea323fe365074dfd771d0d17c"; // Sinfonietta/hostfiles, 2026-09-09 (MIT)
export const EASYLIST = "48ea8294cbb6855914a243e891a3c3b13163fb58"; // easylist/easylist, 2026-10-22 (GPL-3.0+ or CC BY-SA 3.0+)
export const CRUX = "35ba243941defcd21882b134d9dd354824922482"; // zakird/crux-top-lists, 2026-10-13 (CrUX 2026-09)
export const CRUX_MONTH = "202609";
export const PSL = "8437e5262f13305f94087abe699b4fe2be1d1de8"; // publicsuffix/list, 2026-10-22 (MPL-2.0)

export const gh = (repo, sha, file) => `https://raw.githubusercontent.com/${repo}/${sha}/${file}`;
export const easylist = (file) => gh("easylist/easylist", EASYLIST, `easylist/${file}`);

export const CRUX_SOURCES = {
  cruxGlobal: gh("zakird/crux-top-lists", CRUX, `data/global/${CRUX_MONTH}.csv.gz`),
  cruxIsrael: gh("zakird/crux-top-lists", CRUX, `data/country/il/${CRUX_MONTH}.csv.gz`),
};
export const PSL_SOURCE = gh("publicsuffix/list", PSL, "public_suffix_list.dat");

// ---- command line and downloads -------------------------------------------------------------------------------------

/** The options both tools take: --cache-dir <dir>, --offline, --report <file> (default: <cache>/<reportName>). */
export function commandLine(argv, reportName) {
  const option = (name) => {
    const i = argv.indexOf(name);
    return i >= 0 ? argv[i + 1] : undefined;
  };
  const cacheDir = path.resolve(option("--cache-dir") ?? path.join(os.tmpdir(), "webfilter-starter-lists"));
  return {
    cacheDir,
    offline: argv.includes("--offline"),
    reportFile: path.resolve(option("--report") ?? path.join(cacheDir, reportName)),
  };
}

/** A pinned file, from the cache when it is there. Downloads are only ever parsed as text. */
export async function download(name, source, { cacheDir, offline }) {
  const file = path.join(cacheDir, `${name}-${crypto.createHash("sha256").update(source).digest("hex").slice(0, 12)}`);
  if (fs.existsSync(file)) return fs.readFileSync(file);
  if (offline) throw new Error(`${name}: not in the cache (${file}) and --offline was given.`);
  const response = await fetch(source);
  if (!response.ok)
    throw new Error(`${name}: HTTP ${response.status} for ${source}. If the pinned commit is gone, pin a newer one.`);
  const body = Buffer.from(await response.arrayBuffer());
  fs.mkdirSync(cacheDir, { recursive: true });
  fs.writeFileSync(file, body);
  return body;
}

// ---- parsing --------------------------------------------------------------------------------------------------------

export const lines = (buffer) => buffer.toString("utf8").split(/\r?\n/);

/** CrUX top lists (csv.gz of "origin,rank"): rank of every host and of each of its parent domains (best rank wins). */
export function parseCrux(buffer, ranks) {
  for (const line of lines(zlib.gunzipSync(buffer)).slice(1)) {
    const [origin, rank] = line.split(",");
    if (!origin) continue;
    let host;
    try {
      host = new URL(origin).hostname.toLowerCase();
    } catch {
      continue;
    }
    const labels = host.split(".");
    for (let i = 0; i < labels.length - 1; i++) {
      const suffix = labels.slice(i).join(".");
      const current = ranks.get(suffix);
      if (current === undefined || Number(rank) < current) ranks.set(suffix, Number(rank));
    }
  }
  return ranks;
}

/** Public Suffix List: isPublicSuffix(host) for normal, wildcard and exception rules. */
export function parsePsl(buffer) {
  const rules = new Set();
  const exceptions = new Set();
  for (const raw of lines(buffer)) {
    const line = raw.trim();
    if (!line || line.startsWith("//")) continue;
    const rule = url.domainToASCII(line.replace(/^!/, "")) || line.replace(/^!/, "");
    (line.startsWith("!") ? exceptions : rules).add(rule.toLowerCase());
  }
  return (host) => {
    if (exceptions.has(host)) return false;
    if (rules.has(host)) return true;
    const dot = host.indexOf(".");
    return dot > 0 && rules.has("*." + host.slice(dot + 1));
  };
}
