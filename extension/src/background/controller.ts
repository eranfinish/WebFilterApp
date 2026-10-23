import { detectBrowser } from "../shared/browser";
import { DisconnectedError, HostError, type Timers, backoffDelay, realTimers } from "../shared/correlation";
import { classifyNavigation, wouldBlock } from "../shared/dnr-match";
import { explainBlock } from "../shared/explain";
import { JUMP_REASON } from "../shared/jump";
import type {
  DecisionReply,
  MyChannelsReply,
  OverrideJumpReply,
  ReportBlockReply,
  RequestAccessPageMessage,
  RequestAccessReply,
  StatusSnapshot,
  YouTubeDetectReply,
  YouTubeDetection,
  YouTubeStoredState,
} from "../shared/messages";
import { YOUTUBE_STATE_KEY } from "../shared/messages";
import { BLOCK_PAGE, MY_CHANNELS_PAGE, TIMEUP_PAGE, buildBlockPageUrl, buildTimeUpUrl, parseBlockPageUrl, safeReturnUrl } from "../shared/pages";
import {
  type AccessDecidedMessage,
  type DnrRule,
  type FilterMode,
  type HelloResult,
  type IncomingMessage,
  NO_FEATURES,
  type ProtectionFeatures,
  type RequestKind,
  type RulesMessage,
  type TimeState,
  isWebUrl,
  normalizeTime,
} from "../shared/protocol";
import {
  EXTENSION_ONLY_REASONS,
  FAILCLOSED,
  PAGE_DECIDED_REASONS,
  type ReasonCode,
  inferReason,
  reportableReason,
  requestActionFor,
} from "../shared/reasons";
import { MAX_SEARCH_URL_LENGTH, SearchDeduper, extractSearch } from "../shared/search";
import { SHORTS_REASON, shortsRule } from "../shared/shorts";
import {
  type ConnectionState,
  type WarningMemory,
  badgeFor,
  isTimeUp,
  localDateKey,
  nextWarning,
  timeTransition,
} from "../shared/time-actions";
import { type YouTubeContext, approvedChannels, decideYouTube } from "../shared/youtube-decision";
import { isChannelId, isShortsUrl, isYouTubeHost } from "../shared/youtube-url";
import {
  SHORTS_RULE_ID,
  clearOwnSessionRules,
  replaceDynamicRules,
  replaceSessionRules,
  setAdBlocking,
  setFailClosed,
  setOwnSessionRule,
} from "./dnr";
import { JumpGuard } from "./jump-guard";
import { type Connector, NativeClient, errorText, nativeConnector } from "./native-client";

export interface ControllerOptions {
  connector?: Connector;
  /** Test build only: replaces chrome.management.getSelf().installType (e.g. "admin" for an unpacked extension). */
  installTypeOverride?: () => Promise<string | null>;
  timers?: Timers;
  now?: () => number;
  requestTimeoutMs?: number;
  log?: (message: string, detail?: unknown) => void;
}

const WARNINGS_KEY = "wf.warnings";
const ORIGINS_KEY = "wf.frameOrigins";
const REQUEST_KINDS: ReadonlySet<string> = new Set<RequestKind>(["url", "youtube", "app", "time"]);
const MAX_NOTE = 500;

/**
 * The extension's state machine (MVP-PLAN §4.4):
 *  - not force-installed by policy → inert (fail-closed off, no native host);
 *  - policy-installed → fail-closed until hello + rules succeed, then the compiled rules; on any disconnect back to
 *    fail-closed and reconnect with backoff (1 s → 30 s).
 */
export class Controller {
  readonly client: NativeClient;
  private readonly timers: Timers;
  private readonly now: () => number;
  private readonly log: (message: string, detail?: unknown) => void;

  private installType: string | null = null;
  private policyInstalled = false;
  private connection: ConnectionState = "starting";
  private lastError: string | null = null;
  private hello: HelloResult | null = null;
  private rules: RulesMessage | null = null;
  private time: TimeState | null = null;
  private sessionRulesInstalled = false;
  /** The rules actually installed (the service's rules minus any the browser refused). */
  private installedDynamic: DnrRule[] = [];
  private installedSession: DnrRule[] = [];

  /** Incremented by start() and on every new connection; async flows from an older one stop. */
  private epoch = 0;
  private connectionId = 0;
  private attempt = 0;
  private reconnectTimer: unknown = null;
  private reconnectAt = 0;
  private beatTimer: unknown = null;
  private refreshing = false;
  private refreshAgain = false;
  private refreshWaiters: (() => void)[] = [];
  private applyQueue: Promise<unknown> = Promise.resolve();
  private settledWaiters: (() => void)[] = [];
  private readonly decisions = new Map<string, "approved" | "denied">();
  private readonly requested = new Set<string>();
  private warningMemory: WarningMemory | null = null;
  private rulesWarning: string | null = null;
  private frameOrigins = new Map<string, string>();
  private saveTimer: unknown = null;
  /** The jump guard (PROTECTION-SPEC §7); wiring.ts feeds it the webNavigation events. */
  readonly jumps: JumpGuard;
  private readonly searches = new SearchDeduper();
  private shortsRuleInstalled = false;
  /** Whether the ads ruleset was last enabled or disabled by this worker (null: not yet; the browser keeps its state). */
  private adBlocking: boolean | null = null;

