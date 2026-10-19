import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Controller } from "../src/background/controller";
import { FakeHost, type FakeHostScript } from "../src/background/fake-host";
import type { Connector, PortLike } from "../src/background/native-client";
import type { YouTubeDetection } from "../src/shared/messages";
import type { DnrRule, OutgoingMessage } from "../src/shared/protocol";
import { type ChromeMockState, MAX_REGEX, installChromeMock } from "./chrome-mock";

const FRAMES = ["main_frame", "sub_frame"] as chrome.declarativeNetRequest.ResourceType[];
const REDIRECT = { type: "redirect", redirect: { regexSubstitution: "chrome-extension://testextensionid/blocked.html#\\0" } } as const;

// A small allowlist policy in the shape of the v2 compiler output.
const DYNAMIC: DnrRule[] = [
  { id: 1, priority: 1, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
  { id: 3, priority: 196608, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["youtube.com", "youtube-nocookie.com"] } },
  {
    id: 4321,
    priority: 65544,
    action: { type: "allow" },
    // A null member, as older services serialized it: must be stripped before DNR sees it.
    condition: { resourceTypes: FRAMES, requestDomains: ["khanacademy.org", "mashov.info"], regexFilter: null as unknown as undefined },
  },
];
const TIME_UP: DnrRule[] = [
  { id: 10, priority: 262144, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
  { id: 9001, priority: 262145, action: { type: "allow" }, condition: { resourceTypes: FRAMES, requestDomains: ["mashov.info"] } },
];
const YOUTUBE = {
  mode: "allowlist" as const,
  allowed: [{ channelId: "UCkhanKids00000000000001", handle: "@khankids-sim", title: "Khan Kids (sim)" }],
  blocked: [],
};

function script(extra: FakeHostScript = {}): FakeHostScript {
  return {
    rules: { rulesVersion: 5, dynamicRules: DYNAMIC, timeUpSessionRules: TIME_UP, youTube: YOUTUBE, time: { state: "ok", remainingMinutes: 60, windowEndsAt: "19:00" } },
    ...extra,
  };
}

/** chrome.runtime.connectNative when no host is registered: the port closes at once with lastError set. */
function missingHost(state: ChromeMockState, counter: { n: number }): Connector {
  return () => {
    counter.n += 1;
    const disconnect: (() => void)[] = [];
    const port: PortLike = {
      postMessage: () => undefined,
      disconnect: () => undefined,
      onMessage: { addListener: () => undefined },
      onDisconnect: { addListener: (cb) => void disconnect.push(cb) },
    };
    setTimeout(() => {
      state.lastError = { message: "Specified native messaging host not found." };
      for (const cb of disconnect) cb();
      state.lastError = undefined;
    }, 5);
    return port;
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

function start(host: FakeHost | Connector, options: { installType?: string } = {}): Controller {
  if (options.installType) state.installType = options.installType;
  controller = new Controller({ connector: host instanceof FakeHost ? host.connector : host, log: () => undefined });
  void controller.start();
  return controller;
}

describe("policy check", () => {
  it("stays inert when not installed by policy: fail-closed off, no native host", async () => {
    const host = new FakeHost(script());
    const c = start(host, { installType: "development" });
    await settle();
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
    expect(host.connections).toBe(0);
    expect(c.snapshot()).toMatchObject({ connection: "inert", policyInstalled: false, installType: "development" });
    expect(state.local["wf.youtube"]).toEqual({ mode: "inert", version: null });
    expect(state.badgeText).toBe("");
  });

  it("an unreadable install type is enforced (fail closed, not open)", async () => {
    (chrome.management as { getSelf: unknown }).getSelf = async () => {
      throw new Error("boom");
    };
    const host = new FakeHost(script());
    const c = start(host);
    await settle(1000);
    expect(c.snapshot().policyInstalled).toBe(true);
    expect(host.connections).toBe(1);
  });
});

describe("no native host (fail-closed)", () => {
  it("keeps failclosed on, clears rules, and retries with backoff 1 s → 30 s", async () => {
    state.dynamicRules.set(77, { id: 77, action: { type: "allow" }, condition: {} }); // left over from a previous session
    const counter = { n: 0 };
    const c = start(missingHost(state, counter));
    await settle(20);
    expect(counter.n).toBe(1);
    expect(state.enabledRulesets.has("failclosed")).toBe(true);
    expect(state.dynamicRules.size).toBe(0);
    expect(c.snapshot()).toMatchObject({ connection: "disconnected", lastError: "Specified native messaging host not found." });
    expect(state.badgeText).toBe("!");
    const at: number[] = [];
    for (let i = 0; i < 7; i++) {
      const before = counter.n;
      let waited = 0;
      while (counter.n === before && waited < 40_000) {
        await settle(100);
        waited += 100;
      }
      at.push(Math.round(waited / 1000));
    }
    expect(at).toEqual([1, 2, 4, 8, 16, 30, 30]);
    expect(state.enabledRulesets.has("failclosed")).toBe(true);
  });

  it("a host that never answers hello times out and is retried", async () => {
    const host = new FakeHost(script({ silent: true }));
    const c = start(host);
    await settle(14_000);
    expect(c.connectionState).toBe("connecting");
    await settle(1_100);
    expect(c.connectionState).toBe("disconnected");
    expect(state.enabledRulesets.has("failclosed")).toBe(true);
    await settle(1_100);
    expect(host.connections).toBe(2);
  });
});

describe("handshake and rules", () => {
  it("hello then getRules, installs the rules, and only then disables failclosed", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    const [hello] = sent(host, "hello");
    expect(Object.keys(hello!).slice(0, 3)).toEqual(["type", "v", "id"]);
    expect(hello).toMatchObject({ type: "hello", v: 1, extVersion: "0.3.0", browser: expect.stringMatching(/^(edge|chrome)$/), extensionId: "testextensionid" });
    expect(sent(host, "getRules")).toHaveLength(1);
    expect([...state.dynamicRules.keys()].sort((a, b) => a - b)).toEqual([1, 3, 4321]);
    expect(state.dynamicRules.get(4321)!.condition).not.toHaveProperty("regexFilter");
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
    expect(state.sessionRules.size).toBe(0);
    expect(c.snapshot()).toMatchObject({ connection: "service", mode: "allowlist", rulesVersion: 5, canRequest: true, timeUp: false });
    expect(state.local["wf.youtube"]).toEqual({ mode: "allowlist", version: 5 });
    expect(sent(host, "heartbeat")[0]).toMatchObject({ focused: true, idle: "active", hasWebTab: false });
  });

  it("host fallback: rules applied, source shown, requests unavailable", async () => {
    const host = new FakeHost(script({ hello: { source: "host", serviceAvailable: false } }));
    const c = start(host);
    await settle();
    expect(c.snapshot()).toMatchObject({ connection: "host", canRequest: false });
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
    expect(await c.requestAccess({ type: "wf:requestAccess", kind: "time", minutes: 15 })).toEqual({ result: "unavailable", requestId: null });
    expect(sent(host, "requestAccess")).toHaveLength(0);
  });

  it("unrestricted: no dynamic or session rules, failclosed off", async () => {
    const host = new FakeHost(script({ hello: { mode: "unrestricted" }, rules: { dynamicRules: DYNAMIC, timeUpSessionRules: TIME_UP, time: { state: "expired" } } }));
    const c = start(host);
    await settle();
    expect(state.dynamicRules.size).toBe(0);
    expect(state.sessionRules.size).toBe(0);
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
    expect(c.snapshot()).toMatchObject({ mode: "unrestricted", youTubeMode: "unrestricted", timeUp: false });
  });

  it("reassembles chunked replies", async () => {
    const big = Array.from({ length: 2000 }, (_, i) => `site${i}.example`);
    const rules = [...DYNAMIC, { id: 5000, priority: 65545, action: REDIRECT, condition: { resourceTypes: FRAMES, requestDomains: big } }];
    const host = new FakeHost(script({ chunkBytes: 4096, rules: { ...script().rules, dynamicRules: rules } }));
    const c = start(host);
    await settle();
    expect(c.ready).toBe(true);
    expect(state.dynamicRules.get(5000)!.condition.requestDomains).toHaveLength(2000);
  });

  it("rules the browser cannot install keep failclosed on and retry", async () => {
    const duplicate = [...DYNAMIC, { ...DYNAMIC[0]!, condition: { resourceTypes: FRAMES } }];
    const host = new FakeHost(script({ rules: { ...script().rules, dynamicRules: duplicate } }));
    // planRuleUpdate de-duplicates ids, so force a failure another way: the update itself throws.
    const update = chrome.declarativeNetRequest.updateDynamicRules;
    (chrome.declarativeNetRequest as { updateDynamicRules: unknown }).updateDynamicRules = async (u: { addRules?: DnrRule[] }) => {
      if ((u.addRules ?? []).length > 0) throw new Error("Dynamic rule count exceeded");
      return update(u);
    };
    const c = start(host);
    await settle();
    expect(state.enabledRulesets.has("failclosed")).toBe(true);
    expect(c.connectionState).toBe("disconnected");
    expect(c.snapshot().lastError).toMatch(/Dynamic rule count exceeded/);
  });

  it("drops a regex the browser refuses (too big for RE2), installs the rest once more, and reports the ids", async () => {
    const huge: DnrRule = { id: 6000, priority: 65539, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: `^https?://x/(${"a|".repeat(MAX_REGEX)}b)` } };
    const host = new FakeHost(script({ rules: { ...script().rules, dynamicRules: [...DYNAMIC, huge] } }));
    const c = start(host);
    await settle();
    expect(c.ready).toBe(true);
    expect([...state.dynamicRules.keys()].sort((a, b) => a - b)).toEqual([1, 3, 4321]);
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
    const report = sent(host, "error")[0];
    expect(report).toMatchObject({ type: "error", code: "dnr_rules_dropped" });
    expect((report as { message: string }).message).toContain("6000");
    expect(c.snapshot().lastError).toContain("6000");
  });

  it("a failed refresh keeps the previous rules", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    const before = [...state.dynamicRules.keys()];
    (chrome.declarativeNetRequest as { updateDynamicRules: unknown }).updateDynamicRules = async () => {
      throw new Error("Internal error");
    };
    host.script = script({ rules: { ...script().rules, rulesVersion: 6, dynamicRules: [DYNAMIC[0]!] } });
    host.push({ type: "rulesChanged", rulesVersion: 6 });
    await settle();
    expect([...state.dynamicRules.keys()]).toEqual(before);
    expect(c.snapshot().rulesVersion).toBe(5);
    expect(c.ready).toBe(true);
  });
});

