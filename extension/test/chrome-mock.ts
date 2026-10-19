/**
 * A small in-memory stand-in for the chrome.* APIs the extension uses, installed on globalThis.chrome.
 * It keeps just enough state (rulesets, dynamic/session rules, storage, tabs, badge, broadcasts) for assertions.
 */
type Rule = chrome.declarativeNetRequest.Rule;
type Listener = (...args: unknown[]) => unknown;

export interface MockTab {
  id: number;
  url: string;
  active?: boolean;
}

export interface ChromeMockState {
  installType: string;
  enabledRulesets: Set<string>;
  dynamicRules: Map<number, Rule>;
  sessionRules: Map<number, Rule>;
  dynamicUpdates: number;
  local: Record<string, unknown>;
  session: Record<string, unknown>;
  badgeText: string;
  badgeColor: string;
  notifications: { id: string; message: string }[];
  tabs: MockTab[];
  tabUpdates: { tabId: number; url: string }[];
  broadcasts: unknown[];
  tabMessages: { tabId: number; message: unknown; frameId?: number }[];
  focused: boolean;
  idle: string;
  lastError: { message: string } | undefined;
  listeners: Record<string, Listener[]>;
}

function event(state: ChromeMockState, name: string) {
  state.listeners[name] = [];
  return {
    addListener: (cb: Listener) => void state.listeners[name]!.push(cb),
    removeListener: (cb: Listener) => {
      state.listeners[name] = state.listeners[name]!.filter((l) => l !== cb);
    },
  };
}

function hasNull(value: unknown): boolean {
  if (value === null) return true;
  if (Array.isArray(value)) return value.some(hasNull);
  if (typeof value === "object") return Object.values(value as object).some(hasNull);
  return false;
}

/** Stand-in for RE2's compiled-program limit: longer regexes are refused. */
export const MAX_REGEX = 2000;

function applyUpdate(target: Map<number, Rule>, update: { removeRuleIds?: number[]; addRules?: Rule[] }): void {
  for (const id of update.removeRuleIds ?? []) target.delete(id);
  for (const rule of update.addRules ?? []) {
    if (hasNull(rule)) throw new Error(`Error at rule ${rule.id}: null is not a valid value`);
    if (target.has(rule.id)) throw new Error(`Rule with id ${rule.id} does not have a unique ID.`);
    if ((rule.condition?.regexFilter?.length ?? 0) > MAX_REGEX) throw new Error(`Rule with id ${rule.id} specified a more complex regex than allowed as part of the regexFilter key.`);
    target.set(rule.id, structuredClone(rule));
  }
}

