import type { BackgroundRequest, Broadcast, StatusSnapshot } from "../shared/messages";

/** chrome.i18n lookup; falls back to the key so a missing string is visible rather than blank. */
export function t(key: string, substitutions?: string | string[]): string {
  const value = chrome.i18n.getMessage(key, substitutions);
  return value || key;
}

/**
 * Applies the UI language to static markup: <html lang/dir> and every [data-i18n] / [data-i18n-placeholder] element.
 * The markup ships in Hebrew (RTL first); the window title is never translated (lab automation reads it).
 */
export function localizePage(): void {
  const root = document.documentElement;
  root.lang = chrome.i18n.getMessage("langCode") || "he";
  root.dir = chrome.i18n.getMessage("textDirection") === "ltr" ? "ltr" : "rtl";
  for (const el of document.querySelectorAll<HTMLElement>("[data-i18n]")) {
    const text = chrome.i18n.getMessage(el.dataset.i18n ?? "");
    if (text) el.textContent = text;
  }
  for (const el of document.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>("[data-i18n-placeholder]")) {
    const text = chrome.i18n.getMessage(el.dataset.i18nPlaceholder ?? "");
    if (text) el.placeholder = text;
  }
}

export async function ask<T>(message: BackgroundRequest): Promise<T | null> {
  try {
    const reply = (await chrome.runtime.sendMessage(message)) as T | { error: string } | undefined;
    if (!reply || (typeof reply === "object" && "error" in (reply as object))) return null;
    return reply as T;
  } catch {
    return null;
  }
}

export function getStatus(): Promise<StatusSnapshot | null> {
  return ask<StatusSnapshot>({ type: "wf:getStatus" });
}

export function onBroadcast(handler: (message: Broadcast) => void): void {
  chrome.runtime.onMessage.addListener((message: Broadcast) => {
    if (message && typeof message === "object" && typeof message.type === "string" && message.type.startsWith("wf:")) handler(message);
  });
}

export function byId<T extends HTMLElement = HTMLElement>(id: string): T {
  const el = document.getElementById(id);
  if (!el) throw new Error(`#${id} missing`);
  return el as T;
}

export function minutesText(minutes: number): string {
  return minutes === 1 ? t("oneMinute") : t("minutesValue", String(minutes));
}
