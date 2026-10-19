/**
 * YouTube channel detector, injected at document_start in the page's MAIN world (so it can read YouTube's own data)
 * in every youtube.com / youtube-nocookie.com frame. For each page load or SPA navigation it finds the channel of the
 * current URL from a trusted source only (spike S4, MVP-PLAN §7) and posts it to the isolated bridge:
 *   /channel/UC…          the URL
 *   video / Shorts        #movie_player / #shorts-player getPlayerResponse().videoDetails, only when videoId matches
 *   embed                 ytcfg PLAYER_VARS.embedded_player_response (video_id must match), else the player's channel link
 *   handle / custom / user  ytd-browse:not([hidden]).data (handle must match); ytInitialData only on a full load
 * meta, og:url, canonical and ytInitialData are never trusted after an SPA navigation: they keep the previous page.
 */
import type { YouTubeDetection } from "../shared/messages";
import { KINDS_NEEDING_CHANNEL, isChannelId, normalizeHandle, parseYouTubeUrl } from "../shared/youtube-url";

(() => {
  const MARK = "__wfYouTubeDetector";
  const w = window as unknown as Record<string, unknown>;
  if (w[MARK]) return;
  w[MARK] = true;

  // Captured before any page script runs, so the page cannot replace them.
  const post = window.postMessage.bind(window);
  const setTimer = window.setTimeout.bind(window);
  const setRepeat = window.setInterval.bind(window);
  const origin = location.origin;
  const inFrame = window.top !== window;
  const TIMEOUT_MS = 10_000;
  const POLL_MS = 200;

  const safe = <T>(f: () => T): T | undefined => {
    try {
      return f();
    } catch {
      return undefined;
    }
  };

  interface VideoDetails {
    videoId?: string;
    channelId?: string;
  }
  interface ChannelMeta {
    channelId?: string;
    handle?: string | null;
  }

  const channelFromHref = (href: string | null | undefined): string | null =>
    href?.match(/\/channel\/(UC[A-Za-z0-9_-]{22})/)?.[1] ?? null;
  const handleFromHref = (href: string | null | undefined): string | null => {
    const raw = href?.match(/\/(@[^/?#]+)/)?.[1];
    return raw ? normalizeHandle(safe(() => decodeURIComponent(raw)) ?? raw) : null;
  };

  function playerDetails(id: string): VideoDetails | undefined {
    const el = document.getElementById(id) as (HTMLElement & { getPlayerResponse?: () => { videoDetails?: VideoDetails } }) | null;
    return safe(() => el?.getPlayerResponse?.()?.videoDetails);
  }

  function embedPlayerVars(): VideoDetails | undefined {
    return safe(() => {
      const ytcfg = (window as unknown as { ytcfg?: { get?: (k: string) => unknown } }).ytcfg;
      const pv = ytcfg?.get?.("PLAYER_VARS") as { embedded_player_response?: unknown; video_id?: string } | undefined;
      if (!pv?.embedded_player_response) return undefined;
      const raw = pv.embedded_player_response;
      const text = typeof raw === "string" ? raw : JSON.stringify(raw);
      // The embedded response names the owner's channel id exactly once.
      const ids = [...new Set(text.match(/UC[A-Za-z0-9_-]{22}/g) ?? [])];
      return { videoId: pv.video_id, channelId: ids.length === 1 ? ids[0] : undefined };
    });
  }

  function channelMeta(data: unknown): ChannelMeta | undefined {
    const meta = safe(() => (data as { metadata?: { channelMetadataRenderer?: { externalId?: string; vanityChannelUrl?: string } } })?.metadata?.channelMetadataRenderer);
    return meta ? { channelId: meta.externalId, handle: handleFromHref(meta.vanityChannelUrl) } : undefined;
  }

  interface Pick {
    channelId: string;
    via: string;
  }

  function find(url: string, trigger: string): Pick | null {
    const page = parseYouTubeUrl(url);
    if (page.kind === "channel" && isChannelId(page.channelId)) return { channelId: page.channelId, via: "url" };

    if (page.kind === "video" || page.kind === "shorts" || page.kind === "embed") {
      if (!page.videoId) return null;
      const candidates: [string, VideoDetails | undefined][] = [
        ["player:movie_player", playerDetails("movie_player")],
        ["player:shorts-player", playerDetails("shorts-player")],
      ];
      if (page.kind === "embed") candidates.push(["embedPlayerVars", embedPlayerVars()]);
      for (const [via, d] of candidates) {
        if (d && d.videoId === page.videoId && isChannelId(d.channelId)) return { channelId: d.channelId, via };
      }
      // An embed shows one video and never navigates in place, so its channel link is current.
      if (page.kind === "embed") {
        const link = channelFromHref((document.querySelector("a.ytmVideoInfoChannelTitle") as HTMLAnchorElement | null)?.href);
        if (isChannelId(link)) return { channelId: link, via: "embedChannelLink" };
      }
      return null;
    }

    if (page.kind === "handle" || page.kind === "custom" || page.kind === "user") {
      const wanted = page.kind === "handle" ? (page.handle ?? null) : null;
      const matches = (m: ChannelMeta | undefined): m is ChannelMeta & { channelId: string } =>
        !!m && isChannelId(m.channelId) && (wanted === null || m.handle === wanted);
      const browse = channelMeta(safe(() => (document.querySelector("ytd-browse:not([hidden])") as unknown as { data?: unknown } | null)?.data));
      if (matches(browse)) return { channelId: browse.channelId, via: "ytdBrowseData" };
      if (trigger === "load" || trigger === "yt-navigate-finish:first") {
        const initial = channelMeta((window as unknown as { ytInitialData?: unknown }).ytInitialData);
        if (matches(initial)) return { channelId: initial.channelId, via: "ytInitialData" };
        if (wanted === null) {
          const canonical = channelFromHref((document.querySelector('link[rel="canonical"]') as HTMLLinkElement | null)?.href);
          if (isChannelId(canonical)) return { channelId: canonical, via: "canonical" };
        }
      }
    }
    return null;
  }

  let runId = 0;
  function run(trigger: string): void {
    const id = ++runId;
    const started = performance.now();
    const url = location.href;
    const page = parseYouTubeUrl(url);
    const needsChannel = KINDS_NEEDING_CHANNEL.has(page.kind);
    const tick = (): void => {
      if (id !== runId) return; // a newer navigation took over
      if (location.href !== url) return; // the URL changed; the poller starts a new run
      const pick = needsChannel ? find(url, trigger) : null;
      const ms = Math.round(performance.now() - started);
      if (!needsChannel || pick || ms >= TIMEOUT_MS) {
        const detection: YouTubeDetection = {
          url,
          kind: page.kind,
          videoId: page.videoId ?? null,
          handle: page.handle ?? null,
          channelId: pick?.channelId ?? null,
          via: pick?.via ?? null,
          trigger,
          timedOut: needsChannel && !pick,
          ms,
        };
        post({ __wf: "yt-detect", frame: inFrame ? "sub" : "main", detection }, origin);
        return;
      }
      setTimer(tick, POLL_MS);
    };
    tick();
  }

  let lastHref = location.href;
  let navigateFinishSeen = false;
  document.addEventListener("yt-navigate-finish", () => {
    lastHref = location.href;
    run(navigateFinishSeen ? "yt-navigate-finish" : "yt-navigate-finish:first");
    navigateFinishSeen = true;
  });
  // URL changes sometimes arrive before yt-navigate-finish; both are needed (S4).
  setRepeat(() => {
    if (location.href !== lastHref) {
      lastHref = location.href;
      run("url-change");
    }
  }, 250);
  // The bridge asks again when the policy changed or the background saw a history update.
  window.addEventListener("message", (event) => {
    if (event.source === window && (event.data as { __wf?: string } | null)?.__wf === "yt-rerun") {
      lastHref = location.href;
      run("rerun");
    }
  });
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => run("load"), { once: true });
  else run("load");
})();
