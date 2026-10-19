import type { FilterMode, RequestResult, TimeState } from "./protocol";

/** The closed list of reason codes (MVP-PLAN §6, CLAUDE.md rule 8). Order matches ReasonCode in Decision.cs. */
export const REASON_CODES = [
  "allowed_unrestricted",
  "allowed_paused",
  "allowed_always",
  "allowed_rule",
  "allowed_category",
  "allowed_default",
  "allowed_homework",
  "allowed_youtube_channel",
  "allowed_browser",
  "blocked_rule",
  "blocked_category",
  "blocked_not_in_allowlist",
  "blocked_time_window",
  "blocked_daily_limit",
  "blocked_homework",
  "blocked_ip_literal",
  "blocked_youtube_channel",
  "blocked_youtube_unknown",
  "blocked_youtube_off",
  "blocked_service_unavailable",
  "blocked_app_network",
  "redirect_youtube_home",
] as const;

export type ReasonCode = (typeof REASON_CODES)[number];

/**
 * Not a reason code: the static `failclosed` ruleset redirects to `blocked.html?reason=failclosed` while the extension
 * has no confirmed profile from the service (starting up, host missing, or the port closed).
 */
export const FAILCLOSED = "failclosed";

const KNOWN: ReadonlySet<string> = new Set<string>(REASON_CODES);

export function isReasonCode(value: unknown): value is ReasonCode {
  return typeof value === "string" && KNOWN.has(value);
}

/** The _locales message name that explains a reason in plain language. */
export function reasonMessageKey(reason: string | null | undefined): string {
  if (reason === FAILCLOSED) return "reason_failclosed";
  return isReasonCode(reason) ? `reason_${reason}` : "reason_unknown";
}

/** What the block page offers for a reason: ask for a site, a YouTube channel, more time, or nothing. */
export type RequestAction = "url" | "youtube" | "time" | "none";

export function requestActionFor(reason: string | null | undefined): RequestAction {
  switch (reason) {
    case "blocked_rule":
    case "blocked_category":
    case "blocked_not_in_allowlist":
    case "blocked_ip_literal":
      return "url";
    case "blocked_youtube_channel":
      return "youtube";
    case "blocked_time_window":
    case "blocked_daily_limit":
      return "time";
    // A personal allow rule does not help during homework mode, an unknown channel or YouTube "off"; the service being
    // down means requests answer "unavailable" (MVP-PLAN §8.1: no request button).
    case "blocked_homework":
    case "blocked_youtube_unknown":
    case "blocked_youtube_off":
    case "blocked_service_unavailable":
    case "blocked_app_network":
    case FAILCLOSED:
      return "none";
    default:
      return isReasonCode(reason) ? "none" : "url";
  }
}

/** The reason to show when a block-page URL carries none (a compiled rule that only passes the original URL). */
export function inferReason(time: TimeState | null, mode: FilterMode | null): ReasonCode {
  if (time?.state === "expired") return "blocked_daily_limit";
  if (time?.state === "outside_window") return "blocked_time_window";
  if (mode === "allowlist") return "blocked_not_in_allowlist";
  return "blocked_rule";
}

/** reportBlock carries a reason code; the fail-closed page reports as blocked_service_unavailable. */
export function reportableReason(reason: string): ReasonCode | null {
  if (reason === FAILCLOSED) return "blocked_service_unavailable";
  return isReasonCode(reason) ? reason : null;
}

export const REQUEST_RESULTS: readonly RequestResult[] = ["created", "merged", "rate_limited", "cooldown", "queued", "unavailable"];

export function requestResultMessageKey(result: string | null | undefined): string {
  return typeof result === "string" && (REQUEST_RESULTS as readonly string[]).includes(result) ? `result_${result}` : "result_error";
}