describe("pushes", () => {
  it("rulesChanged fetches the rules again and installs only what changed", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    const updates = state.dynamicUpdates;
    host.script = script({ rules: { ...script().rules, rulesVersion: 6, dynamicRules: [DYNAMIC[0]!, DYNAMIC[1]!] } });
    host.push({ type: "rulesChanged", rulesVersion: 6 });
    await settle();
    expect(sent(host, "getRules")).toHaveLength(2);
    expect([...state.dynamicRules.keys()].sort((a, b) => a - b)).toEqual([1, 3]);
    expect(state.dynamicUpdates).toBe(updates + 1);
    expect(c.snapshot().rulesVersion).toBe(6);
    // The same rules again: nothing to update.
    host.push({ type: "rulesChanged", rulesVersion: 6 });
    await settle();
    expect(state.dynamicUpdates).toBe(updates + 1);
  });

  it("time up: session rules on, open tabs to timeup.html except always-allowed; back: rules off", async () => {
    state.tabs = [
      { id: 1, url: "https://www.khanacademy.org/math", active: true },
      { id: 2, url: "https://mashov.info/grades" },
      { id: 3, url: "chrome-extension://testextensionid/blocked.html#https://x.example/" },
    ];
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    host.push({ type: "timeState", state: "expired", remainingMinutes: 0, windowEndsAt: "19:00" });
    await settle();
    expect([...state.sessionRules.keys()].sort((a, b) => a - b)).toEqual([10, 9001]);
    expect(state.tabUpdates).toEqual([{ tabId: 1, url: "chrome-extension://testextensionid/timeup.html#https://www.khanacademy.org/math" }]);
    expect(c.snapshot()).toMatchObject({ timeUp: true, inferredReason: "blocked_daily_limit" });
    expect(state.badgeText).toBe("0");
    expect(state.broadcasts.some((b) => (b as { type: string; status?: { timeUp: boolean } }).type === "wf:stateChanged" && (b as { status: { timeUp: boolean } }).status.timeUp)).toBe(true);

    host.push({ type: "timeState", state: "ok", remainingMinutes: 15, windowEndsAt: "19:00" });
    await settle();
    expect(state.sessionRules.size).toBe(0);
    expect(c.snapshot().timeUp).toBe(false);
    expect(state.tabUpdates).toHaveLength(1);
  });

  it("time up on connect installs the session rules straight away", async () => {
    const host = new FakeHost(script({ rules: { ...script().rules, time: { state: "outside_window", remainingMinutes: 100 } } }));
    const c = start(host);
    await settle();
    expect(state.sessionRules.size).toBe(2);
    expect(c.snapshot().inferredReason).toBe("blocked_time_window");
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
  });

  it("warns at 10/5/1 minutes once each", async () => {
    const host = new FakeHost(script());
    start(host);
    await settle();
    for (const m of [11, 10, 10, 9, 5, 1]) {
      host.push({ type: "timeState", state: m <= 10 ? "warn" : "ok", remainingMinutes: m });
      await settle();
    }
    expect(state.notifications.map((n) => n.message)).toEqual(["notify10", "notify5", "notify1"]);
    expect(state.badgeText).toBe("1");
  });

  it("accessDecided approved: rules are refreshed before the pages hear about it", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    const reply = await (async () => {
      const p = c.requestAccess({ type: "wf:requestAccess", kind: "url", target: "https://pbskids.org/games", note: " for school " });
      await settle();
      return p;
    })();
    expect(reply).toEqual({ result: "created", requestId: "ar-1" });
    expect(sent(host, "requestAccess")[0]).toMatchObject({ kind: "url", target: "https://pbskids.org/games", note: "for school" });
    expect(c.decisionOf("ar-1")).toEqual({ status: "pending" });
    state.broadcasts = [];
    host.push({ type: "accessDecided", requestId: "ar-1", status: "approved" });
    await settle();
    expect(sent(host, "getRules")).toHaveLength(2);
    const types = state.broadcasts.map((b) => (b as { type: string }).type);
    expect(types).toContain("wf:accessDecided");
    expect(types.indexOf("wf:stateChanged")).toBeLessThan(types.indexOf("wf:accessDecided"));
    expect(c.decisionOf("ar-1")).toEqual({ status: "approved" });
    expect(c.decisionOf("nope")).toEqual({ status: "unknown" });
  });

  it("port closed: failclosed on, rules cleared, reconnects after 1 s", async () => {
    state.tabs = [{ id: 1, url: "https://www.khanacademy.org/", active: true }];
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    host.push({ type: "timeState", state: "expired", remainingMinutes: 0 });
    await settle();
    expect(state.sessionRules.size).toBe(2);
    host.hangUp();
    await settle(10);
    expect(c.connectionState).toBe("disconnected");
    expect(state.enabledRulesets.has("failclosed")).toBe(true);
    expect(state.dynamicRules.size).toBe(0);
    expect(state.sessionRules.size).toBe(0);
    expect(state.local["wf.youtube"]).toEqual({ mode: "failclosed", version: null });
    await settle(1_100);
    expect(host.connections).toBe(2);
    expect(c.connectionState).toBe("service");
    expect(state.enabledRulesets.has("failclosed")).toBe(false);
  });
});

