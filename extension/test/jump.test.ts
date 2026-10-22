import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { JUMP_RULE_BASE, SHORTS_RULE_ID } from "../src/background/dnr";
import { JUMP_RULE_LIFETIME_MS, JumpGuard } from "../src/background/jump-guard";
import { realTimers } from "../src/shared/correlation";
import { JUMP_RULE_PRIORITY, SIGN_IN_HOSTS, isSearchRedirector, isSignInHost, judgeCommit, judgeNewTarget, jumpTabRule } from "../src/shared/jump";
import { type ChromeMockState, installChromeMock } from "./chrome-mock";

const BLOCK = "chrome-extension://testextensionid/blocked.html";
const blockPage = (url: string) => `${BLOCK}?reason=blocked_jump#${url}`;

describe("judgeNewTarget (onCreatedNavigationTarget)", () => {
  it("blocks a new tab on another site, watches same-site and not-yet-loaded tabs", () => {
    expect(judgeNewTarget("site-a.test", "https://site-b.test/x")).toEqual({ action: "block", sourceSite: "site-a.test" });
    expect(judgeNewTarget("site-a.test", "https://www.site-a.test/other")).toEqual({ action: "watch", sourceSite: "site-a.test" });
    expect(judgeNewTarget("site-a.test", "about:blank")).toEqual({ action: "watch", sourceSite: "site-a.test" });
    expect(judgeNewTarget("google.com", "https://docs.google.com/document/1")).toMatchObject({ action: "watch" });
    expect(judgeNewTarget("ynet.co.il", "https://www.walla.co.il/")).toMatchObject({ action: "block" });
  });

  it("ignores tabs opened from the browser's or the extension's own pages", () => {
    expect(judgeNewTarget(null, "https://site-b.test/")).toEqual({ action: "ignore" });
  });
});

describe("judgeCommit (onCommitted, top frame)", () => {
  const commit = (over: Partial<Parameters<typeof judgeCommit>[0]>) =>
    judgeCommit({ url: "https://site-b.test/", transitionType: "link", transitionQualifiers: [], previousUrl: "https://site-a.test/", openedFrom: null, ...over });

  it("the member's own link click in the same tab is never blocked (search results must work)", () => {
    expect(commit({})).toEqual({ action: "allow", settle: false });
    expect(commit({ transitionQualifiers: ["server_redirect"] })).toEqual({ action: "allow", settle: false });
    expect(commit({ transitionType: "form_submit" })).toEqual({ action: "allow", settle: false });
  });

  it("an automatic client redirect to another site is blocked; to the same site it is not", () => {
    expect(commit({ transitionQualifiers: ["client_redirect"] })).toEqual({ action: "block", kind: "client_redirect" });
    expect(commit({ transitionQualifiers: ["client_redirect", "server_redirect"] })).toEqual({ action: "block", kind: "client_redirect" });
    expect(commit({ url: "https://www.site-a.test/login", transitionQualifiers: ["client_redirect"] })).toEqual({ action: "allow", settle: false });
  });

  it("typed addresses, bookmarks, Back/Forward and the browser's own pages are not affected", () => {
    expect(commit({ transitionType: "typed" })).toEqual({ action: "allow", settle: false });
    expect(commit({ transitionType: "auto_bookmark" })).toEqual({ action: "allow", settle: false });
    expect(commit({ transitionQualifiers: ["client_redirect", "forward_back"] })).toEqual({ action: "allow", settle: false });
    // Coming from an extension page (the fail-closed or time-up page returning) or a browser page.
    expect(commit({ previousUrl: blockPage("https://site-b.test/"), transitionQualifiers: ["client_redirect"] })).toEqual({ action: "allow", settle: false });
    expect(commit({ previousUrl: "edge://newtab/", transitionQualifiers: ["client_redirect"] })).toEqual({ action: "allow", settle: false });
    expect(commit({ previousUrl: null, transitionQualifiers: ["client_redirect"] })).toEqual({ action: "allow", settle: false });
    expect(commit({ url: "edge://settings/", transitionQualifiers: ["client_redirect"] })).toEqual({ action: "allow", settle: false });
  });

  it("the search engines' click-through redirects are part of the member's click", () => {
    for (const from of [
      "https://www.bing.com/ck/a?!&&p=abc&u=a1aHR0cHM6Ly9zaXRlLWIudGVzdC8",
      "https://duckduckgo.com/l/?uddg=https%3A%2F%2Fsite-b.test%2F",
      "https://www.google.co.il/url?q=https://site-b.test/",
      "https://www.youtube.com/redirect?q=https://site-b.test/",
    ]) {
      expect(commit({ previousUrl: from, transitionQualifiers: ["client_redirect"] }), from).toEqual({ action: "allow", settle: false });
    }
    expect(isSearchRedirector("https://www.bing.com/search?q=x")).toBe(false);
    expect(isSearchRedirector("https://googlesyndication.com/url")).toBe(false);
    expect(isSearchRedirector("https://evil.example/ck/a")).toBe(false);
  });

  it("a tab opened by a page: its own site settles it, another site is blocked, the member may take it over", () => {
    const opened = { openedFrom: "site-a.test", previousUrl: "about:blank" };
    expect(commit({ ...opened })).toEqual({ action: "block", kind: "new_tab" });
    expect(commit({ ...opened, transitionQualifiers: ["server_redirect"] })).toEqual({ action: "block", kind: "new_tab" });
    expect(commit({ ...opened, url: "https://www.site-a.test/x" })).toEqual({ action: "allow", settle: true });
    expect(commit({ ...opened, transitionType: "typed" })).toEqual({ action: "allow", settle: true });
    expect(commit({ ...opened, transitionType: "auto_bookmark" })).toEqual({ action: "allow", settle: true });
    expect(commit({ ...opened, transitionQualifiers: ["from_address_bar"] })).toEqual({ action: "allow", settle: true });
    // Back to a page that slipped in before the block page: still the opener's jump.
    expect(commit({ ...opened, transitionQualifiers: ["forward_back"] })).toEqual({ action: "block", kind: "new_tab" });
    expect(commit({ ...opened, url: "about:blank" })).toEqual({ action: "allow", settle: false });
  });
});

