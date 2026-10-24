import type { AgentUser, ChannelRef, FilterMode, ProtectionFeatures, RequestKind, RequestResult, TimeState, YouTubeMode } from "./protocol";
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
  /** youtube: the channel's title and handle as the page showed them, and the channel search it came from (§15). */
  title?: string | null;
  handle?: string | null;
  searchQuery?: string | null;
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
  /** The detected channel's title and handle, from the same trusted source as its id (for the block page and a request). */
  channelTitle?: string | null;
  channelHandle?: string | null;
  /** Set by the bridge: the channel search this page was opened from (PROTECTION-SPEC §15). */
  searchQuery?: string | null;
}

export interface YouTubeDetectMessage {
  type: "wf:ytDetect";
  detection: YouTubeDetection;
}

/**
 * "Ask for approval" next to a channel in YouTube's channel-only results (PROTECTION-SPEC §15), from the bridge. The
 * background checks that the sender is its own content script in a top-level channel-search page and takes the search
 * from that page's address.
 */
export interface YouTubeRequestChannelMessage {
  type: "wf:ytRequestChannel";
  channelId: string;
  title?: string | null;
  handle?: string | null;
}

/**
 * The block page's "open anyway" on a jump block (teens, features.canOverrideJump). The background checks that the
 * sender is a top-level blocked.html?reason=blocked_jump page for this URL, sends reportOverride and opens it once.
 */
export interface OverrideJumpMessage {
  type: "wf:overrideJump";
  url: string;
}

export type BackgroundRequest =
  | GetStatusMessage
  | OverrideJumpMessage
  | ReportBlockPageMessage
  | RequestAccessPageMessage
  | GetDecisionMessage
  | GetMyChannelsMessage
  | GetOriginalUrlMessage
  | YouTubeDetectMessage
  | YouTubeRequestChannelMessage;

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
  /** The member's protection features in force (all off when not connected or unrestricted). */
  features: ProtectionFeatures;
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

export interface OverrideJumpReply {
  ok: boolean;
  /** not_allowed (no canOverrideJump), bad_request (not a jump block page for this URL), failed. */
  error?: string;
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
  /** The page offers a channel search (features.youTubeChannelSearch in the allowlist mode, PROTECTION-SPEC §15). */
  channelSearch: boolean;
}

/**
 * On an allowed channel-search results page (PROTECTION-SPEC §15): which channels are approved (marked "approved") and
 * whether the others can be asked for (an "ask for approval" button).
 */
export interface ChannelSearchState {
  approved: string[];
  canRequest: boolean;
}

/** The decision plus where the frame should go (block page or "my channels"), or "stale" for an outdated URL. */
export type YouTubeDetectReply = (YouTubeDecision & { navigateTo?: string; channelSearch?: ChannelSearchState }) | { decision: "stale" };

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
  /** features.blockYouTubeShorts: the bridge hides the Shorts shelf and menu entry. */
  blockShorts?: boolean;
  /** features.youTubeChannelSearch in the allowlist mode: the bridge hides every search result that is not a channel. */
  channelSearch?: boolean;
}