describe("block page reasons (reportBlock / decideUrl)", () => {
  it("uses the service's decision: reason, category, request kind", async () => {
    const host = new FakeHost(script({ decide: { "https://www.instagram.com/": { reason: "blocked_category", category: "social" } } }));
    const c = start(host);
    await settle();
    const p = c.reportBlock("https://www.instagram.com/", null, null);
    await settle();
    expect(await p).toEqual({ decision: "block", reason: "blocked_category", source: "service", category: "social", canRequest: true, requestKind: "url", reported: true });
    // The guess sent along is the local inference (allowlist default rule).
    expect(sent(host, "reportBlock")[0]).toMatchObject({ url: "https://www.instagram.com/", reason: "blocked_not_in_allowlist" });
  });

  it("a reload asks with decideUrl and records nothing", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    const p = c.reportBlock("https://x.example/", null, null, "main_frame", true);
    await settle();
    expect(await p).toMatchObject({ source: "service", reported: false });
    expect(sent(host, "decideUrl")).toEqual([expect.objectContaining({ url: "https://x.example/" })]);
    expect(sent(host, "reportBlock")).toHaveLength(0);
  });

  it("decision allow means stale rules: nothing reported", async () => {
    const host = new FakeHost(script({ decide: { "https://ok.example/": { decision: "allow", reason: "allowed_rule", canRequest: false } } }));
    const c = start(host);
    await settle();
    const p = c.reportBlock("https://ok.example/", null, null);
    await settle();
    expect(await p).toMatchObject({ decision: "allow", reason: "allowed_rule", reported: false });
  });

  it("YouTube reasons from the page are sent as they are", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    const p = c.reportBlock("https://www.youtube.com/watch?v=x", "blocked_youtube_channel", null);
    await settle();
    expect(await p).toMatchObject({ reason: "blocked_youtube_channel", requestKind: "youtube" });
    expect(sent(host, "reportBlock")[0]).toMatchObject({ reason: "blocked_youtube_channel" });
    // A reload keeps the page's channel reason; decideUrl would judge only the YouTube host.
    expect(await c.reportBlock("https://www.youtube.com/watch?v=x", "blocked_youtube_channel", null, "main_frame", true)).toMatchObject({
      reason: "blocked_youtube_channel",
      source: "page",
      canRequest: true,
      requestKind: "youtube",
    });
    expect(sent(host, "decideUrl")).toHaveLength(0);
  });

  it("host fallback answers service_unavailable", async () => {
    const host = new FakeHost(script({ hostFallback: true, hello: { source: "host" } }));
    const c = start(host);
    await settle();
    const p = c.reportBlock("https://x.example/", null, null);
    await settle();
    expect(await p).toMatchObject({ reason: "blocked_service_unavailable", source: "host", canRequest: false, requestKind: null });
  });

  it("an older service (error ok) or no service: the local inference is shown", async () => {
    const host = new FakeHost(script({ legacyReportAck: true }));
    const c = start(host);
    await settle();
    const p = c.reportBlock("https://unknown.example/", null, null);
    await settle();
    expect(await p).toMatchObject({ reason: "blocked_not_in_allowlist", source: "local", reported: true });
    host.hangUp();
    await settle(10);
    expect(await c.reportBlock("https://unknown.example/", null, null)).toMatchObject({ source: "local", canRequest: false, reported: false });
    expect(await c.reportBlock("", "failclosed", null)).toMatchObject({ reason: "failclosed", canRequest: false, requestKind: null });
  });
});

