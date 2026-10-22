/**
 * YouTube Shorts (PROTECTION-SPEC §8, features.blockYouTubeShorts). The service's URL rules cannot do this: the YouTube
 * host rule sits above every member rule. So the extension blocks Shorts itself, in three places:
 *   - full loads: a session rule (below) above the YouTube host rule and below the time-up rules;
 *   - in-site (SPA) navigations: webNavigation.onHistoryStateUpdated in the background, and decideYouTube() when the
 *     content script reports the page;
 *   - the Shorts shelf and the Shorts entries in the menus: hidden by the YouTube bridge (SHORTS_HIDE_CSS).
 */
import type { DnrRule } from "./protocol";

export const SHORTS_REASON = "blocked_youtube_shorts";

/** Above the YouTube host rule (196,608) and every member rule, below the time-up rules (262,144). */
export const SHORTS_RULE_PRIORITY = (3 << 16) + 16;

/** `/shorts` as the first path segment on youtube.com and its subdomains (www, m), any case. */
export const SHORTS_REGEX = "^https?://[^/?#]+/shorts(?:[/?#].*)?$";

export function shortsRule(id: number, blockPageUrl: string): DnrRule {
  return {
    id,
    priority: SHORTS_RULE_PRIORITY,
    action: { type: "redirect", redirect: { regexSubstitution: `${blockPageUrl}?reason=${SHORTS_REASON}#\\0` } },
    condition: {
      requestDomains: ["youtube.com"],
      resourceTypes: ["main_frame", "sub_frame"],
      regexFilter: SHORTS_REGEX,
      isUrlFilterCaseSensitive: false,
    },
  };
}

/**
 * What the bridge hides while Shorts are blocked (desktop and m.youtube.com markup as of 2026; YouTube renames these
 * now and then, so this is best effort — the pages themselves are blocked either way):
 *   shelves on home, search, subscriptions and watch pages; single Shorts results and grid items;
 *   the "Shorts" entry of the side menu (guide), the mini guide and the mobile bottom bar; a channel's "Shorts" tab.
 */
export const SHORTS_HIDE_SELECTORS: readonly string[] = [
  "ytd-reel-shelf-renderer",
  "ytd-rich-shelf-renderer[is-shorts]",
  "ytd-rich-section-renderer:has(ytd-rich-shelf-renderer[is-shorts])",
  "ytd-rich-section-renderer:has(ytm-shorts-lockup-view-model)",
  "grid-shelf-view-model:has(ytm-shorts-lockup-view-model)",
  "yt-horizontal-list-renderer:has(ytm-shorts-lockup-view-model)",
  "ytm-shorts-lockup-view-model",
  "ytd-video-renderer:has(a[href^='/shorts/'])",
  "ytd-grid-video-renderer:has(a[href^='/shorts/'])",
  "ytd-rich-item-renderer:has(a[href^='/shorts/'])",
  "ytd-compact-video-renderer:has(a[href^='/shorts/'])",
  "ytd-guide-entry-renderer:has(a[href^='/shorts'])",
  "ytd-guide-entry-renderer:has(a[title='Shorts'])",
  "ytd-mini-guide-entry-renderer:has(a[href^='/shorts'])",
  "ytd-mini-guide-entry-renderer[aria-label='Shorts']",
  "yt-tab-shape[tab-title='Shorts']",
  "ytm-reel-shelf-renderer",
  "ytm-pivot-bar-item-renderer:has(.pivot-shorts)",
  "ytm-pivot-bar-item-renderer:has(a[href^='/shorts'])",
];

/** One rule per selector: a selector the browser rejects then drops only its own line. */
export const SHORTS_HIDE_CSS = SHORTS_HIDE_SELECTORS.map((s) => `${s} { display: none !important; }`).join("\n");