export function installChromeMock(initial: Partial<Pick<ChromeMockState, "installType" | "tabs">> = {}): ChromeMockState {
  const state: ChromeMockState = {
    installType: initial.installType ?? "admin",
    enabledRulesets: new Set(["failclosed"]),
    dynamicRules: new Map(),
    sessionRules: new Map(),
    dynamicUpdates: 0,
    local: {},
    session: {},
    badgeText: "",
    badgeColor: "",
    notifications: [],
    tabs: initial.tabs ?? [],
    tabUpdates: [],
    broadcasts: [],
    tabMessages: [],
    focused: true,
    idle: "active",
    lastError: undefined,
    listeners: {},
  };

  const storageArea = (bag: () => Record<string, unknown>) => ({
    get: async (keys?: string | string[]) => {
      const all = bag();
      if (keys === undefined) return structuredClone(all);
      const list = Array.isArray(keys) ? keys : [keys];
      const out: Record<string, unknown> = {};
      for (const k of list) if (k in all) out[k] = structuredClone(all[k]);
      return out;
    },
    set: async (items: Record<string, unknown>) => {
      Object.assign(bag(), structuredClone(items));
    },
    remove: async (keys: string | string[]) => {
      for (const k of Array.isArray(keys) ? keys : [keys]) delete bag()[k];
    },
  });

  const chromeMock = {
    runtime: {
      id: "testextensionid",
      getURL: (path: string) => `chrome-extension://testextensionid/${path.replace(/^\//, "")}`,
      getManifest: () => ({ version: "0.3.0" }),
      get lastError() {
        return state.lastError;
      },
      connectNative: () => {
        throw new Error("connectNative is not mocked; inject a connector");
      },
      sendMessage: async (message: unknown) => {
        state.broadcasts.push(structuredClone(message));
        return undefined;
      },
      onMessage: event(state, "runtime.onMessage"),
      onStartup: event(state, "runtime.onStartup"),
      onInstalled: event(state, "runtime.onInstalled"),
    },
    management: {
      getSelf: async () => ({ id: "testextensionid", installType: state.installType }),
    },
    declarativeNetRequest: {
      updateEnabledRulesets: async (o: { enableRulesetIds?: string[]; disableRulesetIds?: string[] }) => {
        for (const id of o.disableRulesetIds ?? []) state.enabledRulesets.delete(id);
        for (const id of o.enableRulesetIds ?? []) state.enabledRulesets.add(id);
      },
      getEnabledRulesets: async () => [...state.enabledRulesets],
      getDynamicRules: async () => [...state.dynamicRules.values()].map((r) => structuredClone(r)),
      updateDynamicRules: async (u: { removeRuleIds?: number[]; addRules?: Rule[] }) => {
        state.dynamicUpdates += 1;
        const next = new Map(state.dynamicRules);
        applyUpdate(next, u);
        state.dynamicRules = next;
      },
      isRegexSupported: async ({ regex }: { regex: string }) =>
        regex.length > MAX_REGEX ? { isSupported: false, reason: "memoryLimitExceeded" } : { isSupported: true },
      getSessionRules: async () => [...state.sessionRules.values()].map((r) => structuredClone(r)),
      updateSessionRules: async (u: { removeRuleIds?: number[]; addRules?: Rule[] }) => {
        const next = new Map(state.sessionRules);
        applyUpdate(next, u);
        state.sessionRules = next;
      },
    },
    storage: {
      local: storageArea(() => state.local),
      session: storageArea(() => state.session),
      onChanged: event(state, "storage.onChanged"),
    },
    action: {
      setBadgeText: async ({ text }: { text: string }) => {
        state.badgeText = text;
      },
      setBadgeBackgroundColor: async ({ color }: { color: string }) => {
        state.badgeColor = color;
      },
    },
    notifications: {
      create: async (id: string, options: { message: string }) => {
        state.notifications.push({ id, message: options.message });
        return id;
      },
    },
    i18n: {
      getMessage: (key: string) => key,
    },
    tabs: {
      query: async (q: { url?: string[]; active?: boolean }) =>
        state.tabs
          .filter((t) => (q.active ? t.active : true))
          .filter((t) => (q.url ? /^https?:/.test(t.url) : true))
          .map((t) => ({ ...t })),
      update: async (tabId: number, props: { url: string }) => {
        state.tabUpdates.push({ tabId, url: props.url });
        const tab = state.tabs.find((t) => t.id === tabId);
        if (tab) tab.url = props.url;
        return { id: tabId, url: props.url };
      },
      sendMessage: async (tabId: number, message: unknown, options?: { frameId?: number }) => {
        state.tabMessages.push({ tabId, message, frameId: options?.frameId });
      },
      onActivated: event(state, "tabs.onActivated"),
      onUpdated: event(state, "tabs.onUpdated"),
      onRemoved: event(state, "tabs.onRemoved"),
    },
    windows: {
      getAll: async () => [{ id: 1, focused: state.focused }],
      onFocusChanged: event(state, "windows.onFocusChanged"),
    },
    idle: {
      queryState: async () => state.idle,
      setDetectionInterval: () => undefined,
      onStateChanged: event(state, "idle.onStateChanged"),
    },
    alarms: {
      create: async () => undefined,
      onAlarm: event(state, "alarms.onAlarm"),
    },
    webNavigation: {
      onBeforeNavigate: event(state, "webNavigation.onBeforeNavigate"),
      onHistoryStateUpdated: event(state, "webNavigation.onHistoryStateUpdated"),
    },
  };

  (globalThis as unknown as { chrome: unknown }).chrome = chromeMock;
  return state;
}

/** Calls every listener registered for a mock event and returns their results. */
export function fire(state: ChromeMockState, name: string, ...args: unknown[]): unknown[] {
  return (state.listeners[name] ?? []).map((l) => l(...args));
}
