import type { ChannelRef, FilterMode, TimeState, YouTubePolicy } from "./protocol";
import type { ReasonCode } from "./reasons";
import { normalizeHandle, parseYouTubeUrl } from "./youtube-url";

/** What the content script reports (spike S4: channel id only from sources tied to the current URL). */
export interface YouTubePageReport {
  url: string;
  channelId?: string | null;
  /** Handle the page data confirmed for this URL, if any; the URL's own handle is used otherwise. */
  handle?: string | null;
}

/** What the background knows when it decides. */
export interface YouTubeContext {
  /** Not force-installed by policy: the extension does not filter. */
  inert: boolean;
  /** Hello + rules succeeded (from the service or the host fallback). */
  ready: boolean;
  filterMode: FilterMode | null;
  policy: YouTubePolicy | null;
  time: TimeState | null;
}

export type YouTubeDecisionKind = "allow" | "block" | "redirect";

export interface YouTubeDecision {
  decision: YouTubeDecisionKind;
  reason: ReasonCode;
  /** The channel the decision was about, when known (used for a "youtube" access request). */
  channelId?: string;
}

interface ChannelRecord {
  channelId: string;
  action: "allow" | "block";
}

function findRecord(policy: YouTubePolicy, channelId: string): ChannelRecord | null {
  // The service flattens member and global records into two lists. A channel in both is treated as blocked
  // (fail safe); see README "Decisions to review".
  if (policy.blocked.some((c) => c.channelId === channelId)) return { channelId, action: "block" };
  if (policy.allowed.some((c) => c.channelId === channelId)) return { channelId, action: "allow" };
  return null;
}

function channelByHandle(policy: YouTubePolicy, handle: string): ChannelRef | undefined {
  const wanted = normalizeHandle(handle);
  if (!wanted) return undefined;
  return [...policy.blocked, ...policy.allowed].find((c) => normalizeHandle(c.handle) === wanted);
}

/**
 * The YouTube decision of Decider.YouTube() (src/WebFilter.Core/Engine/Decider.cs; scenarios youtube-decisions.json,
 * README §5) computed from the last rules: unrestricted member → time → mode → channel.
 * Pause and homework are not visible to the extension; the service reflects them in `mode`, `youTube.mode` and `time`.
 */
export function decideYouTube(ctx: YouTubeContext, page: YouTubePageReport): YouTubeDecision {
  if (ctx.inert) return { decision: "allow", reason: "allowed_unrestricted" };
  if (!ctx.ready || !ctx.policy) return { decision: "block", reason: "blocked_service_unavailable" };
  if (ctx.filterMode === "unrestricted") return { decision: "allow", reason: "allowed_unrestricted" };
  if (ctx.time?.state === "outside_window") return { decision: "block", reason: "blocked_time_window" };
  if (ctx.time?.state === "expired") return { decision: "block", reason: "blocked_daily_limit" };

  const policy = ctx.policy;
  if (policy.mode === "unrestricted") return { decision: "allow", reason: "allowed_default" };
  if (policy.mode !== "restricted" && policy.mode !== "allowlist") return { decision: "block", reason: "blocked_youtube_off" };

  const parsed = parseYouTubeUrl(page.url);
  const handle = page.handle ?? parsed.handle ?? null;
  const channelId = page.channelId || parsed.channelId || (handle ? channelByHandle(policy, handle)?.channelId : undefined) || null;
  const record = channelId ? findRecord(policy, channelId) : null;
  const withChannel = (d: YouTubeDecision): YouTubeDecision => (channelId ? { ...d, channelId } : d);

  if (policy.mode === "restricted") {
    return record?.action === "block"
      ? withChannel({ decision: "block", reason: "blocked_youtube_channel" })
      : withChannel({ decision: "allow", reason: "allowed_default" });
  }

  // Allowlist mode.
  if (parsed.kind === "home" || parsed.kind === "search") return { decision: "redirect", reason: "redirect_youtube_home" };
  if (parsed.kind === "other" || parsed.kind === "playlist" || parsed.kind === "not_youtube") {
    return { decision: "allow", reason: "allowed_default" };
  }
  if (!channelId) return { decision: "block", reason: "blocked_youtube_unknown" };
  return record?.action === "allow"
    ? withChannel({ decision: "allow", reason: "allowed_youtube_channel" })
    : withChannel({ decision: "block", reason: "blocked_youtube_channel" });
}

/** The channels for "my channels": approved ones, minus any that are also blocked. */
export function approvedChannels(policy: YouTubePolicy | null): ChannelRef[] {
  if (!policy) return [];
  const blocked = new Set(policy.blocked.map((c) => c.channelId));
  const seen = new Set<string>();
  return policy.allowed.filter((c) => !blocked.has(c.channelId) && !seen.has(c.channelId) && seen.add(c.channelId));
}
