import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Controller } from "../src/background/controller";
import { FakeHost, type FakeHostScript } from "../src/background/fake-host";
import {
  CHANNEL_SEARCH_HIDE_CSS,
  CHANNEL_SEARCH_HIDE_SELECTORS,
  channelMark,
} from "../src/shared/channel-search";
import type { YouTubeDetection } from "../src/shared/messages";
import { buildBlockPageUrl, parseBlockPageUrl } from "../src/shared/pages";
import { NO_FEATURES, type OutgoingMessage } from "../src/shared/protocol";
import { channelLabel, cleanHandle, cleanSearchQuery, cleanTitle } from "../src/shared/request-names";
import { channelSearchUrl } from "../src/shared/youtube-url";
import { type ChromeMockState, installChromeMock } from "./chrome-mock";

/**
 * PROTECTION-SPEC §15 in the extension: channel search (the feature, the stored state the bridge reads, the decision's
 * targets, "my channels", the inline request from the results) and a channel's names (block page, requests).
 */

const KHAN = "UCkhanKids00000000000001";
const NATGEO = "UCnatgeoKids000000000004";
const YOUTUBE = {
  mode: "allowlist" as const,
  allowed: [{ channelId: KHAN, handle: "@khankids-sim", title: "Khan Kids (sim)" }],
  blocked: [],
};
const RESULTS = "https://www.youtube.com/results?search_query=space&sp=EgIQAg%253D%253D";

function script(features: Partial<typeof NO_FEATURES> = { youTubeChannelSearch: true }, extra: FakeHostScript = {}): FakeHostScript {
  return {
    rules: {
      rulesVersion: 5,
      dynamicRules: [],
      timeUpSessionRules: [],
      youTube: YOUTUBE,
      time: { state: "ok", remainingMinutes: 60, windowEndsAt: "19:00" },
      features: { ...NO_FEATURES, ...features },
    },
    ...extra,
  };
}

const detection = (url: string, extra: Partial<YouTubeDetection> = {}): YouTubeDetection => ({
  url,
  kind: "video",
  videoId: "v",
  handle: null,
  channelId: null,
  via: null,
  trigger: "load",
  timedOut: false,
  ms: 1,
  ...extra,
});
const sent = (host: FakeHost, type: string): OutgoingMessage[] => host.received.filter((m) => m.type === type);
const settle = (ms = 50) => vi.advanceTimersByTimeAsync(ms);
/** A request to the fake host: its reply arrives on a timer. */
async function answered<T>(request: Promise<T>): Promise<T> {
  await settle();
  return request;
}

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

async function started(host: FakeHost): Promise<Controller> {
  controller = new Controller({ connector: host.connector, log: () => undefined });
  void controller.start();
  await settle();
  return controller;
}

describe("channel search: the feature and what the bridge reads", () => {
  it("features.youTubeChannelSearch turns it on in the allowlist mode: stored for the bridge, offered by my channels", async () => {
    const c = await started(new FakeHost(script()));
    expect(c.snapshot().features.youTubeChannelSearch).toBe(true);
    expect(state.local["wf.youtube"]).toEqual({ mode: "allowlist", version: 5, channelSearch: true });
    expect(c.myChannels()).toMatchObject({ channelSearch: true, channels: YOUTUBE.allowed });
  });

  it("off without the feature, in another YouTube mode, and for a parent", async () => {
    expect((await started(new FakeHost(script({})))).myChannels().channelSearch).toBe(false);
    expect(state.local["wf.youtube"]).toEqual({ mode: "allowlist", version: 5 });
    controller?.client.close("next");

    const restricted = script();
    restricted.rules = { ...restricted.rules!, youTube: { ...YOUTUBE, mode: "restricted" } };
    expect((await started(new FakeHost(restricted))).myChannels().channelSearch).toBe(false);
    expect(state.local["wf.youtube"]).toEqual({ mode: "restricted", version: 5 });
    controller?.client.close("next");

    const parent = script({ youTubeChannelSearch: true }, { hello: { mode: "unrestricted" } });
    expect((await started(new FakeHost(parent))).myChannels().channelSearch).toBe(false);
  });
});

