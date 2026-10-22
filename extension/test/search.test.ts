import { describe, expect, it } from "vitest";
import { MAX_QUERY_LENGTH, SEARCH_REPEAT_WINDOW_MS, SearchDeduper, extractSearch } from "../src/shared/search";

describe("extractSearch", () => {
  it("recognises the four engines from the results-page address", () => {
    expect(extractSearch("https://www.google.com/search?q=cats&hl=he")).toEqual({ engine: "google", query: "cats", page: 1, key: "google:cats" });
    expect(extractSearch("https://www.google.co.il/search?q=%D7%97%D7%AA%D7%95%D7%9C%D7%99%D7%9D")).toMatchObject({ engine: "google", query: "חתולים" });
    expect(extractSearch("https://www.bing.com/search?q=dogs+and+cats&form=QBLH")).toMatchObject({ engine: "bing", query: "dogs and cats" });
    expect(extractSearch("https://www.youtube.com/results?search_query=minecraft+tutorial")).toMatchObject({ engine: "youtube", query: "minecraft tutorial" });
    expect(extractSearch("https://m.youtube.com/results?search_query=lego")).toMatchObject({ engine: "youtube", query: "lego" });
    expect(extractSearch("https://duckduckgo.com/?q=weather&ia=web")).toMatchObject({ engine: "duckduckgo", query: "weather" });
    expect(extractSearch("https://html.duckduckgo.com/html/?q=weather")).toMatchObject({ engine: "duckduckgo", query: "weather" });
  });

  it("also image and video searches (blocked or not, they are searches)", () => {
    expect(extractSearch("https://www.google.com/search?q=cats&udm=2")).toMatchObject({ engine: "google", query: "cats" });
    expect(extractSearch("https://www.bing.com/images/search?q=cats")).toMatchObject({ engine: "bing", query: "cats" });
  });

  it("collapses whitespace, caps the length, and keys case-insensitively", () => {
    expect(extractSearch("https://www.google.com/search?q=%20%20Big%20%20%20Cats%20")).toMatchObject({ query: "Big Cats", key: "google:big cats" });
    expect(extractSearch(`https://www.google.com/search?q=${"a".repeat(800)}`)!.query).toHaveLength(MAX_QUERY_LENGTH);
  });

  it("marks later result pages as pagination", () => {
    expect(extractSearch("https://www.google.com/search?q=cats&start=10")).toMatchObject({ page: 2 });
    expect(extractSearch("https://www.google.com/search?q=cats&start=0")).toMatchObject({ page: 1 });
    expect(extractSearch("https://www.bing.com/search?q=cats&first=11")).toMatchObject({ page: 2 });
    expect(extractSearch("https://www.bing.com/search?q=cats&first=1")).toMatchObject({ page: 1 });
    expect(extractSearch("https://html.duckduckgo.com/html/?q=cats&s=30")).toMatchObject({ page: 2 });
  });

  it("is not fooled by look-alikes and non-search pages", () => {
    expect(extractSearch("https://www.google.com/")).toBeNull();
    expect(extractSearch("https://www.google.com/search?q=")).toBeNull();
    expect(extractSearch("https://www.google.com/search?q=%20")).toBeNull();
    expect(extractSearch("https://www.google.com/maps/search/pizza")).toBeNull();
    expect(extractSearch("https://googlesearch.example/search?q=x")).toBeNull();
    expect(extractSearch("https://www.bing.com/ck/a?q=x")).toBeNull();
    expect(extractSearch("https://duckduckgo.com/l/?uddg=https%3A%2F%2Fx.example")).toBeNull();
    expect(extractSearch("https://www.youtube.com/watch?v=abc&search_query=x")).toBeNull();
    expect(extractSearch("https://example.com/search?q=x")).toBeNull();
    expect(extractSearch("chrome-extension://id/blocked.html#https://www.google.com/search?q=x")).toBeNull();
    expect(extractSearch("not a url")).toBeNull();
  });
});

describe("SearchDeduper", () => {
  it("sends a search once, again only after the window", () => {
    const d = new SearchDeduper();
    expect(d.admit("google:cats", 0)).toBe(true);
    expect(d.admit("google:cats", 1_000)).toBe(false); // reload
    expect(d.admit("bing:cats", 1_000)).toBe(true); // another engine is another search
    expect(d.admit("google:dogs", 2_000)).toBe(true);
    expect(d.admit("google:cats", SEARCH_REPEAT_WINDOW_MS - 1)).toBe(false);
    expect(d.admit("google:cats", SEARCH_REPEAT_WINDOW_MS)).toBe(true);
    // The window counts from the report, not from the last repeat.
    expect(d.admit("google:cats", SEARCH_REPEAT_WINDOW_MS + 1_000)).toBe(false);
    expect(d.admit("google:cats", 2 * SEARCH_REPEAT_WINDOW_MS)).toBe(true);
  });

  it("forget() lets an undelivered search through again; memory is bounded", () => {
    const d = new SearchDeduper(60_000, 3);
    expect(d.admit("a", 0)).toBe(true);
    d.forget("a");
    expect(d.admit("a", 1)).toBe(true);
    for (const k of ["b", "c", "d"]) d.admit(k, 2);
    expect(d.admit("a", 3)).toBe(true); // evicted as the oldest
  });
});
