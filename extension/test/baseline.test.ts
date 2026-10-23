/**
 * The parents' baseline protection (PROTECTION-SPEC §13) through the controller: mode baseline from the hello or the rules
 * reply, the compiled adult rules, the jump guard with "open anyway" and the ads ruleset by the features, unlimited time,
 * and nothing reported — no reportBlock, reportOverride, reportSearch or request — against the chrome mock and the fake host.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Controller } from "../src/background/controller";
import { ADS_RULESET, JUMP_RULE_BASE } from "../src/background/dnr";
import { FakeHost, type FakeHostScript } from "../src/background/fake-host";
import type { YouTubeDetection } from "../src/shared/messages";
import { type DnrRule, NO_FEATURES, type OutgoingMessage, type ProtectionFeatures } from "../src/shared/protocol";
import { type ChromeMockState, installChromeMock } from "./chrome-mock";

const FRAMES = ["main_frame", "sub_frame"] as chrome.declarativeNetRequest.ResourceType[];
const REDIRECT = { type: "redirect", redirect: { regexSubstitution: "chrome-extension://testextensionid/blocked.html#\\0" } } as const;
const BLOCK = "chrome-extension://testextensionid/blocked.html";
/** What the compiler emits for a parent: the adult list in the global band, the parent's allow rule in the member band. */
const BASELINE_RULES: DnrRule[] = [
  { id: 2001, priority: 65545, action: REDIRECT, condition: { resourceTypes: FRAMES, requestDomains: ["adult.test", "nsfw.test"], regexFilter: "^(.*)$" } },
  { id: 2002, priority: 131080, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["ok.nsfw.test"] } },
];
const PARENT: ProtectionFeatures = { jumpGuard: true, blockYouTubeShorts: false, logSearches: false, canOverrideJump: true, blockAds: true };
const UNLIMITED = { state: "unlimited", remainingMinutes: null, windowEndsAt: null } as const;

