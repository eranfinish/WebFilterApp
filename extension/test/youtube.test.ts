import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import type { FilterMode, TimeState, YouTubePolicy } from "../src/shared/protocol";
import { type YouTubeContext, approvedChannels, decideYouTube } from "../src/shared/youtube-decision";
import { isYouTubeHost, normalizeHandle, parseYouTubeUrl } from "../src/shared/youtube-url";

// The scenario files are the specification (CLAUDE.md): load them instead of copying their cases.
const SIM = join(__dirname, "..", "..", "tests", "simulation");
const load = <T>(path: string): T => JSON.parse(readFileSync(join(SIM, path), "utf8")) as T;

interface ParseCase {
  url: string;
  expect: Record<string, string>;
}
interface Scenario {
  id: string;
  title: string;
  setup?: { members?: { id: string; youtubeMode?: string }[] };
  steps: Record<string, unknown>[];
}

describe("parseYouTubeUrl (youtube-parse.json)", () => {
  const suite = load<{ scenarios: Scenario[] }>("scenarios/engine/youtube-parse.json");
  for (const scenario of suite.scenarios) {
    for (const step of scenario.steps) {
      for (const c of step.cases as ParseCase[]) {
        it(`${scenario.id} ${c.url}`, () => {
          const parsed = parseYouTubeUrl(c.url);
          const { kind, ...rest } = c.expect;
          expect(parsed.kind).toBe(kind);
          for (const [key, value] of Object.entries(rest)) expect(parsed[key as keyof typeof parsed]).toBe(value);
        });
      }
    }
  }

  it("handles the extra forms of the C# parser", () => {
    expect(parseYouTubeUrl("https://www.youtube.com/live/vidLive0001")).toMatchObject({ kind: "video", videoId: "vidLive0001" });
    expect(parseYouTubeUrl("https://www.youtube.com/embed/videoseries?list=PL1")).toEqual({ kind: "embed", site: "www", playlistId: "PL1" });
    expect(parseYouTubeUrl("https://www.youtube.com/watch")).toEqual({ kind: "other", site: "www" });
    expect(parseYouTubeUrl("https://www.youtube.com/shorts")).toEqual({ kind: "other", site: "www" });
    expect(parseYouTubeUrl("https://WWW.YouTube.com./WATCH?v=abc")).toMatchObject({ kind: "video", videoId: "abc" });
    expect(parseYouTubeUrl("https://www.youtube.com/watch?v=&v=second")).toMatchObject({ kind: "video", videoId: "second" });
    expect(parseYouTubeUrl("ftp://www.youtube.com/watch?v=abc").kind).toBe("not_youtube");
    expect(parseYouTubeUrl("not a url").kind).toBe("not_youtube");
    expect(parseYouTubeUrl("https://youtu.be/").kind).toBe("home");
    expect(isYouTubeHost("music.youtube.com")).toBe(true);
    expect(isYouTubeHost("youtube.com.example.net")).toBe(false);
    expect(normalizeHandle("Sesame-Sim")).toBe("@sesame-sim");
    expect(normalizeHandle("@")).toBeNull();
  });
});

// family-base.json → the YouTube policy the service would send each member (member + global channel records).
interface Fixture {
  members: { id: string; filterMode: FilterMode; youtubeMode: YouTubePolicy["mode"] }[];
  youtubeChannels: { channelId: string; handle?: string; title?: string; member?: string | null; action: "allow" | "block" }[];
}
const fixture = load<Fixture>("fixtures/family-base.json");

function contextFor(memberId: string, overrides: { youtubeMode?: string } = {}, time: TimeState | null = null): YouTubeContext {
  const member = fixture.members.find((m) => m.id === memberId)!;
  const channels = fixture.youtubeChannels.filter((c) => !c.member || c.member === memberId);
  const ref = (c: Fixture["youtubeChannels"][number]) => ({ channelId: c.channelId, handle: c.handle ?? null, title: c.title ?? null });
  return {
    inert: false,
    ready: true,
    filterMode: member.filterMode,
    policy: {
      mode: (overrides.youtubeMode as YouTubePolicy["mode"]) ?? member.youtubeMode,
      allowed: channels.filter((c) => c.action === "allow").map(ref),
      blocked: channels.filter((c) => c.action === "block").map(ref),
    },
    time: time ?? { state: "ok", remainingMinutes: 120, windowEndsAt: "19:00" },
  };
}

