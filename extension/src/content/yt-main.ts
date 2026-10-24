/**
 * YouTube channel detector, injected at document_start in the page's MAIN world (so it can read YouTube's own data)
 * in every youtube.com / youtube-nocookie.com frame. For each page load or SPA navigation it finds the channel of the
 * current URL from a trusted source only (spike S4, MVP-PLAN §7) and posts it to the isolated bridge:
 *   /channel/UC…          the URL
 *   video / Shorts        #movie_player / #shorts-player getPlayerResponse().videoDetails, only when videoId matches
 *   embed                 ytcfg PLAYER_VARS.embedded_player_response (video_id must match), else the player's channel link
 *   handle / custom / user  ytd-browse:not([hidden]).data (handle must match); ytInitialData only on a full load
 * meta, og:url, canonical and ytInitialData are never trusted after an SPA navigation: they keep the previous page.
 * The channel's title and handle come from the same source as its id, when it has them (PROTECTION-SPEC §15: the parents
 * see the channel by its name). On YouTube's channel-only results (§15) each channel result is marked with its channel id,
 * title and handle (data-wf-channel, data-wf-title, data-wf-handle) from the result's own data, for the bridge's
 * "approved" mark and "ask for approval" button.
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
    /** The channel's title (videoDetails.author). */
    author?: string;
    /** The channel's handle, from the same player response (microformat's ownerProfileUrl). */
    handle?: string | null;
  }
  interface ChannelMeta {
    channelId?: string;
    handle?: string | null;
    title?: string | null;
  }
  type TextData = { simpleText?: string; runs?: { text?: string }[] } | undefined;
  const textOf = (t: TextData): string | null => t?.simpleText ?? (t?.runs?.map((r) => r.text ?? "").join("") || null);

  const channelFromHref = (href: string | null | undefined): string | null =>
    href?.match(/\/channel\/(UC[A-Za-z0-9_-]{22})/)?.[1] ?? null;
  const handleFromHref = (href: string | null | undefined): string | null => {
    const raw = href?.match(/\/(@[^/?#]+)/)?.[1];
    return raw ? normalizeHandle(safe(() => decodeURIComponent(raw)) ?? raw) : null;
  };

  interface PlayerResponse {
    videoDetails?: VideoDetails;
    microformat?: { playerMicroformatRenderer?: { ownerProfileUrl?: string } };
  }

  function playerDetails(id: string): VideoDetails | undefined {
    const el = document.getElementById(id) as (HTMLElement & { getPlayerResponse?: () => PlayerResponse }) | null;
    return safe(() => {
      const response = el?.getPlayerResponse?.();
      const details = response?.videoDetails;
      return details ? { ...details, handle: handleFromHref(response?.microformat?.playerMicroformatRenderer?.ownerProfileUrl) } : undefined;
    });
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
    const meta = safe(
      () => (data as { metadata?: { channelMetadataRenderer?: { externalId?: string; vanityChannelUrl?: string; title?: string } } })?.metadata?.channelMetadataRenderer,
    );
    return meta ? { channelId: meta.externalId, handle: handleFromHref(meta.vanityChannelUrl), title: meta.title ?? null } : undefined;
  }

  const browseData = (): unknown => safe(() => (document.querySelector("ytd-browse:not([hidden])") as unknown as { data?: unknown } | null)?.data);

  interface Pick {
    channelId: string;
    via: string;
    title?: string | null;
    handle?: string | null;
  }

  /** The names of a channel known by its address only (/channel/UC…): from the page data, when it is about that channel. */
  function namesOf(channelId: string, trigger: string): Pick {
    const sources = [channelMeta(browseData())];
    if (trigger === "load" || trigger === "yt-navigate-finish:first") sources.push(channelMeta((window as unknown as { ytInitialData?: unknown }).ytInitialData));
    const meta = sources.find((m) => m?.channelId === channelId);
    return { channelId, via: "url", title: meta?.title ?? null, handle: meta?.handle ?? null };
  }

  function find(url: string, trigger: string): Pick | null {
    const page = parseYouTubeUrl(url);
    if (page.kind === "channel" && isChannelId(page.channelId)) return namesOf(page.channelId, trigger);

    if (page.kind === "video" || page.kind === "shorts" || page.kind === "embed") {
      if (!page.videoId) return null;
      const candidates: [string, VideoDetails | undefined][] = [
        ["player:movie_player", playerDetails("movie_player")],
        ["player:shorts-player", playerDetails("shorts-player")],
      ];
      if (page.kind === "embed") candidates.push(["embedPlayerVars", embedPlayerVars()]);
      for (const [via, d] of candidates) {
        if (d && d.videoId === page.videoId && isChannelId(d.channelId)) return { channelId: d.channelId, via, title: d.author ?? null, handle: d.handle ?? null };
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
      const browse = channelMeta(browseData());
      if (matches(browse)) return { channelId: browse.channelId, via: "ytdBrowseData", title: browse.title ?? null, handle: browse.handle ?? null };
      if (trigger === "load" || trigger === "yt-navigate-finish:first") {
        const initial = channelMeta((window as unknown as { ytInitialData?: unknown }).ytInitialData);
        if (matches(initial)) return { channelId: initial.channelId, via: "ytInitialData", title: initial.title ?? null, handle: initial.handle ?? null };
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
    if (page.kind === "search") scheduleMark(); // the results of this search, once their data is in
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
          channelTitle: pick?.title ?? null,
          channelHandle: pick?.handle ?? null,
        };
        post({ __wf: "yt-detect", frame: inFrame ? "sub" : "main", detection }, origin);
        return;
      }
      setTimer(tick, POLL_MS);
    };
    tick();
  }

  // ---- channel search results (PROTECTION-SPEC §15) ----------------------------------------------------------------
  interface ChannelRendererData {
    channelId?: string;
    title?: TextData;
    navigationEndpoint?: { browseEndpoint?: { canonicalBaseUrl?: string } };
    subscriberCountText?: TextData;
  }
  const rendererData = (el: Element): ChannelRendererData | undefined =>
    safe(() => {
      const e = el as unknown as { data?: ChannelRendererData; polymerController?: { data?: ChannelRendererData }; __data?: { data?: ChannelRendererData } };
      return e.data ?? e.polymerController?.data ?? e.__data?.data;
    });
  const setData = (el: HTMLElement, name: string, value: string | null): void => {
    if (value === null) {
      if (name in el.dataset) delete el.dataset[name];
    } else if (el.dataset[name] !== value) {
      el.dataset[name] = value;
    }
  };

  /** Marks each channel result with its own data's channel id, title and handle (a recycled element is marked again). */
  function markChannelResults(): void {
    if (inFrame || parseYouTubeUrl(location.href).kind !== "search") return;
    for (const el of document.querySelectorAll<HTMLElement>("ytd-channel-renderer")) {
      const data = rendererData(el);
      const channelId = isChannelId(data?.channelId) ? data.channelId : null;
      const subscribers = textOf(data?.subscriberCountText);
      const handle = handleFromHref(data?.navigationEndpoint?.browseEndpoint?.canonicalBaseUrl) ?? (subscribers?.startsWith("@") ? normalizeHandle(subscribers) : null);
      setData(el, "wfChannel", channelId);
      setData(el, "wfTitle", channelId ? textOf(data?.title) : null);
      setData(el, "wfHandle", channelId ? handle : null);
    }
  }
  let markTimer: number | null = null;
  const scheduleMark = (): void => {
    if (markTimer !== null) return;
    markTimer = setTimer(() => {
      markTimer = null;
      markChannelResults();
    }, 100);
  };
  // Results load in pieces (infinite scroll) and YouTube re-renders them in place: look again after DOM changes.
  if (!inFrame) {
    new MutationObserver(() => {
      if (location.pathname === "/results" || location.pathname === "/search") scheduleMark();
    }).observe(document, { childList: true, subtree: true, characterData: true });
  }

  let lastHref = location.href;
  let navigateFinishSeen = false;
  document.addEventListener("yt-navigate-finish", () => {
    lastHref = location.href;
    run(navigateFinishSeen ? "yt-navigate-finish" : "yt-navigate-finish:first");
    navigateFinishSeen = true;
    scheduleMark();
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