/** A parent's account: hello mode baseline, the rules reply's mode too (unless `mode` says otherwise). */
function parent(features: Partial<ProtectionFeatures> = PARENT, extra: FakeHostScript & { mode?: string } = {}): FakeHostScript {
  const { mode = "baseline", ...rest } = extra;
  return {
    hello: { mode: "baseline", user: { accountId: "S-1-5-21-parent", name: "dana", role: "adult", isAdmin: true } },
    rules: {
      rulesVersion: 7,
      dynamicRules: BASELINE_RULES,
      timeUpSessionRules: [],
      youTube: { mode: "unrestricted", allowed: [], blocked: [] },
      time: UNLIMITED,
      features: { ...NO_FEATURES, ...features },
      ...(mode ? { mode: mode as "baseline" } : {}),
    },
    decide: { "https://adult.test/x": { reason: "blocked_baseline_adult", canRequest: false, requestKind: "url" } },
    ...rest,
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
  controller?.client.close("next");
  controller = new Controller({ connector: host.connector, log: () => undefined });
  void controller.start();
  await settle();
  return controller;
}

async function rulesChanged(host: FakeHost, next: FakeHostScript): Promise<void> {
  host.script = next;
  host.push({ type: "rulesChanged", rulesVersion: 9 });
  await settle();
}

describe("mode baseline", () => {
  it("installs the adult rules, turns the ads ruleset on and fail-closed off, with unlimited time and no time-up rules", async () => {
    const c = await start(new FakeHost(parent()));
    expect(c.ready).toBe(true);
    expect(c.mode).toBe("baseline");
    expect([...state.dynamicRules.keys()].sort()).toEqual([2001, 2002]);
    expect([...state.enabledRulesets]).toEqual([ADS_RULESET]);
    expect(state.sessionRules.size).toBe(0);
    expect(c.features).toEqual(PARENT);
    const status = c.snapshot();
    expect(status).toMatchObject({ mode: "baseline", youTubeMode: "unrestricted", timeUp: false, canRequest: false, inferredReason: "blocked_baseline_adult" });
    expect(status.time).toEqual(UNLIMITED);
    expect(state.badgeText).toBe("");
  });

  it("the installed rules explain a block as blocked_baseline_adult", async () => {
    const c = await start(new FakeHost(parent()));
    expect(c.explain("https://www.adult.test/video")).toBe("blocked_baseline_adult");
  });

  it("a parent's YouTube is never filtered", async () => {
    const c = await start(new FakeHost(parent()));
    const url = "https://www.youtube.com/shorts/abc";
    const detection: YouTubeDetection = { url, kind: "shorts", videoId: "abc", handle: null, channelId: null, via: null, trigger: "load", timedOut: true, ms: 1 };
    expect(await c.decideYouTubeDetection(detection, url)).toMatchObject({ decision: "allow", reason: "allowed_unrestricted" });
  });
});

describe("nothing a parent does is reported (PROTECTION-SPEC §13)", () => {
  it("the block page asks with decideUrl, never reportBlock, and offers no request even if the service did", async () => {
    const host = new FakeHost(parent(PARENT, { decide: { "https://adult.test/x": { reason: "blocked_baseline_adult", canRequest: true, requestKind: "url" } } }));
    const c = await start(host);
    const reply = c.reportBlock("https://adult.test/x", null, null);
    await settle();
    expect(await reply).toMatchObject({ decision: "block", reason: "blocked_baseline_adult", source: "service", canRequest: false, reported: false });
    expect(sent(host, "decideUrl")).toEqual([expect.objectContaining({ url: "https://adult.test/x" })]);
    expect(sent(host, "reportBlock")).toHaveLength(0);
  });

  it("a jump is blocked and can be opened anyway, without reportBlock or reportOverride", async () => {
    const host = new FakeHost(parent());
    const c = await start(host);
    c.jumps.onCommitted({ tabId: 1, frameId: 0, url: "https://site-a.test/", transitionType: "typed", transitionQualifiers: [] });
    await c.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 2, url: "https://site-b.test/x" });
    const page = `${BLOCK}?reason=blocked_jump#https://site-b.test/x`;
    expect(state.tabUpdates).toEqual([{ tabId: 2, url: page }]);
    c.jumps.onCommitted({ tabId: 2, frameId: 0, url: page, transitionType: "link", transitionQualifiers: [] });

    const shown = c.reportBlock("https://site-b.test/x", "blocked_jump", null);
    await settle();
    expect(await shown).toMatchObject({ decision: "block", reason: "blocked_jump", canRequest: false, reported: false });
    const opened = c.overrideJump("https://site-b.test/x", 2, 0, page);
    await settle();
    expect(await opened).toEqual({ ok: true });
    expect(state.tabUpdates.at(-1)).toEqual({ tabId: 2, url: "https://site-b.test/x" });
    expect(sent(host, "reportOverride")).toHaveLength(0);
    expect(sent(host, "reportBlock")).toHaveLength(0);
    expect(sent(host, "decideUrl")).toHaveLength(0);
  });

  it("no searches and no requests, whatever the features say", async () => {
    const host = new FakeHost(parent({ ...PARENT, logSearches: true }));
    const c = await start(host);
    c.noteSearch({ tabId: 1, frameId: 0, url: "https://www.google.com/search?q=anything", transitionType: "typed" });
    await settle();
    expect(sent(host, "reportSearch")).toHaveLength(0);
    expect(await c.requestAccess({ type: "wf:requestAccess", kind: "url", target: "https://adult.test/x" })).toEqual({ result: "unavailable", requestId: null });
    expect(sent(host, "requestAccess")).toHaveLength(0);
    expect(new Set(host.received.map((m) => m.type))).toEqual(new Set(["hello", "getRules", "heartbeat"]));
  });
});

