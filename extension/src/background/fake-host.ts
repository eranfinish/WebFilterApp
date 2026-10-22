import type { HelloResult, IncomingMessage, OutgoingMessage, RulesMessage, TimeState, UrlDecisionFields } from "../shared/protocol";
import type { Connector, PortLike } from "./native-client";

/**
 * An in-process stand-in for the native host + service, speaking the agent protocol over a fake port.
 * Used by unit tests and by the test build's hooks (Playwright drives it inside the service worker), never by the
 * production bundle.
 */
export interface FakeHostScript {
  hello?: Partial<HelloResult>;
  rules?: Partial<RulesMessage>;
  /** Reply to heartbeats; defaults to the rules' time state. */
  heartbeat?: TimeState;
  requestAccess?: { result: string; requestId?: string | null };
  /** When set, replies whose JSON is longer than this many bytes are sent as `chunk` messages. */
  chunkBytes?: number;
  /** Reply to hello with an error instead. */
  failHello?: boolean;
  /** Never answer (to exercise timeouts). */
  silent?: boolean;
  /** The service's decision for reportBlock / decideUrl; defaults to a block with the reported reason (or blocked_rule). */
  decide?: Record<string, Partial<UrlDecisionFields>>;
  /** Answer reportBlock like services before v2 (error code "ok") and decideUrl as unsupported. */
  legacyReportAck?: boolean;
  /** Answer like the native host fallback (service down): reportBlock and decideUrl → error service_unavailable. */
  hostFallback?: boolean;
  /** Answer reportSearch / reportOverride like a service without the protection features (error unsupported). */
  noProtectionMessages?: boolean;
}

/** A rules reply as it goes over the wire: `features` may be absent (older services). */
export type WireRules = Omit<RulesMessage, "features"> & Partial<Pick<RulesMessage, "features">>;

export const DEFAULT_HELLO: HelloResult = {
  type: "helloResult",
  user: { accountId: "S-1-5-21-test", name: "noa", role: "child", isAdmin: false },
  mode: "allowlist",
  source: "service",
  serviceAvailable: true,
  rulesVersion: 1,
  serverVersion: "test",
};

export const DEFAULT_RULES: WireRules = {
  type: "rules",
  rulesVersion: 1,
  dynamicRules: [],
  timeUpSessionRules: [],
  youTube: { mode: "allowlist", allowed: [], blocked: [] },
  time: { state: "ok", remainingMinutes: 60, windowEndsAt: null },
};

type Listener<T extends unknown[]> = (...args: T) => void;

class FakePort implements PortLike {
  private readonly messageListeners: Listener<[unknown]>[] = [];
  private readonly disconnectListeners: Listener<[]>[] = [];
  closed = false;

  constructor(private readonly host: FakeHost) {}

  readonly onMessage = { addListener: (cb: Listener<[unknown]>) => void this.messageListeners.push(cb) };
  readonly onDisconnect = { addListener: (cb: Listener<[]>) => void this.disconnectListeners.push(cb) };

  postMessage(message: unknown): void {
    if (this.closed) throw new Error("Attempting to use a disconnected port object");
    // Round-trip through JSON like the real port.
    this.host.receive(JSON.parse(JSON.stringify(message)) as OutgoingMessage, this);
  }

  disconnect(): void {
    this.closed = true;
  }

  deliver(message: unknown): void {
    if (this.closed) return;
    const copy = JSON.parse(JSON.stringify(message)) as unknown;
    for (const cb of this.messageListeners) cb(copy);
  }

  /** The host side closed the port (host exited). */
  hangUp(): void {
    if (this.closed) return;
    this.closed = true;
    for (const cb of this.disconnectListeners) cb();
  }
}

export class FakeHost {
  /** Every message the extension sent, in order. */
  readonly received: OutgoingMessage[] = [];
  connections = 0;
  private port: FakePort | null = null;

  constructor(public script: FakeHostScript = {}, private readonly schedule: (fn: () => void) => void = (fn) => setTimeout(fn, 0)) {}