  constructor(private readonly options: ControllerOptions = {}) {
    this.timers = options.timers ?? realTimers;
    this.now = options.now ?? (() => Date.now());
    this.log = options.log ?? ((message, detail) => console.info(`[WebFilter] ${message}`, detail ?? ""));
    this.client = new NativeClient(
      options.connector ?? nativeConnector,
      { onPush: (m) => this.onPush(m), onDisconnect: (r) => this.onDisconnect(r) },
      options.requestTimeoutMs ?? 15_000,
      this.timers,
    );
    this.jumps = new JumpGuard({ active: () => this.features.jumpGuard, timers: this.timers, now: this.now, log: this.log });
  }

  /** Hello + rules applied (from the service or the host fallback). */
  get ready(): boolean {
    return (this.connection === "service" || this.connection === "host") && this.rules !== null && this.hello !== null;
  }

  get connectionState(): ConnectionState {
    return this.connection;
  }

  /**
   * The protection features in force: the last rules' `features` while ready and not unrestricted (a member's flags, or
   * a parent's baseline switches), else all off.
   */
  get features(): ProtectionFeatures {
    if (!this.ready || this.mode === "unrestricted") return NO_FEATURES;
    return this.rules?.features ?? NO_FEATURES;
  }

  /** The account's mode in force: the last rules' (PROTECTION-SPEC §13: it follows a re-link without a new hello), else the hello's. */
  get mode(): FilterMode | null {
    return this.rules?.mode ?? this.hello?.mode ?? null;
  }

  /** A parent's own account with the family's baseline protection (§13): nothing is reported, nothing can be requested. */
  private get baseline(): boolean {
    return this.mode === "baseline";
  }

  /** Replaces the native host connector (test build: in-worker fake host). Takes effect on the next connect. */
  setConnector(connector: Connector): void {
    this.client.setConnector(connector);
  }

  // ---- lifecycle --------------------------------------------------------------------------------------------------

  /** Startup (every service worker start): policy check, then fail-closed and connect, or inert. */
  async start(): Promise<void> {
    const epoch = ++this.epoch;
    this.cancelReconnect();
    this.connectionId += 1;
    this.client.close("restarting");
    this.hello = null;
    this.rules = null;
    this.time = null;
    this.attempt = 0;
    this.adBlocking = null;
    this.setConnection("starting");
    void this.loadFrameOrigins();
    void this.jumps.seed();

    const installType = await this.readInstallType();
    if (epoch !== this.epoch) return;
    this.installType = installType;
    // An unreadable install type is treated as policy-installed: fail closed rather than open (README, decisions).
    this.policyInstalled = installType === "admin" || installType === null;

    if (!this.policyInstalled) {
      await this.enqueue(() => this.becomeInert());
      if (epoch !== this.epoch) return;
      this.setConnection("inert");
      this.settle();
      return;
    }
    await this.enqueue(() => this.enterFailClosed());
    if (epoch !== this.epoch) return;
    this.connect();
  }

  /** Every 30 s (chrome.alarms): heartbeat, or a reconnect if the backoff timer was lost with a worker restart. */
  onAlarm(): void {
    if (!this.policyInstalled) return;
    if (this.ready) {
      void this.heartbeat();
      return;
    }
    if (this.connection === "disconnected" && (this.reconnectTimer === null || this.now() > this.reconnectAt + 5_000)) {
      this.connect();
    }
  }

  /** Focus, idle or active-tab change: one heartbeat soon (debounced). */
  requestHeartbeat(delayMs = 1_000): void {
    if (!this.ready || this.beatTimer !== null) return;
    this.beatTimer = this.timers.setTimeout(() => {
      this.beatTimer = null;
      void this.heartbeat();
    }, delayMs);
  }

  /** Resolves once the connection attempt settled (ready, inert or disconnected), or after `timeoutMs`. */
  whenSettled(timeoutMs = 5_000): Promise<void> {
    if (this.connection !== "starting" && this.connection !== "connecting") return Promise.resolve();
    return new Promise((resolve) => {
      const timer = this.timers.setTimeout(resolve, timeoutMs);
      this.settledWaiters.push(() => {
        this.timers.clearTimeout(timer);
        resolve();
      });
    });
  }

  private connect(): void {
    this.cancelReconnect();
    const id = ++this.connectionId;
    this.refreshAgain = false;
    this.setConnection("connecting");
    this.client.connect();
    void this.handshake(id);
  }

  private async handshake(id: number): Promise<void> {
    try {
      const hello = await this.client.request({
        type: "hello",
        extVersion: chrome.runtime.getManifest().version,
        browser: detectBrowser(globalThis.navigator as Parameters<typeof detectBrowser>[0]),
        extensionId: chrome.runtime.id,
      });
      if (id !== this.connectionId || hello.type !== "helloResult") return;
      const rules = await this.client.request({ type: "getRules" });
      if (id !== this.connectionId || rules.type !== "rules") return;
      await this.enqueue(() => this.applyProfile(id, hello, rules));
      if (id !== this.connectionId) return;
      this.attempt = 0;
      this.lastError = null;
      this.setConnection(hello.source === "host" ? "host" : "service");
      this.settle();
      this.broadcastState();
      void this.heartbeat();
      if (this.refreshAgain) void this.refreshRules();
    } catch (error) {
      if (id !== this.connectionId) return;
      this.log("handshake failed", errorText(error));
      // Close the port; onDisconnect re-enters fail-closed and schedules the retry.
      if (this.client.connected) this.client.disconnect(`handshake failed: ${errorText(error)}`);
      else this.onDisconnect(`handshake failed: ${errorText(error)}`);
    }
  }

