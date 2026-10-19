import type { TimeState } from "./protocol";

/** Minutes left at which the child is warned (MVP-PLAN §8). */
export const WARNING_THRESHOLDS = [10, 5, 1] as const;
export type WarningThreshold = (typeof WARNING_THRESHOLDS)[number];

export function isTimeUp(time: TimeState | null | undefined): boolean {
  return time?.state === "expired" || time?.state === "outside_window";
}

/** Minutes left today when there is a limit and browsing is currently allowed; otherwise null. */
export function remainingMinutes(time: TimeState | null | undefined): number | null {
  if (!time || (time.state !== "ok" && time.state !== "warn")) return null;
  return typeof time.remainingMinutes === "number" ? Math.max(0, Math.floor(time.remainingMinutes)) : null;
}

export type ConnectionState = "starting" | "inert" | "connecting" | "service" | "host" | "disconnected" | "error";

export interface Badge {
  text: string;
  color: string;
}

export const BADGE_COLORS = { warn: "#c47500", alert: "#c62828", none: "#5b6470" } as const;

/**
 * Toolbar badge: remaining minutes when 10 or fewer, "!" while a policy-installed extension has no profile
 * (fail-closed), nothing otherwise.
 */
export function badgeFor(time: TimeState | null | undefined, connection: ConnectionState): Badge {
  if (connection === "disconnected" || connection === "error" || connection === "connecting") {
    return { text: "!", color: BADGE_COLORS.alert };
  }
  if (connection !== "service" && connection !== "host") return { text: "", color: BADGE_COLORS.none };
  if (isTimeUp(time)) return { text: "0", color: BADGE_COLORS.alert };
  const left = remainingMinutes(time);
  if (left !== null && left <= WARNING_THRESHOLDS[0]) return { text: String(left), color: BADGE_COLORS.warn };
  return { text: "", color: BADGE_COLORS.none };
}

/** Which thresholds were already announced, per local date. */
export interface WarningMemory {
  date: string;
  sent: number[];
}

export interface WarningStep {
  /** The threshold to announce now, or null. */
  notify: WarningThreshold | null;
  memory: WarningMemory;
}

/**
 * Decides the 10/5/1-minute notifications. Each threshold is announced at most once per day; when several are crossed
 * at once (e.g. the browser starts with 4 minutes left) only the lowest is shown and the higher ones are marked as sent.
 * A threshold is re-armed when the remaining time rises above it again (a parent granted more time), so the child is
 * warned again before the extra time runs out.
 */
export function nextWarning(time: TimeState | null | undefined, memory: WarningMemory | null, today: string): WarningStep {
  let sent = memory && memory.date === today ? [...memory.sent] : [];
  const left = remainingMinutes(time);
  if (left === null) return { notify: null, memory: { date: today, sent } };

  sent = sent.filter((t) => left <= t);
  const crossed = WARNING_THRESHOLDS.filter((t) => left <= t && !sent.includes(t));
  if (crossed.length === 0) return { notify: null, memory: { date: today, sent } };
  const lowest = Math.min(...crossed) as WarningThreshold;
  const nowSent = [...new Set([...sent, ...WARNING_THRESHOLDS.filter((t) => t >= lowest && left <= t)])].sort((a, b) => b - a);
  return { notify: lowest, memory: { date: today, sent: nowSent } };
}

export interface TimeTransition {
  /** Time just ran out: install the time-up session rules and move open tabs to timeup.html. */
  enterTimeUp: boolean;
  /** Time is available again: remove the session rules and let time-up pages return. */
  leaveTimeUp: boolean;
}

export function timeTransition(previous: TimeState | null | undefined, next: TimeState | null | undefined): TimeTransition {
  const was = isTimeUp(previous);
  const now = isTimeUp(next);
  return { enterTimeUp: !was && now, leaveTimeUp: was && !now };
}

/** Local calendar date (YYYY-MM-DD) of a timestamp, used only to reset the once-per-day warnings. */
export function localDateKey(ms: number): string {
  const d = new Date(ms);
  const pad = (n: number): string => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}
