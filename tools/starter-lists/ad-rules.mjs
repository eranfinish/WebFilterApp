// Pure functions behind ad-ruleset.mjs: EasyList's whole-domain ad-server rules and its exceptions become the browser
// extension's static declarativeNetRequest ruleset `ads` (docs/PROTECTION-SPEC.md §12). No I/O, so the extension's
// unit tests (extension/test/ad-rules.test.ts) check them directly.

import url from "node:url";

/**
 * Every ad rule has the lowest priority. The service's compiled rules filter main_frame and sub_frame only and never use
 * allowAllRequests, so for scripts, images and requests an ad rule is the only rule that matches; for a frame, any URL
 * rule that matches (priority 2 and up) still decides, and the allowlist default (1, a redirect) loses to a block at
 * equal priority, so a blocked ad frame collapses instead of showing the block page.
 */
export const AD_RULE_PRIORITY = 1;

/** Page loads stay with the URL rules and the ads-popups list; everything a page loads is in scope. */
export const EXCLUDED_RESOURCE_TYPES = ["main_frame"];

/** Exception options that do not touch network blocking: cosmetic filtering, popups, CSP injection. */
const NOT_NETWORK = new Set(["generichide", "ghide", "elemhide", "ehide", "specifichide", "shide", "genericblock", "popup", "csp"]);

const LABEL = /^(?!-)[a-z0-9-]{1,63}(?<!-)$/;

/** A lowercase ASCII (punycode) host name without a trailing dot, or null (IP addresses, wildcards, junk). */
export function normalizeHost(input) {
  const text = String(input ?? "").trim().toLowerCase().replace(/\.$/, "");
  if (!text || /[*\/\s:^|~]/.test(text)) return null;
  const host = url.domainToASCII(text);
  if (!host) return null;
  const labels = host.split(".");
  if (labels.length < 2 || !labels.every((l) => LABEL.test(l))) return null;
  if (/^[0-9]+$/.test(labels.at(-1))) return null;
  return host;
}

/** `host` is `domain` or below it (domain semantics: a rule for a domain matches its subdomains). */
export const isUnder = (host, domain) => host === domain || host.endsWith("." + domain);

/** The host and each parent domain above it ("a.b.c" → "a.b.c", "b.c"). */
export function selfAndParents(host) {
  const labels = host.split(".");
  const result = [];
  for (let i = 0; i < labels.length - 1; i++) result.push(labels.slice(i).join("."));
  return result;
}

const textLines = (text) => String(text).split(/\r?\n/).map((l) => l.trim());

/**
 * EasyList's whole-domain blocking rules: `||domain^` (any request to the domain) and `||domain^$third-party` (only
 * when the page is on another site). Rules with a path, any other option, regular expressions, comments and cosmetic
 * rules are skipped.
 * @returns {{ rules: { domain: string, thirdParty: boolean }[], skipped: number }}
 */
export function parseBlockRules(text) {
  const rules = [];
  let skipped = 0;
  for (const line of textLines(text)) {
    if (!line || line.startsWith("!") || line.startsWith("[")) continue;
    const match = /^\|\|([^\^\/$*|]+)\^(\$third-party)?$/.exec(line);
    const domain = match && normalizeHost(match[1]);
    if (domain) rules.push({ domain, thirdParty: Boolean(match[2]) });
    else skipped++;
  }
  return { rules, skipped };
}

/**
 * EasyList's network exceptions for a host (`@@||host…`), reduced to the host and the sites they apply on: `sites` is
 * the `$domain=` list (null = every site), `path` says the exception names more than the host. The path, the resource
 * types and `$third-party` are ignored, so an exception is honoured more widely than written: the whole host is spared
 * on those sites. A first-party-only exception (`$~third-party`) applies on the host's own pages. Cosmetic and popup
 * exceptions, and those for a path on any host, do not concern host rules and are skipped.
 * @returns {{ exceptions: { host: string, sites: string[] | null, path: boolean, rule: string }[], skipped: string[] }}
 */
