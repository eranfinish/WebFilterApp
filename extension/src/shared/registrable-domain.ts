/**
 * Registrable domain ("site", eTLD+1) of a host, for the jump guard (PROTECTION-SPEC §7): two addresses belong to the
 * same site when their registrable domains are equal.
 *
 * This is a small built-in approximation of the Public Suffix List, not the list itself (about 10,000 entries, updated
 * weekly; too big to ship and keep current in the extension):
 *   - IP literals and single-label hosts (localhost) are their own site.
 *   - MULTI_PART_SUFFIXES: second-level public suffixes of common country domains (co.il, org.il, ac.il, co.uk, com.au,
 *     co.jp, com.br, …) and a few hosting platforms where every subdomain belongs to someone else (github.io,
 *     blogspot.com, translate.goog, …).
 *   - Any other two-letter country domain whose second label is a generic one (com, net, org, edu, gov, ac, co, …) is
 *     treated as a public suffix too, e.g. `shop.com.xx` → `shop.com.xx`.
 *   - Everything else: the last two labels.
 * Limits: suffixes that are not covered count as registrable (`a.city.state.us` → `state.us`, so two towns would be one
 * site), private suffixes outside the short list are not known (`alice.someplatform.app` and `bob.someplatform.app`
 * count as the same site), and wildcard/exception rules of the real list are not modelled. A wrong answer only changes
 * whether a cross-domain jump is noticed; it never allows a URL that the rules block.
 */

const COUNTRY_SECOND_LEVEL: Record<string, readonly string[]> = {
  il: ["co", "org", "net", "ac", "gov", "muni", "k12", "idf"],
  uk: ["co", "org", "net", "ac", "gov", "ltd", "plc", "me", "sch", "nhs", "police"],
  au: ["com", "net", "org", "edu", "gov", "asn", "id"],
  nz: ["co", "org", "net", "ac", "govt", "school", "geek", "gen", "kiwi"],
  za: ["co", "org", "net", "ac", "gov", "edu", "web"],
  jp: ["co", "ne", "or", "ac", "go", "ed", "gr", "lg", "ad"],
  kr: ["co", "or", "ne", "ac", "go", "re", "pe"],
  in: ["co", "net", "org", "ac", "gov", "edu", "firm", "gen", "ind", "res"],
  br: ["com", "net", "org", "gov", "edu", "art", "blog"],
  ar: ["com", "net", "org", "gob", "edu", "int"],
  mx: ["com", "net", "org", "gob", "edu"],
  tr: ["com", "net", "org", "gov", "edu", "k12", "gen", "web", "bel"],
  cn: ["com", "net", "org", "gov", "edu", "ac"],
  hk: ["com", "net", "org", "gov", "edu", "idv"],
  tw: ["com", "net", "org", "gov", "edu", "idv"],
  sg: ["com", "net", "org", "gov", "edu", "per"],
  my: ["com", "net", "org", "gov", "edu", "name"],
  ua: ["com", "net", "org", "gov", "edu", "in"],
  eg: ["com", "net", "org", "gov", "edu", "sci"],
  sa: ["com", "net", "org", "gov", "edu", "med", "sch"],
  ae: ["co", "net", "org", "gov", "ac", "sch"],
  jo: ["com", "net", "org", "gov", "edu", "sch"],
  ps: ["com", "net", "org", "gov", "edu", "sec", "plo"],
  pl: ["com", "net", "org", "gov", "edu", "info", "waw"],
  es: ["com", "org", "gob", "edu", "nom"],
  gr: ["com", "net", "org", "gov", "edu"],
  id: ["co", "ac", "go", "or", "web", "sch", "net", "my"],
  th: ["co", "ac", "go", "in", "or", "net"],
  ph: ["com", "net", "org", "gov", "edu"],
  pk: ["com", "net", "org", "gov", "edu", "fam"],
  ng: ["com", "net", "org", "gov", "edu", "name", "sch"],
  ke: ["co", "go", "ac", "or", "ne", "sc"],
  vn: ["com", "net", "org", "gov", "edu", "int"],
  co: ["com", "net", "org", "gov", "edu", "nom"],
  pe: ["com", "net", "org", "gob", "edu", "nom"],
  ru: ["com", "net", "org", "msk", "spb"],
  fr: ["gouv", "asso", "com", "tm"],
};

