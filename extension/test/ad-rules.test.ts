/**
 * The ad-server ruleset (PROTECTION-SPEC §12): the generator's pure functions (tools/starter-lists/ad-rules.mjs) on small
 * inputs, and the committed rulesets/ads.json against the guards it must keep.
 */
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import {
  AD_RULE_PRIORITY,
  buildAdRuleset,
  formatRuleset,
  normalizeHost,
  parseBlockRules,
  parseExceptions,
} from "../../tools/starter-lists/ad-rules.mjs";
import { AD_ESSENTIALS, ALLOW_LISTS, NEVER_BLOCK } from "../../tools/starter-lists/curation.mjs";

const ROOT = join(__dirname, "..");
const REPO = join(ROOT, "..");

type Condition = {
  requestDomains: string[];
  excludedRequestDomains?: string[];
  excludedInitiatorDomains?: string[];
  excludedResourceTypes?: string[];
  resourceTypes?: string[];
  domainType?: string;
};
type AdRule = { id: number; priority: number; action: { type: string }; condition: Condition };

const block = (domain: string, thirdParty = false) => ({ domain, thirdParty });
const domainsOf = (rules: AdRule[]) => rules.flatMap((r) => r.condition.requestDomains);
const build = (...args: Parameters<typeof buildAdRuleset>) => buildAdRuleset(...args) as { rules: AdRule[]; report: ReturnType<typeof buildAdRuleset>["report"] };

/** What the ruleset does to a request, as DNR would: blocked unless the page itself (main_frame). */
function blocks(rules: AdRule[], host: string, initiator: string, type = "script"): boolean {
  const under = (h: string, d: string) => h === d || h.endsWith(`.${d}`);
  const site = (h: string) => h.split(".").slice(-2).join(".");
  return rules.some((r) => {
    const c = r.condition;
    if (c.excludedResourceTypes?.includes(type)) return false;
    if (!c.requestDomains.some((d) => under(host, d))) return false;
    if (c.excludedRequestDomains?.some((d) => under(host, d))) return false;
    if (c.excludedInitiatorDomains?.some((d) => under(initiator, d))) return false;
    if (c.domainType === "thirdParty" && site(host) === site(initiator)) return false;
    return true;
  });
}

describe("parsing EasyList", () => {
  it("keeps only whole-domain rules, any party or third-party", () => {
    const { rules, skipped } = parseBlockRules(
      [
        "! Title: EasyList ad servers",
        "[Adblock Plus 2.0]",
        "||ads.example^",
        "||Track.Example^$third-party",
        "||bücher-ads.example^",
        "||path.example/banner/",
        "||script.example^$script",
        "||doc.example^$document",
        "||popup.example^$popup",
        "example.com##.ad-slot",
        "/banner[0-9]+\\.gif/",
        "||192.168.1.1^",
        "||wild*.example^",
        "",
      ].join("\r\n"),
    );
    expect(rules).toEqual([block("ads.example"), block("track.example", true), block("xn--bcher-ads-q9a.example")]);
    expect(skipped).toBe(8);
  });

  it("reads exceptions as a host and the sites they apply on", () => {
    const { exceptions, skipped } = parseExceptions(
      [
        "@@||spared.example^$script,domain=news.example|~other.example|sub.Site.example",
        "@@||path.example/ima3.js$script",
        "@@||first.example^$~third-party",
        "@@||cosmetic.example^$generichide",
        "@@||popup.example^$popup",
        "@@||google.*/search?$xmlhttprequest",
        "@@/generic/path/$image",
        "@@||everywhere.example^",
      ].join("\n"),
    );
    expect(exceptions.map(({ host, sites, path }) => ({ host, sites, path }))).toEqual([
      { host: "spared.example", sites: ["news.example", "sub.site.example"], path: false },
      { host: "path.example", sites: null, path: true },
      { host: "first.example", sites: ["first.example"], path: false },
      { host: "everywhere.example", sites: null, path: false },
    ]);
    expect(skipped).toEqual(["@@||google.*/search?$xmlhttprequest"]);
  });

  it("normalises host names and refuses what is not one", () => {
    expect(normalizeHost("Ads.Example.")).toBe("ads.example");
    expect(normalizeHost("*.example")).toBeNull();
    expect(normalizeHost("10.0.0.1")).toBeNull();
    expect(normalizeHost("localhost")).toBeNull();
    expect(normalizeHost("-bad.example")).toBeNull();
  });
});