describe("channel search: decisions and where they lead", () => {
  it("the channel-only results are allowed with the approved channels and whether to offer requests", async () => {
    const c = await started(new FakeHost(script()));
    expect(await c.decideYouTubeDetection(detection(RESULTS, { kind: "search" }), RESULTS)).toEqual({
      decision: "allow",
      reason: "allowed_default",
      channelSearch: { approved: [KHAN], canRequest: true },
    });
  });

  it("another search goes to the channel-only results on the page's own origin; home goes to my channels with it", async () => {
    const c = await started(new FakeHost(script()));
    const plain = "https://www.youtube.com:18443/results?search_query=space+cats";
    expect(await c.decideYouTubeDetection(detection(plain, { kind: "search" }), plain)).toMatchObject({
      decision: "redirect",
      reason: "redirect_youtube_channel_search",
      navigateTo: "https://www.youtube.com:18443/results?search_query=space%20cats&sp=EgIQAg%253D%253D",
    });
    const home = "https://www.youtube.com:18443/";
    expect(await c.decideYouTubeDetection(detection(home, { kind: "home" }), home)).toMatchObject({
      reason: "redirect_youtube_home",
      navigateTo: "chrome-extension://testextensionid/my-channels.html#https://www.youtube.com:18443",
    });
  });

  it("a blocked channel's block page carries its names and the search it came from — names only for the detected channel", async () => {
    const c = await started(new FakeHost(script()));
    const page = "https://www.youtube.com/@natgeokids-sim";
    const reply = await c.decideYouTubeDetection(
      detection(page, { kind: "handle", channelId: NATGEO, channelTitle: " NatGeo‮ Kids ", channelHandle: "@NatGeoKids-Sim", searchQuery: "space" }),
      page,
    );
    const info = parseBlockPageUrl((reply as { navigateTo: string }).navigateTo);
    expect(info).toMatchObject({ reason: "blocked_youtube_channel", channelId: NATGEO, title: "NatGeo Kids", handle: "@natgeokids-sim", searchQuery: "space", url: page });

    // A channel the decision found by its stored handle, not the page's id: no names from the page.
    const handleOnly = await c.decideYouTubeDetection(detection(page, { kind: "handle", handle: "@unknown", channelTitle: "Other" }), page);
    expect(parseBlockPageUrl((handleOnly as { navigateTo: string }).navigateTo)).toMatchObject({ title: null, handle: null });
  });
});

describe("channel search: asking from the results", () => {
  it("the bridge's request names the channel and takes the search from the page, not the message", async () => {
    const host = new FakeHost(script());
    const c = await started(host);

    const reply = await answered(c.requestChannelFromSearch({ type: "wf:ytRequestChannel", channelId: NATGEO, title: "NatGeo Kids", handle: "@NatGeoKids" }, RESULTS, 0));

    expect(reply).toEqual({ result: "created", requestId: "ar-1" });
    expect(sent(host, "requestAccess")).toMatchObject([{ kind: "youtube", target: NATGEO, title: "NatGeo Kids", handle: "@natgeokids", searchQuery: "space" }]);
  });

  it("refused outside the channel-only results, from a frame, for a bad id, or without the feature", async () => {
    const host = new FakeHost(script());
    const c = await started(host);
    const ask = (url: string, frameId = 0, channelId = NATGEO) => c.requestChannelFromSearch({ type: "wf:ytRequestChannel", channelId }, url, frameId);

    expect(await ask("https://www.youtube.com/results?search_query=space")).toEqual({ result: "error", requestId: null });
    expect(await ask("https://www.youtube.com/@natgeokids-sim")).toEqual({ result: "error", requestId: null });
    expect(await ask(RESULTS, 3)).toEqual({ result: "error", requestId: null });
    expect(await ask(RESULTS, 0, "UC-not-an-id")).toEqual({ result: "error", requestId: null });
    expect(sent(host, "requestAccess")).toHaveLength(0);
    controller?.client.close("next");

    const off = await started(new FakeHost(script({})));
    expect(await off.requestChannelFromSearch({ type: "wf:ytRequestChannel", channelId: NATGEO }, RESULTS, 0)).toEqual({ result: "error", requestId: null });
  });

  it("a block page's request carries the names, cleaned; other kinds carry none", async () => {
    const host = new FakeHost(script());
    const c = await started(host);
    await answered(c.requestAccess({ type: "wf:requestAccess", kind: "youtube", target: NATGEO, title: "  NatGeo  Kids ", handle: "natgeokids", searchQuery: " space " }));
    await answered(c.requestAccess({ type: "wf:requestAccess", kind: "youtube", target: KHAN, title: "   ", handle: "@bad handle" }));
    await answered(c.requestAccess({ type: "wf:requestAccess", kind: "url", target: "https://a.example/", title: "A", handle: "@a" }));

    const [named, empty, url] = sent(host, "requestAccess");
    expect(named).toMatchObject({ title: "NatGeo Kids", handle: "@natgeokids", searchQuery: "space" });
    expect(empty).not.toHaveProperty("title");
    expect(empty).not.toHaveProperty("handle");
    expect(url).not.toHaveProperty("title");
  });
});

