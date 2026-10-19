import { ChunkAssembler, Correlator, DisconnectedError, type Timers, realTimers } from "../shared/correlation";
import {
  type IncomingMessage,
  NATIVE_HOST_NAME,
  type OutgoingBody,
  REPLY_TYPES,
  type RequestBody,
  encodeRequest,
  parseIncoming,
} from "../shared/protocol";

/** The subset of chrome.runtime.Port the client uses, so tests and the test build can supply a fake host. */
export interface PortLike {
  postMessage(message: unknown): void;
  disconnect(): void;
  onMessage: { addListener(callback: (message: unknown) => void): void };
  onDisconnect: { addListener(callback: () => void): void };
}

export type Connector = () => PortLike;

/** chrome.runtime.connectNative to the WebFilter native host. */
export const nativeConnector: Connector = () => chrome.runtime.connectNative(NATIVE_HOST_NAME);

export interface NativeClientEvents {
  /** A message that is not a reply: rulesChanged, timeState, accessDecided (or an unsolicited error). */
  onPush(message: IncomingMessage): void;
  /** The port closed (host exited, not registered, or disconnect()). Not called for ports replaced by connect(). */
  onDisconnect(reason: string): void;
}

/**
 * One native messaging port with request/reply correlation and chunk reassembly. The port is kept open: it keeps the
 * service worker alive (spike S2) and carries the service's pushes.
 */
export class NativeClient {
  private port: PortLike | null = null;
  private generation = 0;
  private readonly correlator: Correlator;
  private readonly chunks = new ChunkAssembler();

  constructor(
    private connector: Connector,
    private readonly events: NativeClientEvents,
    timeoutMs = 15_000,
    timers: Timers = realTimers,
  ) {
    this.correlator = new Correlator(timeoutMs, timers);
  }

  get connected(): boolean {
    return this.port !== null;
  }

  get pendingCount(): number {
    return this.correlator.size;
  }

  setConnector(connector: Connector): void {
    this.connector = connector;
  }

  /** Opens a new port (closing any previous one silently). Failures surface through onDisconnect. */
  connect(): void {
    this.close("replaced by a new connection");
    const generation = ++this.generation;
    let port: PortLike;
    try {
      port = this.connector();
    } catch (error) {
      queueMicrotask(() => this.events.onDisconnect(`connect failed: ${errorText(error)}`));
      return;
    }
    this.port = port;
    port.onMessage.addListener((raw) => {
      if (generation === this.generation) this.receive(raw);
    });
    port.onDisconnect.addListener(() => {
      if (generation !== this.generation || this.port !== port) return;
      const reason = lastErrorText() ?? "native host disconnected";
      this.port = null;
      this.chunks.clear();
      this.correlator.failAll(new DisconnectedError(reason));
      this.events.onDisconnect(reason);
    });
  }

  /** Sends a request and resolves with its reply (rejects on an error reply, timeout or disconnect). */
  request(body: RequestBody): Promise<IncomingMessage> {
    const port = this.port;
    if (!port) return Promise.reject(new DisconnectedError("not connected"));
    const id = this.correlator.nextId();
    const reply = this.correlator.track(id, body.type, REPLY_TYPES[body.type]);
    try {
      port.postMessage(encodeRequest(body, id));
    } catch (error) {
      // postMessage throws when the port is already closed; the onDisconnect handler will follow.
      this.correlator.settle({ type: "error", id, code: "post_failed", message: errorText(error) });
    }
    return reply;
  }

  /** Sends a message without waiting for a reply (a reply, if any, arrives as a push). Returns false when not connected. */
  notify(body: OutgoingBody): boolean {
    if (!this.port) return false;
    try {
      this.port.postMessage(encodeRequest(body, this.correlator.nextId()));
      return true;
    } catch {
      return false;
    }
  }

  /** Closes the port and reports it through onDisconnect. */
  disconnect(reason = "disconnected by the extension"): void {
    if (!this.port) return;
    this.close(reason);
    this.events.onDisconnect(reason);
  }

  /** Closes the port without reporting a disconnect (restart, or a port replaced by connect()). */
  close(reason: string): void {
    const port = this.port;
    this.port = null;
    this.generation += 1;
    this.chunks.clear();
    this.correlator.failAll(new DisconnectedError(reason));
    if (port) {
      try {
        port.disconnect();
      } catch {
        // already closed
      }
    }
  }

  private receive(raw: unknown): void {
    let message = parseIncoming(raw);
    if (!message) return;
    if (message.type === "chunk") {
      message = this.chunks.add(message);
      if (!message) return;
    }
    if (!this.correlator.settle(message)) this.events.onPush(message);
  }
}

function lastErrorText(): string | null {
  try {
    const message = typeof chrome !== "undefined" ? chrome.runtime?.lastError?.message : undefined;
    return message ?? null;
  } catch {
    return null;
  }
}

export function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