  private onDisconnect(reason: string): void {
    this.connectionId += 1;
    this.hello = null;
    this.rules = null;
    this.time = null;
    this.lastError = reason;
    this.cancelBeat();
    if (!this.policyInstalled) return;
    this.log("disconnected", reason);
    this.setConnection("disconnected");
    void this.enqueue(() => this.enterFailClosed()).then(() => this.broadcastState());
    this.scheduleReconnect();
    this.settle();
  }

  private scheduleReconnect(): void {
    this.cancelReconnect();
    const delay = backoffDelay(this.attempt);
    this.attempt += 1;
    this.reconnectAt = this.now() + delay;
    this.reconnectTimer = this.timers.setTimeout(() => {
      this.reconnectTimer = null;
      if (this.connection === "disconnected") this.connect();
    }, delay);
  }

  private cancelReconnect(): void {
    if (this.reconnectTimer !== null) this.timers.clearTimeout(this.reconnectTimer);
    this.reconnectTimer = null;
  }

  private cancelBeat(): void {
    if (this.beatTimer !== null) this.timers.clearTimeout(this.beatTimer);
    this.beatTimer = null;
  }

  private settle(): void {
    const waiters = this.settledWaiters;
    this.settledWaiters = [];
    for (const w of waiters) w();
  }

  private setConnection(state: ConnectionState): void {
    this.connection = state;
    void this.updateBadge();
  }

  private async readInstallType(): Promise<string | null> {
    const override = await this.options.installTypeOverride?.().catch(() => null);
    if (override) return override;
    for (let i = 0; i < 3; i++) {
      try {
        const self = await chrome.management.getSelf();
        if (self?.installType) return self.installType;
      } catch (error) {
        this.log("management.getSelf failed", errorText(error));
      }
      await new Promise((r) => this.timers.setTimeout(() => r(undefined), 200));
    }
    return null;
  }

  // ---- enforcement ------------------------------------------------------------------------------------------------

  /** Serializes everything that changes DNR state, so overlapping pushes cannot interleave. */
  private enqueue<T>(work: () => Promise<T>): Promise<T> {
    const run = this.applyQueue.then(work, work);
    this.applyQueue = run.catch(() => undefined);
    return run;
  }

  private async enterFailClosed(): Promise<void> {
    // Order matters: block everything first, then drop the rules (allow rules would outrank the fail-closed redirect).
    await setFailClosed(true);
    await replaceDynamicRules([]);
    await replaceSessionRules([]);
    await this.clearProtection();
    // No policy yet: the strictest state. New pages and frames are blocked anyway; pages already open stop loading ads.
    await this.applyAdBlocking(true);
    this.installedDynamic = [];
    this.installedSession = [];
    this.sessionRulesInstalled = false;
    await this.storeYouTubeState("failclosed");
    await this.updateBadge();
  }

  private async becomeInert(): Promise<void> {
    await setFailClosed(false);
    await replaceDynamicRules([]);
    await replaceSessionRules([]);
    await this.clearProtection();
    await this.applyAdBlocking(false);
    this.installedDynamic = [];
    this.installedSession = [];
    this.sessionRulesInstalled = false;
    await this.storeYouTubeState("inert");
    await this.updateBadge();
  }

  private async applyProfile(id: number, hello: HelloResult, rules: RulesMessage): Promise<void> {
    if (id !== this.connectionId) return;
    this.hello = hello;
    await this.applyRules(rules);
  }

  /**
   * Installs a rules message; the fail-closed ruleset is disabled only after the rules are in place. A parent's baseline
   * (mode baseline, PROTECTION-SPEC §13) is installed like a member's rules: the adult list, the jump guard and the ads
   * ruleset by its features, with unlimited time.
   */
  private async applyRules(rules: RulesMessage): Promise<void> {
    const hello = this.hello;
    if (!hello) return;
    if ((rules.mode ?? hello.mode) === "unrestricted") {
      await replaceDynamicRules([]);
      await replaceSessionRules([]);
      await this.clearProtection();
      await this.applyAdBlocking(false);
      this.installedDynamic = [];
      this.installedSession = [];
      this.sessionRulesInstalled = false;
      this.rules = rules;
      this.time = { state: "unlimited", remainingMinutes: null, windowEndsAt: null };
      await setFailClosed(false);
      await this.storeYouTubeState("unrestricted");
    } else {
      this.rulesWarning = null;
      const result = await replaceDynamicRules(rules.dynamicRules);
      this.installedDynamic = result.installed;
      this.reportDropped("dynamic", result.dropped);
      this.rules = rules;
      await this.applyTime(rules.time, true);
      await this.applyProtection(rules);
      await setFailClosed(false);
      await this.storeYouTubeState(rules.youTube.mode);
    }
    await this.updateBadge();
  }

