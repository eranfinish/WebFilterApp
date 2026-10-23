import { winningRule } from "./dnr-match";
import type { DnrRule, FilterMode, TimeState } from "./protocol";
import { type ReasonCode, inferReason } from "./reasons";
import type { ConnectionState } from "./time-actions";

/**
 * Rule layout of the compiler (src/WebFilter.Core/Dnr/DnrPriorities.cs, DnrRuleIds; merged on v2). Compiled block rules
 * redirect with the original URL only, so the block page derives a reason from the rule that matched.
 */
export const DNR_LAYOUT = {
  ids: { allowlistDefault: 1, ipLiteralGate: 2, youTube: 3, youTubeShort: 4, timeUpBlock: 10 },
  bands: { global: 1 << 16, member: 2 << 16, youTube: 3 << 16, timeUp: 4 << 16 },
} as const;

export interface ExplainContext {
  connection: ConnectionState;
  mode: FilterMode | null;
  time: TimeState | null;
}

const isBlocking = (rule: DnrRule | null): rule is DnrRule => rule?.action.type === "block" || rule?.action.type === "redirect";

/**
 * The reason a navigation to `url` was blocked, as far as the installed rules tell:
 *   host fallback (service down)        → blocked_service_unavailable
 *   time-up session rule                → blocked_daily_limit / blocked_time_window (from the time state)
 *   id 1, allowlist default             → blocked_not_in_allowlist
 *   id 2, IP-literal gate               → blocked_ip_literal
 *   YouTube host rule (ids 3, 4)        → blocked_youtube_off
 *   member or global band               → blocked_rule (category domains share rules with global rules, so
 *                                          blocked_category cannot be told apart here)
 *   a parent's baseline (mode baseline) → blocked_baseline_adult (its only block rules are the adult list, §13)
 * Anything else falls back to the time state and the filter mode.
 */
export function explainBlock(
  url: string,
  dynamicRules: readonly DnrRule[],
  sessionRules: readonly DnrRule[],
  ctx: ExplainContext,
  resourceType: "main_frame" | "sub_frame" = "main_frame",
): ReasonCode {
  if (ctx.connection === "host") return "blocked_service_unavailable";
  if (ctx.mode === "baseline") return "blocked_baseline_adult";
  const session = winningRule(sessionRules, url, resourceType);
  const dynamic = winningRule(dynamicRules, url, resourceType);
  // Session rules sit above every dynamic rule in the layout; compare priorities anyway.
  const sessionWins = isBlocking(session) && (session.priority ?? 1) >= (dynamic?.priority ?? 0);
  if (sessionWins) return ctx.time?.state === "outside_window" ? "blocked_time_window" : "blocked_daily_limit";
  if (!isBlocking(dynamic)) return inferReason(ctx.time, ctx.mode);

  const { ids, bands } = DNR_LAYOUT;
  if (dynamic.id === ids.allowlistDefault) return "blocked_not_in_allowlist";
  if (dynamic.id === ids.ipLiteralGate) return "blocked_ip_literal";
  if (dynamic.id === ids.youTube || dynamic.id === ids.youTubeShort) return "blocked_youtube_off";
  const priority = dynamic.priority ?? 1;
  if (priority >= bands.youTube && priority < bands.timeUp) return "blocked_youtube_off";
  if (priority >= bands.global && priority < bands.youTube) return "blocked_rule";
  return inferReason(ctx.time, ctx.mode);
}