describe("building the ruleset", () => {
  it("has the rule shape the manifest's static ruleset needs: block, priority 1, every type but main_frame", () => {
    const { rules } = build([block("b.example"), block("a.example"), block("t.example", true)]);
    expect(rules).toEqual([
      { id: 1, priority: 1, action: { type: "block" }, condition: { excludedResourceTypes: ["main_frame"], requestDomains: ["a.example", "b.example"] } },
      { id: 2, priority: 1, action: { type: "block" }, condition: { domainType: "thirdParty", excludedResourceTypes: ["main_frame"], requestDomains: ["t.example"] } },
    ]);
    expect(AD_RULE_PRIORITY).toBe(1);
    // Every subresource type is blocked, sub_frame included; the page itself is not.
    for (const type of ["script", "image", "xmlhttprequest", "sub_frame", "ping", "media", "font", "stylesheet", "websocket", "object", "other"])
      expect(blocks(rules, "a.example", "news.example", type), type).toBe(true);
    expect(blocks(rules, "a.example", "news.example", "main_frame")).toBe(false);
  });

  it("maps $third-party to domainType thirdParty: the ad host's own pages keep their scripts", () => {
    const { rules } = build([block("cdn-ads.example", true)]);
    expect(blocks(rules, "cdn-ads.example", "news.example")).toBe(true);
    expect(blocks(rules, "static.cdn-ads.example", "www.cdn-ads.example")).toBe(false);
  });

  it("de-duplicates: one entry per domain, any party wins, subdomains of a listed domain are dropped", () => {
    const { rules, report } = build([
      block("dup.example"),
      block("dup.example"),
      block("both.example", true),
      block("both.example"),
      block("x.dup.example"),
      block("y.dup.example", true),
      block("third.example", true),
      block("z.third.example", true),
      block("any.third.example"),
    ]);
    expect(domainsOf(rules).sort()).toEqual(["any.third.example", "both.example", "dup.example", "third.example"]);
    expect(rules[0]!.condition.requestDomains).toEqual(["any.third.example", "both.example", "dup.example"]);
    expect(rules[1]!.condition).toMatchObject({ domainType: "thirdParty", requestDomains: ["third.example"] });
    expect(report.covered).toBe(3);
  });

  it("never includes a protected domain, a host below an essential or allowed domain, or a parent of one", () => {
    const { rules, report } = build(
      [
        block("google.com"),
        block("adservice.google.com"),
        block("ads.school.example"),
        block("example"),
        block("teacher.example"),
        block("cloudfront.net"),
        block("d123.cloudfront.net"),
        block("co.il"),
        block("removed.example"),
        block("x.removed.example"),
        block("ads.example"),
      ],
      [],
      {
        isPublicSuffix: (h: string) => h === "co.il" || h === "example",
        protectTree: ["google.com", "school.example", "lessons.teacher.example"],
        protectSelfAndParents: ["cloudfront.net"],
        remove: ["removed.example"],
      },
    );
    expect(domainsOf(rules).sort()).toEqual(["ads.example", "d123.cloudfront.net"]);
    expect(report.protected).toEqual(["ads.school.example", "adservice.google.com", "cloudfront.net", "google.com", "teacher.example"]);
    expect(report.publicSuffix).toEqual(["co.il", "example"]);
    expect(report.removed).toEqual(["removed.example", "x.removed.example"]);
  });

  it("honours exceptions per host: everywhere → left out or carved out, on some sites → its own rule without them", () => {
    const { rules, report } = build(
      [block("gone.example"), block("ads.example"), block("net.example"), block("cdn.example", true)],
      [
        { host: "gone.example", sites: null }, // a whole-domain exception everywhere
        { host: "video.ads.example", sites: null }, // a host below a listed domain, everywhere
        { host: "net.example", sites: ["news.example"] }, // the listed domain itself, on one site
        { host: "player.cdn.example", sites: ["tv.example", "radio.example"] }, // a host below, on two sites
        { host: "cdn.example", sites: ["tv.example"] },
      ],
    );
    expect(report.exemptEverywhere).toEqual(["gone.example", "video.ads.example"]);
    expect(rules.map((r) => r.condition)).toEqual([
      { excludedRequestDomains: ["video.ads.example"], excludedResourceTypes: ["main_frame"], requestDomains: ["ads.example"] },
      { excludedInitiatorDomains: ["news.example"], excludedResourceTypes: ["main_frame"], requestDomains: ["net.example"] },
      { domainType: "thirdParty", excludedInitiatorDomains: ["tv.example"], excludedRequestDomains: ["player.cdn.example"], excludedResourceTypes: ["main_frame"], requestDomains: ["cdn.example"] },
      { domainType: "thirdParty", excludedInitiatorDomains: ["radio.example", "tv.example"], excludedResourceTypes: ["main_frame"], requestDomains: ["player.cdn.example"] },
    ]);
    expect(rules.map((r) => r.id)).toEqual([1, 2, 3, 4]);
    expect(blocks(rules, "net.example", "news.example")).toBe(false);
    expect(blocks(rules, "net.example", "blog.example")).toBe(true);
    expect(blocks(rules, "video.ads.example", "blog.example")).toBe(false);
    expect(blocks(rules, "img.ads.example", "blog.example")).toBe(true);
    expect(blocks(rules, "player.cdn.example", "radio.example")).toBe(false);
    expect(blocks(rules, "player.cdn.example", "blog.example")).toBe(true);
    expect(blocks(rules, "cdn.example", "radio.example")).toBe(true);
  });

  it("ignores a path exception on every site for a shared host above listed domains, and unrelated exceptions", () => {
    const { rules, report } = build(
      [block("d1.cloudfront.net"), block("d2.cloudfront.net")],
      [
        { host: "cloudfront.net", sites: null, path: true }, // cloudfront.net/js/common/invoke.js
        { host: "unrelated.example", sites: null },
      ],
    );
    expect(domainsOf(rules)).toEqual(["d1.cloudfront.net", "d2.cloudfront.net"]);
    expect(report.exemptEverywhere).toEqual([]);
    // On named sites the shared host's exception still spares everything below it there.
    const onSites = build([block("d1.cloudfront.net")], [{ host: "cloudfront.net", sites: ["spiegel.example"], path: true }]);
    expect(onSites.rules[0]!.condition.excludedInitiatorDomains).toEqual(["spiegel.example"]);
  });

  it("formats one domain per line as valid JSON", () => {
    const { rules } = build([block("a.example"), block("b.example"), block("t.example", true)], [{ host: "b.example", sites: ["s.example"] }]);
    const text = formatRuleset(rules);
    expect(JSON.parse(text)).toEqual(rules);
    expect(text.split("\n")).toContain('"a.example"');
    expect(text.endsWith("]\n")).toBe(true);
  });
});

