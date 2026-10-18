// Runs in the page's MAIN world so it can read YouTube's own data
// (player responses, ytInitialData, Polymer element data). For every page or
// SPA navigation it records what each candidate source says, picks the best
// channel id, and hands the result to the isolated content script.
(() => {
  const CHANNEL_RE = /^UC[A-Za-z0-9_-]{22}$/;
  const isChannelId = (v) => typeof v === "string" && CHANNEL_RE.test(v);
  const inFrame = window.top !== window;
  const safe = (f) => { try { return f(); } catch { return undefined; } };

  function pageInfo() {
    const u = new URL(location.href);
    const p = u.pathname;
    const info = { pageType: "other", videoId: null, handle: null, urlChannelId: null };
    let m;
    if (p === "/") info.pageType = "home";
    else if (p === "/results") info.pageType = "search";
    else if (p === "/watch") { info.pageType = "watch"; info.videoId = u.searchParams.get("v"); }
    else if ((m = p.match(/^\/shorts\/([^/]+)/))) { info.pageType = "shorts"; info.videoId = m[1]; }
    else if ((m = p.match(/^\/embed\/([^/]+)/))) { info.pageType = "embed"; info.videoId = m[1]; }
    else if ((m = p.match(/^\/channel\/(UC[A-Za-z0-9_-]{22})/))) { info.pageType = "channel"; info.urlChannelId = m[1]; }
    else if ((m = p.match(/^\/(@[^/]+)/))) { info.pageType = "channel"; info.handle = decodeURIComponent(m[1]).toLowerCase(); }
    else if (/^\/(c|user)\/[^/]+/.test(p)) info.pageType = "channel";
    return info;
  }

  const details = (vd) => (vd ? { videoId: vd.videoId, channelId: vd.channelId } : undefined);
  const channelFromUrl = (href) => href?.match(/\/channel\/(UC[A-Za-z0-9_-]{22})/)?.[1] ?? null;
  const handleFromUrl = (href) => href?.match(/\/(@[^/?#]+)/)?.[1]?.toLowerCase() ?? null;

  function collect() {
    const s = {};
    s.ytInitialPlayerResponse = details(safe(() => window.ytInitialPlayerResponse?.videoDetails));
    for (const id of ["movie_player", "shorts-player"]) {
      const pr = safe(() => document.getElementById(id)?.getPlayerResponse?.());
      s[`player:${id}`] = details(pr?.videoDetails);
    }
    // Embeds have no videoDetails before playback; the embedded response mentions the owner's channel id exactly once.
    s.embedPlayerVars = safe(() => {
      const pv = window.ytcfg?.get?.("PLAYER_VARS");
      if (!pv?.embedded_player_response) return undefined;
      const raw = pv.embedded_player_response;
      const ids = [...new Set((typeof raw === "string" ? raw : JSON.stringify(raw)).match(/UC[A-Za-z0-9_-]{22}/g) ?? [])];
      return { videoId: pv.video_id, channelId: ids.length === 1 ? ids[0] : undefined, candidates: ids.length };
    });
    s.embedChannelLink = channelFromUrl(document.querySelector("a.ytmVideoInfoChannelTitle")?.href);
    s.ytdWatchPlayerData = details(safe(() => document.querySelector("ytd-watch-flexy")?.playerData?.videoDetails));

    const initialMeta = safe(() => window.ytInitialData?.metadata?.channelMetadataRenderer);
    s.ytInitialData = initialMeta ? { channelId: initialMeta.externalId, handle: handleFromUrl(initialMeta.vanityChannelUrl) } : undefined;
    const browseMeta = safe(() => document.querySelector("ytd-browse:not([hidden])")?.data?.metadata?.channelMetadataRenderer);
    s.ytdBrowseData = browseMeta ? { channelId: browseMeta.externalId, handle: handleFromUrl(browseMeta.vanityChannelUrl) } : undefined;

    s.metaIdentifier = document.querySelector('meta[itemprop="identifier"]')?.content ?? null;
    s.metaChannelId = document.querySelector('meta[itemprop="channelId"]')?.content ?? null;
    s.canonical = channelFromUrl(document.querySelector('link[rel="canonical"]')?.href);
    s.ogUrl = channelFromUrl(document.querySelector('meta[property="og:url"]')?.content);
    s.ownerHandle = handleFromUrl(document.querySelector('ytd-video-owner-renderer a[href^="/@"]')?.getAttribute("href"));
    return s;
  }

  // Video pages: trust only a player response whose videoId matches the URL (guards against stale data after SPA navigation).
  // Channel pages: trust live Polymer data whose handle matches the URL; static markup only on a full page load.
  function best(info, s, trigger) {
    if (info.urlChannelId) return { channelId: info.urlChannelId, via: "url" };
    if (info.videoId) {
      for (const via of ["player:movie_player", "player:shorts-player", "embedPlayerVars", "ytdWatchPlayerData", "ytInitialPlayerResponse"]) {
        const c = s[via];
        if (c && c.videoId === info.videoId && isChannelId(c.channelId)) return { channelId: c.channelId, via };
      }
      // An embed page shows a single video and never navigates in place, so its channel link is current.
      if (info.pageType === "embed" && isChannelId(s.embedChannelLink)) return { channelId: s.embedChannelLink, via: "embedChannelLink" };
      return null;
    }
    if (info.pageType === "channel") {
      const handleOk = (c) => c && isChannelId(c.channelId) && (!info.handle || c.handle === info.handle);
      if (handleOk(s.ytdBrowseData)) return { channelId: s.ytdBrowseData.channelId, via: "ytdBrowseData" };
      if (trigger === "load" || trigger === "yt-navigate-finish:first") {
        if (handleOk(s.ytInitialData)) return { channelId: s.ytInitialData.channelId, via: "ytInitialData" };
        for (const via of ["canonical", "metaIdentifier", "ogUrl"]) if (isChannelId(s[via]) && !info.handle) return { channelId: s[via], via };
      }
    }
    return null;
  }

  const post = (detection) => window.postMessage({ __wf: "yt-detect", detection }, location.origin);

  let runId = 0;
  function run(trigger) {
    const id = ++runId;
    const t0 = performance.now();
    const href = location.href;
    const info = pageInfo();
    const needsChannel = ["watch", "shorts", "embed", "channel"].includes(info.pageType);
    const tick = () => {
      if (id !== runId) return; // superseded by a newer navigation
      const sources = collect();
      const pick = needsChannel ? best(info, sources, trigger) : null;
      const ms = Math.round(performance.now() - t0);
      if (!needsChannel || pick || ms > 10000) {
        post({ url: href, frame: inFrame ? "sub" : "main", trigger, ms, ...info,
               channelId: pick?.channelId ?? null, via: pick?.via ?? null, timedOut: needsChannel && !pick, sources });
        return;
      }
      setTimeout(tick, 200);
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
  setInterval(() => {
    if (location.href !== lastHref) { lastHref = location.href; run("url-change"); }
  }, 250);
  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", () => run("load"), { once: true });
  else run("load");
})();
