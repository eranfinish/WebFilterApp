/**
 * YouTube URL parser, a port of src/WebFilter.Core/YouTube/YouTubeUrlParser.cs (scenarios: youtube-parse.json).
 * Handles are lowercase with the leading '@'; custom and user names lowercase; ids keep their case.
 */

export type YouTubeUrlKind =
  | "channel"
  | "handle"
  | "custom"
  | "user"
  | "video"
  | "shorts"
  | "embed"
  | "playlist"
  | "home"
  | "search"
  | "other"
  | "not_youtube";

/** www covers youtube.com with or without www (and other subdomains); short is youtu.be. */
export type YouTubeSite = "www" | "m" | "music" | "nocookie" | "short";

export interface YouTubeUrl {
  kind: YouTubeUrlKind;
  site?: YouTubeSite;
  channelId?: string;
  handle?: string;
  customName?: string;
  userName?: string;
  videoId?: string;
  playlistId?: string;
  /** Search pages: the terms (search_query, or q on YouTube Music), trimmed; absent when there are none. */
  searchQuery?: string;
  /** Search pages: YouTube's "Type: Channels" filter is the only filter (PROTECTION-SPEC §15). */
  channelsOnly?: boolean;
}

/**
 * YouTube's search filter "Type: Channels" (`sp`, base64 of the filter message: filters { type: channel }), as in
 * YouTubeUrlParser.ChannelFilter. Checked on youtube.com on 2026-10-25: the filter's own link is `sp=EgIQAg%253D%253D`.
 */
export const CHANNEL_FILTER = "EgIQAg==";
/** CHANNEL_FILTER as YouTube's own "Channels" link writes it in a URL (encoded twice). */
export const CHANNEL_FILTER_PARAMETER = "EgIQAg%253D%253D";
export const YOUTUBE_ORIGIN = "https://www.youtube.com";

/**
 * YouTube's results for `query` restricted to channels (PROTECTION-SPEC §15), on `origin` (a youtube.com page's
 * scheme, host and port). The same URL as YouTubeUrlParser.ChannelSearchUrl.
 */
export function channelSearchUrl(query: string, origin: string = YOUTUBE_ORIGIN): string {
  return `${origin}/results?search_query=${encodeURIComponent(query.trim())}&sp=${CHANNEL_FILTER_PARAMETER}`;
}

/**
 * Whether the only `sp` parameter is CHANNEL_FILTER: encoded once or twice (YouTube's own links), with or without the
 * padding. Another filter, a sort order or a second `sp` is not channels only. `search` is the raw query string.
 */
function isChannelFilter(search: string): boolean {
  const values = new URLSearchParams(search).getAll("sp");
  if (values.length !== 1) return false;
  let value = values[0]!;
  for (let i = 0; i < 2; i++) {
    if (value === CHANNEL_FILTER || value === "EgIQAg") return true;
    const decoded = unescape(value);
    if (decoded === value) return false;
    value = decoded;
  }
  return false;
}

const NOT_YOUTUBE: YouTubeUrl = Object.freeze({ kind: "not_youtube" });

function isDomainOrSubdomain(host: string, domain: string): boolean {
  return host === domain || (host.length > domain.length + 1 && host.endsWith(`.${domain}`));
}

export function youTubeSiteOf(hostname: string): YouTubeSite | null {
  let host = hostname.toLowerCase();
  if (host.endsWith(".")) host = host.slice(0, -1);
  if (isDomainOrSubdomain(host, "youtube.com")) {
    if (host === "m.youtube.com") return "m";
    if (host === "music.youtube.com") return "music";
    return "www";
  }
  if (isDomainOrSubdomain(host, "youtube-nocookie.com")) return "nocookie";
  if (isDomainOrSubdomain(host, "youtu.be")) return "short";
  return null;
}

/** True for every host whose access is governed by the YouTube mode rather than by URL rules. */
export function isYouTubeHost(hostname: string): boolean {
  return youTubeSiteOf(hostname) !== null;
}

/**
 * Google's sign-in passes through accounts.youtube.com to set YouTube's cookies: no YouTube mode blocks it, Off included
 * (PROTECTION-SPEC §11, YouTubeUrlParser.SignInHost; the compiled rules allow it too).
 */
export const YOUTUBE_SIGN_IN_HOST = "accounts.youtube.com";

export function isYouTubeSignInHost(hostname: string): boolean {
  let host = hostname.toLowerCase();
  if (host.endsWith(".")) host = host.slice(0, -1);
  return isDomainOrSubdomain(host, YOUTUBE_SIGN_IN_HOST);
}

