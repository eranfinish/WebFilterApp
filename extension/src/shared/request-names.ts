/**
 * The names a YouTube request carries to the parents (PROTECTION-SPEC §15): the channel's title and handle as the page
 * showed them, and the channel search it was found with. The same cleaning as RequestNames in
 * src/WebFilter.Core/Engine/AccessRequests.cs (the service cleans them again): whitespace and control characters become
 * single spaces, direction marks and overrides are dropped (format characters other than the emoji joiners), lengths are
 * bounded, and a handle is "@name" in lower case or nothing.
 */

export const MAX_TITLE = 100;
export const MAX_HANDLE = 101;
export const MAX_SEARCH_QUERY = 200;

const FORMAT = /\p{Cf}/u;
const HANDLE = /^@[\p{L}\p{Nd}._\-·]+$/u;

function clean(value: unknown, max: number): string | undefined {
  if (typeof value !== "string") return undefined;
  const text = [...value]
    .filter((c) => !FORMAT.test(c) || c === "‌" || c === "‍")
    .map((c) => (/[\p{Cc}\s]/u.test(c) ? " " : c))
    .join("")
    .split(" ")
    .filter((part) => part.length > 0)
    .join(" ");
  if (!text) return undefined;
  return text.length <= max ? text : text.slice(0, max).trimEnd();
}

export function cleanTitle(value: unknown): string | undefined {
  return clean(value, MAX_TITLE);
}

export function cleanSearchQuery(value: unknown): string | undefined {
  return clean(value, MAX_SEARCH_QUERY);
}

export function cleanHandle(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  let text = value.trim();
  if (!text) return undefined;
  if (!text.startsWith("@")) text = `@${text}`;
  if (text.length < 2 || text.length > MAX_HANDLE || !HANDLE.test(text)) return undefined;
  return text.toLowerCase();
}

/** "NASA (@nasa)", the title or the handle alone, or undefined when neither is known (YouTubeChannelLabel.Name in C#). */
export function channelLabel(title: string | null | undefined, handle: string | null | undefined): string | undefined {
  const t = title?.trim() || undefined;
  const h = handle?.trim() || undefined;
  if (t && h) return `${t} (${h})`;
  return t ?? h;
}