describe("decideYouTube (youtube-decisions.json)", () => {
  const suite = load<{ scenarios: Scenario[] }>("scenarios/engine/youtube-decisions.json");
  for (const scenario of suite.scenarios) {
    // YT-003 browses until the daily limit is used up: the service then reports time "expired".
    const browsedOut = scenario.steps.some((s) => s.do === "browse");
    const time: TimeState | null = browsedOut ? { state: "expired", remainingMinutes: 0, windowEndsAt: "19:00" } : null;
    const overrides = Object.fromEntries((scenario.setup?.members ?? []).map((m) => [m.id, { youtubeMode: m.youtubeMode }]));
    for (const step of scenario.steps) {
      if (step.do === "youtube") {
        const page = step.page as { url: string; channelId?: string; handle?: string };
        const expected = step.expect as { decision: string; reason: string };
        it(`${scenario.id} ${String(step.member)} ${page.url}`, () => {
          const ctx = contextFor(String(step.member), overrides[String(step.member)], time);
          expect(decideYouTube(ctx, page)).toMatchObject(expected);
        });
      }
      if (step.do === "checkUrls") {
        // URL-level YouTube "off" (enforced by DNR); the content script must agree.
        for (const c of step.cases as { url: string; expect: { decision: string; reason: string } }[]) {
          it(`${scenario.id} ${String(step.member)} ${c.url} (content script agrees)`, () => {
            const ctx = contextFor(String(step.member), overrides[String(step.member)], time);
            expect(decideYouTube(ctx, { url: c.url })).toMatchObject(c.expect);
          });
        }
      }
    }
  }
});

describe("decideYouTube: extension states", () => {
  const noa = (): YouTubeContext => contextFor("noa");
  const video = { url: "https://www.youtube.com/watch?v=vidKhan0001", channelId: "UCkhanKids00000000000001" };

  it("does nothing when not policy-installed", () => {
    expect(decideYouTube({ ...noa(), inert: true, ready: false, policy: null }, video)).toEqual({ decision: "allow", reason: "allowed_unrestricted" });
  });

  it("fails closed without rules", () => {
    expect(decideYouTube({ ...noa(), ready: false }, video)).toEqual({ decision: "block", reason: "blocked_service_unavailable" });
    expect(decideYouTube({ ...noa(), policy: null }, video).decision).toBe("block");
  });

  it("blocks outside the time window before looking at the channel", () => {
    const ctx = { ...noa(), time: { state: "outside_window" as const, remainingMinutes: 100, windowEndsAt: null } };
    expect(decideYouTube(ctx, video)).toEqual({ decision: "block", reason: "blocked_time_window" });
  });

  it("treats a channel that is both allowed and blocked as blocked", () => {
    const ctx = noa();
    ctx.policy!.blocked.push({ channelId: "UCkhanKids00000000000001", handle: null, title: null });
    expect(decideYouTube(ctx, video)).toMatchObject({ decision: "block", reason: "blocked_youtube_channel" });
    expect(approvedChannels(ctx.policy).map((c) => c.channelId)).toEqual(["UCsesame0000000000000002"]);
  });

  it("allows non-channel pages in allowlist mode and returns the channel for requests", () => {
    expect(decideYouTube(noa(), { url: "https://www.youtube.com/feed/trending" })).toEqual({ decision: "allow", reason: "allowed_default" });
    expect(decideYouTube(noa(), { url: "https://www.youtube.com/playlist?list=PL1" }).decision).toBe("allow");
    expect(decideYouTube(noa(), { url: "https://www.youtube.com/watch?v=x", channelId: "UCgamerX0000000000000003" })).toEqual({
      decision: "block",
      reason: "blocked_youtube_channel",
      channelId: "UCgamerX0000000000000003",
    });
  });

  it("an unknown mode is treated as off", () => {
    const ctx = noa();
    ctx.policy!.mode = "kids" as YouTubePolicy["mode"];
    expect(decideYouTube(ctx, video)).toEqual({ decision: "block", reason: "blocked_youtube_off" });
  });
});
