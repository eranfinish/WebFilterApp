import type { StatusSnapshot } from "../shared/messages";
import { displayUrl, originalUrlFromHash, safeReturnUrl } from "../shared/pages";
import { byId, getStatus, localizePage, minutesText, onBroadcast, t } from "./common";
import { followDecision, pendingRequestId, returnTo, showResult, wireRequestButtons } from "./requests";

const root = document.documentElement;

function render(status: StatusSnapshot | null): void {
  const state = status?.time?.state ?? "expired";
  const outside = state === "outside_window";
  root.dataset.timeState = state;
  byId("heading").textContent = t(outside ? "timeupHeadingWindow" : "timeupHeading");
  byId("detail").textContent = t(outside ? "timeupBodyWindow" : "timeupBodyLimit");
  // Remaining time and the window end matter only once time is available again (a grant), before the page returns.
  const up = state === "expired" || outside;
  const remaining = status?.time?.remainingMinutes;
  const showRemaining = typeof remaining === "number" && !up;
  byId("remaining-row").hidden = !showRemaining;
  if (showRemaining) byId("remaining").textContent = minutesText(Math.max(0, remaining));
  const windowEnd = status?.time?.windowEndsAt;
  byId("window-row").hidden = !windowEnd || up;
  if (windowEnd) byId("window-end").textContent = windowEnd;
}

async function main(): Promise<void> {
  localizePage();
  root.dataset.page = "timeup";
  const url = originalUrlFromHash(location.href);
  const back = safeReturnUrl(url);
  byId("url").textContent = url ? displayUrl(url) : "";
  byId("url-row").hidden = !url;

  const status = await getStatus();
  render(status);
  const canAsk = status?.canRequest === true;
  byId("request").hidden = !canAsk;
  if (!canAsk && status?.policyInstalled) showResult(t("result_unavailable"), "unavailable");
  wireRequestButtons(
    [
      { element: byId<HTMLButtonElement>("ask-15"), kind: "time", minutes: 15 },
      { element: byId<HTMLButtonElement>("ask-30"), kind: "time", minutes: 30 },
    ],
    byId<HTMLTextAreaElement>("note"),
  );
  if (pendingRequestId()) showResult(t("result_waiting"), "waiting");
  root.dataset.ready = "true";

  const timeIsBack = (s: StatusSnapshot | null): boolean =>
    !!s && (s.connection === "service" || s.connection === "host") && !s.timeUp;
  followDecision(() => {
    // An approved time request: return once the new time state has arrived.
    void getStatus().then((s) => {
      if (timeIsBack(s)) returnTo(back);
    });
  });
  onBroadcast((message) => {
    if (message.type !== "wf:stateChanged") return;
    render(message.status);
    if (timeIsBack(message.status)) {
      byId("back-row").hidden = !back;
      returnTo(back);
    }
  });
  if (timeIsBack(status)) {
    byId("back-row").hidden = !back;
    returnTo(back);
  }
  if (back) byId<HTMLAnchorElement>("back").href = back;
}

void main();
