import type { BackgroundRequest, YouTubeUrlChangedMessage } from "../shared/messages";
import { isYouTubeHost } from "../shared/youtube-url";
import { Controller, type ControllerOptions } from "./controller";

const HEARTBEAT_ALARM = "wf-heartbeat";

function isExtensionPage(sender: chrome.runtime.MessageSender): boolean {
  return sender.id === chrome.runtime.id && typeof sender.url === "string" && sender.url.startsWith(chrome.runtime.getURL(""));
}

function isOwnContentScript(sender: chrome.runtime.MessageSender): boolean {
  return sender.id === chrome.runtime.id && sender.tab !== undefined;
}

/**
 * Creates the controller and registers every event listener synchronously (MV3: listeners registered at the top
 * level wake the service worker), then starts it.
 */
export function createBackground(options: ControllerOptions = {}): Controller {
  const controller = new Controller(options);

  chrome.runtime.onMessage.addListener((message: BackgroundRequest, sender, sendResponse) => {
    const reply = handleMessage(controller, message, sender);
    if (reply === undefined) return false;
    reply.then(sendResponse, (error: unknown) => sendResponse({ error: String(error) }));
    return true;
  });

  chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === HEARTBEAT_ALARM) controller.onAlarm();
  });
  // 30 s is the MV3 minimum period; focus and idle changes add immediate beats.
  void chrome.alarms.create(HEARTBEAT_ALARM, { periodInMinutes: 0.5, delayInMinutes: 0.5 });

  chrome.windows.onFocusChanged.addListener(() => controller.requestHeartbeat());
  chrome.idle.setDetectionInterval(60);
  chrome.idle.onStateChanged.addListener(() => controller.requestHeartbeat(0));
  chrome.tabs.onActivated.addListener(() => controller.requestHeartbeat());
  chrome.tabs.onUpdated.addListener((_tabId, change, tab) => {
    if (change.url && tab.active) controller.requestHeartbeat();
  });
  chrome.tabs.onRemoved.addListener((tabId) => controller.forgetTab(tabId));

  // The static fail-closed rule cannot carry the original URL, so remember what each frame tried to load.
  chrome.webNavigation.onBeforeNavigate.addListener((details) => controller.noteNavigation(details.tabId, details.frameId, details.url));

  // In-page (history API) navigations, which DNR does not see: block them if the rules would. On YouTube the
  // content script decides channels; tell that frame's bridge at once (its own events and URL polling also trigger).
  chrome.webNavigation.onHistoryStateUpdated.addListener((details) => {
    let host = "";
    try {
      host = new URL(details.url).hostname;
    } catch {
      return;
    }
    if (isYouTubeHost(host)) {
      const message: YouTubeUrlChangedMessage = { type: "wf:ytUrlChanged" };
      chrome.tabs.sendMessage(details.tabId, message, { frameId: details.frameId }).catch(() => undefined);
    }
    void controller.checkInPageNavigation(details.tabId, details.frameId, details.url);
  });

  chrome.runtime.onStartup.addListener(() => undefined);
  chrome.runtime.onInstalled.addListener(() => undefined);

  void controller.start();
  return controller;
}

function handleMessage(
  controller: Controller,
  message: BackgroundRequest,
  sender: chrome.runtime.MessageSender,
): Promise<unknown> | undefined {
  if (!message || typeof message !== "object" || typeof message.type !== "string") return undefined;
  switch (message.type) {
    case "wf:getStatus":
      return Promise.resolve(controller.snapshot());
    case "wf:getMyChannels":
      return Promise.resolve(controller.myChannels());
    case "wf:getDecision":
      return isExtensionPage(sender) ? Promise.resolve(controller.decisionOf(String(message.requestId))) : undefined;
    case "wf:reportBlock":
      return isExtensionPage(sender)
        ? controller.reportBlock(
            String(message.url ?? ""),
            message.reason ?? null,
            message.ruleId ?? null,
            message.frame === "sub_frame" ? "sub_frame" : "main_frame",
            message.alreadyReported === true,
          )
        : undefined;
    case "wf:requestAccess":
      return isExtensionPage(sender) ? controller.requestAccess(message) : undefined;
    case "wf:getOriginalUrl":
      return isExtensionPage(sender) ? Promise.resolve({ url: controller.originalUrlFor(sender.tab?.id, sender.frameId) }) : undefined;
    case "wf:ytDetect":
      return isOwnContentScript(sender) && message.detection
        ? controller.decideYouTubeDetection(message.detection, sender.url)
        : undefined;
    default:
      return undefined;
  }
}