describe("the switches and the mode follow the rules reply", () => {
  it("jump guard off and ads off: the guard does nothing and the ruleset goes off", async () => {
    const host = new FakeHost(parent());
    const c = await start(host);
    c.jumps.onCommitted({ tabId: 1, frameId: 0, url: "https://site-a.test/", transitionType: "typed", transitionQualifiers: [] });
    await c.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 2, url: "about:blank" });
    expect([...state.sessionRules.keys()].some((id) => id >= JUMP_RULE_BASE)).toBe(true);

    await rulesChanged(host, parent({ ...PARENT, jumpGuard: false, blockAds: false }));
    expect(c.features).toMatchObject({ jumpGuard: false, blockAds: false, canOverrideJump: true });
    expect(state.enabledRulesets.has(ADS_RULESET)).toBe(false);
    expect([...state.sessionRules.keys()].some((id) => id >= JUMP_RULE_BASE)).toBe(false);
    state.tabUpdates = [];
    await c.jumps.onCreatedNavigationTarget({ sourceTabId: 1, tabId: 3, url: "https://site-b.test/" });
    expect(state.tabUpdates).toEqual([]);
    expect([...state.dynamicRules.keys()].sort()).toEqual([2001, 2002]); // blockAdult still on
  });

  it("every switch off: the service says unrestricted in the rules reply, and everything is removed without a new hello", async () => {
    const host = new FakeHost(parent());
    const c = await start(host);
    await rulesChanged(host, parent(NO_FEATURES, { mode: "unrestricted", rules: { ...parent().rules!, dynamicRules: [], features: NO_FEATURES, mode: "unrestricted" } }));
    expect(c.mode).toBe("unrestricted");
    expect(state.dynamicRules.size).toBe(0);
    expect(state.enabledRulesets.size).toBe(0);
    expect(c.features).toEqual(NO_FEATURES);
    expect(sent(host, "hello")).toHaveLength(1);

    await rulesChanged(host, parent());
    expect(c.mode).toBe("baseline");
    expect([...state.dynamicRules.keys()].sort()).toEqual([2001, 2002]);
    expect(state.enabledRulesets.has(ADS_RULESET)).toBe(true);
  });

  it("a child's account linked to a parent becomes baseline with the next rules, and stops asking", async () => {
    const child: FakeHostScript = {
      rules: {
        rulesVersion: 3, dynamicRules: [], timeUpSessionRules: [], youTube: { mode: "allowlist", allowed: [], blocked: [] },
        time: { state: "ok", remainingMinutes: 30, windowEndsAt: null }, features: { ...NO_FEATURES, logSearches: true }, mode: "allowlist",
      },
    };
    const host = new FakeHost(child);
    const c = await start(host);
    expect(c.snapshot()).toMatchObject({ mode: "allowlist", canRequest: true });
    await rulesChanged(host, { ...parent(), hello: undefined });
    expect(c.snapshot()).toMatchObject({ mode: "baseline", canRequest: false, time: UNLIMITED });
    const reply = c.reportBlock("https://adult.test/x", null, null);
    await settle();
    expect((await reply).canRequest).toBe(false);
    expect(sent(host, "reportBlock")).toHaveLength(0);
  });

  it("an older service (no mode in the rules reply) keeps the hello's mode", async () => {
    const c = await start(new FakeHost(parent(PARENT, { mode: "" })));
    expect(c.mode).toBe("baseline");
    expect(c.snapshot().canRequest).toBe(false);
  });
});

describe("service down (the native host answers by itself)", () => {
  it("a parent is unrestricted as before: no rules, no features, no ads ruleset, nothing blocked by the extension", async () => {
    const host = new FakeHost({
      hello: { mode: "unrestricted", source: "host", serviceAvailable: false, user: { accountId: "S-1-5-21-parent", name: "dana", role: "admin", isAdmin: true } },
      rules: { rulesVersion: 7, dynamicRules: [], timeUpSessionRules: [], youTube: { mode: "unrestricted", allowed: [], blocked: [] }, time: UNLIMITED },
      hostFallback: true,
    });
    const c = await start(host);
    expect(c.connectionState).toBe("host");
    expect(c.mode).toBe("unrestricted");
    expect(state.dynamicRules.size).toBe(0);
    expect(state.enabledRulesets.size).toBe(0);
    expect(c.features).toEqual(NO_FEATURES);
  });
});
