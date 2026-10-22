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
    case "search":
      return { kind: "search", site };
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
