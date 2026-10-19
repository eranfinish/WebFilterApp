/**
 * Agent protocol (MVP-PLAN §10.1) as seen by the extension. Mirrors src/WebFilter.Contracts/Agent/AgentMessages.cs:
 * camelCase JSON, discriminator `type`, envelope `v` (protocol version) and `id` (replies echo the request id).
 *
 * The C# side deserializes with System.Text.Json polymorphism, which requires `type` to be the FIRST property of every
 * message the extension sends (see encodeRequest).
 */

export const PROTOCOL_VERSION = 1;
export const NATIVE_HOST_NAME = "com.webfilter.host";

export type FilterMode = "unrestricted" | "blocklist" | "allowlist";
export type YouTubeMode = "off" | "restricted" | "allowlist" | "unrestricted";
export type TimeStateName = "ok" | "warn" | "expired" | "outside_window" | "unlimited";
export type RequestKind = "url" | "youtube" | "app" | "time";
export type RequestResult = "created" | "merged" | "rate_limited" | "cooldown" | "queued" | "unavailable";
export type BrowserName = "edge" | "chrome";

export type DnrRule = chrome.declarativeNetRequest.Rule;

// ---- extension → host ---------------------------------------------------------------------------------------------

export interface HelloRequest {
  type: "hello";
  extVersion: string;
  browser: BrowserName;
  extensionId?: string;
}

export interface GetRulesRequest {
  type: "getRules";
  knownVersion?: number;
}

export interface HeartbeatRequest {
  type: "heartbeat";
  focused: boolean;
  /** chrome.idle state: active | idle | locked. */
  idle: string;
  hasWebTab: boolean;
}

export interface ReportBlockRequest {
  type: "reportBlock";
  url: string;
  reason: string;
  ruleId?: string;
}

export interface RequestAccessRequest {
  type: "requestAccess";
  kind: RequestKind;
  target?: string;
  minutes?: number;
  note?: string;
}

/** Asks why a URL is blocked without recording anything (v2: AgentMessages.DecideUrlRequest). */
export interface DecideUrlRequest {
  type: "decideUrl";
  url: string;
}

export interface ResolveYouTubeRequest {
  type: "resolveYouTube";
  handle?: string;
  videoId?: string;
}

/**
 * An error report from the extension (e.g. DNR rules the browser refused). `error` is part of the C# message union, so the
 * service accepts it; today it answers "unsupported", which the extension ignores.
 */
export interface ErrorReport {
  type: "error";
  code: string;
  message: string;
}

export type RequestBody =
  | HelloRequest
  | GetRulesRequest
  | HeartbeatRequest
  | ReportBlockRequest
  | RequestAccessRequest
  | DecideUrlRequest
  | ResolveYouTubeRequest;

export type OutgoingBody = RequestBody | ErrorReport;

export type OutgoingMessage = OutgoingBody & { v: number; id: string };

// ---- host → extension ---------------------------------------------------------------------------------------------

interface Envelope {
  v?: number;
  id?: string | null;
}

export interface AgentUser {
  /** Opaque (a SID on Windows); never interpreted by the extension. */
  accountId: string;
  name: string;
  role: string;
  isAdmin: boolean;
}

export interface HelloResult extends Envelope {
  type: "helloResult";
  user: AgentUser;
  mode: FilterMode;
  /** "service", or "host" when the native host answers by itself because the service is down. */
  source: "service" | "host";
  serviceAvailable: boolean;
  rulesVersion: number;
  serverVersion: string;
}

export interface ChannelRef {
  channelId: string;
  handle?: string | null;
  title?: string | null;
}

export interface YouTubePolicy {
  mode: YouTubeMode;
  allowed: ChannelRef[];
  blocked: ChannelRef[];
}

export interface TimeState {
  state: TimeStateName;
  remainingMinutes?: number | null;
  /** Local "HH:MM" (settings time zone) of the end of the current window, or null. */
  windowEndsAt?: string | null;
}

export interface RulesMessage extends Envelope {
  type: "rules";
  rulesVersion: number;
  dynamicRules: DnrRule[];
  timeUpSessionRules: DnrRule[];
  /** C# property `YouTube` → camelCase `youTube` (not `youtube`). */
  youTube: YouTubePolicy;
  time: TimeState;
}

export interface TimeStateMessage extends Envelope, TimeState {
  type: "timeState";
}

export interface RequestAccessResult extends Envelope {
  type: "requestAccessResult";
  result: RequestResult;
  requestId?: string | null;
}

export interface ResolveYouTubeResult extends Envelope {
  type: "resolveYouTubeResult";
  channelId?: string | null;
}

export interface RulesChangedMessage extends Envelope {
  type: "rulesChanged";
  rulesVersion: number;
}

export interface AccessDecidedMessage extends Envelope {
  type: "accessDecided";
  requestId: string;
  status: "approved" | "denied";
  until?: string | null;
}

/** What the service decides for a URL (v2: ReportBlockResult / DecideUrlResponse in AgentMessages.cs). */
export interface UrlDecisionFields {
  decision: "allow" | "block";
  /** The service's own reason code; for YouTube channel reasons the reported reason stands. */
  reason: string;
  ruleId?: string | null;
  /** Set for blocked_category, e.g. "social". */
  category?: string | null;
  /** The user may send the parents a request (a linked family member). */
  canRequest: boolean;
  /** url | time | youtube. */
  requestKind: "url" | "time" | "youtube" | string;
}

/**
 * Reply to reportBlock: the service records ITS reason (the extension's is a guess from the rules). decision "allow"
 * means the extension's rules are stale: nothing is recorded and rulesChanged is pushed.
 */
