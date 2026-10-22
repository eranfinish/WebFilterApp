import type { Timers } from "../shared/correlation";
import { JUMP_REASON, judgeCommit, judgeNewTarget, jumpTabRule } from "../shared/jump";
import { BLOCK_PAGE, buildBlockPageUrl } from "../shared/pages";
import { siteOf } from "../shared/registrable-domain";
import { JUMP_RULE_BASE, JUMP_RULE_SPAN, clearOwnSessionRules, isJumpRuleId } from "./dnr";
import { errorText } from "./native-client";

export interface JumpGuardDeps {
  /** features.jumpGuard is on (and the profile is in force). */
  active(): boolean;
  timers: Timers;
  now(): number;
  log(message: string, detail?: unknown): void;
}

/** A tab a page opened, guarded until it settles on a page of the opener's site (or the member takes it over). */
interface OpenedTab {
  sourceSite: string;
  /** pending: nothing but about:blank has committed yet; blocked: the block page is showing. */
  phase: "pending" | "blocked";
  ruleId: number | null;
  ruleTimer: unknown;
  redirected: string | null;
  redirectedAt: number;
}

/** The per-tab DNR rule is removed at the tab's first page, or after this long (a popup left on about:blank). */
export const JUMP_RULE_LIFETIME_MS = 60_000;
const MAX_TRACKED_TABS = 1_000;

/**
 * The jump guard (PROTECTION-SPEC §7) on top of the pure decisions in shared/jump.ts.
 *
 * The race with a new tab's first navigation, which is already in flight when onCreatedNavigationTarget arrives:
 *   1. a cross-site target is sent to the block page with tabs.update at once (in the same task as the event), which
 *      cancels the navigation if it has not committed yet;
 *   2. a session rule for that tab only (`tabIds`, above every compiled rule) redirects any top-level request outside
 *      the opener's site to the block page; it catches server redirects of the first navigation and a later
 *      `opened.location = …` before a request is sent, until the tab's first page commits;
 *   3. onBeforeNavigate re-checks the tab while nothing has committed, and onCommitted is the backstop: a page of
 *      another site that committed anyway (local servers answer within a millisecond) is replaced by the block page.
 * DNR cannot do it alone: a rule cannot say "a different site than the opener" for all tabs, and the new tab's id is
 * unknown before the event. Client redirects are only visible at commit (transitionQualifiers), so they are handled
 * there; the page may have started to load.
 */
export class JumpGuard {
  private readonly lastUrl = new Map<number, string>();
  private readonly opened = new Map<number, OpenedTab>();
  private sequence = 0;
  private ruleQueue: Promise<unknown> = Promise.resolve();

  constructor(private readonly deps: JumpGuardDeps) {}

  /** Tabs currently guarded as opened by a page (tests and diagnostics). */
  guardedTabs(): number[] {
    return [...this.opened.keys()];
  }

  /** Learns the open tabs' addresses (after a worker restart the map is empty). */
  async seed(): Promise<void> {
    try {
      for (const tab of await chrome.tabs.query({})) {
        if (tab.id !== undefined && tab.url && !this.lastUrl.has(tab.id)) this.lastUrl.set(tab.id, tab.url);
      }
    } catch {
      // best effort
    }
  }

  /** webNavigation.onCreatedNavigationTarget: a page opened `tabId` (new tab or window, window.open, target=_blank). */
  async onCreatedNavigationTarget(details: { sourceTabId: number; tabId: number; url: string }): Promise<void> {
    if (!this.deps.active() || details.tabId < 0) return;
    let sourceUrl = this.lastUrl.get(details.sourceTabId);
    if (sourceUrl === undefined) {
      sourceUrl = await chrome.tabs.get(details.sourceTabId).then((t) => t.url ?? t.pendingUrl, () => undefined);
      if (!this.deps.active()) return;
    }
    // An opened tab still on about:blank that opens another one passes on its opener's site.
    const sourceSite = siteOf(sourceUrl) ?? this.opened.get(details.sourceTabId)?.sourceSite ?? null;
    const verdict = judgeNewTarget(sourceSite, details.url);
    if (verdict.action === "ignore") return;
    // Asking the browser took a moment: the tab may already show its first page. Same site: nothing left to guard.
    const shown = siteOf(this.lastUrl.get(details.tabId));
    if (shown !== null && shown === verdict.sourceSite) return;
    const entry: OpenedTab = { sourceSite: verdict.sourceSite, phase: "pending", ruleId: null, ruleTimer: null, redirected: null, redirectedAt: 0 };
    if (shown !== null) {
      // Another site slipped in: replace it, and guard the tab from the block page on.
      this.opened.set(details.tabId, entry);
      this.redirect(details.tabId, entry, this.lastUrl.get(details.tabId)!);
      return;
    }
    this.forgetOpened(details.tabId);
    this.opened.set(details.tabId, entry);
    if (this.opened.size > MAX_TRACKED_TABS) this.forgetOpened(this.opened.keys().next().value!);
    if (verdict.action === "block") this.redirect(details.tabId, entry, details.url);
    this.installRule(details.tabId, entry);
    await this.ruleQueue;
  }

