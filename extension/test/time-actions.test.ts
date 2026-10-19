import { describe, expect, it } from "vitest";
import type { TimeState } from "../src/shared/protocol";
import { BADGE_COLORS, badgeFor, isTimeUp, localDateKey, nextWarning, remainingMinutes, timeTransition } from "../src/shared/time-actions";

const ok = (m: number): TimeState => ({ state: m <= 10 ? "warn" : "ok", remainingMinutes: m, windowEndsAt: null });

describe("badge", () => {
  it("shows minutes at 10 or fewer", () => {
    expect(badgeFor(ok(45), "service")).toEqual({ text: "", color: BADGE_COLORS.none });
    expect(badgeFor(ok(10), "service")).toEqual({ text: "10", color: BADGE_COLORS.warn });
    expect(badgeFor(ok(3), "host")).toEqual({ text: "3", color: BADGE_COLORS.warn });
    expect(badgeFor({ state: "expired", remainingMinutes: 0 }, "service")).toEqual({ text: "0", color: BADGE_COLORS.alert });
    expect(badgeFor({ state: "unlimited" }, "service").text).toBe("");
  });

  it("shows ! while fail-closed and nothing when inert", () => {
    expect(badgeFor(null, "disconnected").text).toBe("!");
    expect(badgeFor(null, "connecting").text).toBe("!");
    expect(badgeFor(ok(5), "inert").text).toBe("");
    expect(badgeFor(null, "starting").text).toBe("");
  });
});

describe("warnings 10/5/1", () => {
  const day = "2026-11-01";

  it("announces each threshold once as time runs down", () => {
    let memory = null;
    const out: (number | null)[] = [];
    for (const m of [30, 12, 10, 9, 8, 5, 5, 4, 2, 1, 1, 0]) {
      const step = nextWarning(ok(m), memory, day);
      memory = step.memory;
      out.push(step.notify);
    }
    expect(out).toEqual([null, null, 10, null, null, 5, null, null, null, 1, null, null]);
  });

  it("announces only the lowest when several are crossed at once", () => {
    const step = nextWarning(ok(4), null, day);
    expect(step.notify).toBe(5);
    expect(step.memory.sent).toEqual([10, 5]);
    expect(nextWarning(ok(4), step.memory, day).notify).toBeNull();
  });

  it("resets on a new day and re-arms after more time is granted", () => {
    const sent = { date: day, sent: [10, 5, 1] };
    expect(nextWarning(ok(1), sent, day).notify).toBeNull();
    expect(nextWarning(ok(10), sent, "2026-11-02").notify).toBe(10);
    // +15 granted at 1 minute left: 16 left re-arms all three.
    const granted = nextWarning(ok(16), sent, day);
    expect(granted.notify).toBeNull();
    expect(granted.memory.sent).toEqual([]);
    expect(nextWarning(ok(10), granted.memory, day).notify).toBe(10);
  });

  it("ignores states without a running limit", () => {
    expect(nextWarning({ state: "unlimited" }, null, day).notify).toBeNull();
    expect(nextWarning({ state: "expired", remainingMinutes: 0 }, null, day).notify).toBeNull();
    expect(nextWarning(null, null, day).notify).toBeNull();
  });
});

describe("transitions", () => {
  it("detects entering and leaving time-up", () => {
    expect(timeTransition(ok(1), { state: "expired", remainingMinutes: 0 })).toEqual({ enterTimeUp: true, leaveTimeUp: false });
    expect(timeTransition({ state: "expired" }, { state: "outside_window" })).toEqual({ enterTimeUp: false, leaveTimeUp: false });
    expect(timeTransition({ state: "outside_window" }, ok(15))).toEqual({ enterTimeUp: false, leaveTimeUp: true });
    expect(timeTransition(null, { state: "expired" })).toEqual({ enterTimeUp: true, leaveTimeUp: false });
    expect(isTimeUp({ state: "warn", remainingMinutes: 0 })).toBe(false);
    expect(remainingMinutes({ state: "ok", remainingMinutes: 7.9 })).toBe(7);
    expect(localDateKey(new Date(2026, 10, 1, 23, 59).getTime())).toBe("2026-11-01");
  });
});
