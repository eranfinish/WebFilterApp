import { describe, expect, it } from "vitest";
import { isPublicSuffix, registrableDomain, sameSite, siteOf } from "../src/shared/registrable-domain";

describe("registrableDomain", () => {
  it("takes the last two labels of an ordinary domain", () => {
    expect(registrableDomain("www.example.com")).toBe("example.com");
    expect(registrableDomain("a.b.c.example.org")).toBe("example.org");
    expect(registrableDomain("example.com")).toBe("example.com");
    expect(registrableDomain("WWW.Example.COM.")).toBe("example.com");
    expect(registrableDomain("site-a.test")).toBe("site-a.test");
    expect(registrableDomain("shop.example.de")).toBe("example.de");
  });

  it("knows the common multi-part suffixes (co.il, org.il, ac.il, co.uk, com.au, …)", () => {
    expect(registrableDomain("www.ynet.co.il")).toBe("ynet.co.il");
    expect(registrableDomain("www.google.co.il")).toBe("google.co.il");
    expect(registrableDomain("edu.gov.il")).toBe("edu.gov.il");
    expect(registrableDomain("lgn.edu.gov.il")).toBe("edu.gov.il");
    expect(registrableDomain("moodle.huji.ac.il")).toBe("huji.ac.il");
    expect(registrableDomain("www.kan.org.il")).toBe("kan.org.il");
    expect(registrableDomain("www.bbc.co.uk")).toBe("bbc.co.uk");
    expect(registrableDomain("news.abc.net.au")).toBe("abc.net.au");
    expect(registrableDomain("www.amazon.co.jp")).toBe("amazon.co.jp");
    expect(registrableDomain("www.uol.com.br")).toBe("uol.com.br");
  });

  it("treats a generic second level under an unlisted two-letter country as a suffix", () => {
    expect(registrableDomain("www.shop.com.xx")).toBe("shop.com.xx");
    expect(registrableDomain("a.school.edu.qq")).toBe("school.edu.qq");
    // Not a country code: the ordinary rule.
    expect(registrableDomain("www.co.example")).toBe("co.example");
  });

  it("splits hosting platforms per customer", () => {
    expect(registrableDomain("alice.github.io")).toBe("alice.github.io");
    expect(registrableDomain("bob.github.io")).toBe("bob.github.io");
    expect(registrableDomain("x.blogspot.com")).toBe("x.blogspot.com");
    expect(registrableDomain("www-example-com.translate.goog")).toBe("www-example-com.translate.goog");
    expect(registrableDomain("github.io")).toBe("github.io");
  });

  it("keeps IP literals, localhost and suffix-only hosts as they are", () => {
    expect(registrableDomain("127.0.0.1")).toBe("127.0.0.1");
    expect(registrableDomain("[::1]")).toBe("[::1]");
    expect(registrableDomain("localhost")).toBe("localhost");
    expect(registrableDomain("co.il")).toBe("co.il");
    expect(registrableDomain("")).toBe("");
  });

  it("isPublicSuffix", () => {
    expect(isPublicSuffix("co.il")).toBe(true);
    expect(isPublicSuffix("com")).toBe(true);
    expect(isPublicSuffix("example.com")).toBe(false);
    expect(isPublicSuffix("com.zz")).toBe(true);
    expect(isPublicSuffix("example.zz")).toBe(false);
  });
});

describe("siteOf / sameSite", () => {
  it("only web addresses have a site", () => {
    expect(siteOf("https://www.youtube.com:8443/watch?v=1")).toBe("youtube.com");
    expect(siteOf("http://m.youtube.com/")).toBe("youtube.com");
    expect(siteOf("about:blank")).toBeNull();
    expect(siteOf("chrome-extension://abc/blocked.html#https://x.example/")).toBeNull();
    expect(siteOf("edge://settings")).toBeNull();
    expect(siteOf("data:text/html,hi")).toBeNull();
    expect(siteOf("blob:https://a.example/123")).toBeNull();
    expect(siteOf(null)).toBeNull();
    expect(siteOf("https://")).toBeNull();
  });

  it("compares sites, not hosts or schemes", () => {
    expect(sameSite("https://docs.google.com/x", "https://classroom.google.com/")).toBe(true);
    expect(sameSite("http://example.com/", "https://www.example.com:8443/")).toBe(true);
    expect(sameSite("https://www.google.com/", "https://www.google.co.il/")).toBe(false);
    expect(sameSite("https://alice.github.io/", "https://bob.github.io/")).toBe(false);
    expect(sameSite("https://a.ynet.co.il/", "https://b.walla.co.il/")).toBe(false);
    expect(sameSite("about:blank", "about:blank")).toBe(false);
  });
});