  /**
   * The protection features that need browser state (PROTECTION-SPEC §7–§8, §12): the Shorts session rule, the jump
   * guard's tab rules when the guard was switched off, and the ads ruleset. A rule the browser refuses is logged; the
   * content script and the history check still block Shorts.
   */
  private async applyProtection(rules: RulesMessage): Promise<void> {
    const features = rules.features;
    await this.applyAdBlocking(features.blockAds);
    const wantShorts = features.blockYouTubeShorts && rules.youTube.mode !== "off";
    if (wantShorts !== this.shortsRuleInstalled) {
      try {
        await setOwnSessionRule(SHORTS_RULE_ID, wantShorts ? shortsRule(SHORTS_RULE_ID, chrome.runtime.getURL(BLOCK_PAGE)) : null);
        this.shortsRuleInstalled = wantShorts;
      } catch (error) {
        this.log("Shorts rule update failed", errorText(error));
      }
    }
    if (!features.jumpGuard) await this.jumps.reset();
  }

  /**
   * Enables or disables the ads ruleset (PROTECTION-SPEC §12) when this worker has not already. Ad blocks are quiet: a
   * blocked request never reaches a block page, so nothing is reported. A browser that refuses is logged and asked
   * again with the next rules.
   */
  private async applyAdBlocking(enabled: boolean): Promise<void> {
    if (this.adBlocking === enabled) return;
    try {
      await setAdBlocking(enabled);
      this.adBlocking = enabled;
    } catch (error) {
      this.adBlocking = null;
      this.log("ads ruleset update failed", errorText(error));
    }
  }

  /** Removes the extension's own session rules (Shorts, jump-guard tabs) and forgets the guarded tabs. */
  private async clearProtection(): Promise<void> {
    try {
      await clearOwnSessionRules();
    } catch (error) {
      this.log("clearing own session rules failed", errorText(error));
    }
    this.shortsRuleInstalled = false;
    await this.jumps.reset(false);
  }

  /** Applies a time state: time-up session rules, open tabs, badge, warnings. */
  private async applyTime(time: TimeState, rulesChanged = false): Promise<void> {
    if (!this.rules || this.mode === "unrestricted") return;
    const previous = this.time;
    this.time = time;
    const up = isTimeUp(time);
    if (up && (!this.sessionRulesInstalled || rulesChanged)) {
      const result = await replaceSessionRules(this.rules.timeUpSessionRules);
      this.installedSession = result.installed;
      this.reportDropped("session", result.dropped);
      this.sessionRulesInstalled = true;
    } else if (!up && this.sessionRulesInstalled) {
      await replaceSessionRules([]);
      this.installedSession = [];
      this.sessionRulesInstalled = false;
    }
    const transition = timeTransition(previous, time);
    if (transition.enterTimeUp) await this.moveTabsToTimeUp();
    await this.updateBadge();
    await this.maybeWarn();
    if (!sameTime(previous, time)) this.broadcastState();
  }

  /** Moves open http(s) tabs that the time-up rules would block to timeup.html (always-allowed sites stay). */
  private async moveTabsToTimeUp(): Promise<void> {
    const sessionRules = this.installedSession;
    const page = chrome.runtime.getURL(TIMEUP_PAGE);
    let tabs: chrome.tabs.Tab[] = [];
    try {
      tabs = await chrome.tabs.query({ url: ["http://*/*", "https://*/*"] });
    } catch (error) {
      this.log("tabs.query failed", errorText(error));
    }
    for (const tab of tabs) {
      const url = tab.url ?? tab.pendingUrl;
      if (tab.id === undefined || !isWebUrl(url)) continue;
      if (sessionRules.length > 0 && !wouldBlock(sessionRules, url!)) continue;
      try {
        await chrome.tabs.update(tab.id, { url: buildTimeUpUrl(page, url) });
      } catch (error) {
        this.log("tabs.update failed", errorText(error));
      }
    }
  }

  private async updateBadge(): Promise<void> {
    const badge = badgeFor(this.time, this.connection);
    try {
      await chrome.action.setBadgeText({ text: badge.text });
      if (badge.text) await chrome.action.setBadgeBackgroundColor({ color: badge.color });
    } catch {
      // The action API can be unavailable very early in startup; the next update fixes the badge.
    }
  }

  private async maybeWarn(): Promise<void> {
    const today = localDateKey(this.now());
    if (!this.warningMemory) {
      try {
        const stored = (await chrome.storage.local.get(WARNINGS_KEY))[WARNINGS_KEY] as WarningMemory | undefined;
        this.warningMemory = stored ?? null;
      } catch {
        this.warningMemory = null;
      }
    }
    const step = nextWarning(this.time, this.warningMemory, today);
    const changed = JSON.stringify(step.memory) !== JSON.stringify(this.warningMemory);
    this.warningMemory = step.memory;
    if (changed) {
      try {
        await chrome.storage.local.set({ [WARNINGS_KEY]: step.memory });
      } catch {
        // best effort
      }
    }
    if (step.notify === null) return;
    try {
      await chrome.notifications.create(`wf-time-${step.notify}-${this.now()}`, {
        type: "basic",
        iconUrl: chrome.runtime.getURL("icons/icon-128.png"),
        title: "WebFilter",
        message: chrome.i18n.getMessage(`notify${step.notify}`) || `${step.notify} min`,
        priority: 2,
      });
    } catch (error) {
      this.log("notification failed", errorText(error));
    }
  }