function unescape(segment: string): string {
  if (!segment.includes("%")) return segment;
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

/** The first non-empty value of a query parameter, form-decoded. */
function queryValue(search: string, name: string): string | undefined {
  const params = new URLSearchParams(search);
  return params.getAll(name).find((v) => v.length > 0);
}

export function parseYouTubeUrl(url: string): YouTubeUrl {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return NOT_YOUTUBE;
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return NOT_YOUTUBE;
  const site = youTubeSiteOf(parsed.hostname);
  if (!site) return NOT_YOUTUBE;

  const segments = parsed.pathname.split("/").filter((s) => s.length > 0);
  const playlistId = queryValue(parsed.search, "list");
  const withPlaylist = (u: YouTubeUrl): YouTubeUrl => (playlistId !== undefined ? { ...u, playlistId } : u);

  if (site === "short") {
    const first = segments[0];
    return first === undefined ? { kind: "home", site } : withPlaylist({ kind: "video", site, videoId: unescape(first) });
  }

  const rawFirst = segments[0];
  if (rawFirst === undefined) return { kind: "home", site };
  const first = unescape(rawFirst);
  const second = segments[1] !== undefined ? unescape(segments[1]) : undefined;

  if (first.length > 1 && first.startsWith("@")) return { kind: "handle", site, handle: first.toLowerCase() };

  switch (first.toLowerCase()) {
    case "watch": {
      const videoId = queryValue(parsed.search, "v");
      if (videoId !== undefined) return withPlaylist({ kind: "video", site, videoId });
      return playlistId !== undefined ? { kind: "playlist", site, playlistId } : { kind: "other", site };
    }
    case "live":
      if (second !== undefined) return withPlaylist({ kind: "video", site, videoId: second });
      break;
    case "shorts":
      if (second !== undefined) return { kind: "shorts", site, videoId: second };
      break;
    case "embed": {
      const embedded = second === undefined || second.toLowerCase() === "videoseries" ? undefined : second;
      const result: YouTubeUrl = { kind: "embed", site };
      if (embedded !== undefined) result.videoId = embedded;
      return withPlaylist(result);
    }
    case "playlist":
      if (playlistId !== undefined) return { kind: "playlist", site, playlistId };
      break;
    case "results":
    case "search": {
      const terms = (queryValue(parsed.search, "search_query") ?? queryValue(parsed.search, "q"))?.trim();
      const result: YouTubeUrl = { kind: "search", site, channelsOnly: isChannelFilter(parsed.search) };
      if (terms) result.searchQuery = terms;
      return result;
    }
    case "channel":
      if (second !== undefined) return { kind: "channel", site, channelId: second };
      break;
    case "c":
      if (second !== undefined) return { kind: "custom", site, customName: second.toLowerCase() };
      break;
    case "user":
      if (second !== undefined) return { kind: "user", site, userName: second.toLowerCase() };
      break;
  }
  return { kind: "other", site };
}

/**
 * A YouTube Shorts page (PROTECTION-SPEC §8): youtube.com (any subdomain) with `/shorts` as the first path segment,
 * including `/shorts/` without an id (the Shorts feed). youtu.be and /watch?v= addresses of a Short are ordinary
 * video pages and are not covered.
 */
export function isShortsUrl(url: string): boolean {
  let parsed: URL;
  try {
    parsed = new URL(url);
  } catch {
    return false;
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return false;
  const site = youTubeSiteOf(parsed.hostname);
  if (site !== "www" && site !== "m") return false;
  const first = parsed.pathname.split("/").find((s) => s.length > 0);
  return first !== undefined && unescape(first).toLowerCase() === "shorts";
}

/** Page kinds that need a channel identity before an allowlist decision. */
export const KINDS_NEEDING_CHANNEL: ReadonlySet<YouTubeUrlKind> = new Set<YouTubeUrlKind>([
  "channel",
  "handle",
  "custom",
  "user",
  "video",
  "shorts",
  "embed",
]);

/** YouTube channel ids: "UC" + 22 characters of the URL-safe base64 alphabet. */
export const CHANNEL_ID_RE = /^UC[A-Za-z0-9_-]{22}$/;

export function isChannelId(value: unknown): value is string {
  return typeof value === "string" && CHANNEL_ID_RE.test(value);
}

/** Lowercase with exactly one leading '@', or null. */
export function normalizeHandle(handle: string | null | undefined): string | null {
  if (typeof handle !== "string") return null;
  const trimmed = handle.trim().toLowerCase();
  if (trimmed.length === 0 || trimmed === "@") return null;
  return trimmed.startsWith("@") ? trimmed : `@${trimmed}`;
}