describe("requests from pages", () => {
  it("validates targets and maps time minutes", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    expect(await c.requestAccess({ type: "wf:requestAccess", kind: "url", target: "javascript:alert(1)" })).toEqual({ result: "error", requestId: null });
    expect(await c.requestAccess({ type: "wf:requestAccess", kind: "youtube", target: "not-a-channel" })).toEqual({ result: "error", requestId: null });
    expect(await c.requestAccess({ type: "wf:requestAccess", kind: "bogus" as "url" })).toEqual({ result: "error", requestId: null });
    const p = c.requestAccess({ type: "wf:requestAccess", kind: "time", minutes: 45, note: "" });
    await settle();
    await p;
    expect(sent(host, "requestAccess")).toEqual([expect.objectContaining({ kind: "time", minutes: 15 })]);
    expect(sent(host, "requestAccess")[0]).not.toHaveProperty("note");
  });

  it("remembers what each frame tried to load (for the fail-closed page)", async () => {
    const c = start(new FakeHost(script()));
    c.noteNavigation(4, 0, "https://a.example/x");
    c.noteNavigation(4, 7, "https://frame.example/");
    c.noteNavigation(4, 0, "chrome-extension://testextensionid/blocked.html?reason=failclosed");
    expect(c.originalUrlFor(4, 0)).toBe("https://a.example/x");
    expect(c.originalUrlFor(4, 7)).toBe("https://frame.example/");
    c.forgetTab(4);
    expect(c.originalUrlFor(4, 0)).toBeNull();
  });
});

