import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  ChunkAssembler,
  Correlator,
  DisconnectedError,
  HostError,
  RequestTimeoutError,
  backoffDelay,
} from "../src/shared/correlation";
import type { ChunkMessage, IncomingMessage } from "../src/shared/protocol";

/** Splits a message the way the native host's BrowserWriter does: raw UTF-8 slices, each base64-encoded. */
function chunk(message: object, sliceBytes: number, chunkOf = "c1"): ChunkMessage[] {
  const bytes = Buffer.from(JSON.stringify(message), "utf8");
  const count = Math.ceil(bytes.length / sliceBytes);
  return Array.from({ length: count }, (_, i) => ({
    type: "chunk",
    chunkOf,
    index: i,
    count,
    data: bytes.subarray(i * sliceBytes, (i + 1) * sliceBytes).toString("base64"),
    v: 1,
  }));
}

describe("Correlator", () => {
  beforeEach(() => void vi.useFakeTimers());
  afterEach(() => void vi.useRealTimers());

  it("resolves the request whose id the reply echoes, and leaves pushes alone", async () => {
    const c = new Correlator(1000);
    const a = c.nextId();
    const b = c.nextId();
    expect(a).not.toBe(b);
    const pa = c.track(a, "hello", "helloResult");
    const pb = c.track(b, "getRules", "rules");
    expect(c.settle({ type: "rules", id: b } as IncomingMessage)).toBe(true);
    expect(c.settle({ type: "helloResult", id: a } as IncomingMessage)).toBe(true);
    await expect(pa).resolves.toMatchObject({ type: "helloResult" });
    await expect(pb).resolves.toMatchObject({ type: "rules" });
    expect(c.settle({ type: "timeState", state: "ok" } as IncomingMessage)).toBe(false); // push: no id
    expect(c.settle({ type: "timeState", state: "ok", id: "other" } as IncomingMessage)).toBe(false); // unknown id
    expect(c.size).toBe(0);
  });

  it("rejects on an error reply but accepts the service's ok acknowledgement of reportBlock", async () => {
    const c = new Correlator(1000);
    const report = c.track("r", "reportBlock", "error");
    const hello = c.track("h", "hello", "helloResult");
    c.settle({ type: "error", id: "r", code: "ok", message: "recorded" });
    c.settle({ type: "error", id: "h", code: "service_unavailable", message: "down" });
    await expect(report).resolves.toMatchObject({ code: "ok" });
    await expect(hello).rejects.toBeInstanceOf(HostError);
    await expect(hello).rejects.toMatchObject({ code: "service_unavailable" });
  });

  it("rejects a reply of the wrong type", async () => {
    const c = new Correlator(1000);
    const p = c.track("1", "hello", "helloResult");
    c.settle({ type: "rules", id: "1" } as IncomingMessage);
    await expect(p).rejects.toMatchObject({ code: "unexpected_reply" });
  });

  it("times out", async () => {
    const c = new Correlator(500);
    const p = c.track("1", "heartbeat", "timeState");
    const check = expect(p).rejects.toBeInstanceOf(RequestTimeoutError);
    await vi.advanceTimersByTimeAsync(500);
    await check;
    expect(c.size).toBe(0);
    expect(c.settle({ type: "timeState", id: "1" } as IncomingMessage)).toBe(false); // late reply is ignored
  });

  it("failAll rejects everything pending", async () => {
    const c = new Correlator(1000);
    const p1 = c.track("1", "hello", "helloResult");
    const p2 = c.track("2", "getRules", "rules");
    c.failAll(new DisconnectedError("port closed"));
    await expect(p1).rejects.toBeInstanceOf(DisconnectedError);
    await expect(p2).rejects.toThrow("port closed");
  });
});

describe("ChunkAssembler", () => {
  const big = {
    type: "rules",
    id: "42",
    rulesVersion: 3,
    dynamicRules: Array.from({ length: 200 }, (_, i) => ({ id: i + 1, action: { type: "block" }, condition: { requestDomains: [`ד${i}.example`] } })),
    youTube: { mode: "allowlist", allowed: [{ channelId: "UCkhanKids00000000000001", title: "ערוץ עם עברית 🎈" }], blocked: [] },
    time: { state: "ok", remainingMinutes: 30 },
  };

  it("reassembles chunks, including a multi-byte character split across slices", () => {
    const parts = chunk(big, 7); // tiny slices: Hebrew letters and the emoji are split mid-character
    expect(parts.length).toBeGreaterThan(100);
    const a = new ChunkAssembler();
    let result: IncomingMessage | null = null;
    for (const p of parts) result = a.add(p);
    expect(result).toMatchObject({ type: "rules", id: "42", rulesVersion: 3 });
    expect((result as { youTube: { allowed: { title: string }[] } }).youTube.allowed[0]!.title).toBe("ערוץ עם עברית 🎈");
    expect(a.partialCount).toBe(0);
  });

  it("accepts chunks out of order and ignores duplicates", () => {
    const parts = chunk(big, 1000);
    const a = new ChunkAssembler();
    const shuffled = [...parts].reverse();
    let result: IncomingMessage | null = null;
    for (const p of [shuffled[0]!, ...shuffled]) result = a.add(p) ?? result;
    expect(result).toMatchObject({ type: "rules", id: "42" });
  });

  it("keeps two frames apart by chunkOf", () => {
    const one = chunk({ type: "timeState", state: "warn", remainingMinutes: 5 }, 10, "a");
    const two = chunk({ type: "rulesChanged", rulesVersion: 9 }, 10, "b");
    const a = new ChunkAssembler();
    const out: IncomingMessage[] = [];
    for (let i = 0; i < Math.max(one.length, two.length); i++) {
      for (const p of [one[i], two[i]]) {
        const m = p ? a.add(p) : null;
        if (m) out.push(m);
      }
    }
    expect(out.map((m) => m.type).sort()).toEqual(["rulesChanged", "timeState"]);
  });

  it("drops malformed frames without throwing", () => {
    const a = new ChunkAssembler();
    expect(a.add({ type: "chunk", chunkOf: "x", index: 2, count: 2, data: "" })).toBeNull();
    expect(a.lastError).toBe("malformed chunk header");
    expect(a.add({ type: "chunk", chunkOf: "x", index: 0, count: 1, data: "!!!" })).toBeNull();
    expect(a.lastError).toBe("invalid base64");
    expect(a.add({ type: "chunk", chunkOf: "y", index: 0, count: 1, data: Buffer.from("{not json").toString("base64") })).toBeNull();
    expect(a.lastError).toMatch(/not valid UTF-8 JSON/);
    expect(a.add({ type: "chunk", chunkOf: "z", index: 0, count: 1, data: Buffer.from('{"type":"chunk"}').toString("base64") })).toBeNull();
    const parts = chunk({ type: "rulesChanged", rulesVersion: 1 }, 5, "w");
    a.add(parts[0]!);
    expect(a.add({ ...parts[1]!, count: parts[1]!.count + 1 })).toBeNull();
    expect(a.lastError).toBe("chunk count changed");
    expect(a.partialCount).toBe(0);
  });

  it("enforces the size cap", () => {
    const a = new ChunkAssembler(100);
    const parts = chunk(big, 60);
    let result: IncomingMessage | null = null;
    for (const p of parts) result = a.add(p) ?? result;
    expect(result).toBeNull();
    expect(a.lastError).toBe("frame too large");
  });
});

describe("backoffDelay", () => {
  it("doubles from 1 s and caps at 30 s", () => {
    expect([0, 1, 2, 3, 4, 5, 6, 50].map((n) => backoffDelay(n))).toEqual([1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000]);
  });
});