describe("the committed rulesets/ads.json", () => {
  const manifest = JSON.parse(readFileSync(join(ROOT, "manifest.json"), "utf8")) as {
    declarative_net_request: { rule_resources: { id: string; enabled: boolean; path: string }[] };
  };
  const resource = manifest.declarative_net_request.rule_resources.find((r) => r.id === "ads");
  const text = readFileSync(join(ROOT, "rulesets", "ads.json"), "utf8");
  const rules = JSON.parse(text) as AdRule[];
  const all = domainsOf(rules);

  it("is declared in the manifest, disabled until the rules say blockAds", () => {
    expect(resource).toEqual({ id: "ads", enabled: false, path: "rulesets/ads.json" });
    expect(manifest.declarative_net_request.rule_resources.find((r) => r.id === "failclosed")?.enabled).toBe(true);
  });

  it("holds only block rules at priority 1 that never touch page loads", () => {
    expect(rules.map((r) => r.id)).toEqual(rules.map((_, i) => i + 1));
    for (const rule of rules) {
      expect(rule.priority).toBe(1);
      expect(rule.action).toEqual({ type: "block" });
      expect(rule.condition.excludedResourceTypes).toEqual(["main_frame"]);
      expect(rule.condition.resourceTypes).toBeUndefined();
      expect(["thirdParty", undefined]).toContain(rule.condition.domainType);
      expect(rule.condition.requestDomains.length).toBeGreaterThan(0);
      expect([...rule.condition.requestDomains].sort()).toEqual(rule.condition.requestDomains);
    }
    expect(rules[0]!.condition.domainType).toBeUndefined();
    expect(rules[1]!.condition.domainType).toBe("thirdParty");
    expect(text).not.toContain("allowAllRequests");
    expect(text).not.toContain('"allow"');
  });

  it("is EasyList-sized and stays a reasonable part of the package", () => {
    expect(all.length).toBeGreaterThan(40_000);
    for (const rule of rules) expect(new Set(rule.condition.requestDomains).size).toBe(rule.condition.requestDomains.length);
    const anyParty = new Set(rules[0]!.condition.requestDomains);
    expect(rules[1]!.condition.requestDomains.filter((d) => anyParty.has(d))).toEqual([]);
    expect(text.length).toBeLessThan(2_000_000);
    for (const d of all) expect(normalizeHost(d), d).toBe(d);
  });

  it("never blocks a host of the essentials or the allow lists, nor a parent of them or of a platform", () => {
    const allowed = (ALLOW_LISTS as string[]).flatMap(
      (name) => (JSON.parse(readFileSync(join(REPO, "data", "starter-lists", `${name}.json`), "utf8")) as { domains: string[] }).domains,
    );
    const tree = [...(AD_ESSENTIALS as string[]), ...allowed];
    const under = (h: string, d: string) => h === d || h.endsWith(`.${d}`);
    for (const d of all) {
      expect(tree.find((p) => under(d, p) || under(p, d)), d).toBeUndefined();
      expect((NEVER_BLOCK as string[]).find((p) => under(p, d)), d).toBeUndefined();
    }
    for (const host of ["www.google.com", "accounts.google.com", "classroom.google.com", "fonts.gstatic.com", "www.youtube.com", "i.ytimg.com", "login.microsoftonline.com", "teams.microsoft.com", "www.apple.com", "www.khanacademy.org"])
      expect(blocks(rules, host, "news.example"), host).toBe(false);
  });

  it("blocks the big ad networks' hosts on any site", () => {
    for (const host of ["pagead2.googlesyndication.com", "securepubads.g.doubleclick.net", "ib.adnxs.com", "ads.pubmatic.com", "bidder.criteo.com"])
      expect(blocks(rules, host, "www.ynet.co.il"), host).toBe(true);
  });

  it("ships with its licence notice: EasyList, CC BY-SA, the pinned commit", () => {
    const notice = readFileSync(join(ROOT, "rulesets", "ads-notice.txt"), "utf8");
    const sources = readFileSync(join(REPO, "tools", "starter-lists", "sources.mjs"), "utf8");
    const commit = /EASYLIST = "([0-9a-f]{40})"/.exec(sources)![1]!;
    expect(notice).toContain("EasyList");
    expect(notice).toContain("Attribution-ShareAlike 4.0");
    expect(notice).toContain(commit);
    expect(notice).toContain(`${all.length} domains in ${rules.length} rules`);
    expect(existsSync(join(ROOT, "rulesets", "ads.json"))).toBe(true);
  });
});
