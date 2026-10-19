import { describe, expect, it } from "vitest";
import { detectBrowser } from "../src/shared/browser";
import { REPLY_TYPES, encodeRequest, isWebUrl, normalizeTime, normalizeYouTube, parseIncoming } from "../src/shared/protocol";

// Frames exactly as WebFilter.Contracts' AgentProtocol.Serialize writes them (captured from the C# serializer):
// `type` first, `v`/`id` last, nulls omitted, YouTube → "youTube".
const CSHARP = {
  hello:
    '{"type":"helloResult","user":{"accountId":"S-1-5-21-1","name":"noa","role":"child","isAdmin":false},"mode":"allowlist","source":"service","serviceAvailable":true,"rulesVersion":7,"serverVersion":"2.0.0","v":1,"id":"1"}',
  rules:
    '{"type":"rules","rulesVersion":7,"dynamicRules":[{"id":1,"priority":1,"action":{"type":"block"},"condition":{"resourceTypes":["main_frame"]}}],"timeUpSessionRules":[],"youTube":{"mode":"allowlist","allowed":[{"channelId":"UCkhanKids00000000000001","handle":"@khankids-sim","title":"Khan Kids"}],"blocked":[]},"time":{"state":"ok","remainingMinutes":45,"windowEndsAt":"19:00","v":1},"v":1,"id":"2"}',
  timeState: '{"type":"timeState","state":"warn","remainingMinutes":5,"v":1}',
  accessDecided: '{"type":"accessDecided","requestId":"ar-1","status":"approved","until":"2026-11-01T16:20:00+02:00","v":1}',
  ack: '{"type":"error","code":"ok","message":"recorded","v":1,"id":"3"}',
  rulesChanged: '{"type":"rulesChanged","rulesVersion":8,"v":1}',
  requestAccess: '{"type":"requestAccessResult","result":"created","requestId":"ar-1","v":1,"id":"4"}',
};

describe("encodeRequest", () => {
  it("puts type first, then v and id (System.Text.Json needs the discriminator first)", () => {
    const wire = encodeRequest({ type: "hello", extVersion: "0.3.0", browser: "edge", extensionId: "abc" }, "x-1");
    expect(Object.keys(wire)).toEqual(["type", "v", "id", "extVersion", "browser", "extensionId"]);
    expect(JSON.stringify(wire)).toBe('{"type":"hello","v":1,"id":"x-1","extVersion":"0.3.0","browser":"edge","extensionId":"abc"}');
  });

  it("drops undefined optional fields", () => {
    const wire = encodeRequest({ type: "requestAccess", kind: "time", minutes: 15, target: undefined, note: undefined }, "7");
    expect(JSON.stringify(wire)).toBe('{"type":"requestAccess","v":1,"id":"7","kind":"time","minutes":15}');
  });

  it("maps every request to the reply types the service sends", () => {
    expect(REPLY_TYPES).toEqual({
      hello: ["helloResult"],
      getRules: ["rules"],
      heartbeat: ["timeState"],
      reportBlock: ["reportBlockResult", "error"],
      requestAccess: ["requestAccessResult"],
      decideUrl: ["decideUrlResult"],
      resolveYouTube: ["resolveYouTubeResult"],
    });
  });
});

describe("parseIncoming", () => {
  it("accepts every C# frame", () => {
    for (const json of Object.values(CSHARP)) expect(parseIncoming(JSON.parse(json))).not.toBeNull();
  });

  it("normalizes rules, reading youTube (C#) and the plan's youtube spelling", () => {
    const rules = parseIncoming(JSON.parse(CSHARP.rules));
    expect(rules).toMatchObject({
      type: "rules",
      id: "2",
      rulesVersion: 7,
      youTube: { mode: "allowlist", allowed: [{ channelId: "UCkhanKids00000000000001", handle: "@khankids-sim", title: "Khan Kids" }], blocked: [] },
      time: { state: "ok", remainingMinutes: 45, windowEndsAt: "19:00" },
    });
    const planSpelling = parseIncoming({ type: "rules", youtube: { mode: "restricted", allowed: [], blocked: [] } });
    expect(planSpelling).toMatchObject({ youTube: { mode: "restricted" }, dynamicRules: [], timeUpSessionRules: [] });
  });

  it("drops unknown or malformed messages", () => {
    expect(parseIncoming(null)).toBeNull();
    expect(parseIncoming("rules")).toBeNull();
    expect(parseIncoming([])).toBeNull();
    expect(parseIncoming({ type: "format-disk" })).toBeNull();
    expect(parseIncoming({ v: 1 })).toBeNull();
  });

  it("fails closed on unknown time states and YouTube modes", () => {
    expect(normalizeTime({ state: "party" }).state).toBe("expired");
    expect(normalizeTime(undefined).state).toBe("unlimited");
    expect(normalizeYouTube({ mode: "kids" }).mode).toBe("off");
    expect(normalizeYouTube(undefined)).toEqual({ mode: "off", allowed: [], blocked: [] });
    expect(normalizeYouTube({ mode: "allowlist", allowed: [{ channelId: 5 }, { channelId: "UCx" }] }).allowed).toEqual([
      { channelId: "UCx", handle: null, title: null },
    ]);
  });
});

describe("helpers", () => {
  it("isWebUrl accepts only http and https", () => {
    expect(isWebUrl("https://a.example/")).toBe(true);
    expect(isWebUrl("HTTP://a.example/")).toBe(true);
    expect(isWebUrl("chrome-extension://id/blocked.html")).toBe(false);
    expect(isWebUrl("javascript:alert(1)")).toBe(false);
    expect(isWebUrl(undefined)).toBe(false);
  });

  it("detectBrowser reads userAgentData brands, then the UA string", () => {
    expect(detectBrowser({ userAgentData: { brands: [{ brand: "Microsoft Edge", version: "156" }, { brand: "Chromium", version: "156" }] } })).toBe("edge");
    expect(detectBrowser({ userAgentData: { brands: [{ brand: "Google Chrome", version: "156" }] } })).toBe("chrome");
    expect(detectBrowser({ userAgent: "Mozilla/5.0 … Chrome/156.0.0.0 Safari/537.36 Edg/156.0.4314.30" })).toBe("edge");
    expect(detectBrowser({ userAgent: "Mozilla/5.0 … Chrome/156.0.0.0 Safari/537.36" })).toBe("chrome");
    expect(detectBrowser(undefined)).toBe("chrome");
  });
});