describe("jumpTabRule", () => {
  it("redirects top-level loads outside the opener's site in that tab only, above every compiled rule", () => {
    const rule = jumpTabRule(JUMP_RULE_BASE + 7, 42, "site-a.test", BLOCK);
    expect(rule).toEqual({
      id: JUMP_RULE_BASE + 7,
      priority: JUMP_RULE_PRIORITY,
      action: { type: "redirect", redirect: { regexSubstitution: `${BLOCK}?reason=blocked_jump#\\0` } },
      condition: { tabIds: [42], resourceTypes: ["main_frame"], excludedRequestDomains: ["site-a.test", ...SIGN_IN_HOSTS], regexFilter: "^https?://.*$" },
    });
    expect(JUMP_RULE_PRIORITY).toBeGreaterThan(262_145);
  });
});

describe("JumpGuard (chrome mock)", () => {
  let state: ChromeMockState;
  let active: boolean;
  let guard: JumpGuard;
  const flush = () => vi.advanceTimersByTimeAsync(0);
  const jumpRules = () => [...state.sessionRules.values()].filter((r) => r.id >= JUMP_RULE_BASE);
  const commit = (tabId: number, url: string, transitionType = "link", transitionQualifiers: string[] = []) =>
    guard.onCommitted({ tabId, frameId: 0, url, transitionType, transitionQualifiers, documentLifecycle: "active" });

  beforeEach(() => {
    vi.useFakeTimers();
    state = installChromeMock({ tabs: [{ id: 1, url: "https://site-a.test/page", active: true }] });
    active = true;
    guard = new JumpGuard({ active: () => active, timers: realTimers, now: () => Date.now(), log: () => undefined });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("window.open / target=_blank to another site: block page at once, a tab rule until the block page shows", async () => {
    commit(1, "https://site-a.test/page", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 2, url: "https://site-b.test/ad?x=1" });
    expect(state.tabUpdates).toEqual([{ tabId: 2, url: blockPage("https://site-b.test/ad?x=1") }]);
    expect(jumpRules()).toHaveLength(1);
    expect(jumpRules()[0]!.condition).toMatchObject({ tabIds: [2], excludedRequestDomains: ["site-a.test", ...SIGN_IN_HOSTS] });
    // The target answered before the redirect landed: its commit does not trigger a second redirect.
    commit(2, "https://site-b.test/ad?x=1");
    await flush();
    expect(state.tabUpdates).toHaveLength(1);
    commit(2, blockPage("https://site-b.test/ad?x=1"), "link");
    await flush();
    expect(jumpRules()).toHaveLength(0);
    expect(guard.guardedTabs()).toEqual([2]);
    // The opener navigates the tab again later: blocked at commit (no tab rule any more).
    commit(2, "https://site-c.test/", "link");
    expect(state.tabUpdates.at(-1)).toEqual({ tabId: 2, url: blockPage("https://site-c.test/") });
    // The member types an address in that tab: theirs now.
    commit(2, "https://site-d.test/", "typed");
    expect(guard.guardedTabs()).toEqual([]);
    expect(state.tabUpdates).toHaveLength(2);
  });

  it("a same-site new tab loads and settles; its rule goes away", async () => {
    commit(1, "https://www.site-a.test/page", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 3, url: "https://docs.site-a.test/d" });
    expect(state.tabUpdates).toEqual([]);
    expect(jumpRules()).toHaveLength(1);
    commit(3, "https://docs.site-a.test/d");
    await flush();
    expect(jumpRules()).toHaveLength(0);
    expect(guard.guardedTabs()).toEqual([]);
    // From then on it is an ordinary tab: the member's clicks go anywhere.
    commit(3, "https://site-b.test/", "link");
    expect(state.tabUpdates).toEqual([]);
  });

  it("window.open('about:blank') then opened.location = another site: stopped before it loads", async () => {
    commit(1, "https://site-a.test/page", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 4, url: "about:blank" });
    expect(state.tabUpdates).toEqual([]);
    commit(4, "about:blank");
    expect(jumpRules()).toHaveLength(1);
    guard.onBeforeNavigate(4, 0, "https://site-b.test/later");
    expect(state.tabUpdates).toEqual([{ tabId: 4, url: blockPage("https://site-b.test/later") }]);
    guard.onBeforeNavigate(4, 0, "https://site-b.test/later");
    expect(state.tabUpdates).toHaveLength(1);
  });

  it("a client redirect to another site is blocked at commit; a link click is not", () => {
    commit(1, "https://site-a.test/page", "typed");
    commit(1, "https://site-b.test/landing", "link");
    expect(state.tabUpdates).toEqual([]);
    commit(1, "https://site-b.test/meta", "link");
    commit(1, "https://site-c.test/frommeta", "link", ["client_redirect"]);
    expect(state.tabUpdates).toEqual([{ tabId: 1, url: blockPage("https://site-c.test/frommeta") }]);
  });

  it("asks the browser for the opener's address when it was not seen (worker restart)", async () => {
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 5, url: "https://site-b.test/" });
    expect(state.tabUpdates).toEqual([{ tabId: 5, url: blockPage("https://site-b.test/") }]);
  });

  it("when the opener's address must be asked for, a first page that committed meanwhile is judged too", async () => {
    // Another site committed while the browser was asked: replaced, and the tab is guarded.
    const cross = guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 20, url: "https://site-b.test/" });
    commit(20, "https://site-b.test/");
    await cross;
    expect(state.tabUpdates).toEqual([{ tabId: 20, url: blockPage("https://site-b.test/") }]);
    expect(guard.guardedTabs()).toEqual([20]);
    // The opener's own site committed meanwhile: nothing left to guard, no rule.
    const same = guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 21, url: "https://www.site-a.test/x" });
    commit(21, "https://www.site-a.test/x");
    await same;
    expect(guard.guardedTabs()).toEqual([20]);
    expect(jumpRules()).toHaveLength(0);
    commit(21, "https://site-b.test/", "link");
    expect(state.tabUpdates).toHaveLength(1);
  });

  it("seed() learns the open tabs", async () => {
    await guard.seed();
    commit(1, "https://site-c.test/x", "link", ["client_redirect"]);
    expect(state.tabUpdates).toEqual([{ tabId: 1, url: blockPage("https://site-c.test/x") }]);
  });

  it("does nothing while the feature is off, or for tabs opened by browser pages", async () => {
    active = false;
    commit(1, "https://site-a.test/page", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 6, url: "https://site-b.test/" });
    commit(1, "https://site-b.test/", "link", ["client_redirect"]);
    expect(state.tabUpdates).toEqual([]);
    expect(jumpRules()).toHaveLength(0);
    active = true;
    commit(1, "edge://newtab/", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 7, url: "https://site-b.test/" });
    expect(state.tabUpdates).toEqual([]);
  });

  it("an opened tab still on about:blank that opens another tab passes on its opener's site", async () => {
    commit(1, "https://site-a.test/page", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 8, url: "about:blank" });
    commit(8, "about:blank");
    await guard.onCreatedNavigationTarget({ sourceTabId: 8, tabId: 9, url: "https://site-b.test/" });
    expect(state.tabUpdates).toEqual([{ tabId: 9, url: blockPage("https://site-b.test/") }]);
  });

  it("override: allowOnce lets this tab's next page through; the opener's next jump is blocked again", async () => {
    commit(1, "https://site-a.test/page", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 10, url: "https://site-b.test/" });
    commit(10, blockPage("https://site-b.test/"));
    await guard.allowOnce(10);
    expect(guard.guardedTabs()).toEqual([]);
    commit(10, "https://site-b.test/", "link"); // the extension's tabs.update
    expect(state.tabUpdates).toHaveLength(1);
    // The opened site redirects on by itself: still a jump.
    commit(10, "https://site-c.test/", "link", ["client_redirect"]);
    expect(state.tabUpdates.at(-1)).toEqual({ tabId: 10, url: blockPage("https://site-c.test/") });
    // And the same jump from the opener again.
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 11, url: "https://site-b.test/" });
    expect(state.tabUpdates.at(-1)).toEqual({ tabId: 11, url: blockPage("https://site-b.test/") });
  });

  it("tab rules expire, go with their tab, and reset() removes only them", async () => {
    state.sessionRules.set(10, { id: 10, priority: 262144, action: { type: "block" }, condition: {} });
    state.sessionRules.set(SHORTS_RULE_ID, { id: SHORTS_RULE_ID, priority: 196624, action: { type: "block" }, condition: {} });
    commit(1, "https://site-a.test/page", "typed");
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 12, url: "about:blank" });
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 13, url: "about:blank" });
    expect(jumpRules()).toHaveLength(2);
    guard.onTabRemoved(12);
    await flush();
    expect(jumpRules().map((r) => r.condition.tabIds)).toEqual([[13]]);
    await vi.advanceTimersByTimeAsync(JUMP_RULE_LIFETIME_MS + 10);
    expect(jumpRules()).toHaveLength(0);
    await guard.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 14, url: "about:blank" });
    expect(jumpRules()).toHaveLength(1);
    await guard.reset();
    expect(jumpRules()).toHaveLength(0);
    expect(guard.guardedTabs()).toEqual([]);
    expect([...state.sessionRules.keys()].sort((a, b) => a - b)).toEqual([10, SHORTS_RULE_ID]);
  });

  it("ignores sub-frames and prerendered pages", () => {
    commit(1, "https://site-a.test/page", "typed");
    guard.onCommitted({ tabId: 1, frameId: 3, url: "https://site-b.test/", transitionType: "link", transitionQualifiers: ["client_redirect"] });
    guard.onCommitted({ tabId: 1, frameId: 0, url: "https://site-b.test/", transitionType: "link", transitionQualifiers: ["client_redirect"], documentLifecycle: "prerender" });
    expect(state.tabUpdates).toEqual([]);
  });
});

