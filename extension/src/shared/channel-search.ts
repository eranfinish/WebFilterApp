/**
 * YouTube channel search (PROTECTION-SPEC §15, features.youTubeChannelSearch, "approved channels only" mode). The decision
 * allows YouTube's results only when they are restricted to channels (youtube-decision.ts); on that page the bridge hides
 * every result that is not a channel — videos, Shorts, playlists, mixes, shelves and their thumbnails — and marks each
 * channel: "approved" for an approved one, an "ask for approval" button for the others.
 *
 * The hiding is an allowlist of what may show, not a list of what to hide: a result type YouTube adds later is hidden
 * too, and a renamed channel result hides everything (nothing shows rather than videos). The bridge applies it from
 * document_start, before the page is revealed. The masthead's search box stays: a search there is sent back to the
 * channel-only results.
 */

/** The channel result element and the mark the bridge adds inside it. */
export const CHANNEL_RESULT = "ytd-channel-renderer";
export const CHANNEL_ACTION_CLASS = "wf-channel-action";

export const CHANNEL_SEARCH_HIDE_SELECTORS: readonly string[] = [
  // Anything in the result list but sections of results and the "more results" loader.
  "ytd-search ytd-section-list-renderer > #contents > :not(ytd-item-section-renderer):not(ytd-continuation-item-renderer)",
  // In a section of results: only channels (and the loader).
  `ytd-search ytd-item-section-renderer > #contents > :not(${CHANNEL_RESULT}):not(ytd-continuation-item-renderer)`,
  // The side column (a watch card with videos) and the filter bar (its "Videos" and "Shorts" chips).
  "ytd-search #secondary",
  "ytd-search ytd-secondary-search-container-renderer",
  "ytd-search ytd-search-header-renderer",
  "ytd-search ytd-search-sub-menu-renderer",
  "ytd-search #chips-wrapper",
];

/** One rule per selector: a selector the browser rejects then drops only its own line. */
export const CHANNEL_SEARCH_HIDE_CSS = CHANNEL_SEARCH_HIDE_SELECTORS.map((s) => `${s} { display: none !important; }`).join("\n");

/** How the mark looks: YouTube's own font, a pill under the channel's name; the button in YouTube's blue. */
export const CHANNEL_ACTION_CSS = [
  `.${CHANNEL_ACTION_CLASS} { display: block; margin: 6px 0 0; font: 500 14px/20px Roboto, Arial, sans-serif; }`,
  `.${CHANNEL_ACTION_CLASS} [data-wf-badge] { display: inline-block; padding: 2px 10px; border-radius: 12px; background: #e6f4ea; color: #137333; }`,
  `.${CHANNEL_ACTION_CLASS} button { cursor: pointer; padding: 6px 14px; border: 0; border-radius: 18px; background: #065fd4; color: #fff; font: inherit; }`,
  `.${CHANNEL_ACTION_CLASS} button:disabled { cursor: default; opacity: 0.6; }`,
  `.${CHANNEL_ACTION_CLASS} [data-wf-result] { display: inline-block; color: #606060; }`,
].join("\n");

/** What the mark next to a channel says. */
export type ChannelMark = "approved" | "ask" | "sent" | "none";

/**
 * The mark for one channel result: approved; a result of a request sent from this page; the button when requests can be
 * sent; otherwise nothing (service down, a parent's own account).
 */
export function channelMark(channelId: string, approved: ReadonlySet<string>, sent: ReadonlyMap<string, string>, canRequest: boolean): ChannelMark {
  if (approved.has(channelId)) return "approved";
  if (sent.has(channelId)) return "sent";
  return canRequest ? "ask" : "none";
}
