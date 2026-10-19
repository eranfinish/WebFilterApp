import type { AgentUser, ChannelRef, FilterMode, RequestKind, RequestResult, TimeState, YouTubeMode } from "./protocol";
import type { ReasonCode } from "./reasons";
import type { ConnectionState } from "./time-actions";
import type { YouTubeDecision } from "./youtube-decision";
import type { YouTubeUrlKind } from "./youtube-url";

/** Messages from extension pages and content scripts to the background (chrome.runtime.sendMessage). */

export interface GetStatusMessage {
  type: "wf:getStatus";
}

/** Sent by every block page on load. Without a reason (compiled rules pass only the URL) the reply says what to show. */
export interface ReportBlockPageMessage {
  type: "wf:reportBlock";
  url: string;
  reason?: string | null;
  ruleId?: string | null;
  frame?: "main_frame" | "sub_frame";
  /** This page already reported once (a reload): ask with decideUrl instead, so nothing is recorded twice. */
  alreadyReported?: boolean;
}

export interface RequestAccessPageMessage {
  type: "wf:requestAccess";
  kind: RequestKind;
  target?: string | null;
  minutes?: number | null;
  note?: string | null;
}

export interface GetDecisionMessage {
  type: "wf:getDecision";
  requestId: string;
}

export interface GetMyChannelsMessage {
  type: "wf:getMyChannels";
}

/** A block page asks which URL its frame tried to load (the static fail-closed rule cannot pass it). */
export interface GetOriginalUrlMessage {
  type: "wf:getOriginalUrl";
}

/** From the YouTube detector (MAIN world) via the isolated bridge. */
export interface YouTubeDetection {
  url: string;
  kind: YouTubeUrlKind;
  videoId: string | null;
  handle: string | null;
  channelId: string | null;
  via: string | null;
  trigger: string;
  timedOut: boolean;
  ms: number;
}

export interface YouTubeDetectMessage {
  type: "wf:ytDetect";
  detection: YouTubeDetection;
}

export type BackgroundRequest =
  | GetStatusMessage
  | ReportBlockPageMessage
  | RequestAccessPageMessage
  | GetDecisionMessage
  | GetMyChannelsMessage
  | GetOriginalUrlMessage
  | YouTubeDetectMessage;

// ---- replies ------------------------------------------------------------------------------------------------------

export interface StatusSnapshot {
  version: string;
  installType: string | null;
  policyInstalled: boolean;
  connection: ConnectionState;
  lastError: string | null;
  user: Pick<AgentUser, "name" | "role"> | null;
  mode: FilterMode | null;
  youTubeMode: YouTubeMode | null;
  time: TimeState | null;
  timeUp: boolean;
  rulesVersion: number | null;
  serverVersion: string | null;
  /** Reason for a block page that carries none. */
  inferredReason: ReasonCode;
  /** Access requests can be sent right now (connected to the service, not the host fallback). */
  canRequest: boolean;
}

export interface RequestAccessReply {
  result: RequestResult | "error";
  requestId: string | null;
}

export interface ReportBlockReply {
  /** "allow": the service would allow the URL now (the extension's rules were stale). */
  decision: "allow" | "block";
  /** The reason to show. */
  reason: string;
  /**
   * service: decided by the service; host: the service is down (host fallback); page: from the block-page URL;
   * local: inferred from the installed rules (offline fallback).
   */
  source: "service" | "host" | "page" | "local";
  category: string | null;
  canRequest: boolean;
  requestKind: "url" | "time" | "youtube" | null;
  reported: boolean;
}

export interface DecisionReply {
  status: "approved" | "denied" | "pending" | "unknown";
}

export interface OriginalUrlReply {
  url: string | null;
}

export interface MyChannelsReply {
  ready: boolean;
  mode: YouTubeMode | null;
  channels: ChannelRef[];
}

/** The decision plus where the frame should go (block page or "my channels"), or "stale" for an outdated URL. */
export type YouTubeDetectReply = (YouTubeDecision & { navigateTo?: string }) | { decision: "stale" };

// ---- broadcasts from the background to extension pages --------------------------------------------------------------

export interface StateChangedBroadcast {
  type: "wf:stateChanged";
  status: StatusSnapshot;
}

export interface AccessDecidedBroadcast {
  type: "wf:accessDecided";
  requestId: string;
  status: "approved" | "denied";
}

export type Broadcast = StateChangedBroadcast | AccessDecidedBroadcast;

/** From the background to a YouTube frame's bridge (chrome.tabs.sendMessage). */
export interface YouTubeUrlChangedMessage {
  type: "wf:ytUrlChanged";
}

/** chrome.storage.local key the bridge reads at document_start to know whether to hide the page. */
export const YOUTUBE_STATE_KEY = "wf.youtube";

/** "inert": not policy-installed; "failclosed": no profile yet. */
export interface YouTubeStoredState {
  mode: YouTubeMode | "inert" | "failclosed";
  /** rulesVersion of the policy, so open pages decide again when the channel lists change. */
  version?: number | null;
}