  /** webNavigation.onBeforeNavigate: an opened tab that has shown nothing yet is about to load another site. */
  onBeforeNavigate(tabId: number, frameId: number, url: string): void {
    if (frameId !== 0 || !this.deps.active()) return;
    const entry = this.opened.get(tabId);
    if (!entry || entry.phase !== "pending") return;
    const target = siteOf(url);
    if (target !== null && target !== entry.sourceSite) this.redirect(tabId, entry, url);
  }

  /** webNavigation.onCommitted (top frame): opened tabs' first page, and client redirects in every tab. */
  onCommitted(details: {
    tabId: number;
    frameId: number;
    url: string;
    transitionType: string;
    transitionQualifiers?: readonly string[];
    documentLifecycle?: string;
  }): void {
    if (details.frameId !== 0 || details.tabId < 0 || details.documentLifecycle === "prerender") return;
    const previousUrl = this.lastUrl.get(details.tabId) ?? null;
    this.lastUrl.delete(details.tabId);
    this.lastUrl.set(details.tabId, details.url);
    if (this.lastUrl.size > MAX_TRACKED_TABS) this.lastUrl.delete(this.lastUrl.keys().next().value!);
    if (!this.deps.active()) return;

    const entry = this.opened.get(details.tabId) ?? null;
    if (entry && details.url.startsWith(chrome.runtime.getURL(BLOCK_PAGE))) {
      // The block page is up: drop the tab rule so a typed address works; later page-driven loads are judged here.
      entry.phase = "blocked";
      this.removeRule(entry);
      return;
    }
    const verdict = judgeCommit({
      url: details.url,
      transitionType: details.transitionType,
      transitionQualifiers: details.transitionQualifiers ?? [],
      previousUrl,
      openedFrom: entry?.sourceSite ?? null,
    });
    if (verdict.action === "block") {
      this.deps.log(`jump blocked (${verdict.kind})`, details.url);
      this.redirect(details.tabId, entry, details.url);
    } else if (entry && verdict.settle) {
      this.forgetOpened(details.tabId);
    }
  }

  onTabRemoved(tabId: number): void {
    this.lastUrl.delete(tabId);
    this.forgetOpened(tabId);
  }

  /**
   * "Open anyway" (teens): the tab stops being guarded as opened, and its rule is removed before the caller navigates
   * it. Only this navigation is let through: the opener's next jump is blocked again, and so is a client redirect of
   * the page that opens.
   */
  async allowOnce(tabId: number): Promise<void> {
    this.forgetOpened(tabId);
    await this.ruleQueue;
  }

  /** Feature off, fail-closed or inert: forget the opened tabs, and remove their rules unless they are gone already. */
  async reset(removeRules = true): Promise<void> {
    for (const entry of this.opened.values()) this.cancelRuleTimer(entry);
    this.opened.clear();
    if (removeRules) {
      this.queueRules(() => clearOwnSessionRules(isJumpRuleId));
      await this.ruleQueue;
    }
  }

  // ---- internals --------------------------------------------------------------------------------------------------

  private redirect(tabId: number, entry: OpenedTab | null, url: string): void {
    const now = this.deps.now();
    if (entry) {
      // One redirect per URL: the commit of a page that slipped in after the first redirect would ask again.
      if (entry.redirected === url && now - entry.redirectedAt < 3_000) return;
      entry.redirected = url;
      entry.redirectedAt = now;
    }
    const page = buildBlockPageUrl(chrome.runtime.getURL(BLOCK_PAGE), { reason: JUMP_REASON, url });
    chrome.tabs.update(tabId, { url: page }).catch((error: unknown) => this.deps.log("jump block failed", errorText(error)));
  }

  private installRule(tabId: number, entry: OpenedTab): void {
    const id = JUMP_RULE_BASE + (this.sequence++ % JUMP_RULE_SPAN);
    entry.ruleId = id;
    const rule = jumpTabRule(id, tabId, entry.sourceSite, chrome.runtime.getURL(BLOCK_PAGE));
    this.queueRules(() => chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds: [id], addRules: [rule] }));
    entry.ruleTimer = this.deps.timers.setTimeout(() => {
      entry.ruleTimer = null;
      this.removeRule(entry);
    }, JUMP_RULE_LIFETIME_MS);
  }

  private removeRule(entry: OpenedTab): void {
    this.cancelRuleTimer(entry);
    const id = entry.ruleId;
    if (id === null) return;
    entry.ruleId = null;
    this.queueRules(() => chrome.declarativeNetRequest.updateSessionRules({ removeRuleIds: [id] }));
  }

  private cancelRuleTimer(entry: OpenedTab): void {
    if (entry.ruleTimer !== null) this.deps.timers.clearTimeout(entry.ruleTimer);
    entry.ruleTimer = null;
  }

  private forgetOpened(tabId: number): void {
    const entry = this.opened.get(tabId);
    if (!entry) return;
    this.opened.delete(tabId);
    this.removeRule(entry);
  }

  /** Rule changes run in order, so a removal never overtakes the installation it undoes. */
  private queueRules(op: () => Promise<unknown>): void {
    this.ruleQueue = this.ruleQueue.then(op).catch((error: unknown) => this.deps.log("jump rule update failed", errorText(error)));
  }
}
