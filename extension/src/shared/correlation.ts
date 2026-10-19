import type { ChunkMessage, IncomingMessage, IncomingType } from "./protocol";
import { parseIncoming } from "./protocol";

/** Timer functions, injectable so tests can use fake timers or a manual clock. */
export interface Timers {
  setTimeout(fn: () => void, ms: number): unknown;
  clearTimeout(handle: unknown): void;
}

export const realTimers: Timers = {
  setTimeout: (fn, ms) => setTimeout(fn, ms),
  clearTimeout: (handle) => clearTimeout(handle as ReturnType<typeof setTimeout>),
};

export class RequestTimeoutError extends Error {
  constructor(readonly requestType: string, readonly ms: number) {
    super(`No reply to ${requestType} within ${ms} ms`);
    this.name = "RequestTimeoutError";
  }
}

/** The host or service answered with an `error` message (other than the "ok" acknowledgement). */
export class HostError extends Error {
  constructor(readonly code: string, message: string) {
    super(`${code}: ${message}`);
    this.name = "HostError";
  }
}

export class DisconnectedError extends Error {
  constructor(reason: string) {
    super(reason);
    this.name = "DisconnectedError";
  }
}

interface Pending {
  requestType: string;
  expect: readonly IncomingType[];
  resolve(message: IncomingMessage): void;
  reject(error: Error): void;
  timer: unknown;
}

/**
 * Matches replies to requests by `id`. Every request gets a fresh id; a reply with a known id settles it (an `error`
 * reply rejects it, except the service's `{type:"error", code:"ok"}` acknowledgement of reportBlock). Messages without
 * a known id are pushes and are left to the caller.
 */
export class Correlator {
  private readonly pending = new Map<string, Pending>();
  private counter = 0;

  constructor(
    private readonly timeoutMs = 15_000,
    private readonly timers: Timers = realTimers,
    private readonly prefix = Math.random().toString(36).slice(2, 8),
  ) {}

  get size(): number {
    return this.pending.size;
  }

  nextId(): string {
    this.counter += 1;
    return `${this.prefix}-${this.counter}`;
  }

  /** Registers a pending request; the returned promise settles on the reply, a timeout, or failAll. */
  track(id: string, requestType: string, expect: IncomingType | readonly IncomingType[]): Promise<IncomingMessage> {
    const expected = typeof expect === "string" ? [expect] : expect;
    return new Promise<IncomingMessage>((resolve, reject) => {
      const timer = this.timers.setTimeout(() => {
        if (this.pending.delete(id)) reject(new RequestTimeoutError(requestType, this.timeoutMs));
      }, this.timeoutMs);
      this.pending.set(id, { requestType, expect: expected, resolve, reject, timer });
    });
  }

  /** Settles the request this message answers. Returns false when it is not a reply (a push or an unknown id). */
  settle(message: IncomingMessage): boolean {
    const id = message.id;
    if (typeof id !== "string") return false;
    const entry = this.pending.get(id);
    if (!entry) return false;
    this.pending.delete(id);
    this.timers.clearTimeout(entry.timer);
    if (message.type === "error" && !(entry.expect.includes("error") && message.code === "ok")) {
      entry.reject(new HostError(message.code, message.message));
    } else if (!entry.expect.includes(message.type)) {
      entry.reject(new HostError("unexpected_reply", `Expected ${entry.expect.join("|")} for ${entry.requestType}, got ${message.type}`));
    } else {
      entry.resolve(message);
    }
    return true;
  }

  /** Rejects every pending request (port closed). */
  failAll(error: Error): void {
    const entries = [...this.pending.values()];
    this.pending.clear();
    for (const entry of entries) {
      this.timers.clearTimeout(entry.timer);
      entry.reject(error);
    }
  }
}

/** Same cap as MessageFraming.MaxFrame on the C# side. */
export const MAX_REASSEMBLED_BYTES = 64 * 1024 * 1024;

interface PartialFrame {
  count: number;
  parts: (Uint8Array | undefined)[];
  received: number;
  bytes: number;
}

export function decodeBase64(data: string): Uint8Array {
  const binary = atob(data);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

/**
 * Reassembles host→extension frames over the browser's 1 MB limit. The native host (BrowserWriter.cs) splits the
 * original UTF-8 JSON into raw slices and sends each as `{type:"chunk", chunkOf, index, count, data: base64(slice)}`.
 * Slices are decoded separately (their base64 cannot be concatenated) and joined as bytes, so a multi-byte character
 * split across chunks survives. Malformed input discards that frame; it never throws.
 */
export class ChunkAssembler {
  private readonly frames = new Map<string, PartialFrame>();
  /** Diagnostics: why the last frame was dropped. */
  lastError: string | null = null;

  constructor(private readonly maxBytes = MAX_REASSEMBLED_BYTES, private readonly maxFrames = 8) {}

  get partialCount(): number {
    return this.frames.size;
  }

  /** Adds one chunk; returns the parsed original message once all parts arrived, otherwise null. */
  add(chunk: ChunkMessage): IncomingMessage | null {
    const { chunkOf, index, count } = chunk;
    if (typeof chunkOf !== "string" || !Number.isInteger(count) || count < 1 || !Number.isInteger(index) || index < 0 || index >= count) {
      return this.drop(typeof chunkOf === "string" ? chunkOf : null, "malformed chunk header");
    }
    let frame = this.frames.get(chunkOf);
    if (frame && frame.count !== count) return this.drop(chunkOf, "chunk count changed");
    if (!frame) {
      if (this.frames.size >= this.maxFrames) {
        const oldest = this.frames.keys().next().value;
        if (oldest !== undefined) this.frames.delete(oldest);
      }
      frame = { count, parts: new Array<Uint8Array | undefined>(count), received: 0, bytes: 0 };
      this.frames.set(chunkOf, frame);
    }

    let bytes: Uint8Array;
    try {
      bytes = decodeBase64(typeof chunk.data === "string" ? chunk.data : "");
    } catch {
      return this.drop(chunkOf, "invalid base64");
    }
    const previous = frame.parts[index];
    if (previous) {
      frame.bytes -= previous.length;
    } else {
      frame.received += 1;
    }
    frame.parts[index] = bytes;
    frame.bytes += bytes.length;
    if (frame.bytes > this.maxBytes) return this.drop(chunkOf, "frame too large");
    if (frame.received < frame.count) return null;

    this.frames.delete(chunkOf);
    const joined = new Uint8Array(frame.bytes);
    let offset = 0;
    for (const part of frame.parts) {
      if (!part) return this.drop(chunkOf, "missing part");
      joined.set(part, offset);
      offset += part.length;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(joined));
    } catch {
      return this.drop(chunkOf, "reassembled frame is not valid UTF-8 JSON");
    }
    const message = parseIncoming(parsed);
    if (!message || message.type === "chunk") return this.drop(chunkOf, "reassembled frame is not a known message");
    this.lastError = null;
    return message;
  }

  clear(): void {
    this.frames.clear();
  }

  private drop(chunkOf: string | null, why: string): null {
    if (chunkOf !== null) this.frames.delete(chunkOf);
    this.lastError = why;
    return null;
  }
}

/** Reconnect delay after `attempt` consecutive failures: 1 s, 2 s, 4 s, … capped at 30 s. */
export function backoffDelay(attempt: number, baseMs = 1_000, maxMs = 30_000): number {
  const exp = Math.min(Math.max(0, Math.floor(attempt)), 16);
  return Math.min(maxMs, baseMs * 2 ** exp);
}