  /** Rules the browser refused were left out: log it, show it in the popup, and tell the service. */
  private reportDropped(set: "dynamic" | "session", ids: readonly number[]): void {
    if (ids.length === 0) return;
    const message = `The browser refused ${ids.length} ${set} rule(s) (regex not supported, e.g. too large for RE2); installed the rest. Dropped rule ids: ${ids.join(", ")}`;
    this.log("rules dropped", message);
    this.rulesWarning = message;
    this.client.notify({ type: "error", code: "dnr_rules_dropped", message });
  }

  private async storeYouTubeState(mode: YouTubeStoredState["mode"]): Promise<void> {
    try {
      const blockShorts =
        mode !== "inert" && mode !== "failclosed" && this.mode !== "unrestricted" && this.rules?.features.blockYouTubeShorts === true;
      const state: YouTubeStoredState = { mode, version: this.rules?.rulesVersion ?? null, ...(blockShorts ? { blockShorts } : {}) };
      await chrome.storage.local.set({ [YOUTUBE_STATE_KEY]: state });
    } catch {
      // best effort: the bridge hides the page by default and asks the background anyway
    }
  }

  // ---- host messages ----------------------------------------------------------------------------------------------

  private onPush(message: IncomingMessage): void {
    switch (message.type) {
      case "rulesChanged":
        void this.refreshRules();
        break;
      case "timeState":
        // Before the profile is applied, a pushed time state is older than the rules reply that follows it.
        if (this.ready) void this.enqueue(() => this.applyTime(normalizeTime(message)));
        break;
      case "accessDecided":
        void this.onAccessDecided(message);
        break;
      case "error":
        this.log("host error", `${message.code}: ${message.message}`);
        break;
      default:
        break;
    }
  }

  /**
   * getRules again (coalesced: changes during a refresh trigger one more). Resolves once the rules asked for are
   * installed — also when a refresh was already running (it then runs once more and this call waits for it).
   */
  async refreshRules(): Promise<void> {
    if (!this.ready) {
      this.refreshAgain = true;
      return;
    }
    if (this.refreshing) {
      this.refreshAgain = true;
      return new Promise((resolve) => this.refreshWaiters.push(resolve));
    }
    this.refreshing = true;
    try {
      do {
        this.refreshAgain = false;
        const id = this.connectionId;
        const rules = await this.client.request({ type: "getRules" });
        if (id !== this.connectionId || rules.type !== "rules") return;
        await this.enqueue(() => (id === this.connectionId ? this.applyRules(rules) : Promise.resolve()));
        this.broadcastState();
      } while (this.refreshAgain && this.ready);
    } catch (error) {
      this.log("rules refresh failed", errorText(error));
    } finally {
      this.refreshing = false;
      const waiters = this.refreshWaiters;
      this.refreshWaiters = [];
      for (const resolve of waiters) resolve();
    }
  }

  private async onAccessDecided(message: AccessDecidedMessage): Promise<void> {
    const status = message.status === "approved" ? "approved" : "denied";
    this.decisions.set(message.requestId, status);
    // Install the new rule before the block page navigates back, whatever order the service pushes in.
    if (status === "approved" && this.ready) await this.refreshRules();
    this.broadcast({ type: "wf:accessDecided", requestId: message.requestId, status });
    // Tell the child, for requests sent from this browser (the service pushes every request of the member).
    if (this.requested.has(message.requestId)) {
      try {
        await chrome.notifications.create(`wf-decision-${message.requestId}`, {
          type: "basic",
          iconUrl: chrome.runtime.getURL("icons/icon-128.png"),
          title: "WebFilter",
          message: chrome.i18n.getMessage(status === "approved" ? "notifyApproved" : "notifyDenied") || status,
          priority: 1,
        });
      } catch (error) {
        this.log("notification failed", errorText(error));
      }
    }
  }

  async heartbeat(): Promise<void> {
    if (!this.ready) return;
    const id = this.connectionId;
    try {
      const activity = await collectActivity();
      const reply = await this.client.request({ type: "heartbeat", ...activity });
      if (id === this.connectionId && reply.type === "timeState") await this.enqueue(() => this.applyTime(normalizeTime(reply)));
    } catch (error) {
      this.log("heartbeat failed", errorText(error));
    }
  }

  // ---- page and content-script requests ---------------------------------------------------------------------------

  snapshot(): StatusSnapshot {
    const mode = this.mode;
    return {
      version: chrome.runtime.getManifest().version,
      installType: this.installType,
      policyInstalled: this.policyInstalled,
      connection: this.connection,
      lastError: this.lastError ?? this.rulesWarning,
      user: this.hello ? { name: this.hello.user?.name ?? "", role: this.hello.user?.role ?? "" } : null,
      mode,
      youTubeMode: this.ready ? (mode === "unrestricted" ? "unrestricted" : (this.rules?.youTube.mode ?? null)) : null,
      time: this.time,
      timeUp: isTimeUp(this.time),
      rulesVersion: this.rules?.rulesVersion ?? null,
      serverVersion: this.hello?.serverVersion ?? null,
      inferredReason: inferReason(this.time, mode),
      canRequest: this.connection === "service" && mode !== "baseline",
      features: { ...this.features },
    };
  }