export interface ReportBlockResult extends Envelope, UrlDecisionFields {
  type: "reportBlockResult";
}

/** Reply to decideUrl (nothing recorded). */
export interface DecideUrlResult extends Envelope, UrlDecisionFields {
  type: "decideUrlResult";
}

export interface ErrorMessage extends Envelope {
  type: "error";
  code: string;
  message: string;
}

export interface ChunkMessage extends Envelope {
  type: "chunk";
  chunkOf: string;
  index: number;
  count: number;
  /** Base64 of a slice of the original frame's UTF-8 JSON. */
  data: string;
}

export type IncomingMessage =
  | HelloResult
  | RulesMessage
  | TimeStateMessage
  | RequestAccessResult
  | ResolveYouTubeResult
  | RulesChangedMessage
  | AccessDecidedMessage
  | ReportBlockResult
  | DecideUrlResult
  | ErrorMessage
  | ChunkMessage;

export type IncomingType = IncomingMessage["type"];

const INCOMING_TYPES: ReadonlySet<string> = new Set<IncomingType>([
  "helloResult",
  "rules",
  "timeState",
  "requestAccessResult",
  "resolveYouTubeResult",
  "rulesChanged",
  "accessDecided",
  "reportBlockResult",
  "decideUrlResult",
  "error",
  "chunk",
]);

/**
 * The reply types each request accepts. reportBlock is answered with `reportBlockResult` (v2); services before that
 * acknowledged it with `{type:"error", code:"ok"}`, which is still accepted.
 */
export const REPLY_TYPES: Readonly<Record<RequestBody["type"], readonly IncomingType[]>> = {
  hello: ["helloResult"],
  getRules: ["rules"],
  heartbeat: ["timeState"],
  reportBlock: ["reportBlockResult", "error"],
  requestAccess: ["requestAccessResult"],
  decideUrl: ["decideUrlResult"],
  resolveYouTube: ["resolveYouTubeResult"],
};

/** Builds the wire object with `type` first, then `v` and `id`; undefined fields are dropped. */
export function encodeRequest(body: OutgoingBody, id: string): OutgoingMessage {
  const out: Record<string, unknown> = { type: body.type, v: PROTOCOL_VERSION, id };
  for (const [key, value] of Object.entries(body)) {
    if (key !== "type" && value !== undefined) out[key] = value;
  }
  return out as unknown as OutgoingMessage;
}

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);

/** Accepts a host message if it is an object with a known `type`; anything else is dropped (returns null). */
export function parseIncoming(value: unknown): IncomingMessage | null {
  if (!isObject(value) || typeof value.type !== "string" || !INCOMING_TYPES.has(value.type)) return null;
  if (value.type === "rules") return normalizeRules(value);
  return value as unknown as IncomingMessage;
}

const TIME_STATES: ReadonlySet<string> = new Set<TimeStateName>(["ok", "warn", "expired", "outside_window", "unlimited"]);
const YOUTUBE_MODES: ReadonlySet<string> = new Set<YouTubeMode>(["off", "restricted", "allowlist", "unrestricted"]);

/** A time state with a known `state`; an unknown state is treated as expired (fail closed). */
export function normalizeTime(value: unknown): TimeState {
  if (!isObject(value)) return { state: "unlimited", remainingMinutes: null, windowEndsAt: null };
  const state = typeof value.state === "string" && TIME_STATES.has(value.state) ? (value.state as TimeStateName) : "expired";
  return {
    state,
    remainingMinutes: typeof value.remainingMinutes === "number" ? value.remainingMinutes : null,
    windowEndsAt: typeof value.windowEndsAt === "string" ? value.windowEndsAt : null,
  };
}

function normalizeChannels(value: unknown): ChannelRef[] {
  if (!Array.isArray(value)) return [];
  return value
    .filter((c): c is Record<string, unknown> => isObject(c) && typeof c.channelId === "string")
    .map((c) => ({
      channelId: c.channelId as string,
      handle: typeof c.handle === "string" ? c.handle : null,
      title: typeof c.title === "string" ? c.title : null,
    }));
}

/** An unknown or missing YouTube mode is treated as "off" (fail closed). */
export function normalizeYouTube(value: unknown): YouTubePolicy {
  if (!isObject(value)) return { mode: "off", allowed: [], blocked: [] };
  const mode = typeof value.mode === "string" && YOUTUBE_MODES.has(value.mode) ? (value.mode as YouTubeMode) : "off";
  return { mode, allowed: normalizeChannels(value.allowed), blocked: normalizeChannels(value.blocked) };
}

function normalizeRules(value: Record<string, unknown>): RulesMessage {
  const message: RulesMessage = {
    type: "rules",
    rulesVersion: typeof value.rulesVersion === "number" ? value.rulesVersion : 0,
    dynamicRules: Array.isArray(value.dynamicRules) ? (value.dynamicRules as DnrRule[]) : [],
    timeUpSessionRules: Array.isArray(value.timeUpSessionRules) ? (value.timeUpSessionRules as DnrRule[]) : [],
    // The plan (§10.1) spells it `youtube`; the C# contract serializes `youTube`. Accept both.
    youTube: normalizeYouTube(value.youTube ?? value.youtube),
    time: normalizeTime(value.time),
  };
  if (typeof value.v === "number") message.v = value.v;
  if (typeof value.id === "string") message.id = value.id;
  return message;
}

export function isWebUrl(url: string | undefined | null): boolean {
  return typeof url === "string" && /^https?:\/\//i.test(url);
}