describe("in-page (history API) navigations", () => {
  it("sends a top frame to the block page when the rules would block the new address", async () => {
    const c = start(new FakeHost(script()));
    await settle();
    expect(await c.checkInPageNavigation(7, 0, "https://www.khanacademy.org/other")).toBe(false);
    expect(await c.checkInPageNavigation(7, 3, "https://elsewhere.example/")).toBe(false); // sub-frame
    expect(await c.checkInPageNavigation(7, 0, "https://elsewhere.example/x")).toBe(true);
    expect(state.tabUpdates).toEqual([{ tabId: 7, url: "chrome-extension://testextensionid/blocked.html#https://elsewhere.example/x" }]);
  });

  it("does nothing for unrestricted users or before the rules arrive", async () => {
    const c = start(new FakeHost(script({ hello: { mode: "unrestricted" } })));
    expect(await c.checkInPageNavigation(7, 0, "https://elsewhere.example/x")).toBe(false);
    await settle();
    expect(await c.checkInPageNavigation(7, 0, "https://elsewhere.example/x")).toBe(false);
    expect(state.tabUpdates).toHaveLength(0);
  });
});

describe("decision notifications", () => {
  it("notifies only for requests sent from this browser", async () => {
    const host = new FakeHost(script());
    const c = start(host);
    await settle();
    const p = c.requestAccess({ type: "wf:requestAccess", kind: "time", minutes: 30 });
    await settle();
    await p;
    host.push({ type: "accessDecided", requestId: "ar-1", status: "denied" });
    host.push({ type: "accessDecided", requestId: "other-device", status: "approved" });
    await settle();
    expect(state.notifications.map((n) => n.message)).toEqual(["notifyDenied"]);
  });
});