  /**
   * The block page's report and its question "why?". Compiled rules redirect with the URL only, so the service decides:
   * reportBlock (first load: records the block) or decideUrl (reloads: records nothing) returns the service's reason,
   * category, whether a request can be sent, and what kind. The reason the page sends is a guess from the installed
   * rules (explainBlock); it stands only for YouTube channel reasons, and it is what the page shows when the service
   * cannot answer (disconnected, or an older service). The host fallback answers service_unavailable. A parent's own
   * block (mode baseline, PROTECTION-SPEC §13) is never reported: decideUrl only, and never a request.
   */
  async reportBlock(
    url: string,
    reason: string | null | undefined,
    ruleId: string | null | undefined,
    frame: "main_frame" | "sub_frame" = "main_frame",
    alreadyReported = false,
  ): Promise<ReportBlockReply> {
    const guess: string = reason ?? (isWebUrl(url) ? this.explain(url, frame) : inferReason(this.time, this.mode));
    const action = requestActionFor(guess);
    const baseline = this.baseline;
    const offline: ReportBlockReply = {
      decision: "block",
      reason: guess,
      source: reason ? "page" : "local",
      category: null,
      canRequest: this.connection === "service" && reason !== FAILCLOSED && !baseline,
      requestKind: action === "none" ? null : action,
      reported: false,
    };
    // A frame (an ad slot, an embed) is never reported: blocked ad networks would flood the parents' activity log. The
    // block page shows a quiet placeholder there and does not ask; this is the background's own guard.
    if (reason === FAILCLOSED || !this.ready || !isWebUrl(url) || frame === "sub_frame") return offline;
    // Channel, Shorts and jump reasons are decided by the extension (the service cannot see page data or where a page
    // came from, and decideUrl would judge the URL only): a reload keeps the page's reason without asking again.
    const pageDecided = typeof reason === "string" && PAGE_DECIDED_REASONS.has(reason);
    // Nor does a parent's (a jump): there is nothing to record and nothing to ask.
    if (pageDecided && (alreadyReported || baseline)) return offline;
    // A blocked search is a search too (e.g. image search, or a search engine that is not allowed).
    if (!alreadyReported) this.logSearch(url);
    // For the protection reasons the URL itself may well be allowed: a service that answers "allow" (or an older one
    // that judges the URL) must not turn the page into "allowed now".
    const extensionOnly = typeof reason === "string" && EXTENSION_ONLY_REASONS.has(reason);
    // decideUrl records nothing: every reload, and every block of a parent's.
    const askOnly = alreadyReported || baseline;
    try {
      const reply = askOnly
        ? await this.client.request({ type: "decideUrl", url })
        : await this.client.request({ type: "reportBlock", url, reason: reportableReason(guess) ?? guess, ...(ruleId ? { ruleId } : {}) });
      if (reply.type === "reportBlockResult" || reply.type === "decideUrlResult") {
        const kind = reply.requestKind === "url" || reply.requestKind === "time" || reply.requestKind === "youtube" ? reply.requestKind : null;
        if (extensionOnly) {
          return { ...offline, source: "service", canRequest: false, requestKind: null, reported: reply.type === "reportBlockResult" };
        }
        return {
          decision: reply.decision === "allow" ? "allow" : "block",
          reason: typeof reply.reason === "string" && reply.reason ? reply.reason : guess,
          source: "service",
          category: reply.category ?? null,
          canRequest: reply.canRequest === true && !baseline,
          requestKind: kind,
          reported: reply.type === "reportBlockResult" && reply.decision !== "allow",
        };
      }
      return { ...offline, reported: !askOnly }; // an older service acknowledged with error "ok"
    } catch (error) {
      if (error instanceof HostError && error.code === "service_unavailable") {
        return { ...offline, reason: "blocked_service_unavailable", source: "host", canRequest: false, requestKind: null };
      }
      this.log(askOnly ? "decideUrl failed" : "reportBlock failed", errorText(error));
      return offline;
    }
  }

  /** The reason the installed rules give for blocking `url` (see shared/explain.ts). */
  explain(url: string, frame: "main_frame" | "sub_frame" = "main_frame"): ReasonCode {
    const session = this.sessionRulesInstalled ? this.installedSession : [];
    const ctx = { connection: this.connection, mode: this.mode, time: this.time };
    return explainBlock(url, this.installedDynamic, session, ctx, frame);
  }

  async requestAccess(message: RequestAccessPageMessage): Promise<RequestAccessReply> {
    if (!REQUEST_KINDS.has(message.kind)) return { result: "error", requestId: null };
    // A parent has no one to ask (PROTECTION-SPEC §13); the service would answer "unavailable" too.
    if (!this.ready || this.connection !== "service" || this.baseline) return { result: "unavailable", requestId: null };
    const note = typeof message.note === "string" && message.note.trim() ? message.note.trim().slice(0, MAX_NOTE) : undefined;
    let target: string | undefined;
    let minutes: number | undefined;
    if (message.kind === "url") {
      if (!isWebUrl(message.target)) return { result: "error", requestId: null };
      target = message.target!;
    } else if (message.kind === "youtube") {
      if (!isChannelId(message.target)) return { result: "error", requestId: null };
      target = message.target;
    } else if (message.kind === "time") {
      minutes = message.minutes === 30 ? 30 : 15;
    } else if (typeof message.target === "string") {
      target = message.target;
    }
    try {
      const reply = await this.client.request({
        type: "requestAccess",
        kind: message.kind,
        ...(target !== undefined ? { target } : {}),
        ...(minutes !== undefined ? { minutes } : {}),
        ...(note !== undefined ? { note } : {}),
      });
      if (reply.type !== "requestAccessResult") return { result: "error", requestId: null };
      if (reply.requestId) this.requested.add(reply.requestId);
      return { result: reply.result, requestId: reply.requestId ?? null };
    } catch (error) {
      this.log("requestAccess failed", errorText(error));
      const unavailable = error instanceof HostError && error.code === "service_unavailable";
      return { result: unavailable ? "unavailable" : "error", requestId: null };
    }
  }

