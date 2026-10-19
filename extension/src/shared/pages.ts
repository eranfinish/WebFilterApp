import { isWebUrl } from "./protocol";

/**
 * URLs of the extension pages and what they carry.
 *
 * blocked.html accepts:
 *   ?reason=failclosed                          static `failclosed` ruleset (extensionPath)
 *   #<original URL>                             compiled rule with regexSubstitution `…/blocked.html#\0` (spike S3)
 *   ?reason=<code>[&rule=<ruleId>]#<original>   compiled rule that also names its reason (preferred)
 *   ?reason=<code>&channel=<UC…>#<original>     YouTube content script
 * The original URL is everything after the first '#', unencoded (DNR substitutes it verbatim).
 *
 * timeup.html#<original URL> — a tab that was open when time ran out.
 */

export const BLOCK_PAGE = "blocked.html";
export const TIMEUP_PAGE = "timeup.html";
export const MY_CHANNELS_PAGE = "my-channels.html";

export interface BlockPageInfo {
  reason: string | null;
  ruleId: string | null;
  channelId: string | null;
  /** The original URL, or "" when none was passed. */
  url: string;
}

function splitHash(href: string): { base: string; hash: string } {
  const at = href.indexOf("#");
  return at < 0 ? { base: href, hash: "" } : { base: href.slice(0, at), hash: href.slice(at + 1) };
}

export function parseBlockPageUrl(href: string): BlockPageInfo {
  const { base, hash } = splitHash(href);
  const q = base.indexOf("?");
  const params = new URLSearchParams(q < 0 ? "" : base.slice(q + 1));
  const nonEmpty = (v: string | null): string | null => (v && v.length > 0 ? v : null);
  return {
    reason: nonEmpty(params.get("reason")),
    ruleId: nonEmpty(params.get("rule")),
    channelId: nonEmpty(params.get("channel")),
    url: hash,
  };
}

export function buildBlockPageUrl(
  pageUrl: string,
  info: { reason: string; url: string; channelId?: string | null; ruleId?: string | null },
): string {
  const params = new URLSearchParams({ reason: info.reason });
  if (info.ruleId) params.set("rule", info.ruleId);
  if (info.channelId) params.set("channel", info.channelId);
  return `${pageUrl}?${params.toString()}#${info.url}`;
}

export function buildTimeUpUrl(pageUrl: string, originalUrl: string | null | undefined): string {
  return originalUrl ? `${pageUrl}#${originalUrl}` : pageUrl;
}

export function originalUrlFromHash(href: string): string {
  return splitHash(href).hash;
}

/**
 * The URL a page may navigate back to: only http(s). The hash of a web-accessible page can be set by any web page,
 * so anything else (javascript:, data:, chrome:) is refused.
 */
export function safeReturnUrl(url: string | null | undefined): string | null {
  if (!url || !isWebUrl(url)) return null;
  try {
    const parsed = new URL(url);
    return parsed.protocol === "http:" || parsed.protocol === "https:" ? parsed.href : null;
  } catch {
    return null;
  }
}

/** A readable form of a URL for display (percent-decoded when possible); never used for navigation. */
export function displayUrl(url: string): string {
  try {
    return decodeURI(url);
  } catch {
    return url;
  }
}