describe("YouTube detections", () => {
  const detection = (url: string, channelId: string | null = null): YouTubeDetection => ({
    url,
    kind: "video",
    videoId: "v",
    handle: null,
    channelId,
    via: channelId ? "player:movie_player" : null,
    trigger: "load",
    timedOut: !channelId,
    ms: 1,
  });

  it("allows approved channels, blocks others with a block-page URL, redirects home to my channels", async () => {
    const c = start(new FakeHost(script()));
    await settle();
    const url = "https://www.youtube.com/watch?v=v";
    expect(await c.decideYouTubeDetection(detection(url, "UCkhanKids00000000000001"), url)).toMatchObject({ decision: "allow", reason: "allowed_youtube_channel" });
    expect(await c.decideYouTubeDetection(detection(url, "UCgamerX0000000000000003"), url)).toEqual({
      decision: "block",
      reason: "blocked_youtube_channel",
      channelId: "UCgamerX0000000000000003",
      navigateTo: `chrome-extension://testextensionid/blocked.html?reason=blocked_youtube_channel&channel=UCgamerX0000000000000003#${url}`,
    });
    expect(await c.decideYouTubeDetection(detection(url), url)).toMatchObject({ decision: "block", reason: "blocked_youtube_unknown" });
    expect(await c.decideYouTubeDetection({ ...detection("https://www.youtube.com/"), kind: "home" }, "https://www.youtube.com/")).toEqual({
      decision: "redirect",
      reason: "redirect_youtube_home",
      navigateTo: "chrome-extension://testextensionid/my-channels.html",
    });
    // SPA: the detector's path wins when the origin matches the frame's.
    expect(await c.decideYouTubeDetection(detection("https://www.youtube.com/watch?v=v2", "UCkhanKids00000000000001"), url)).toMatchObject({ decision: "allow" });
    // A frame that is not YouTube is ignored.
    expect(await c.decideYouTubeDetection(detection(url), "https://evil.example/")).toEqual({ decision: "stale" });
    expect(c.myChannels()).toEqual({ ready: true, mode: "allowlist", channels: YOUTUBE.allowed });
  });

  it("disconnected → blocked (service unavailable); inert → allowed", async () => {
    const counter = { n: 0 };
    const c = start(missingHost(state, counter));
    await settle(20);
    const url = "https://www.youtube.com/watch?v=v";
    expect(await c.decideYouTubeDetection(detection(url, "UCkhanKids00000000000001"), url)).toMatchObject({ decision: "block", reason: "blocked_service_unavailable" });
    controller?.client.close("x");
    state.installType = "normal";
    const inert = start(new FakeHost(script()));
    await settle();
    expect(await inert.decideYouTubeDetection(detection(url, "UCgamerX0000000000000003"), url)).toMatchObject({ decision: "allow" });
  });
});