  decisionOf(requestId: string): DecisionReply {
    const decided = this.decisions.get(requestId);
    if (decided) return { status: decided };
    return { status: this.requested.has(requestId) ? "pending" : "unknown" };
  }

  myChannels(): MyChannelsReply {
    const policy = this.ready ? (this.rules?.youTube ?? null) : null;
    return { ready: this.ready, mode: policy?.mode ?? null, channels: approvedChannels(policy) };
  }

  youTubeContext(): YouTubeContext {
    return {
      inert: !this.policyInstalled,
      ready: this.ready,
      filterMode: this.mode,
      policy: this.rules?.youTube ?? null,
      time: this.time,
      blockShorts: this.features.blockYouTubeShorts,
    };
  }

  /** Decides a YouTube detection from a content script and says where the frame should go. */
  async decideYouTubeDetection(detection: YouTubeDetection, senderUrl: string | undefined): Promise<YouTubeDetectReply> {
    await this.whenSettled(5_000);
    const url = pageUrlFor(detection.url, senderUrl);
    if (!url) return { decision: "stale" };
    const decision = decideYouTube(this.youTubeContext(), { url, channelId: detection.channelId, handle: detection.handle });
    if (decision.decision === "allow") return decision;
    if (decision.decision === "redirect") return { ...decision, navigateTo: chrome.runtime.getURL(MY_CHANNELS_PAGE) };
    return {
      ...decision,
      navigateTo: buildBlockPageUrl(chrome.runtime.getURL(BLOCK_PAGE), { reason: decision.reason, url, channelId: decision.channelId ?? null }),
    };
  }

  /**
   * webNavigation.onHistoryStateUpdated: a page changed its address in place (history API), which DNR never sees.
   * If the installed rules would block the new address, the tab goes to the block page as a real navigation would.
   * Only top frames (a sub-frame cannot be navigated without more permissions; YouTube frames are handled by the
   * content script), and only when every applicable rule could be evaluated here.
   */
  async checkInPageNavigation(tabId: number, frameId: number, url: string): Promise<boolean> {
    if (!this.ready || frameId !== 0 || tabId < 0 || !isWebUrl(url) || this.mode === "unrestricted") return false;
    const rules = [...(this.sessionRulesInstalled ? this.installedSession : []), ...this.installedDynamic];
    let target: string | null = null;
    if (classifyNavigation(rules, url) === "block") target = `${chrome.runtime.getURL(BLOCK_PAGE)}#${url}`;
    // YouTube is an SPA: moving to a Short changes the address in place (PROTECTION-SPEC §8).
    else if (this.features.blockYouTubeShorts && isShortsUrl(url)) target = buildBlockPageUrl(chrome.runtime.getURL(BLOCK_PAGE), { reason: SHORTS_REASON, url });
    if (target === null) return false;
    try {
      await chrome.tabs.update(tabId, { url: target });
      return true;
    } catch (error) {
      this.log("in-page navigation block failed", errorText(error));
      return false;
    }
  }

  // ---- protection features: jump override, search log ----------------------------------------------------------

  /**
   * "Open anyway" on a jump block (teens and parents, features.canOverrideJump). Only a top-level
   * blocked.html?reason=blocked_jump page may ask, for its own URL. reportOverride is sent (never for a parent's own,
   * PROTECTION-SPEC §13), the tab stops being guarded, and the target opens once.
   */
  async overrideJump(url: string, tabId: number | undefined, frameId: number | undefined, pageUrl: string | undefined): Promise<OverrideJumpReply> {
    const features = this.features;
    if (!features.jumpGuard || !features.canOverrideJump) return { ok: false, error: "not_allowed" };
    const target = safeReturnUrl(url);
    const page = parseBlockPageUrl(pageUrl ?? "");
    const samePage = page.reason === JUMP_REASON && (!page.url || safeReturnUrl(page.url) === target);
    if (tabId === undefined || tabId < 0 || frameId !== 0 || !target || !samePage) return { ok: false, error: "bad_request" };
    if (!this.baseline) {
      this.client
        .request({ type: "reportOverride", url: target, reason: "blocked_jump" })
        .catch((error: unknown) => this.log("reportOverride failed", errorText(error)));
    }
    await this.jumps.allowOnce(tabId);
    try {
      await chrome.tabs.update(tabId, { url: target });
      return { ok: true };
    } catch (error) {
      this.log("override navigation failed", errorText(error));
      return { ok: false, error: "failed" };
    }
  }

  /**
   * A top-frame commit or in-page address change (webNavigation): a search results page is reported once per distinct
   * search (PROTECTION-SPEC §9). Prerendered pages, reloads and Back/Forward are not new searches.
   */
  noteSearch(details: {
    tabId: number;
    frameId: number;
    url: string;
    transitionType?: string;
    transitionQualifiers?: readonly string[];
    documentLifecycle?: string;
  }): void {
    if (details.frameId !== 0 || details.tabId < 0 || details.documentLifecycle === "prerender") return;
    if (details.transitionType === "reload" || details.transitionQualifiers?.includes("forward_back")) return;
    this.logSearch(details.url);
  }

