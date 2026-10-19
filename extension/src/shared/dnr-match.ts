import type { DnrRule } from "./protocol";

/**
 * A small evaluator for the declarativeNetRequest conditions the rule compiler emits (requestDomains,
 * excludedRequestDomains, regexFilter, urlFilter, resourceTypes). It is used only to decide which already-open tabs
 * the time-up session rules would block, so they can be moved to timeup.html while always-allowed sites stay open.
 * The browser remains the enforcer for every new request.
 */

type ResourceType = "main_frame" | "sub_frame";

// Domain lists can hold hundreds of thousands of entries (S3): look up the host and its parent domains in a Set.
const domainSets = new WeakMap<readonly string[], Set<string>>();

function hostMatches(host: string, domains: readonly string[]): boolean {
  let set = domainSets.get(domains);
  if (!set) {
    set = new Set(domains.map((d) => d.toLowerCase()));
    domainSets.set(domains, set);
  }
  for (let candidate = host; ; ) {
    if (set.has(candidate)) return true;
    const dot = candidate.indexOf(".");
    if (dot < 0) return false;
    candidate = candidate.slice(dot + 1);
  }
}

const regexCache = new Map<string, RegExp | null>();

function compile(source: string, caseSensitive: boolean): RegExp | null {
  const key = `${caseSensitive ? "s" : "i"}:${source}`;
  if (!regexCache.has(key)) {
    let re: RegExp | null;
    try {
      re = new RegExp(source, caseSensitive ? "" : "i");
    } catch {
      re = null;
    }
    if (regexCache.size > 2000) regexCache.clear();
    regexCache.set(key, re);
  }
  return regexCache.get(key) ?? null;
}

const escapeRegex = (s: string): string => s.replace(/[.+?^${}()|[\]\\/]/g, "\\$&");

/** Converts a DNR urlFilter (`||`, `|`, `*`, `^`) into a regular expression source. */
export function urlFilterToRegex(filter: string): string {
  let rest = filter;
  let prefix = "";
  let suffix = "";
  if (rest.startsWith("||")) {
    prefix = "^[a-z][a-z0-9+.-]*://([^/?#]*\\.)?";
    rest = rest.slice(2);
  } else if (rest.startsWith("|")) {
    prefix = "^";
    rest = rest.slice(1);
  }
  if (rest.endsWith("|")) {
    suffix = "$";
    rest = rest.slice(0, -1);
  }
  let body = "";
  for (const ch of rest) {
    if (ch === "*") body += ".*";
    else if (ch === "^") body += "(?:[^A-Za-z0-9_\\-.%]|$)";
    else body += escapeRegex(ch);
  }
  return prefix + body + suffix;
}

/** true / false, or null when the rule's pattern cannot be evaluated here (a regex this JS engine rejects). */
function match(rule: DnrRule, url: string, resourceType: ResourceType): boolean | null {
  const c = rule.condition ?? {};
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  const types = c.resourceTypes as string[] | undefined;
  if (types ? !types.includes(resourceType) : resourceType === "main_frame") return false;
  const excludedTypes = c.excludedResourceTypes as string[] | undefined;
  if (excludedTypes?.includes(resourceType)) return false;

  const host = parsed.hostname.toLowerCase().replace(/\.$/, "");
  if (c.requestDomains && !hostMatches(host, c.requestDomains)) return false;
  if (c.excludedRequestDomains && hostMatches(host, c.excludedRequestDomains)) return false;

  const caseSensitive = c.isUrlFilterCaseSensitive === true;
  if (c.regexFilter !== undefined) {
    const re = compile(c.regexFilter, caseSensitive);
    if (!re) return null;
    if (!re.test(parsed.href)) return false;
  }
  if (c.urlFilter !== undefined) {
    const re = compile(urlFilterToRegex(c.urlFilter), caseSensitive);
    if (!re) return null;
    if (!re.test(parsed.href)) return false;
  }
  return true;
}

export function ruleMatches(rule: DnrRule, url: string, resourceType: ResourceType = "main_frame"): boolean {
  return match(rule, url, resourceType) === true;
}

// Within one priority, DNR prefers allow > allowAllRequests > block > upgradeScheme > redirect.
const ACTION_RANK: Record<string, number> = { allow: 5, allowAllRequests: 4, block: 3, upgradeScheme: 2, redirect: 1, modifyHeaders: 0 };

/** The rule DNR would apply to a navigation to `url`, or null when none matches. */
export function winningRule(rules: readonly DnrRule[], url: string, resourceType: ResourceType = "main_frame"): DnrRule | null {
  let best: DnrRule | null = null;
  for (const rule of rules) {
    if (!ruleMatches(rule, url, resourceType)) continue;
    if (!best) {
      best = rule;
      continue;
    }
    const p = rule.priority ?? 1;
    const bp = best.priority ?? 1;
    if (p > bp || (p === bp && (ACTION_RANK[rule.action.type] ?? 0) > (ACTION_RANK[best.action.type] ?? 0))) best = rule;
  }
  return best;
}

export function wouldBlock(rules: readonly DnrRule[], url: string): boolean {
  const rule = winningRule(rules, url);
  return rule !== null && (rule.action.type === "block" || rule.action.type === "redirect");
}

/**
 * Like wouldBlock, but "unknown" when a rule that applies to this URL's host cannot be evaluated here (RE2 syntax the
 * JS engine rejects): the caller must then not act, so an allowed page is never blocked by mistake.
 */
export function classifyNavigation(rules: readonly DnrRule[], url: string, resourceType: ResourceType = "main_frame"): "block" | "allow" | "unknown" {
  if (rules.some((r) => match(r, url, resourceType) === null)) return "unknown";
  const rule = winningRule(rules, url, resourceType);
  return rule !== null && (rule.action.type === "block" || rule.action.type === "redirect") ? "block" : "allow";
}

/** Removes null members recursively: the service may serialize absent optional members as null, which DNR rejects. */
export function stripNulls<T>(value: T): T {
  if (Array.isArray(value)) return value.map((v) => stripNulls(v)) as unknown as T;
  if (value !== null && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      if (v !== null && v !== undefined) out[k] = stripNulls(v);
    }
    return out as T;
  }
  return value;
}

/** Stable JSON (sorted keys) for comparing rules. */
export function canonicalJson(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(",")}]`;
  if (value !== null && typeof value === "object") {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, v]) => v !== undefined)
      .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
    return `{${entries.map(([k, v]) => `${JSON.stringify(k)}:${canonicalJson(v)}`).join(",")}}`;
  }
  return JSON.stringify(value);
}

export interface RuleUpdate {
  removeRuleIds: number[];
  addRules: DnrRule[];
}

/** The minimal updateDynamicRules call that turns `existing` into `desired` (S3: reinstall only changed rules). */
export function planRuleUpdate(existing: readonly DnrRule[], desired: readonly DnrRule[]): RuleUpdate {
  const wanted = new Map(desired.map((r) => [r.id, r]));
  const have = new Map(existing.map((r) => [r.id, r]));
  const removeRuleIds: number[] = [];
  const addRules: DnrRule[] = [];
  for (const [id, rule] of have) {
    const next = wanted.get(id);
    if (!next || canonicalJson(next) !== canonicalJson(rule)) removeRuleIds.push(id);
  }
  for (const [id, rule] of wanted) {
    const current = have.get(id);
    if (!current || canonicalJson(current) !== canonicalJson(rule)) addRules.push(rule);
  }
  return { removeRuleIds, addRules };
}
