/**
 * Isolated-world bridge on YouTube frames: hides the page in allowlist mode until a decision arrives, forwards the
 * MAIN-world detector's findings to the background, and enforces the answer (block page, "my channels", or the
 * channel-only results). While Shorts are blocked (PROTECTION-SPEC §8) it also hides the Shorts shelves and menu entries.
 * With channel search (§15) it hides every search result that is not a channel, marks the approved channels and offers
 * "ask for approval" next to the others, and tells the background which channel search a channel page came from.
 */
import { CHANNEL_ACTION_CLASS, CHANNEL_ACTION_CSS, CHANNEL_RESULT, CHANNEL_SEARCH_HIDE_CSS, channelMark } from "../shared/channel-search";
import type { ChannelSearchState, RequestAccessReply, YouTubeDetectReply, YouTubeDetection, YouTubeStoredState } from "../shared/messages";
import { YOUTUBE_STATE_KEY } from "../shared/messages";
import { requestResultMessageKey } from "../shared/reasons";
import { SHORTS_HIDE_CSS } from "../shared/shorts";
import { parseYouTubeUrl } from "../shared/youtube-url";

(() => {
  const root = document.documentElement;
  const inFrame = window.top !== window;
  const veil = document.createElement("style");
  veil.textContent = "html { visibility: hidden !important; }";
  // Allowlist mode: no recommendations next to an approved video and no end screen (MVP-PLAN §7).
  const trim = document.createElement("style");
  trim.textContent = [
    "#secondary, #related, ytd-watch-next-secondary-results-renderer,",
    ".ytp-endscreen-content, .ytp-ce-element, .ytp-autonav-endscreen-countdown-overlay, .ytp-pause-overlay,",
    "ytd-reel-video-renderer .ytd-reel-player-overlay-renderer #comments-button { display: none !important; }",
  ].join("\n");

  // Shorts blocked: no Shorts shelf, no Shorts entry in the menus. Pure CSS, so the channel detection is untouched.
  const shorts = document.createElement("style");
  shorts.textContent = SHORTS_HIDE_CSS;

  // Channel search: only channel results show on a results page, with the bridge's mark under each.
  const channelsOnly = document.createElement("style");
  channelsOnly.textContent = `${CHANNEL_SEARCH_HIDE_CSS}\n${CHANNEL_ACTION_CSS}`;

  let mode: YouTubeStoredState["mode"] | null = null;
  let leaving = false;

  const hide = (): void => {
    if (!veil.isConnected) (document.head ?? root).appendChild(veil);
  };
  const reveal = (): void => veil.remove();
  const strict = (): boolean => mode === null || mode === "allowlist";

  // Hide first: the mode is read asynchronously, and an allowlist page must not show before its decision.
  hide();

  const applyShorts = (blocked: boolean): void => {
    if (blocked) {
      if (!shorts.isConnected) (document.head ?? root).appendChild(shorts);
      root.dataset.wfShorts = "hidden"; // read by the Edge harness
    } else {
      shorts.remove();
      delete root.dataset.wfShorts;
    }
  };

  const applyChannelSearch = (on: boolean): void => {
    if (on && !inFrame) {
      if (!channelsOnly.isConnected) (document.head ?? root).appendChild(channelsOnly);
      root.dataset.wfChannelSearch = "on"; // read by the Edge harness
    } else {
      channelsOnly.remove();
      delete root.dataset.wfChannelSearch;
    }
  };

  const applyMode = (next: YouTubeStoredState["mode"] | null): void => {
    mode = next ?? "failclosed";
    if (mode === "allowlist") {
      if (!trim.isConnected) (document.head ?? root).appendChild(trim);
    } else {
      trim.remove();
      reveal();
    }
  };

  chrome.storage.local
    .get(YOUTUBE_STATE_KEY)
    .then((stored) => {
      const state = stored[YOUTUBE_STATE_KEY] as YouTubeStoredState | undefined;
      applyShorts(state?.blockShorts === true);
      applyChannelSearch(state?.channelSearch === true);
      applyMode(state?.mode ?? null);
    })
    .catch(() => applyMode(null));

  const askAgain = (): void => {
    if (strict()) hide();
    window.postMessage({ __wf: "yt-rerun" }, location.origin);
  };

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== "local" || !changes[YOUTUBE_STATE_KEY]) return;
    const before = changes[YOUTUBE_STATE_KEY].oldValue as YouTubeStoredState | undefined;
    const after = changes[YOUTUBE_STATE_KEY].newValue as YouTubeStoredState | undefined;
    applyShorts(after?.blockShorts === true);
    applyChannelSearch(after?.channelSearch === true);
    applyMode(after?.mode ?? null);
    // The policy changed under an open page: decide again.
    const changed =
      before?.mode !== after?.mode ||
      before?.version !== after?.version ||
      before?.blockShorts !== after?.blockShorts ||
      before?.channelSearch !== after?.channelSearch;
    if (changed) askAgain();
  });

  document.addEventListener("yt-navigate-start", () => {
    if (strict()) hide();
  });

  chrome.runtime.onMessage.addListener((message: { type?: string }) => {
    if (message?.type === "wf:ytUrlChanged") askAgain();
  });

  window.addEventListener("message", (event) => {
    if (event.source !== window || leaving) return;
    const data = event.data as { __wf?: string; detection?: YouTubeDetection } | null;
    if (data?.__wf !== "yt-detect" || !data.detection) return;
    void decide(withSearch(data.detection));
  });

  // ---- the channel search a page came from (PROTECTION-SPEC §15) ----------------------------------------------------
  // A channel the child opens from the channel-only results is asked for "found in the search …": the next page after
  // the results carries their terms (and keeps them while it is decided again); any other page forgets them.
  let lastResults: string | null = null;
  let carried: { url: string; query: string } | null = null;

  function withSearch(detection: YouTubeDetection): YouTubeDetection {
    if (inFrame) return detection;
    const page = parseYouTubeUrl(detection.url);
    if (page.kind === "search") {
      lastResults = page.channelsOnly === true && page.searchQuery ? page.searchQuery : null;
      carried = null;
      return detection;
    }
    if (carried?.url !== detection.url) {
      carried = lastResults ? { url: detection.url, query: lastResults } : null;
      lastResults = null;
    }
    return carried ? { ...detection, searchQuery: carried.query } : detection;
  }

  async function decide(detection: YouTubeDetection): Promise<void> {
    let reply: YouTubeDetectReply | undefined;
    try {
      reply = (await chrome.runtime.sendMessage({ type: "wf:ytDetect", detection })) as YouTubeDetectReply | undefined;
    } catch {
      // The background is unreachable (extension reloaded or updated): keep an allowlist page hidden.
      return;
    }
    if (!reply || reply.decision === "stale" || leaving) return;
    if (detection.url !== location.href) return; // outdated: a newer detection follows
    if (reply.decision === "allow") {
      results = !inFrame && "channelSearch" in reply && reply.channelSearch ? reply.channelSearch : null;
      markResults();
      reveal();
      return;
    }
    if ("navigateTo" in reply && reply.navigateTo) {
      leaving = true;
      hide();
      location.replace(reply.navigateTo);
    }
  }

  // ---- channel results: "approved" or "ask for approval" (PROTECTION-SPEC §15) -----------------------------------
  // The MAIN-world detector marks each channel result with its channel (data-wf-channel, -title, -handle, from the
  // result's own data); the bridge adds one mark inside it and keeps it in step as results load or are re-used.
  let results: ChannelSearchState | null = null;
  const sent = new Map<string, string>();

  function markResults(): void {
    if (inFrame) return;
    const onResults = results !== null && parseYouTubeUrl(location.href).kind === "search";
    const approved = new Set(results?.approved ?? []);
    for (const el of document.querySelectorAll<HTMLElement>(CHANNEL_RESULT)) {
      let mark = el.querySelector<HTMLElement>(`:scope > .${CHANNEL_ACTION_CLASS}`);
      const channelId = el.dataset.wfChannel;
      if (!onResults || !channelId) {
        mark?.remove();
        continue;
      }
      const kind = channelMark(channelId, approved, sent, results?.canRequest === true);
      if (mark?.dataset.wfFor === channelId && mark.dataset.wfMark === kind) continue;
      if (!mark) {
        mark = document.createElement("div");
        mark.className = CHANNEL_ACTION_CLASS;
        el.appendChild(mark);
      }
      mark.dataset.wfFor = channelId;
      mark.dataset.wfMark = kind;
      mark.replaceChildren(...content(kind, channelId));
    }
  }

  function content(kind: ReturnType<typeof channelMark>, channelId: string): Node[] {
    if (kind === "approved") {
      const badge = document.createElement("span");
      badge.dataset.wfBadge = "approved";
      badge.textContent = chrome.i18n.getMessage("channelApproved") || "approved";
      return [badge];
    }
    if (kind === "sent") {
      const outcome = sent.get(channelId) ?? "error";
      const result = document.createElement("span");
      result.dataset.wfResult = outcome;
      // "Created" in short (the block page's sentence promises a page that opens by itself); the rest as on the block page.
      result.textContent = chrome.i18n.getMessage(outcome === "created" ? "channelAsked" : requestResultMessageKey(outcome));
      return [result];
    }
    if (kind === "none") return [];
    const button = document.createElement("button");
    button.type = "button";
    button.textContent = chrome.i18n.getMessage("channelAskApproval") || "Ask";
    button.addEventListener("click", (event) => void ask(event, button));
    return [button];
  }

  /** A real click only (not one a page script made up), for the channel this result shows at that moment. */
  async function ask(event: MouseEvent, button: HTMLButtonElement): Promise<void> {
    event.preventDefault();
    event.stopPropagation(); // the result itself links to the channel's page
    if (!event.isTrusted) return;
    const result = button.closest<HTMLElement>(CHANNEL_RESULT);
    const channelId = result?.dataset.wfChannel;
    if (!result || !channelId) return;
    button.disabled = true;
    let reply: RequestAccessReply | undefined;
    try {
      reply = (await chrome.runtime.sendMessage({
        type: "wf:ytRequestChannel",
        channelId,
        title: result.dataset.wfTitle ?? null,
        handle: result.dataset.wfHandle ?? null,
      })) as RequestAccessReply | undefined;
    } catch {
      reply = undefined;
    }
    const outcome = reply?.result ?? "error";
    if (outcome === "error") {
      button.disabled = false;
      return;
    }
    sent.set(channelId, outcome);
    markResults();
  }

  if (!inFrame) {
    let timer: number | null = null;
    new MutationObserver(() => {
      if (timer !== null) return;
      timer = window.setTimeout(() => {
        timer = null;
        markResults();
      }, 100);
    }).observe(document, { childList: true, subtree: true, attributes: true, attributeFilter: ["data-wf-channel"] });
  }
})();
