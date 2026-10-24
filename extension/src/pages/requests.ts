import type { DecisionReply, RequestAccessReply } from "../shared/messages";
import type { RequestKind } from "../shared/protocol";
import { requestResultMessageKey } from "../shared/reasons";
import { ask, byId, onBroadcast, t } from "./common";

const pendingKey = (): string => `wf.request:${location.href}`;

/** The request this page already sent (kept across reloads of the same block page). */
export function pendingRequestId(): string | null {
  try {
    return sessionStorage.getItem(pendingKey());
  } catch {
    return null;
  }
}

function rememberRequest(id: string): void {
  try {
    sessionStorage.setItem(pendingKey(), id);
  } catch {
    // private mode or storage disabled: the broadcast still reaches this page while it stays open
  }
}

function forgetRequest(): void {
  try {
    sessionStorage.removeItem(pendingKey());
  } catch {
    // nothing kept
  }
}

export function showResult(text: string, state: string): void {
  const el = byId("result");
  el.textContent = text;
  el.hidden = false;
  el.dataset.state = state;
  document.documentElement.dataset.requestResult = state;
}

/** A button's request: its kind and target, and for a YouTube channel the names the page knew (PROTECTION-SPEC §15). */
export interface RequestButton {
  element: HTMLButtonElement;
  kind: RequestKind;
  target?: string | null;
  minutes?: number;
  title?: string | null;
  handle?: string | null;
  searchQuery?: string | null;
}

/**
 * Wires the request buttons of a page. Each button sends requestAccess with its kind/target/minutes (and a channel's
 * names) and the note, then shows the result in plain language. Buttons are disabled after a request was accepted.
 */
export function wireRequestButtons(buttons: RequestButton[], note: HTMLTextAreaElement | null): void {
  const all = buttons.map((b) => b.element);
  for (const button of buttons) {
    button.element.addEventListener("click", async () => {
      for (const b of all) b.disabled = true;
      showResult(t("requestSending"), "sending");
      const reply = await ask<RequestAccessReply>({
        type: "wf:requestAccess",
        kind: button.kind,
        target: button.target ?? null,
        minutes: button.minutes ?? null,
        note: note?.value ?? null,
        title: button.title ?? null,
        handle: button.handle ?? null,
        searchQuery: button.searchQuery ?? null,
      });
      const result = reply?.result ?? "error";
      showResult(t(requestResultMessageKey(result)), result);
      if (reply?.requestId) rememberRequest(reply.requestId);
      // Allow another try only when nothing was recorded.
      if (result === "error") for (const b of all) b.disabled = false;
    });
  }
}

/**
 * Follows the decision on this page's request: the broadcast while open, and a lookup when the page becomes visible
 * again (a frozen or discarded tab misses broadcasts). On approval `onApproved` runs (navigate back).
 */
export function followDecision(onApproved: () => void): void {
  const handle = (status: DecisionReply["status"]): void => {
    if (status === "approved") {
      // Forget it before going back: if the page is blocked again — for another reason, such as a time grant for a site
      // that is still not allowed — that block page shows its own reason and request, instead of finding this decided
      // request, announcing the approval again and going back until the loop guard stops it (the lab, 2026-10-25).
      forgetRequest();
      showResult(t("decision_approved"), "approved");
      setTimeout(onApproved, 700);
    } else if (status === "denied") {
      showResult(t("decision_denied"), "denied");
    }
  };
  onBroadcast((message) => {
    if (message.type === "wf:accessDecided" && message.requestId === pendingRequestId()) handle(message.status);
  });
  const check = async (): Promise<void> => {
    const id = pendingRequestId();
    if (!id) return;
    const reply = await ask<DecisionReply>({ type: "wf:getDecision", requestId: id });
    if (reply) handle(reply.status);
  };
  document.addEventListener("visibilitychange", () => {
    if (document.visibilityState === "visible") void check();
  });
  void check();
}

/**
 * Navigates back to the original URL, at most three times per URL within 30 s (a URL that is still blocked comes
 * back with a different reason, but never loop).
 */
export function returnTo(url: string | null): void {
  if (!url) return;
  const key = `wf.return:${url}`;
  const now = Date.now();
  let history: number[] = [];
  try {
    history = (JSON.parse(sessionStorage.getItem(key) ?? "[]") as number[]).filter((ts) => now - ts < 30_000);
    if (history.length >= 3) return;
    sessionStorage.setItem(key, JSON.stringify([...history, now]));
  } catch {
    // no storage: still navigate
  }
  location.replace(url);
}
