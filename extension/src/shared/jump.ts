/**
 * Jump guard decisions (PROTECTION-SPEC §7), pure so they can be unit-tested. The background's JumpGuard feeds them
 * from webNavigation events and enforces the answers.
 *
 *   new tab / window opened by a page   → blocked when its page is on another site than the opener's tab
 *   automatic client redirect           → blocked when it leaves the site (transitionQualifiers has client_redirect)
 *   the member's own link click, typed addresses, bookmarks, the browser's and the extension's pages → never
 *
 * "Site" is the registrable domain (registrable-domain.ts). In Edge 156 `client_redirect` marks meta refresh and
 * script redirects that run while the page is still loading; a script that navigates later (a timer after the load
 * event) and an auto-submitted form look like a link click to the API and are not caught (README, decisions).
 */
import type { DnrRule } from "./protocol";
import { siteOf } from "./registrable-domain";

export const JUMP_REASON = "blocked_jump";

/** What to do with a tab a page just opened (webNavigation.onCreatedNavigationTarget). */
export type NewTargetVerdict =
  /** The opener is not a web page (new-tab page, browser or extension page): not guarded. */
  | { action: "ignore" }
  /** A web page on another site: show the block page now. */
  | { action: "block"; sourceSite: string }
  /** Same site, or nothing loaded yet (about:blank, then set by the opener): guard the tab until its first page. */
  | { action: "watch"; sourceSite: string };

export function judgeNewTarget(sourceSite: string | null, targetUrl: string): NewTargetVerdict {
  if (!sourceSite) return { action: "ignore" };
  const target = siteOf(targetUrl);
  if (target !== null && target !== sourceSite) return { action: "block", sourceSite };
  return { action: "watch", sourceSite };
}

/** Transitions that come from the member, not from a page: an opened tab they navigate themselves is theirs. */
const MEMBER_TRANSITIONS: ReadonlySet<string> = new Set([
  "typed",
  "auto_bookmark",
  "generated",
  "keyword",
  "keyword_generated",
  "start_page",
  "auto_toplevel",
]);

export interface CommitInput {
  url: string;
  transitionType: string;
  transitionQualifiers: readonly string[];
  /** The tab's previous top-level document, if known. */
  previousUrl: string | null;
  /** The opener's site while this tab (opened by a page) has not settled on a page of its own. */
  openedFrom: string | null;
}

export type CommitVerdict =
  /** `settle`: an opened tab reached its own page; stop guarding it. */
  | { action: "allow"; settle: boolean }
  | { action: "block"; kind: "new_tab" | "client_redirect" };

/** The decision for a top-frame commit (webNavigation.onCommitted, frameId 0). */
export function judgeCommit(input: CommitInput): CommitVerdict {
  const target = siteOf(input.url);
  // about:blank, the block page, the browser's pages: nothing to judge (an opened tab stays guarded).
  if (target === null) return { action: "allow", settle: false };
  const qualifiers = input.transitionQualifiers;

  if (input.openedFrom !== null) {
    const byMember = MEMBER_TRANSITIONS.has(input.transitionType) || qualifiers.includes("from_address_bar");
    if (byMember || target === input.openedFrom) return { action: "allow", settle: true };
    // Also a Back to a page that slipped in before the block page replaced it.
    return { action: "block", kind: "new_tab" };
  }

  if (qualifiers.includes("client_redirect") && !qualifiers.includes("forward_back")) {
    const from = siteOf(input.previousUrl);
    if (from !== null && from !== target && !isSearchRedirector(input.previousUrl!)) return { action: "block", kind: "client_redirect" };
  }
  return { action: "allow", settle: false };
}

/**
 * Click-through pages of the search engines that forward to the result by script or meta refresh (Bing `/ck/a`,
 * DuckDuckGo `/l/`, Google `/url`, YouTube `/redirect`). The member clicked the result, so their redirect is not a
 * jump: search results must work (PROTECTION-SPEC §7).
 */
export function isSearchRedirector(url: string): boolean {
  const site = siteOf(url);
  if (!site) return false;
  let path: string;
  try {
    path = new URL(url).pathname;
  } catch {
    return false;
  }
  const name = site.split(".")[0];
  if (name === "bing") return path.startsWith("/ck/");
  if (name === "google") return path === "/url";
  if (site === "duckduckgo.com") return path === "/l/" || path === "/l";
  if (site === "youtube.com") return path === "/redirect";
  return false;
}

// ---- the per-tab DNR rule ------------------------------------------------------------------------------------------

/** Above every compiled rule (time-up session rules are at 262,145), so a guarded tab cannot leave by any rule. */
export const JUMP_RULE_PRIORITY = 5 << 16;

/**
 * Session rule for a tab a page opened: any top-level page outside the opener's site goes to the block page. It is in
 * place from onCreatedNavigationTarget until the tab's first page (or the block page) commits, so server redirects of
 * the first navigation and a later `opened.location = …` from the opener are stopped before any request is sent.
 */
export function jumpTabRule(id: number, tabId: number, sourceSite: string, blockPageUrl: string): DnrRule {
  return {
    id,
    priority: JUMP_RULE_PRIORITY,
    action: { type: "redirect", redirect: { regexSubstitution: `${blockPageUrl}?reason=${JUMP_REASON}#\\0` } },
    condition: {
      tabIds: [tabId],
      resourceTypes: ["main_frame"],
      excludedRequestDomains: [sourceSite],
      regexFilter: "^https?://.*$",
    },
  };
}
