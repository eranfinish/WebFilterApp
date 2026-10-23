import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { buildBlockPageUrl, buildTimeUpUrl, displayUrl, originalUrlFromHash, parseBlockPageUrl, safeReturnUrl } from "../src/shared/pages";
import {
  EXTENSION_ONLY_REASONS,
  FAILCLOSED,
  PAGE_DECIDED_REASONS,
  REASON_CODES,
  REQUEST_RESULTS,
  inferReason,
  reasonMessageKey,
  reportableReason,
  requestActionFor,
  requestResultMessageKey,
} from "../src/shared/reasons";

const ROOT = join(__dirname, "..");
type Messages = Record<string, { message: string; placeholders?: Record<string, { content: string }> }>;
const en = JSON.parse(readFileSync(join(ROOT, "_locales/en/messages.json"), "utf8")) as Messages;
const he = JSON.parse(readFileSync(join(ROOT, "_locales/he/messages.json"), "utf8")) as Messages;

describe("block page URLs", () => {
  const page = "chrome-extension://abc/blocked.html";

  it("reads the compiled-rule form: #<original URL>, unencoded, including its own query and Hebrew", () => {
    const original = "https://www.example.com/search?q=%D7%A9%D7%9C%D7%95%D7%9D&x=1#frag";
    expect(parseBlockPageUrl(`${page}#${original}`)).toEqual({ reason: null, ruleId: null, channelId: null, url: original });
  });

  it("reads the static fail-closed form", () => {
    expect(parseBlockPageUrl(`${page}?reason=failclosed`)).toEqual({ reason: "failclosed", ruleId: null, channelId: null, url: "" });
  });

  it("round-trips the YouTube form", () => {
    const href = buildBlockPageUrl(page, { reason: "blocked_youtube_channel", url: "https://www.youtube.com/watch?v=a&t=1", channelId: "UCgamerX0000000000000003" });
    expect(href).toBe(`${page}?reason=blocked_youtube_channel&channel=UCgamerX0000000000000003#https://www.youtube.com/watch?v=a&t=1`);
    expect(parseBlockPageUrl(href)).toEqual({
      reason: "blocked_youtube_channel",
      ruleId: null,
      channelId: "UCgamerX0000000000000003",
      url: "https://www.youtube.com/watch?v=a&t=1",
    });
  });

  it("time-up page keeps the original URL", () => {
    const href = buildTimeUpUrl("chrome-extension://abc/timeup.html", "https://a.example/x?y=1");
    expect(originalUrlFromHash(href)).toBe("https://a.example/x?y=1");
    expect(buildTimeUpUrl("chrome-extension://abc/timeup.html", null)).toBe("chrome-extension://abc/timeup.html");
  });

  it("navigates back only to http(s)", () => {
    expect(safeReturnUrl("https://a.example/x")).toBe("https://a.example/x");
    expect(safeReturnUrl("javascript:alert(1)")).toBeNull();
    expect(safeReturnUrl("data:text/html,hi")).toBeNull();
    expect(safeReturnUrl("chrome://settings")).toBeNull();
    expect(safeReturnUrl("")).toBeNull();
    expect(displayUrl("https://a.example/%D7%A9")).toBe("https://a.example/ש");
    expect(displayUrl("https://a.example/%E0%A4%A")).toBe("https://a.example/%E0%A4%A");
  });
});