export function parseExceptions(text) {
  const exceptions = [];
  const skipped = [];
  for (const line of textLines(text)) {
    if (!line.startsWith("@@||")) continue;
    const body = line.slice(4);
    const dollar = body.lastIndexOf("$");
    const pattern = dollar >= 0 ? body.slice(0, dollar) : body;
    const options = dollar >= 0 ? body.slice(dollar + 1).split(",") : [];
    const hostPart = /^[^\^\/*:|$]+/.exec(pattern)?.[0] ?? "";
    const host = normalizeHost(hostPart);
    if (!host) {
      skipped.push(line);
      continue;
    }
    const kinds = [];
    const sites = [];
    let firstParty = false;
    for (const option of options) {
      const [name, value = ""] = option.split("=");
      if (name === "domain") {
        for (const entry of value.split("|")) {
          if (entry.startsWith("~")) continue; // an exception everywhere but there is an exception everywhere
          const site = normalizeHost(entry);
          if (site) sites.push(site);
        }
      } else if (name === "~third-party" || name === "1p" || name === "first-party") firstParty = true;
      else if (name !== "third-party" && name !== "3p" && name !== "match-case") kinds.push(name.replace(/^~/, ""));
    }
    if (kinds.length > 0 && kinds.every((k) => NOT_NETWORK.has(k))) continue;
    if (sites.length === 0 && firstParty) sites.push(host);
    const path = !["", "^", "^|"].includes(pattern.slice(hostPart.length));
    exceptions.push({ host, sites: sites.length > 0 ? [...new Set(sites)].sort() : null, path, rule: line });
  }
  return { exceptions, skipped };
}

const sameSet = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
/** Code-unit order, the same on every machine (host names are ASCII). */
const byText = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

/**
 * Builds the ruleset. Each EasyList domain is blocked for every resource type but main_frame (`$third-party` →
 * `domainType: "thirdParty"`), packed with `requestDomains` into one rule for any-party domains and one for third-party
 * domains. Guards drop public suffixes and any domain equal to, below or above a protected domain (`protectTree`: the
 * allow lists and the essentials), or equal to or above one of `protectSelfAndParents` (platforms whose own hosts must
 * keep working, while one site's host below them may stay). Exceptions are honoured at host level: a host spared on
 * every site is left out (`excludedRequestDomains` when it is below a listed domain); a host spared on some sites gets
 * its own rule with those sites in `excludedInitiatorDomains`. Only block rules, so the ruleset can never allow anything.
 * @param {{ domain: string, thirdParty: boolean }[]} blocks
 * @param {{ host: string, sites: string[] | null, path?: boolean, rule?: string }[]} exceptions
 * @param {{ isPublicSuffix?: (host: string) => boolean, protectTree?: Iterable<string>, protectSelfAndParents?: Iterable<string>, remove?: Iterable<string> }} guards
 */
export function buildAdRuleset(blocks, exceptions = [], guards = {}) {
  const isPublicSuffix = guards.isPublicSuffix ?? (() => false);
  const tree = new Set(guards.protectTree ?? []);
  const treeParents = new Set([...tree].flatMap((d) => selfAndParents(d).slice(1)));
  const platforms = new Set(guards.protectSelfAndParents ?? []);
  const platformParents = new Set([...platforms].flatMap((d) => selfAndParents(d).slice(1)));
  const removed = new Set(guards.remove ?? []);
  const report = { sourceRules: blocks.length, publicSuffix: [], protected: [], removed: [], covered: 0, exemptEverywhere: [], exemptOnSites: [] };

  // 1. One entry per domain; a domain listed both ways is blocked for any party.
  const party = new Map();
  for (const { domain, thirdParty } of blocks) {
    if (party.get(domain) === "any") continue;
    party.set(domain, thirdParty ? "thirdParty" : "any");
  }

  // 2. Guards.
  for (const domain of [...party.keys()]) {
    let reason = null;
    if (isPublicSuffix(domain)) reason = "publicSuffix";
    else if (selfAndParents(domain).some((d) => tree.has(d)) || treeParents.has(domain) || platforms.has(domain) || platformParents.has(domain))
      reason = "protected";
    else if (selfAndParents(domain).some((d) => removed.has(d))) reason = "removed";
    if (reason === null) continue;
    report[reason].push(domain);
    party.delete(domain);
  }

  // 3. A subdomain of a listed domain adds nothing, unless it is listed for any party below a third-party domain.
  for (const [domain, kind] of [...party]) {
    const covered = selfAndParents(domain).slice(1).some((p) => party.get(p) === "any" || (party.get(p) === "thirdParty" && kind === "thirdParty"));
    if (covered) {
      party.delete(domain);
      report.covered++;
    }
  }

  // 4. Exceptions that touch a listed domain (one below it, at it, or above it): the sites each host is spared on. A
  // path on every site above listed domains is a shared host's file (cloudfront.net/js/common/invoke.js), not a reason
  // to spare every ad host below it, so it counts only at or below a listed domain.
  const aboveListed = new Set([...party.keys()].flatMap(selfAndParents));
  const touches = ({ host, sites, path }) =>
    selfAndParents(host).some((d) => party.has(d)) || (aboveListed.has(host) && !(sites === null && path));
  const spared = new Map(); // host → Set of sites, or null for every site
  for (const exception of exceptions) {
    const { host, sites } = exception;
    if (!touches(exception)) continue;
    const current = spared.get(host);
    if (current === null) continue;
    if (sites === null) spared.set(host, null);
    else spared.set(host, new Set([...(current ?? []), ...sites]));
  }
  /** The sites a host is spared on, from every exception for it or above it: null = every site, [] = none. */
  const sitesFor = (host) => {
    const all = new Set();
    for (const [exempt, sites] of spared) {
      if (!isUnder(host, exempt)) continue;
      if (sites === null) return null;
      for (const s of sites) all.add(s);
    }
    return [...all].sort();
  };

  // 5. Place every domain in a rule keyed by party and spared sites (no sites: the two main rules).
  const rules = new Map(); // key → { kind, sites, domains: Set, excluded: Set }
  const ruleFor = (kind, sites) => {
    const key = `${kind}|${sites.join("|")}`;
    if (!rules.has(key)) rules.set(key, { kind, sites, domains: new Set(), excluded: new Set() });
    return rules.get(key);
  };
  const placed = new Map(); // domain → its rule
  for (const [domain, kind] of party) {
    const sites = sitesFor(domain);
    if (sites === null) {
      report.exemptEverywhere.push(domain);
      continue;
    }
    const rule = ruleFor(kind, sites);
    rule.domains.add(domain);
    placed.set(domain, rule);
  }
  // A spared host below a listed domain is carved out of that domain's rule, and blocked by a rule of its own when it
  // is spared on some sites only. A host spared on no more sites than the rule already spares needs nothing.
  for (const host of [...spared.keys()].sort()) {
    const sites = sitesFor(host);
    for (const domain of selfAndParents(host).slice(1)) {
      const rule = placed.get(domain);
      if (!rule || (sites !== null && sameSet(sites, rule.sites))) continue;
      rule.excluded.add(host);
      if (sites === null) report.exemptEverywhere.push(host);
      else ruleFor(rule.kind, sites).domains.add(host);
    }
  }
  for (const [host, sites] of spared) if (sites !== null) report.exemptOnSites.push({ host, sites: [...sites].sort() });

  // 6. The rules: the two main ones first, then the spared hosts.
  const ordered = [...rules.values()].sort(
    (a, b) => a.sites.length - b.sites.length || (a.kind === b.kind ? 0 : a.kind === "any" ? -1 : 1) || byText(a.sites.join("|"), b.sites.join("|")),
  );
  const output = ordered
    .filter((r) => r.domains.size > 0)
    .map((r, i) => {
      const condition = {};
      if (r.kind === "thirdParty") condition.domainType = "thirdParty";
      if (r.sites.length > 0) condition.excludedInitiatorDomains = r.sites;
      const excluded = [...r.excluded].filter((h) => !r.domains.has(h)).sort();
      if (excluded.length > 0) condition.excludedRequestDomains = excluded;
      condition.excludedResourceTypes = [...EXCLUDED_RESOURCE_TYPES];
      condition.requestDomains = [...r.domains].sort();
      return { id: i + 1, priority: AD_RULE_PRIORITY, action: { type: "block" }, condition };
    });

  for (const key of ["publicSuffix", "protected", "removed", "exemptEverywhere"]) report[key] = [...new Set(report[key])].sort();
  report.exemptOnSites.sort((a, b) => byText(a.host, b.host));
  const count = (kind) => output.filter((r) => (r.condition.domainType ?? "any") === kind).reduce((n, r) => n + r.condition.requestDomains.length, 0);
  report.domains = { any: count("any"), thirdParty: count("thirdParty") };
  return { rules: output, report };
}

/**
 * The ruleset as JSON with one domain per line, so a regenerated file diffs line by line and stays compact (no
 * indentation inside the domain lists).
 */
export function formatRuleset(rules) {
  const body = rules.map((rule) => {
    const { requestDomains, ...rest } = rule.condition;
    const head = JSON.stringify({ ...rule, condition: { ...rest, requestDomains: [] } });
    const [before, after] = head.split('"requestDomains":[]');
    return `${before}"requestDomains":[\n${requestDomains.map((d) => JSON.stringify(d)).join(",\n")}\n]${after}`;
  });
  return `[\n${body.join(",\n")}\n]\n`;
}