  readonly connector: Connector = () => {
    this.connections += 1;
    this.port = new FakePort(this);
    return this.port;
  };

  get isOpen(): boolean {
    return this.port !== null && !this.port.closed;
  }

  /** Sends a push (rulesChanged, timeState, accessDecided…) to the extension. */
  push(message: IncomingMessage | Record<string, unknown>): void {
    const port = this.port;
    if (port) this.schedule(() => this.send(port, { v: 1, ...message }));
  }

  /** Simulates the host process exiting. */
  hangUp(): void {
    this.port?.hangUp();
  }

  rules(): WireRules {
    return { ...DEFAULT_RULES, ...this.script.rules, type: "rules" };
  }

  receive(message: OutgoingMessage, port: FakePort): void {
    this.received.push(message);
    if (this.script.silent) return;
    const reply = this.replyTo(message);
    if (reply) this.schedule(() => this.send(port, { ...reply, v: 1, id: message.id }));
  }

  private replyTo(message: OutgoingMessage): Record<string, unknown> | null {
    switch (message.type) {
      case "hello":
        return this.script.failHello
          ? { type: "error", code: "internal_error", message: "hello failed" }
          : { ...DEFAULT_HELLO, ...this.script.hello, type: "helloResult" };
      case "getRules":
        return { ...this.rules() };
      case "heartbeat":
        return { type: "timeState", ...(this.script.heartbeat ?? this.rules().time) };
      case "requestAccess":
        return { type: "requestAccessResult", ...(this.script.requestAccess ?? { result: "created", requestId: "ar-1" }) };
      case "reportBlock":
      case "decideUrl": {
        if (this.script.hostFallback) return { type: "error", code: "service_unavailable", message: "The filtering service is not running." };
        if (this.script.legacyReportAck) {
          return message.type === "reportBlock"
            ? { type: "error", code: "ok", message: "recorded" }
            : { type: "error", code: "unsupported", message: "Unknown message type 'decideUrl'." };
        }
        const reported = message.type === "reportBlock" ? message.reason : undefined;
        const decided = this.script.decide?.[message.url] ?? {};
        const reason = decided.reason ?? reported ?? "blocked_rule";
        const aboutTime = reason === "blocked_time_window" || reason === "blocked_daily_limit";
        return {
          type: message.type === "reportBlock" ? "reportBlockResult" : "decideUrlResult",
          decision: "block",
          reason,
          canRequest: true,
          requestKind: reason.startsWith("blocked_youtube") ? "youtube" : aboutTime ? "time" : "url",
          ...decided,
        };
      }
      case "resolveYouTube":
        return { type: "resolveYouTubeResult" };
      case "reportSearch":
      case "reportOverride":
        // PROTECTION-SPEC §6: acknowledged with error "ok".
        return this.script.noProtectionMessages
          ? { type: "error", code: "unsupported", message: `Unknown message type '${message.type}'.` }
          : { type: "error", code: "ok", message: "recorded" };
      default:
        return { type: "error", code: "unsupported", message: `Unknown message type '${String((message as { type?: string }).type)}'.` };
    }
  }

  private send(port: FakePort, message: Record<string, unknown>): void {
    const limit = this.script.chunkBytes;
    const json = JSON.stringify(message);
    const bytes = new TextEncoder().encode(json);
    if (!limit || bytes.length <= limit) {
      port.deliver(message);
      return;
    }
    const count = Math.ceil(bytes.length / limit);
    const chunkOf = typeof message.id === "string" ? message.id : `push-${Math.random().toString(36).slice(2)}`;
    for (let i = 0; i < count; i++) {
      const slice = bytes.subarray(i * limit, Math.min(bytes.length, (i + 1) * limit));
      let binary = "";
      for (const b of slice) binary += String.fromCharCode(b);
      port.deliver({ type: "chunk", chunkOf, index: i, count, data: btoa(binary), v: 1 });
    }
  }
}
