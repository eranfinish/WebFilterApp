import type { OriginalUrlReply, ReportBlockReply, StatusSnapshot } from "../shared/messages";
import { displayUrl, parseBlockPageUrl, safeReturnUrl } from "../shared/pages";
import { FAILCLOSED, reasonMessageKey, requestActionFor } from "../shared/reasons";
import { ask, byId, getStatus, localizePage, onBroadcast, t } from "./common";
import { followDecision, pendingRequestId, returnTo, showResult, wireRequestButtons } from "./requests";

const root = document.documentElement;

function isTimeReason(reason: string): boolean {
  return reason === "blocked_daily_limit" || reason === "blocked_time_window";
}

/** Whether the condition that blocked this page is gone, so the original URL should be tried again. */
function shouldReturn(reason: string, allowedNow: boolean, status: StatusSnapshot | null): boolean {
  if (!status) return false;
  const ready = status.connection === "service" || status.connection === "host";
  if (reason === FAILCLOSED) return ready || status.connection === "inert";
  if (reason === "blocked_service_unavailable") return status.connection === "service";
  if (isTimeReason(reason)) return ready && !status.timeUp;
  // The service said the URL is allowed (stale rules) and pushed rulesChanged: the next state broadcast follows the
  // rules refresh, so go back then (only on broadcasts, see below).
  if (allowedNow) return ready;
  return false;
}

/** Remembers that this block page reported once, so a reload asks (decideUrl) instead of recording again. */
function markReported(): boolean {
  const key = `wf.reported:${location.href}`;
  try {
    const already = sessionStorage.getItem(key) === "1";
    sessionStorage.setItem(key, "1");
    return already;
  } catch {
    return false;
  }
}

async function main(): Promise<void> {
  localizePage();
  root.dataset.page = "blocked";
  const info = parseBlockPageUrl(location.href);
  const status = await getStatus();

  let url = info.url;
  if (!url) url = (await ask<OriginalUrlReply>({ type: "wf:getOriginalUrl" }))?.url ?? "";
  const back = safeReturnUrl(url);

  // Compiled rules redirect with the URL only, so the service says why (reportBlockResult). The fail-closed page is
  // never reported; without the service the background infers the reason from the installed rules.
  let reply: ReportBlockReply | null = null;
  if (info.reason !== FAILCLOSED) {
    reply = await ask<ReportBlockReply>({
      type: "wf:reportBlock",
      url,
      reason: info.reason,
      ruleId: info.ruleId,
      frame: window.top === window ? "main_frame" : "sub_frame",
      alreadyReported: markReported(),
    });
  }
  const reason = reply?.reason ?? info.reason ?? status?.inferredReason ?? "blocked_rule";
  const allowedNow = reply?.decision === "allow";

  root.dataset.reason = reason;
  root.dataset.reasonSource = reply?.source ?? (info.reason ? "page" : "status");
  root.dataset.decision = allowedNow ? "allow" : "block";
  root.dataset.url = url;
  if (allowedNow) byId("heading").textContent = t("allowedNowHeading");
  byId("reason").textContent = t(reasonMessageKey(reason));
  byId("url").textContent = url ? displayUrl(url) : "—";
  byId("url-row").hidden = !url;
  if (reply?.category) {
    byId("category").textContent = reply.category;
    byId("category-row").hidden = false;
  }

  if (allowedNow && back) {
    byId("retry-row").hidden = false;
    byId("retry").addEventListener("click", () => location.replace(back));
  }

  // What may be asked: the service's canRequest/requestKind, except where an approval cannot help (homework, YouTube
  // off, an unknown channel, service down — see requestActionFor).
  const action = requestActionFor(reason);
  const canAsk = !allowedNow && action !== "none" && (reply ? reply.canRequest : status?.canRequest === true);
  const kind = action === "none" ? null : (reply?.requestKind ?? action);
  const note = byId<HTMLTextAreaElement>("note");
  if (canAsk && kind === "url" && back) {
    byId("request").hidden = false;
    byId("ask-url").hidden = false;
    wireRequestButtons([{ element: byId<HTMLButtonElement>("ask-url"), kind: "url", target: back }], note);
  } else if (canAsk && kind === "youtube" && info.channelId) {
    byId("request").hidden = false;
    byId("ask-channel").hidden = false;
    wireRequestButtons([{ element: byId<HTMLButtonElement>("ask-channel"), kind: "youtube", target: info.channelId }], note);
  } else if (canAsk && kind === "time") {
    byId("request").hidden = false;
    byId("ask-time").hidden = false;
    wireRequestButtons(
      [
        { element: byId<HTMLButtonElement>("ask-15"), kind: "time", minutes: 15 },
        { element: byId<HTMLButtonElement>("ask-30"), kind: "time", minutes: 30 },
      ],
      note,
    );
  } else if (!allowedNow && action !== "none" && status?.policyInstalled && !canAsk) {
    // MVP-PLAN §8.1: no request button while the service is unavailable.
    const serviceDown = reply?.source === "host" || reply?.source === "local" || status.connection !== "service";
    if (serviceDown) showResult(t("result_unavailable"), "unavailable");
  }
  root.dataset.canRequest = String(canAsk);
  if (pendingRequestId()) showResult(t("result_waiting"), "waiting");
  root.dataset.ready = "true";

  followDecision(() => returnTo(back));
  if (!allowedNow && shouldReturn(reason, false, status)) returnTo(back);
  onBroadcast((message) => {
    if (message.type === "wf:stateChanged" && shouldReturn(reason, allowedNow, message.status)) returnTo(back);
  });
}

void main();
