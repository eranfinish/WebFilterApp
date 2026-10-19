import { planRuleUpdate, stripNulls } from "../shared/dnr-match";
import type { DnrRule } from "../shared/protocol";

/** The static ruleset (rulesets/failclosed.json) that redirects every http(s) frame to blocked.html?reason=failclosed. */
export const FAILCLOSED_RULESET = "failclosed";

export async function setFailClosed(enabled: boolean): Promise<void> {
  await chrome.declarativeNetRequest.updateEnabledRulesets(
    enabled ? { enableRulesetIds: [FAILCLOSED_RULESET] } : { disableRulesetIds: [FAILCLOSED_RULESET] },
  );
}

export async function isFailClosedEnabled(): Promise<boolean> {
  return (await chrome.declarativeNetRequest.getEnabledRulesets()).includes(FAILCLOSED_RULESET);
}

export interface InstallResult {
  /** The rules now installed. */
  installed: DnrRule[];
  /** Ids of rules the browser refused (regex not supported by its RE2 build, e.g. too large) and that were left out. */
  dropped: number[];
}

/** Splits rules into those the browser's regex engine accepts and the ids it rejects (chrome.declarativeNetRequest.isRegexSupported). */
export async function partitionByRegexSupport(rules: readonly DnrRule[]): Promise<{ kept: DnrRule[]; dropped: number[] }> {
  const kept: DnrRule[] = [];
  const dropped: number[] = [];
  for (const rule of rules) {
    const regex = rule.condition?.regexFilter;
    if (!regex) {
      kept.push(rule);
      continue;
    }
    let supported = true;
    try {
      const result = await chrome.declarativeNetRequest.isRegexSupported({
        regex,
        isCaseSensitive: rule.condition.isUrlFilterCaseSensitive === true,
      });
      supported = result.isSupported;
    } catch {
      supported = true; // cannot tell: keep it and let the update decide
    }
    if (supported) kept.push(rule);
    else dropped.push(rule.id);
  }
  return { kept, dropped };
}

/**
 * Installs exactly `desired` as the dynamic rules, touching only rules that changed. Chrome rejects a whole
 * updateDynamicRules call when one regex is too big for RE2; a failed call changes nothing, so the previous rules stay
 * in place. Then every regexFilter rule is checked with isRegexSupported, the rejected ones are left out, and the
 * update is retried once. A second failure throws (the previous rules are still installed).
 */
export async function replaceDynamicRules(desired: readonly DnrRule[]): Promise<InstallResult> {
  const clean = desired.map((r) => stripNulls(r));
  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const plan = planRuleUpdate(existing, clean);
  if (plan.removeRuleIds.length === 0 && plan.addRules.length === 0) return { installed: clean, dropped: [] };
  try {
    await chrome.declarativeNetRequest.updateDynamicRules(plan);
    return { installed: clean, dropped: [] };
  } catch (error) {
    const { kept, dropped } = await partitionByRegexSupport(plan.addRules);
    if (dropped.length === 0) throw error;
    await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: plan.removeRuleIds, addRules: kept });
    const left = new Set(dropped);
    return { installed: clean.filter((r) => !left.has(r.id)), dropped };
  }
}

/** Replaces all session rules (the time-up rules) with `desired`, with the same regex fallback as dynamic rules. */
export async function replaceSessionRules(desired: readonly DnrRule[]): Promise<InstallResult> {
  const clean = desired.map((r) => stripNulls(r));
  const existing = await chrome.declarativeNetRequest.getSessionRules();
  if (existing.length === 0 && clean.length === 0) return { installed: [], dropped: [] };
  const removeRuleIds = existing.map((r) => r.id);
  try {
    await chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds, addRules: clean });
    return { installed: clean, dropped: [] };
  } catch (error) {
    const { kept, dropped } = await partitionByRegexSupport(clean);
    if (dropped.length === 0) throw error;
    await chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds, addRules: kept });
    return { installed: kept, dropped };
  }
}
