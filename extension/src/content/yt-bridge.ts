/**
 * Isolated-world bridge on YouTube frames: hides the page in allowlist mode until a decision arrives, forwards the
 * MAIN-world detector's findings to the background, and enforces the answer (block page or "my channels").
 */
import type { YouTubeDetectReply, YouTubeDetection, YouTubeStoredState } from "../shared/messages";
import { YOUTUBE_STATE_KEY } from "../shared/messages";

(() => {
  const root = document.documentElement;
  const veil = document.createElement("style");
  veil.textContent = "html { visibility: hidden !important; }";
  // Allowlist mode: no recommendations next to an approved video and no end screen (MVP-PLAN §7).
  const trim = document.createElement("style");
  trim.textContent = [
    "#secondary, #related, ytd-watch-next-secondary-results-renderer,",
    ".ytp-endscreen-content, .ytp-ce-element, .ytp-autonav-endscreen-countdown-overlay, .ytp-pause-overlay,",
    "ytd-reel-video-renderer .ytd-reel-player-overlay-renderer #comments-button { display: none !important; }",
  ].join("\n");

  let mode: YouTubeStoredState["mode"] | null = null;
  let leaving = false;

  const hide = (): void => {
    if (!veil.isConnected) (document.head ?? root).appendChild(veil);
  };
  const reveal = (): void => veil.remove();
  const strict = (): boolean => mode === null || mode === "allowlist";

  // Hide first: the mode is read asynchronously, and an allowlist page must not show before its decision.
  hide();

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
    .then((stored) => applyMode((stored[YOUTUBE_STATE_KEY] as YouTubeStoredState | undefined)?.mode ?? null))
    .catch(() => applyMode(null));

  const askAgain = (): void => {
    if (strict()) hide();
    window.postMessage({ __wf: "yt-rerun" }, location.origin);
  };

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area !== "local" || !changes[YOUTUBE_STATE_KEY]) return;
    const before = changes[YOUTUBE_STATE_KEY].oldValue as YouTubeStoredState | undefined;
    const after = changes[YOUTUBE_STATE_KEY].newValue as YouTubeStoredState | undefined;
    applyMode(after?.mode ?? null);
    // The policy changed under an open page: decide again.
    if (before?.mode !== after?.mode || before?.version !== after?.version) askAgain();
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
    void decide(data.detection);
  });

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
      reveal();
      return;
    }
    if ("navigateTo" in reply && reply.navigateTo) {
      leaving = true;
      hide();
      location.replace(reply.navigateTo);
    }
  }
})();
