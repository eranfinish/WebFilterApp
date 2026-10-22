/**
 * The protection features through the controller (PROTECTION-SPEC §6–§9): features from the rules reply, the Shorts
 * rule and SPA check, the jump override, and the search log, against the chrome mock and the fake host.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Controller } from "../src/background/controller";
import { ADS_RULESET, JUMP_RULE_BASE, SHORTS_RULE_ID } from "../src/background/dnr";
import { FakeHost, type FakeHostScript } from "../src/background/fake-host";
import type { YouTubeDetection } from "../src/shared/messages";
import { type DnrRule, NO_FEATURES, type OutgoingMessage, type ProtectionFeatures } from "../src/shared/protocol";
import { SEARCH_REPEAT_WINDOW_MS } from "../src/shared/search";
import { type ChromeMockState, fire, installChromeMock } from "./chrome-mock";

const FRAMES = ["main_frame", "sub_frame"] as chrome.declarativeNetRequest.ResourceType[];
const REDIRECT = { type: "redirect", redirect: { regexSubstitution: "chrome-extension://testextensionid/blocked.html#\\0" } } as const;
const BLOCK = "chrome-extension://testextensionid/blocked.html";
const DYNAMIC: DnrRule[] = [
  { id: 1, priority: 1, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
  { id: 3, priority: 196608, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["youtube.com", "youtube-nocookie.com"] } },
  { id: 4321, priority: 65544, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["site-a.test", "site-b.test", "google.com"] } },
];
const TIME_UP: DnrRule[] = [{ id: 10, priority: 262144, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } }];
const ALL_ON: ProtectionFeatures = { jumpGuard: true, blockYouTubeShorts: true, logSearches: true, canOverrideJump: true, blockAds: true };

function script(features: Partial<ProtectionFeatures> | null, extra: FakeHostScript = {}): FakeHostScript {
  return {
    rules: {
      rulesVersion: 5,
      dynamicRules: DYNAMIC,
      timeUpSessionRules: TIME_UP,
      youTube: { mode: "restricted", allowed: [], blocked: [] },
      time: { state: "ok", remainingMinutes: 60, windowEndsAt: null },
      ...(features ? { features: { ...NO_FEATURES, ...features } } : {}),
    },
    ...extra,
  };
}

const sent = (host: FakeHost, type: string): OutgoingMessage[] => host.received.filter((m) => m.type === type);
const settle = (ms = 50) => vi.advanceTimersByTimeAsync(ms);

let state: ChromeMockState;
let controller: Controller | null = null;

beforeEach(() => {
  vi.useFakeTimers();
  state = installChromeMock();
});

afterEach(() => {
  controller?.client.close("test end");
  controller = null;
  vi.useRealTimers();
});

async function start(host: FakeHost): Promise<Controller> {
  controller = new Controller({ connector: host.connector, log: () => undefined });
  void controller.start();
  await settle();
  return controller;
}

describe("features in the rules reply", () => {
  it("absent → all off (older services)", async () => {
    const c = await start(new FakeHost(script(null)));
    expect(c.ready).toBe(true);
    expect(c.features).toEqual(NO_FEATURES);
    expect(c.snapshot().features).toEqual(NO_FEATURES);
    expect(state.sessionRules.has(SHORTS_RULE_ID)).toBe(false);
  });

  it("present → in the status for the popup and the block page", async () => {
    const c = await start(new FakeHost(script({ logSearches: true, jumpGuard: true })));
    expect(c.snapshot().features).toEqual({ ...NO_FEATURES, logSearches: true, jumpGuard: true });
  });

  it("an unrestricted account ignores them, and so does a disconnected extension", async () => {
    const host = new FakeHost(script(ALL_ON, { hello: { mode: "unrestricted" } }));
    const c = await start(host);
    expect(c.features).toEqual(NO_FEATURES);
    expect(state.sessionRules.size).toBe(0);
    const restricted = new FakeHost(script(ALL_ON));
    controller?.client.close("x");
    const d = await start(restricted);
    expect(d.features).toEqual(ALL_ON);
    restricted.hangUp();
    await settle(10);
    expect(d.features).toEqual(NO_FEATURES);
  });
});

describe("ad blocking (PROTECTION-SPEC §12)", () => {
  const ads = () => state.enabledRulesets.has(ADS_RULESET);
  const rulesChanged = async (host: FakeHost, next: FakeHostScript) => {
    host.script = next;
    host.push({ type: "rulesChanged", rulesVersion: 9 });
    await settle();
  };

  it("features.blockAds turns the ads ruleset on, after the rules and with fail-closed off", async () => {
    const c = await start(new FakeHost(script({ blockAds: true })));
    expect(c.ready).toBe(true);
    expect([...state.enabledRulesets].sort()).toEqual([ADS_RULESET]);
    expect(c.snapshot().features.blockAds).toBe(true);
  });

  it("follows the switch in later rules, and does not touch the ruleset when nothing changed", async () => {
    const host = new FakeHost(script({ blockAds: false }));
    await start(host);
    expect(ads()).toBe(false);
    await rulesChanged(host, script({ blockAds: true }));
    expect(ads()).toBe(true);
    const adUpdates = () => state.rulesetUpdates.filter((u) => u.endsWith(ADS_RULESET));
    expect(adUpdates()).toEqual(["+ads", "-ads", "+ads"]); // fail-closed at start, the first rules, the switch
    await rulesChanged(host, script({ blockAds: true, jumpGuard: true }));
    expect(adUpdates()).toEqual(["+ads", "-ads", "+ads"]); // unchanged: no call
    // The parent's escape hatch: switched off for this child.
    await rulesChanged(host, script({ blockAds: false, jumpGuard: true }));
    expect(ads()).toBe(false);
  });

  it("is off while the member is paused (the service sends no rules and blockAds false), and back after", async () => {
    const host = new FakeHost(script({ blockAds: true }));
    const c = await start(host);
    const base = script({ logSearches: true }).rules!;
    const paused = { rules: { ...base, dynamicRules: [], timeUpSessionRules: [], time: { state: "unlimited" as const, remainingMinutes: null, windowEndsAt: null } } };
    await rulesChanged(host, paused);
    expect(ads()).toBe(false);
    expect(c.features).toEqual({ ...NO_FEATURES, logSearches: true });
    await rulesChanged(host, script({ blockAds: true }));
    expect(ads()).toBe(true);
  });

  it("is on while fail-closed (no rules yet: the strictest state) and the rules decide once connected", async () => {
    const silent = new FakeHost(script({ blockAds: false }, { silent: true }));
    const c = await start(silent);
    expect(c.ready).toBe(false);
    expect([...state.enabledRulesets].sort()).toEqual([ADS_RULESET, "failclosed"]);
    controller?.client.close("x");
    const host = new FakeHost(script({ blockAds: false }));
    await start(host);
    expect([...state.enabledRulesets]).toEqual([]);
    host.hangUp();
    await settle(10);
    expect([...state.enabledRulesets].sort()).toEqual([ADS_RULESET, "failclosed"]);
  });

  it("is off for an unrestricted account, an extension that is not policy-installed, and the host's own answers", async () => {
    await start(new FakeHost(script({ blockAds: true }, { hello: { mode: "unrestricted" } })));
    expect(ads()).toBe(false);
    controller?.client.close("x");
    // The native host answering by itself (service down) sends no features: only always-allowed sites load.
    await start(new FakeHost(script(null, { hello: { source: "host", serviceAvailable: false }, hostFallback: true })));
    expect(controller!.ready).toBe(true);
    expect(ads()).toBe(false);
    controller?.client.close("x");
    state.installType = "development";
    state.enabledRulesets.add(ADS_RULESET);
    await start(new FakeHost(script({ blockAds: true })));
    expect(controller!.connectionState).toBe("inert");
    expect([...state.enabledRulesets]).toEqual([]);
  });

  it("is quiet: blocked ad requests never reach a block page, so the extension sends nothing about them", async () => {
    const host = new FakeHost(script({ blockAds: true }));
    await start(host);
    expect(new Set(host.received.map((m) => m.type))).toEqual(new Set(["hello", "getRules", "heartbeat"]));
  });

  it("a browser that refuses the ruleset is logged; the profile still applies, and the next rules try again", async () => {
    const dnr = (globalThis as unknown as { chrome: typeof chrome }).chrome.declarativeNetRequest;
    const original = dnr.updateEnabledRulesets;
    let refuse = true;
    dnr.updateEnabledRulesets = (async (o: chrome.declarativeNetRequest.UpdateRulesetOptions) => {
      if (refuse && o.enableRulesetIds?.includes(ADS_RULESET)) throw new Error("Internal error while updating rulesets.");
      return original(o);
    }) as typeof dnr.updateEnabledRulesets;
    const logs: string[] = [];
    const host = new FakeHost(script({ blockAds: true }));
    controller = new Controller({ connector: host.connector, log: (m) => logs.push(m) });
    void controller.start();
    await settle();
    expect(controller.ready).toBe(true);
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
    expect(ads()).toBe(false);
    expect(logs).toContain("ads ruleset update failed");
    refuse = false;
    await rulesChanged(host, script({ blockAds: true }));
    expect(ads()).toBe(true);
  });
});

describe("YouTube Shorts", () => {
  it("installs the Shorts session rule next to the time-up rules, and removes it when switched off", async () => {
    const host = new FakeHost(script({ blockYouTubeShorts: true }));
    const c = await start(host);
    const rule = state.sessionRules.get(SHORTS_RULE_ID);
    expect(rule?.action.redirect?.regexSubstitution).toBe(`${BLOCK}?reason=blocked_youtube_shorts#\\0`);
    expect(state.local["wf.youtube"]).toEqual({ mode: "restricted", version: 5, blockShorts: true });
    // Time up and back: the time-up rules come and go, the Shorts rule stays.
    host.push({ type: "timeState", state: "expired", remainingMinutes: 0 });
    await settle();
    expect([...state.sessionRules.keys()].sort((a, b) => a - b)).toEqual([10, SHORTS_RULE_ID]);
    host.push({ type: "timeState", state: "ok", remainingMinutes: 30 });
    await settle();
    expect([...state.sessionRules.keys()]).toEqual([SHORTS_RULE_ID]);
    // The parent switches it off.
    host.script = script({ blockYouTubeShorts: false }, { rules: { ...script(null).rules, rulesVersion: 6 } });
    host.push({ type: "rulesChanged", rulesVersion: 6 });
    await settle();
    expect(state.sessionRules.has(SHORTS_RULE_ID)).toBe(false);
    expect(state.local["wf.youtube"]).toEqual({ mode: "restricted", version: 6 });
    expect(c.features.blockYouTubeShorts).toBe(false);
  });

  it("no Shorts rule while YouTube is off (the host rule blocks it all); gone when fail-closed", async () => {
    const off = new FakeHost(script({ blockYouTubeShorts: true }, { rules: { ...script({ blockYouTubeShorts: true }).rules, youTube: { mode: "off", allowed: [], blocked: [] } } }));
    await start(off);
    expect(state.sessionRules.has(SHORTS_RULE_ID)).toBe(false);
    controller?.client.close("x");
    const host = new FakeHost(script({ blockYouTubeShorts: true }));
    await start(host);
    expect(state.sessionRules.has(SHORTS_RULE_ID)).toBe(true);
    host.hangUp();
    await settle(10);
    expect(state.sessionRules.size).toBe(0);
    expect(state.enabledRulesets.has("failclosed")).toBe(true);
  });

  it("in-site (SPA) navigation to a Short → block page with blocked_youtube_shorts", async () => {
    const c = await start(new FakeHost(script({ blockYouTubeShorts: true })));
    expect(await c.checkInPageNavigation(7, 0, "https://www.youtube.com/watch?v=abc")).toBe(false);
    expect(await c.checkInPageNavigation(7, 0, "https://www.youtube.com/shorts/abc")).toBe(true);
    expect(state.tabUpdates).toEqual([{ tabId: 7, url: `${BLOCK}?reason=blocked_youtube_shorts#https://www.youtube.com/shorts/abc` }]);
  });

  it("without the feature, Shorts stay a YouTube decision", async () => {
    const c = await start(new FakeHost(script({ blockYouTubeShorts: false })));
    expect(await c.checkInPageNavigation(7, 0, "https://www.youtube.com/shorts/abc")).toBe(false);
    expect(state.tabUpdates).toEqual([]);
  });

  it("the content script's report of a Short is blocked too (full loads that slipped past the rule)", async () => {
    const c = await start(new FakeHost(script({ blockYouTubeShorts: true })));
    const url = "https://www.youtube.com/shorts/abc";
    const detection: YouTubeDetection = { url, kind: "shorts", videoId: "abc", handle: null, channelId: null, via: null, trigger: "load", timedOut: true, ms: 1 };
    expect(await c.decideYouTubeDetection(detection, url)).toEqual({
      decision: "block",
      reason: "blocked_youtube_shorts",
      navigateTo: `${BLOCK}?reason=blocked_youtube_shorts#${url}`,
    });
  });
});

describe("jump guard through the controller", () => {
  it("works only with features.jumpGuard", async () => {
    const c = await start(new FakeHost(script({ jumpGuard: false })));
    c.jumps.onCommitted({ tabId: 1, frameId: 0, url: "https://site-a.test/", transitionType: "typed", transitionQualifiers: [] });
    await c.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 2, url: "https://site-b.test/" });
    expect(state.tabUpdates).toEqual([]);
    controller?.client.close("x");
    const d = await start(new FakeHost(script({ jumpGuard: true })));
    d.jumps.onCommitted({ tabId: 1, frameId: 0, url: "https://site-a.test/", transitionType: "typed", transitionQualifiers: [] });
    await d.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 2, url: "https://site-b.test/" });
    expect(state.tabUpdates).toEqual([{ tabId: 2, url: `${BLOCK}?reason=blocked_jump#https://site-b.test/` }]);
  });

  it("switching the guard off removes its tab rules", async () => {
    const host = new FakeHost(script({ jumpGuard: true }));
    const c = await start(host);
    c.jumps.onCommitted({ tabId: 1, frameId: 0, url: "https://site-a.test/", transitionType: "typed", transitionQualifiers: [] });
    await c.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 2, url: "about:blank" });
    expect([...state.sessionRules.keys()].some((id) => id >= JUMP_RULE_BASE)).toBe(true);
    host.script = script({ jumpGuard: false });
    host.push({ type: "rulesChanged", rulesVersion: 6 });
    await settle();
    expect([...state.sessionRules.keys()].some((id) => id >= JUMP_RULE_BASE)).toBe(false);
    expect(c.jumps.guardedTabs()).toEqual([]);
  });

  it("the block page reports blocked_jump; the service's 'allow' does not undo it; a reload asks nothing", async () => {
    const host = new FakeHost(script({ jumpGuard: true }, { decide: { "https://site-b.test/": { decision: "allow", reason: "allowed_rule" } } }));
    const c = await start(host);
    const p = c.reportBlock("https://site-b.test/", "blocked_jump", null);
    await settle();
    expect(await p).toMatchObject({ decision: "block", reason: "blocked_jump", source: "service", canRequest: false, requestKind: null, reported: true });
    expect(sent(host, "reportBlock")).toEqual([expect.objectContaining({ url: "https://site-b.test/", reason: "blocked_jump" })]);
    expect(await c.reportBlock("https://site-b.test/", "blocked_jump", null, "main_frame", true)).toMatchObject({ decision: "block", reason: "blocked_jump" });
    expect(sent(host, "decideUrl")).toHaveLength(0);
  });

  describe("override (teens)", () => {
    const page = (url: string) => `${BLOCK}?reason=blocked_jump#${url}`;

    it("sends reportOverride and opens the address once in the same tab", async () => {
      const host = new FakeHost(script({ jumpGuard: true, canOverrideJump: true }));
      const c = await start(host);
      c.jumps.onCommitted({ tabId: 1, frameId: 0, url: "https://site-a.test/", transitionType: "typed", transitionQualifiers: [] });
      await c.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 2, url: "https://site-b.test/x" });
      c.jumps.onCommitted({ tabId: 2, frameId: 0, url: page("https://site-b.test/x"), transitionType: "link", transitionQualifiers: [] });
      state.tabUpdates = [];
      const reply = c.overrideJump("https://site-b.test/x", 2, 0, page("https://site-b.test/x"));
      await settle();
      expect(await reply).toEqual({ ok: true });
      const [report] = sent(host, "reportOverride");
      expect(report).toMatchObject({ type: "reportOverride", url: "https://site-b.test/x", reason: "blocked_jump" });
      expect(Object.keys(report!).slice(0, 3)).toEqual(["type", "v", "id"]);
      expect(state.tabUpdates).toEqual([{ tabId: 2, url: "https://site-b.test/x" }]);
      expect(c.jumps.guardedTabs()).toEqual([]);
      // Once: the opener's next jump to the same site is blocked again.
      await c.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 3, url: "https://site-b.test/x" });
      expect(state.tabUpdates.at(-1)).toEqual({ tabId: 3, url: page("https://site-b.test/x") });
    });

    it("children (no canOverrideJump) and wrong senders are refused", async () => {
      const child = new FakeHost(script({ jumpGuard: true, canOverrideJump: false }));
      const c = await start(child);
      expect(await c.overrideJump("https://site-b.test/", 2, 0, page("https://site-b.test/"))).toEqual({ ok: false, error: "not_allowed" });
      controller?.client.close("x");
      const teen = new FakeHost(script({ jumpGuard: true, canOverrideJump: true }));
      const d = await start(teen);
      expect(await d.overrideJump("https://site-b.test/", 2, 3, page("https://site-b.test/"))).toEqual({ ok: false, error: "bad_request" }); // sub-frame
      expect(await d.overrideJump("https://site-c.test/", 2, 0, page("https://site-b.test/"))).toEqual({ ok: false, error: "bad_request" }); // another URL
      expect(await d.overrideJump("https://site-b.test/", 2, 0, `${BLOCK}#https://site-b.test/`)).toEqual({ ok: false, error: "bad_request" }); // not a jump block
      expect(await d.overrideJump("javascript:alert(1)", 2, 0, page("javascript:alert(1)"))).toEqual({ ok: false, error: "bad_request" });
      expect(await d.overrideJump("https://site-b.test/", undefined, 0, page("https://site-b.test/"))).toEqual({ ok: false, error: "bad_request" });
      expect(sent(teen, "reportOverride")).toHaveLength(0);
      expect(state.tabUpdates).toEqual([]);
    });

    it("an older service that does not know reportOverride: the page still opens", async () => {
      const host = new FakeHost(script({ jumpGuard: true, canOverrideJump: true }, { noProtectionMessages: true }));
      const c = await start(host);
      const reply = c.overrideJump("https://site-b.test/", 2, 0, page("https://site-b.test/"));
      await settle();
      expect(await reply).toEqual({ ok: true });
      expect(state.tabUpdates).toEqual([{ tabId: 2, url: "https://site-b.test/" }]);
    });
  });
});

describe("search log", () => {
  const nav = (url: string, over: Record<string, unknown> = {}) => ({ tabId: 1, frameId: 0, url, transitionType: "link", transitionQualifiers: [] as string[], documentLifecycle: "active", ...over });

  it("reports each distinct search once: not for reloads, Back, pagination or a repeat within the window", async () => {
    const host = new FakeHost(script({ logSearches: true }));
    const c = await start(host);
    c.noteSearch(nav("https://www.google.com/search?q=cats&hl=he", { transitionType: "form_submit" }));
    await settle();
    expect(sent(host, "reportSearch")).toEqual([
      expect.objectContaining({ type: "reportSearch", engine: "google", query: "cats", url: "https://www.google.com/search?q=cats&hl=he" }),
    ]);
    c.noteSearch(nav("https://www.google.com/search?q=cats&hl=he", { transitionType: "reload" }));
    c.noteSearch(nav("https://www.google.com/search?q=cats&hl=he", { transitionQualifiers: ["forward_back"] }));
    c.noteSearch(nav("https://www.google.com/search?q=cats&start=10"));
    c.noteSearch(nav("https://www.google.com/search?q=Cats&udm=2"));
    c.noteSearch(nav("https://www.google.com/search?q=cats", { frameId: 2 }));
    c.noteSearch(nav("https://www.google.com/search?q=dogs", { documentLifecycle: "prerender" }));
    await settle();
    expect(sent(host, "reportSearch")).toHaveLength(1);
    // YouTube searches change the address in place (onHistoryStateUpdated); other engines are other searches.
    c.noteSearch(nav("https://www.youtube.com/results?search_query=cats"));
    c.noteSearch(nav("https://www.bing.com/search?q=cats"));
    c.noteSearch(nav("https://duckduckgo.com/?q=cats&ia=web"));
    await settle();
    expect(sent(host, "reportSearch").map((m) => (m as { engine: string }).engine)).toEqual(["google", "youtube", "bing", "duckduckgo"]);
    // After the window the same search counts again.
    await vi.advanceTimersByTimeAsync(SEARCH_REPEAT_WINDOW_MS);
    c.noteSearch(nav("https://www.google.com/search?q=cats"));
    await settle();
    expect(sent(host, "reportSearch")).toHaveLength(5);
  });

  it("a blocked search is logged from its block page's first report", async () => {
    const host = new FakeHost(script({ logSearches: true }));
    const c = await start(host);
    const p = c.reportBlock("https://www.google.com/search?q=cats&udm=2", null, null);
    await settle();
    await p;
    expect(sent(host, "reportSearch")).toEqual([expect.objectContaining({ engine: "google", query: "cats" })]);
    const reload = c.reportBlock("https://www.google.com/search?q=other&udm=2", null, null, "main_frame", true);
    await settle();
    await reload;
    expect(sent(host, "reportSearch")).toHaveLength(1);
  });

  it("sends nothing when logSearches is off or the service is down", async () => {
    const off = new FakeHost(script({ logSearches: false }));
    const c = await start(off);
    c.noteSearch(nav("https://www.google.com/search?q=cats"));
    await settle();
    expect(sent(off, "reportSearch")).toHaveLength(0);
    controller?.client.close("x");
    const fallback = new FakeHost(script({ logSearches: true }, { hello: { source: "host", serviceAvailable: false } }));
    const d = await start(fallback);
    d.noteSearch(nav("https://www.google.com/search?q=cats"));
    await settle();
    expect(sent(fallback, "reportSearch")).toHaveLength(0);
  });
});

describe("blocks inside frames (ad slots, embeds) are never reported", () => {
  const blocked = "https://ads.example/banner?slot=1";

  it("the controller sends neither reportBlock nor decideUrl for a sub_frame, and logs no search", async () => {
    const host = new FakeHost(script({ logSearches: true }));
    const c = await start(host);
    const first = c.reportBlock(blocked, null, null, "sub_frame", false);
    const reload = c.reportBlock(blocked, null, null, "sub_frame", true);
    const search = c.reportBlock("https://www.google.com/search?q=x&udm=2", null, null, "sub_frame", false);
    await settle();
    expect(await first).toMatchObject({ decision: "block", source: "local", reported: false });
    await reload;
    await search;
    expect(sent(host, "reportBlock")).toHaveLength(0);
    expect(sent(host, "decideUrl")).toHaveLength(0);
    expect(sent(host, "reportSearch")).toHaveLength(0);
    // A top-level block page still reports.
    const top = c.reportBlock(blocked, null, null, "main_frame", false);
    await settle();
    await top;
    expect(sent(host, "reportBlock")).toHaveLength(1);
  });

  it("the worker trusts the browser's frame id, not only what the page says", async () => {
    const host = new FakeHost(script(null));
    const { createBackground } = await import("../src/background/wiring");
    controller = createBackground({ connector: host.connector, log: () => undefined });
    await settle();
    const ask = async (frameId: number, frame: "main_frame" | "sub_frame") => {
      let reply: unknown;
      const sender = { id: "testextensionid", url: `${BLOCK}#${blocked}`, tab: { id: 5 } as chrome.tabs.Tab, frameId };
      fire(state, "runtime.onMessage", { type: "wf:reportBlock", url: blocked, reason: null, frame }, sender, (r: unknown) => (reply = r));
      await settle();
      return reply;
    };
    expect(await ask(3, "main_frame")).toMatchObject({ reported: false, source: "local" });
    expect(sent(host, "reportBlock")).toHaveLength(0);
    expect(await ask(0, "main_frame")).toMatchObject({ reported: true, source: "service" });
    expect(sent(host, "reportBlock")).toHaveLength(1);
  });
});
