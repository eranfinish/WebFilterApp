/**
 * Search-term log (PROTECTION-SPEC §9): searches recognised from the address of a results page, and the rule for
 * sending each one once.
 *
 *   Google      google.<any country>/search?q=…        (pages: start=10, 20, …)
 *   Bing        bing.com/search?q=… (also /images/search, /videos/search, /news/search; pages: first=11, 21, …)
 *   YouTube     youtube.com/results?search_query=…    (more results load in place)
 *   DuckDuckGo  duckduckgo.com/?q=… (also /html/, /lite/; pages: s=…)
 */
import type { SearchEngine } from "./protocol";
import { siteOf } from "./registrable-domain";

export interface SearchHit {
  engine: SearchEngine;
  /** The terms as typed, whitespace collapsed. */
  query: string;
  /** 1 for the first page of results; a later page is pagination, not a new search. */
  page: number;
  /** Engine + lower-cased query: two hits with the same key are the same search. */
  key: string;
}

export const MAX_QUERY_LENGTH = 500;
export const MAX_SEARCH_URL_LENGTH = 2048;
/** The same search again within this time (a reload, Back, another results tab) is not sent again. */
export const SEARCH_REPEAT_WINDOW_MS = 10 * 60_000;

function param(params: URLSearchParams, name: string): string | null {
  const value = params.getAll(name).find((v) => v.trim().length > 0);
  return value ?? null;
}

function intParam(params: URLSearchParams, name: string): number {
  const n = Number.parseInt(params.get(name) ?? "", 10);
  return Number.isFinite(n) ? n : 0;
}

/** The search a results-page URL shows, or null when it is not one. */
export function extractSearch(url: string): SearchHit | null {
  const site = siteOf(url);
  if (!site) return null;
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }
  const params = parsed.searchParams;
  const path = parsed.pathname.toLowerCase();
  const name = site.split(".")[0];
  let engine: SearchEngine | null = null;
  let raw: string | null = null;
  let page = 1;

  if (name === "google" && path === "/search") {
    engine = "google";
    raw = param(params, "q");
    const start = intParam(params, "start");
    page = start > 0 ? Math.floor(start / 10) + 1 : 1;
  } else if (name === "bing" && /^\/(?:(?:images|videos|news)\/)?search$/.test(path)) {
    engine = "bing";
    raw = param(params, "q");
    const first = intParam(params, "first");
    page = first > 1 ? Math.floor((first - 1) / 10) + 1 : 1;
  } else if (site === "youtube.com" && path === "/results") {
    engine = "youtube";
    raw = param(params, "search_query");
  } else if (site === "duckduckgo.com" && (path === "/" || path === "/html/" || path === "/html" || path === "/lite/" || path === "/lite")) {
    engine = "duckduckgo";
    raw = param(params, "q");
    page = intParam(params, "s") > 0 ? 2 : 1;
  }
  if (!engine || raw === null) return null;
  const query = raw.replace(/\s+/g, " ").trim().slice(0, MAX_QUERY_LENGTH);
  if (!query) return null;
  return { engine, query, page, key: `${engine}:${query.toLocaleLowerCase()}` };
}

/**
 * Remembers which searches were sent: the same search (same key) again within `windowMs` of the last report is
 * dropped. The window is counted from the report, not slid by repeats, so a search repeated over a long time is sent
 * again every `windowMs` at most.
 */
export class SearchDeduper {
  private readonly sent = new Map<string, number>();

  constructor(
    private readonly windowMs = SEARCH_REPEAT_WINDOW_MS,
    private readonly max = 500,
  ) {}

  /** True when this search should be reported now (and remembers it). */
  admit(key: string, now: number): boolean {
    const at = this.sent.get(key);
    if (at !== undefined && now - at < this.windowMs) return false;
    this.sent.delete(key);
    this.sent.set(key, now);
    while (this.sent.size > this.max) {
      const oldest = this.sent.keys().next().value;
      if (oldest === undefined) break;
      this.sent.delete(oldest);
    }
    return true;
  }

  /** Forgets a search whose report could not be delivered, so the next sighting tries again. */
  forget(key: string): void {
    this.sent.delete(key);
  }

  clear(): void {
    this.sent.clear();
  }
}