const PLATFORM_SUFFIXES = [
  "github.io",
  "githubusercontent.com",
  "gitlab.io",
  "blogspot.com",
  "appspot.com",
  "herokuapp.com",
  "netlify.app",
  "vercel.app",
  "pages.dev",
  "workers.dev",
  "web.app",
  "firebaseapp.com",
  "azurewebsites.net",
  "cloudfront.net",
  "translate.goog",
  "glitch.me",
  "onrender.com",
  "wixsite.com",
  "neocities.org",
];

export const MULTI_PART_SUFFIXES: ReadonlySet<string> = new Set([
  ...Object.entries(COUNTRY_SECOND_LEVEL).flatMap(([tld, labels]) => labels.map((label) => `${label}.${tld}`)),
  ...PLATFORM_SUFFIXES,
]);

/** Second labels that make `<label>.<two-letter country>` a public suffix when the country is not listed above. */
const GENERIC_SECOND_LEVEL: ReadonlySet<string> = new Set(["com", "net", "org", "edu", "gov", "gob", "ac", "co", "or", "ne", "go", "mil", "nom", "sch"]);

const IPV4 = /^\d{1,3}(?:\.\d{1,3}){3}$/;

export function normalizeHost(host: string): string {
  let h = host.trim().toLowerCase();
  if (h.endsWith(".")) h = h.slice(0, -1);
  return h;
}

export function isIpLiteral(host: string): boolean {
  const h = normalizeHost(host);
  return IPV4.test(h) || h.startsWith("[") || h.includes(":");
}

/** True when `suffix` (e.g. "co.il") is a public suffix by the rules above. */
export function isPublicSuffix(suffix: string): boolean {
  const s = normalizeHost(suffix);
  if (MULTI_PART_SUFFIXES.has(s)) return true;
  const labels = s.split(".");
  if (labels.length === 1) return true;
  if (labels.length === 2) {
    const [second, tld] = labels as [string, string];
    return tld.length === 2 && GENERIC_SECOND_LEVEL.has(second);
  }
  return false;
}

/** The registrable domain of a host name (no scheme, no port). Empty input gives "". */
export function registrableDomain(host: string): string {
  const h = normalizeHost(host);
  if (!h || isIpLiteral(h)) return h;
  const labels = h.split(".").filter((l) => l.length > 0);
  // "co.il" itself, a plain "example.com" or "localhost": the host is its own site.
  if (labels.length <= 2) return labels.join(".");
  // The longest public suffix wins (three labels, then two); a host that is itself a suffix is its own site.
  for (let take = Math.min(3, labels.length); take >= 2; take--) {
    const suffix = labels.slice(-take).join(".");
    const isSuffix = take === 2 ? isPublicSuffix(suffix) : MULTI_PART_SUFFIXES.has(suffix);
    if (isSuffix) return labels.length > take ? labels.slice(-(take + 1)).join(".") : suffix;
  }
  return labels.slice(-2).join(".");
}

/** The site of an http(s) URL, or null for anything else (about:, data:, blob:, the browser's and extensions' pages). */
export function siteOf(url: string | null | undefined): string | null {
  if (typeof url !== "string" || !/^https?:\/\//i.test(url)) return null;
  try {
    const host = new URL(url).hostname;
    return host ? registrableDomain(host) : null;
  } catch {
    return null;
  }
}

export function sameSite(a: string, b: string): boolean {
  const sa = siteOf(a);
  return sa !== null && sa === siteOf(b);
}
