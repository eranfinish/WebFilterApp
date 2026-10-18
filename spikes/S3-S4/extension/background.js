// Spike background. The harness drives it through globalThis functions
// (Playwright evaluates them inside this service worker).

const BLOCK_RULE_ID = 100;
const ALLOW_RULE_ID = 101;
const FRAMES = ["main_frame", "sub_frame"];

const blockedPagePrefix = () => `chrome-extension://${chrome.runtime.id}/blocked.html#`;

// Redirect that carries the original URL: regexFilter "^(.*)$" + regexSubstitution "\0".
function redirectWithUrl() {
  return { type: "redirect", redirect: { regexSubstitution: `${blockedPagePrefix()}\\0` } };
}

async function replaceDynamicRules(addRules) {
  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const t0 = performance.now();
  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: existing.map((r) => r.id),
    addRules,
  });
  return Math.round(performance.now() - t0);
}

// --- harness API ---------------------------------------------------------

globalThis.hello = async () => {
  await chrome.declarativeNetRequest.updateEnabledRulesets({ disableRulesetIds: ["failclosed"] });
  return chrome.declarativeNetRequest.getEnabledRulesets();
};

globalThis.failClosed = async () => {
  await chrome.declarativeNetRequest.updateEnabledRulesets({ enableRulesetIds: ["failclosed"] });
  return chrome.declarativeNetRequest.getEnabledRulesets();
};

globalThis.applyBlocklist = (domains) =>
  replaceDynamicRules([
    {
      id: BLOCK_RULE_ID,
      priority: 1,
      action: redirectWithUrl(),
      condition: { regexFilter: "^(.*)$", requestDomains: domains, resourceTypes: FRAMES },
    },
  ]);

// Generates n synthetic domains inside the worker so only rule installation is timed.
globalThis.applySyntheticBlocklist = (n, extra = []) => {
  const domains = Array.from({ length: n }, (_, i) => `d${String(i).padStart(7, "0")}.example`);
  return globalThis.applyBlocklist([...domains, ...extra]);
};

globalThis.applyAllowlist = (allowedDomains) =>
  replaceDynamicRules([
    {
      id: BLOCK_RULE_ID,
      priority: 1,
      action: redirectWithUrl(),
      condition: { regexFilter: "^(https?://.*)$", resourceTypes: FRAMES },
    },
    {
      id: ALLOW_RULE_ID,
      priority: 2,
      action: { type: "allow" },
      condition: { requestDomains: allowedDomains, resourceTypes: FRAMES },
    },
  ]);

globalThis.clearRules = () => replaceDynamicRules([]);

globalThis.ruleSummary = async () =>
  (await chrome.declarativeNetRequest.getDynamicRules()).map((r) => ({
    id: r.id,
    domains: r.condition.requestDomains?.length ?? 0,
    action: r.action.type,
  }));

// --- YouTube -------------------------------------------------------------

const yt = { mode: "observe", allowed: new Set(), blocked: new Set(), log: [] };

globalThis.setYouTube = async (mode, allowed = [], blocked = []) => {
  yt.mode = mode;
  yt.allowed = new Set(allowed);
  yt.blocked = new Set(blocked);
  await chrome.storage.local.set({ ytMode: mode });
  return mode;
};

globalThis.getYtLog = () => yt.log;
globalThis.clearYtLog = () => { yt.log = []; };

function decideYouTube(d) {
  if (yt.mode === "observe") return { decision: "allow", reason: "observe" };
  if (yt.mode === "allowlist") {
    if (d.pageType === "home" || d.pageType === "search") return { decision: "redirect", reason: "redirect_youtube_home" };
    if (d.pageType === "other") return { decision: "allow", reason: "allowed_default" };
    if (!d.channelId) return { decision: "block", reason: "blocked_youtube_unknown" };
    return yt.allowed.has(d.channelId)
      ? { decision: "allow", reason: "allowed_youtube_channel" }
      : { decision: "block", reason: "blocked_youtube_channel" };
  }
  if (yt.mode === "restricted" && d.channelId && yt.blocked.has(d.channelId))
    return { decision: "block", reason: "blocked_youtube_channel" };
  return { decision: "allow", reason: "allowed_default" };
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg?.type !== "yt-detect") return;
  const result = decideYouTube(msg.detection);
  yt.log.push({ ...msg.detection, frameId: sender.frameId, tabId: sender.tab?.id, ...result, mode: yt.mode });
  sendResponse(result);
});