describe("channel search: the results page's marks and hiding", () => {
  it("approved, a sent request, the button, or nothing", () => {
    const approved = new Set([KHAN]);
    const asked = new Map([[NATGEO, "created"]]);
    expect(channelMark(KHAN, approved, asked, true)).toBe("approved");
    expect(channelMark(NATGEO, approved, asked, true)).toBe("sent");
    expect(channelMark("UCother0000000000000000000", approved, asked, true)).toBe("ask");
    expect(channelMark("UCother0000000000000000000", approved, asked, false)).toBe("none");
  });

  it("hides everything but channels, one rule per selector", () => {
    const lines = CHANNEL_SEARCH_HIDE_CSS.split("\n");
    expect(lines).toHaveLength(CHANNEL_SEARCH_HIDE_SELECTORS.length);
    for (const line of lines) expect(line).toMatch(/ \{ display: none !important; \}$/);
    expect(CHANNEL_SEARCH_HIDE_SELECTORS.some((s) => s.includes(":not(ytd-channel-renderer)"))).toBe(true);
  });
});

describe("a channel's names", () => {
  it("are cleaned like the service's RequestNames", () => {
    expect(cleanTitle("  NASA  ")).toBe("NASA");
    expect(cleanTitle("Kids‮ TV\nnews\t ")).toBe("Kids TV news");
    expect(cleanTitle("👨‍👩‍👧 Family")).toBe("👨‍👩‍👧 Family");
    expect(cleanTitle("   ")).toBeUndefined();
    expect(cleanTitle(5)).toBeUndefined();
    expect(cleanTitle("a".repeat(300))).toHaveLength(100);
    expect(cleanSearchQuery("q".repeat(300))).toHaveLength(200);
    expect(cleanHandle("@NASA")).toBe("@nasa");
    expect(cleanHandle("NatGeoKids-Sim")).toBe("@natgeokids-sim");
    expect(cleanHandle("@ערוץ_הילדים")).toBe("@ערוץ_הילדים");
    expect(cleanHandle("@with space")).toBeUndefined();
    expect(cleanHandle("@x/y")).toBeUndefined();
    expect(cleanHandle("@")).toBeUndefined();
  });

  it("are shown as \"NASA (@nasa)\", or one of them", () => {
    expect(channelLabel("NASA", "@nasa")).toBe("NASA (@nasa)");
    expect(channelLabel("NASA", null)).toBe("NASA");
    expect(channelLabel(" ", "@nasa")).toBe("@nasa");
    expect(channelLabel(null, undefined)).toBeUndefined();
  });

  it("the channel search URL is YouTube's own Channels filter", () => {
    expect(channelSearchUrl("space")).toBe(RESULTS);
    expect(buildBlockPageUrl("chrome-extension://x/blocked.html", { reason: "blocked_youtube_channel", url: "https://www.youtube.com/@a", channelId: KHAN, title: "A B" }))
      .toBe(`chrome-extension://x/blocked.html?reason=blocked_youtube_channel&channel=${KHAN}&title=A+B#https://www.youtube.com/@a`);
  });
});