describe("reasons", () => {
  it("the closed list matches MVP-PLAN §6 plus PROTECTION-SPEC §6 and §13 (26 codes)", () => {
    expect(REASON_CODES).toHaveLength(26);
    expect(REASON_CODES.at(-1)).toBe("blocked_baseline_adult");
    const plan = readFileSync(join(ROOT, "..", "docs", "MVP-PLAN.md"), "utf8");
    const line = plan.split("\n").find((l) => l.startsWith("**קודי סיבה**"))!;
    const fromPlan = [...line.matchAll(/`([a-z_]+)`/g)].map((m) => m[1]!);
    const spec = readFileSync(join(ROOT, "..", "docs", "PROTECTION-SPEC.md"), "utf8");
    const specLine = spec.split("\n").find((l) => l.includes("New reason codes"))!;
    const fromSpec = [...specLine.matchAll(/`([a-z_]+)`/g)].map((m) => m[1]!);
    expect(fromSpec).toEqual(["blocked_image_search", "blocked_jump", "blocked_youtube_shorts"]);
    // The plan may already list the new codes (the service workstream updates it): compare the union.
    expect([...REASON_CODES].sort()).toEqual([...new Set([...fromPlan, ...fromSpec])].sort());
  });

  it("every reason code and fail-closed has a plain-language sentence in both languages", () => {
    for (const code of [...REASON_CODES, FAILCLOSED, "something_new"]) {
      const key = reasonMessageKey(code);
      expect(en[key]?.message, `${key} (en)`).toBeTruthy();
      expect(he[key]?.message, `${key} (he)`).toBeTruthy();
    }
    expect(reasonMessageKey("something_new")).toBe("reason_unknown");
  });

  it("every requestAccess result has a sentence", () => {
    for (const result of [...REQUEST_RESULTS, "nonsense"]) {
      const key = requestResultMessageKey(result);
      expect(en[key]?.message).toBeTruthy();
      expect(he[key]?.message).toBeTruthy();
    }
  });

  it("offers the right request per reason", () => {
    expect(requestActionFor("blocked_category")).toBe("url");
    expect(requestActionFor("blocked_not_in_allowlist")).toBe("url");
    expect(requestActionFor("blocked_youtube_channel")).toBe("youtube");
    expect(requestActionFor("blocked_daily_limit")).toBe("time");
    expect(requestActionFor("blocked_time_window")).toBe("time");
    expect(requestActionFor("blocked_homework")).toBe("none");
    expect(requestActionFor("blocked_youtube_off")).toBe("none");
    expect(requestActionFor("blocked_service_unavailable")).toBe("none");
    expect(requestActionFor(FAILCLOSED)).toBe("none");
    expect(requestActionFor("allowed_rule")).toBe("none");
    // Member settings: approving the URL would not help (PROTECTION-SPEC §1).
    expect(requestActionFor("blocked_image_search")).toBe("none");
    expect(requestActionFor("blocked_jump")).toBe("none");
    expect(requestActionFor("blocked_youtube_shorts")).toBe("none");
    // A parent's own block (PROTECTION-SPEC §13): nobody to ask.
    expect(requestActionFor("blocked_baseline_adult")).toBe("none");
  });

  it("the protection reasons are decided by the extension, like the YouTube channel reasons", () => {
    expect([...PAGE_DECIDED_REASONS].sort()).toEqual(["blocked_jump", "blocked_youtube_channel", "blocked_youtube_shorts", "blocked_youtube_unknown"]);
    expect([...EXTENSION_ONLY_REASONS].sort()).toEqual(["blocked_jump", "blocked_youtube_shorts"]);
    expect(reportableReason("blocked_jump")).toBe("blocked_jump");
    expect(reportableReason("blocked_image_search")).toBe("blocked_image_search");
  });

  it("the protection texts say what happened, in both languages", () => {
    expect(he.reason_blocked_jump!.message).toContain("האתר ניסה לפתוח אתר אחר");
    expect(en.reason_blocked_jump!.message).toMatch(/tried to open another site/);
    expect(he.reason_blocked_image_search!.message).toContain("חיפוש תמונות");
    expect(he.reason_blocked_youtube_shorts!.message).toContain("Shorts");
    expect(he.overrideButton!.message).toBe("פתח בכל זאת");
    expect(he.searchesVisible!.message).toBe("החיפושים שלך גלויים להורים");
    expect(he.reason_blocked_baseline_adult!.message).toBe("אתר למבוגרים — חסום בהגנה הבסיסית של המשפחה.");
    expect(en.reason_blocked_baseline_adult!.message).toMatch(/baseline protection/);
    expect(he.baselineHint!.message).toContain("ממשק הניהול");
    expect(he.baselineHint!.message).toContain("הגדרות");
    expect(he.mode_baseline!.message).toContain("הגנה בסיסית");
  });

  it("infers a reason when the page has none, and reports fail-closed as service unavailable", () => {
    expect(inferReason({ state: "expired" }, "blocklist")).toBe("blocked_daily_limit");
    expect(inferReason({ state: "outside_window" }, "allowlist")).toBe("blocked_time_window");
    expect(inferReason({ state: "ok" }, "allowlist")).toBe("blocked_not_in_allowlist");
    expect(inferReason(null, "blocklist")).toBe("blocked_rule");
    // A parent's baseline (PROTECTION-SPEC §13) blocks the adult list only, whatever the time says.
    expect(inferReason({ state: "unlimited" }, "baseline")).toBe("blocked_baseline_adult");
    expect(inferReason({ state: "expired" }, "baseline")).toBe("blocked_baseline_adult");
    expect(reportableReason(FAILCLOSED)).toBe("blocked_service_unavailable");
    expect(reportableReason("blocked_rule")).toBe("blocked_rule");
    expect(reportableReason("nope")).toBeNull();
  });
});

describe("locales", () => {
  it("en and he have the same keys and placeholders", () => {
    expect(Object.keys(he).sort()).toEqual(Object.keys(en).sort());
    for (const key of Object.keys(en)) expect(he[key]!.placeholders, key).toEqual(en[key]!.placeholders);
    expect(he.textDirection!.message).toBe("rtl");
    expect(en.textDirection!.message).toBe("ltr");
  });

  it("store description fits 132 characters", () => {
    expect(en.extDescription!.message.length).toBeLessThanOrEqual(132);
    expect(he.extDescription!.message.length).toBeLessThanOrEqual(132);
  });

  it("every data-i18n key used by the pages exists", () => {
    for (const page of ["blocked.html", "timeup.html", "my-channels.html", "popup.html"]) {
      const html = readFileSync(join(ROOT, "pages", page), "utf8");
      for (const m of html.matchAll(/data-i18n(?:-placeholder)?="([^"]+)"/g)) expect(en[m[1]!], `${page}: ${m[1]}`).toBeDefined();
    }
  });

  it("window titles are fixed for lab automation", () => {
    const title = (page: string) => readFileSync(join(ROOT, "pages", page), "utf8").match(/<title>([^<]*)<\/title>/)?.[1];
    expect(title("blocked.html")).toBe("WebFilter · חסום");
    expect(title("timeup.html")).toBe("WebFilter · הזמן נגמר");
  });
});