  private logSearch(url: string): void {
    if (!this.features.logSearches || this.connection !== "service" || this.baseline) return;
    const hit = extractSearch(url);
    if (!hit || hit.page > 1 || !this.searches.admit(hit.key, this.now())) return;
    this.client
      .request({ type: "reportSearch", engine: hit.engine, query: hit.query, url: url.slice(0, MAX_SEARCH_URL_LENGTH) })
      .catch((error: unknown) => {
        // Lost with the connection: the next sighting tries again. Any other answer (an older service) is final.
        if (error instanceof DisconnectedError) this.searches.forget(hit.key);
        this.log("reportSearch failed", errorText(error));
      });
  }

  // ---- original URLs of fail-closed block pages -----------------------------------------------------------------

  /** webNavigation.onBeforeNavigate: remember the http(s) URL each frame is about to load. */
  noteNavigation(tabId: number, frameId: number, url: string): void {
    if (tabId < 0 || !isWebUrl(url)) return;
    this.frameOrigins.set(`${tabId}:${frameId}`, url);
    if (this.frameOrigins.size > 500) {
      const oldest = this.frameOrigins.keys().next().value;
      if (oldest !== undefined) this.frameOrigins.delete(oldest);
    }
    this.scheduleSaveFrameOrigins();
  }

  forgetTab(tabId: number): void {
    for (const key of [...this.frameOrigins.keys()]) if (key.startsWith(`${tabId}:`)) this.frameOrigins.delete(key);
    this.scheduleSaveFrameOrigins();
  }

  /** The URL a frame tried to load before it was redirected to a block page (the static ruleset cannot pass it). */
  originalUrlFor(tabId: number | undefined, frameId: number | undefined): string | null {
    if (tabId === undefined) return null;
    return this.frameOrigins.get(`${tabId}:${frameId ?? 0}`) ?? null;
  }

  private async loadFrameOrigins(): Promise<void> {
    try {
      const stored = (await chrome.storage.session.get(ORIGINS_KEY))[ORIGINS_KEY] as Record<string, string> | undefined;
      if (stored) for (const [k, v] of Object.entries(stored)) if (!this.frameOrigins.has(k)) this.frameOrigins.set(k, v);
    } catch {
      // storage.session unavailable: memory only
    }
  }

  /** Mirrors the map to storage.session (survives a worker restart), at most twice a second. */
  private scheduleSaveFrameOrigins(): void {
    if (this.saveTimer !== null) return;
    this.saveTimer = this.timers.setTimeout(() => {
      this.saveTimer = null;
      void this.saveFrameOrigins();
    }, 500);
  }

  private async saveFrameOrigins(): Promise<void> {
    try {
      await chrome.storage.session.set({ [ORIGINS_KEY]: Object.fromEntries(this.frameOrigins) });
    } catch {
      // best effort
    }
  }

  // ---- broadcasts -------------------------------------------------------------------------------------------------

  private broadcastState(): void {
    this.broadcast({ type: "wf:stateChanged", status: this.snapshot() });
  }

  private broadcast(message: unknown): void {
    try {
      const sent = chrome.runtime.sendMessage(message) as Promise<unknown> | undefined;
      sent?.catch?.(() => undefined); // no extension page open
    } catch {
      // no receivers
    }
  }
}

function sameTime(a: TimeState | null, b: TimeState | null): boolean {
  return a?.state === b?.state && (a?.remainingMinutes ?? null) === (b?.remainingMinutes ?? null) && (a?.windowEndsAt ?? null) === (b?.windowEndsAt ?? null);
}

/**
 * The URL to decide on: the detector's URL when it is on the same YouTube origin as the frame the browser reports
 * (SPA navigations change the path without a new document), otherwise the browser's own URL.
 */
export function pageUrlFor(detectionUrl: string, senderUrl: string | undefined): string | null {
  let sender: URL | null = null;
  try {
    sender = senderUrl ? new URL(senderUrl) : null;
  } catch {
    sender = null;
  }
  if (sender && !isYouTubeHost(sender.hostname)) return null;
  try {
    const detected = new URL(detectionUrl);
    if (!sender || detected.origin === sender.origin) return detected.href;
  } catch {
    // fall through to the sender URL
  }
  return sender ? sender.href : null;
}

/** The heartbeat payload: a browser window focused, idle state, and whether the active tab is a web page. */
export async function collectActivity(): Promise<{ focused: boolean; idle: string; hasWebTab: boolean }> {
  const [windows, idle, tabs] = await Promise.all([
    chrome.windows.getAll().catch(() => [] as chrome.windows.Window[]),
    chrome.idle.queryState(60).catch(() => "active" as chrome.idle.IdleState),
    chrome.tabs.query({ active: true, lastFocusedWindow: true }).catch(() => [] as chrome.tabs.Tab[]),
  ]);
  const active = tabs[0];
  return {
    focused: windows.some((w) => w.focused),
    idle: String(idle),
    hasWebTab: isWebUrl(active?.url ?? active?.pendingUrl),
  };
}