describe("sign-in hosts (Google, Microsoft, Apple) are not jumps", () => {
  const commit = (over: Partial<Parameters<typeof judgeCommit>[0]>) =>
    judgeCommit({ url: "https://site-b.test/", transitionType: "link", transitionQualifiers: [], previousUrl: "https://site-a.test/", openedFrom: null, ...over });

  it("matches the exact sign-in hosts only, not the rest of their sites", () => {
    for (const url of ["https://accounts.google.com/o/oauth2/v2/auth?x=1", "https://login.microsoftonline.com/common/oauth2/authorize", "https://appleid.apple.com/auth/authorize", "https://ACCOUNTS.google.com./x"]) {
      expect(isSignInHost(url), url).toBe(true);
    }
    for (const url of ["https://www.google.com/search?q=x", "https://mail.google.com/", "https://www.microsoft.com/", "https://evil.test/accounts.google.com", "chrome://accounts.google.com/", null]) {
      expect(isSignInHost(url), String(url)).toBe(false);
    }
  });

  it("a sign-in popup opened by a site is not guarded", () => {
    expect(judgeNewTarget("site-a.test", "https://accounts.google.com/o/oauth2/v2/auth")).toEqual({ action: "ignore" });
    expect(judgeNewTarget("site-a.test", "https://www.google.com/")).toEqual({ action: "block", sourceSite: "site-a.test" });
  });

  it("a popup sent to the provider after about:blank settles there", () => {
    expect(commit({ url: "https://login.microsoftonline.com/common/login", openedFrom: "site-a.test", previousUrl: "about:blank" })).toEqual({ action: "allow", settle: true });
  });

  it("a client redirect to the provider, and the provider's return to the site, are allowed", () => {
    expect(commit({ url: "https://accounts.google.com/signin", transitionQualifiers: ["client_redirect"] })).toEqual({ action: "allow", settle: false });
    expect(commit({ url: "https://site-c.test/callback", previousUrl: "https://accounts.google.com/signin/oauth/consent", transitionQualifiers: ["client_redirect"] })).toEqual({ action: "allow", settle: false });
    // Not a sign-in host: still a jump.
    expect(commit({ url: "https://site-c.test/", previousUrl: "https://www.google.com/", transitionQualifiers: ["client_redirect"] })).toEqual({ action: "block", kind: "client_redirect" });
  });

  it("the tab rule lets the sign-in hosts through", () => {
    const rule = jumpTabRule(JUMP_RULE_BASE + 1, 7, "site-a.test", BLOCK);
    expect(rule.condition.excludedRequestDomains).toEqual(expect.arrayContaining(["site-a.test", ...SIGN_IN_HOSTS]));
  });
});
