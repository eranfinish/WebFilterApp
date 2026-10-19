import { describe, expect, it } from "vitest";
import { canonicalJson, classifyNavigation, planRuleUpdate, ruleMatches, stripNulls, urlFilterToRegex, winningRule, wouldBlock } from "../src/shared/dnr-match";
import { explainBlock } from "../src/shared/explain";
import type { DnrRule } from "../src/shared/protocol";

const FRAMES = ["main_frame", "sub_frame"] as chrome.declarativeNetRequest.ResourceType[];
const REDIRECT = { type: "redirect", redirect: { regexSubstitution: "chrome-extension://id/blocked.html#\\0" } } as const;
const ALLOW = { type: "allow" } as const;

/** Rules shaped like the v2 DnrCompiler output (DnrPriorities / DnrRuleIds layout). */
const allowlistPolicy: DnrRule[] = [
  { id: 1, priority: 1, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
  { id: 3, priority: 196608, action: ALLOW, condition: { resourceTypes: FRAMES, requestDomains: ["youtube-nocookie.com", "youtube.com"] } },
  { id: 4321, priority: 65536 + 2 * 4, action: ALLOW, condition: { resourceTypes: FRAMES, requestDomains: ["khanacademy.org", "pbskids.org"], isUrlFilterCaseSensitive: false } },
  { id: 5555, priority: 131072 + 2 * 5 + 1, action: REDIRECT, condition: { resourceTypes: FRAMES, requestDomains: ["games.pbskids.org"] } },
];

const blocklistPolicy: DnrRule[] = [
  { id: 2, priority: 2, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^[^:]+://(?:[^/@]*@)?(?:[0-9]+(?:\\.[0-9]+){3}|\\[[0-9a-f:.]*\\])(?::[0-9]*)?/.*" } },
  { id: 3, priority: 196608, action: REDIRECT, condition: { resourceTypes: FRAMES, requestDomains: ["youtube-nocookie.com", "youtube.com"], regexFilter: "^(.*)$" } },
  { id: 4, priority: 196608, action: REDIRECT, condition: { resourceTypes: FRAMES, requestDomains: ["youtu.be"] } },
  { id: 7777, priority: 65536 + 2 * 4 + 1, action: REDIRECT, condition: { resourceTypes: FRAMES, requestDomains: ["instagram.com", "tiktok.com"] } },
  { id: 8888, priority: 65536 + 2 * 1 + 1, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^.*?(?:casino).*", isUrlFilterCaseSensitive: false } },
];

const timeUp: DnrRule[] = [
  { id: 10, priority: 262144, action: REDIRECT, condition: { resourceTypes: FRAMES, regexFilter: "^(https?://.*)$" } },
  { id: 9001, priority: 262145, action: ALLOW, condition: { resourceTypes: FRAMES, requestDomains: ["mashov.info"] } },
];

describe("ruleMatches / winningRule", () => {
  it("matches requestDomains on the domain and its subdomains only", () => {
    const rule = blocklistPolicy[3]!;
    expect(ruleMatches(rule, "https://www.instagram.com/x")).toBe(true);
    expect(ruleMatches(rule, "https://instagram.com/")).toBe(true);
    expect(ruleMatches(rule, "https://instagram.com.evil.net/")).toBe(false);
    expect(ruleMatches(rule, "https://notinstagram.com/")).toBe(false);
  });

  it("honors resource types (no resourceTypes = everything but main_frame)", () => {
    const rule: DnrRule = { id: 1, action: { type: "block" }, condition: {} };
    expect(ruleMatches(rule, "https://a.example/", "main_frame")).toBe(false);
    expect(ruleMatches(rule, "https://a.example/", "sub_frame")).toBe(true);
    expect(ruleMatches({ ...rule, condition: { excludedResourceTypes: ["sub_frame" as chrome.declarativeNetRequest.ResourceType] } }, "https://a/", "sub_frame")).toBe(false);
  });

  it("applies regexFilter case-insensitively unless isUrlFilterCaseSensitive is true", () => {
    expect(ruleMatches(blocklistPolicy[4]!, "https://games.example/CASINO-royal")).toBe(true);
    const sensitive = { ...blocklistPolicy[4]!, condition: { ...blocklistPolicy[4]!.condition, isUrlFilterCaseSensitive: true } };
    expect(ruleMatches(sensitive, "https://games.example/CASINO-royal")).toBe(false);
    expect(ruleMatches(sensitive, "https://games.example/casino-royal")).toBe(true);
    const broken: DnrRule = { id: 9, action: { type: "block" }, condition: { resourceTypes: FRAMES, regexFilter: "(" } };
    expect(ruleMatches(broken, "https://a/")).toBe(false);
  });

  it("converts urlFilter syntax", () => {
    const re = (f: string) => new RegExp(urlFilterToRegex(f), "i");
    expect(re("||example.com^").test("https://sub.example.com/path")).toBe(true);
    expect(re("||example.com^").test("https://example.com")).toBe(true);
    expect(re("||example.com^").test("https://notexample.com/")).toBe(false);
    expect(re("|https://a.test/x|").test("https://a.test/x")).toBe(true);
    expect(re("|https://a.test/x|").test("https://a.test/xy")).toBe(false);
    expect(re("ads*.js").test("https://cdn.test/ads/1.js")).toBe(true);
  });

  it("picks the highest priority, and allow over redirect at equal priority", () => {
    expect(winningRule(allowlistPolicy, "https://www.khanacademy.org/math")?.id).toBe(4321);
    expect(winningRule(allowlistPolicy, "https://games.pbskids.org/")?.id).toBe(5555);
    expect(winningRule(allowlistPolicy, "https://unknown.example/")?.id).toBe(1);
    const tie: DnrRule[] = [
      { id: 1, priority: 5, action: REDIRECT, condition: { resourceTypes: FRAMES } },
      { id: 2, priority: 5, action: ALLOW, condition: { resourceTypes: FRAMES } },
    ];
    expect(winningRule(tie, "https://a.example/")?.id).toBe(2);
  });

  it("wouldBlock: time-up rules keep always-allowed sites", () => {
    expect(wouldBlock(timeUp, "https://www.mashov.info/grades")).toBe(false);
    expect(wouldBlock(timeUp, "https://www.instagram.com/")).toBe(true);
    expect(wouldBlock([], "https://www.instagram.com/")).toBe(false);
  });

  it("classifyNavigation refuses to decide when an applicable rule cannot be evaluated here", () => {
    expect(classifyNavigation(allowlistPolicy, "https://unknown.example/")).toBe("block");
    expect(classifyNavigation(allowlistPolicy, "https://www.khanacademy.org/")).toBe("allow");
    // An allow rule written in RE2-only syntax: JS cannot compile it, so the outcome is unknown, never a false block.
    const re2Only: DnrRule = { id: 50, priority: 65540, action: ALLOW, condition: { resourceTypes: FRAMES, regexFilter: "(?i)^https://docs\.example/" } };
    expect(classifyNavigation([...allowlistPolicy, re2Only], "https://docs.example/a")).toBe("unknown");
    // Scoped to other domains: it does not apply here, so the decision stands.
    const scoped = { ...re2Only, condition: { ...re2Only.condition, requestDomains: ["docs.example"] } };
    expect(classifyNavigation([...allowlistPolicy, scoped], "https://unknown.example/")).toBe("block");
  });

  it("handles a very large domain list quickly", () => {
    const domains = Array.from({ length: 300_000 }, (_, i) => `d${i}.example`);
    const rule: DnrRule = { id: 1, priority: 1, action: REDIRECT, condition: { resourceTypes: FRAMES, requestDomains: domains } };
    const t0 = performance.now();
    for (let i = 0; i < 50; i++) expect(ruleMatches(rule, `https://www.d${i * 1000}.example/`)).toBe(true);
    expect(ruleMatches(rule, "https://other.example/")).toBe(false);
    expect(performance.now() - t0).toBeLessThan(2000);
  });
});

describe("planRuleUpdate / stripNulls", () => {
  it("removes, adds and replaces only what changed", () => {
    const a: DnrRule = { id: 1, priority: 1, action: { type: "block" }, condition: { requestDomains: ["a.test"] } };
    const b: DnrRule = { id: 2, priority: 1, action: { type: "block" }, condition: { requestDomains: ["b.test"] } };
    const b2: DnrRule = { ...b, condition: { requestDomains: ["b.test", "c.test"] } };
    const c: DnrRule = { id: 3, priority: 1, action: { type: "allow" }, condition: {} };
    expect(planRuleUpdate([a, b], [a, b])).toEqual({ removeRuleIds: [], addRules: [] });
    expect(planRuleUpdate([a, b], [b2, c])).toEqual({ removeRuleIds: [1, 2], addRules: [b2, c] });
    // Key order does not matter.
    const reordered = JSON.parse('{"condition":{"requestDomains":["a.test"]},"action":{"type":"block"},"priority":1,"id":1}') as DnrRule;
    expect(planRuleUpdate([a], [reordered])).toEqual({ removeRuleIds: [], addRules: [] });
    expect(canonicalJson({ b: 1, a: [2, { d: 3, c: 4 }] })).toBe('{"a":[2,{"c":4,"d":3}],"b":1}');
  });

  it("strips nulls that DNR would reject", () => {
    expect(stripNulls({ id: 1, action: { type: "redirect", redirect: { regexSubstitution: "x", extensionPath: null } }, condition: { requestDomains: null, resourceTypes: ["main_frame"] } })).toEqual({
      id: 1,
      action: { type: "redirect", redirect: { regexSubstitution: "x" } },
      condition: { resourceTypes: ["main_frame"] },
    });
  });
});

describe("explainBlock (block reason from the matched rule)", () => {
  const ok = { connection: "service" as const, mode: "allowlist" as const, time: { state: "ok" as const, remainingMinutes: 50 } };

  it("maps the structural rules", () => {
    expect(explainBlock("https://unknown.example/", allowlistPolicy, [], ok)).toBe("blocked_not_in_allowlist");
    const bl = { ...ok, mode: "blocklist" as const };
    expect(explainBlock("http://10.0.0.1/admin", blocklistPolicy, [], bl)).toBe("blocked_ip_literal");
    expect(explainBlock("https://www.youtube.com/watch?v=1", blocklistPolicy, [], bl)).toBe("blocked_youtube_off");
    expect(explainBlock("https://youtu.be/abc", blocklistPolicy, [], bl)).toBe("blocked_youtube_off");
  });

  it("maps member and global bands to blocked_rule", () => {
    expect(explainBlock("https://games.pbskids.org/", allowlistPolicy, [], ok)).toBe("blocked_rule");
    expect(explainBlock("https://www.tiktok.com/@x", blocklistPolicy, [], { ...ok, mode: "blocklist" })).toBe("blocked_rule");
    expect(explainBlock("https://casino.example/", blocklistPolicy, [], { ...ok, mode: "blocklist" })).toBe("blocked_rule");
  });

  it("time-up session rules win and follow the time state", () => {
    const expired = { ...ok, time: { state: "expired" as const, remainingMinutes: 0 } };
    expect(explainBlock("https://www.khanacademy.org/", allowlistPolicy, timeUp, expired)).toBe("blocked_daily_limit");
    const outside = { ...ok, time: { state: "outside_window" as const } };
    expect(explainBlock("https://www.khanacademy.org/", allowlistPolicy, timeUp, outside)).toBe("blocked_time_window");
    // Always-allowed during time-up: the session allow wins, so the dynamic rules explain the block.
    expect(explainBlock("https://mashov.info/", allowlistPolicy, timeUp, expired)).toBe("blocked_not_in_allowlist");
  });

  it("host fallback means the service is down; no matching rule falls back to state", () => {
    expect(explainBlock("https://unknown.example/", allowlistPolicy, [], { ...ok, connection: "host" })).toBe("blocked_service_unavailable");
    expect(explainBlock("https://www.khanacademy.org/", allowlistPolicy, [], ok)).toBe("blocked_not_in_allowlist");
    expect(explainBlock("https://a.example/", [], [], { ...ok, mode: "blocklist" })).toBe("blocked_rule");
  });
});
