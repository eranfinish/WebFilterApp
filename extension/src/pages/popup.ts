import type { StatusSnapshot } from "../shared/messages";
import { remainingMinutes } from "../shared/time-actions";
import { byId, getStatus, localizePage, minutesText, onBroadcast, t } from "./common";

type Look = "ok" | "warn" | "error" | "idle";

function connectionView(status: StatusSnapshot | null): { look: Look; title: string; detail: string } {
  if (!status) return { look: "error", title: t("statusUnknown"), detail: "" };
  switch (status.connection) {
    case "inert":
      return { look: "idle", title: t("statusInert"), detail: t("statusInertDetail") };
    case "service":
      // A parent's own account (PROTECTION-SPEC §13): only the baseline applies, and nothing of it is recorded.
      return { look: "ok", title: t("statusService"), detail: t(status.mode === "baseline" ? "statusBaselineDetail" : "statusServiceDetail") };
    case "host":
      return { look: "warn", title: t("statusHost"), detail: t("statusHostDetail") };
    case "starting":
    case "connecting":
      return { look: "warn", title: t("statusConnecting"), detail: t("statusDisconnectedDetail") };
    default:
      return { look: "error", title: t("statusDisconnected"), detail: t("statusDisconnectedDetail") };
  }
}

function timeText(status: StatusSnapshot): string {
  const time = status.time;
  if (!time) return "—";
  if (time.state === "unlimited") return t("time_unlimited");
  if (time.state === "expired") return t("time_expired");
  if (time.state === "outside_window") return t("time_outside_window");
  const left = remainingMinutes(time);
  return left === null ? t("time_unlimited") : minutesText(left);
}

function render(status: StatusSnapshot | null): void {
  const view = connectionView(status);
  const box = byId("status");
  box.dataset.state = view.look;
  byId("status-title").textContent = view.title;
  byId("status-detail").textContent = view.detail;
  document.documentElement.dataset.connection = status?.connection ?? "unknown";
  // Technical detail for a parent or support: why it is disconnected, or rules the browser refused.
  const diag = byId("diag");
  diag.textContent = status?.lastError ?? "";
  diag.hidden = !status?.lastError || status.connection === "inert";

  const connected = status?.connection === "service" || status?.connection === "host";
  byId("profile").hidden = !connected;
  // PROTECTION-SPEC §9: the member is told while their searches are recorded.
  const logged = connected && status?.features?.logSearches === true;
  byId("search-notice").hidden = !logged;
  document.documentElement.dataset.searchLog = String(logged);
  // PROTECTION-SPEC §12: a short line while ads are blocked.
  const ads = connected && status?.features?.blockAds === true;
  byId("ads-notice").hidden = !ads;
  document.documentElement.dataset.adBlocking = String(ads);
  if (status && connected) {
    byId("user").textContent = status.user?.name || "—";
    byId("mode").textContent = status.mode ? t(`mode_${status.mode}`) : "—";
    byId("time").textContent = timeText(status);
  }
  byId("version").textContent = status?.version ?? chrome.runtime.getManifest().version;
  byId("install-type").textContent = status?.installType ?? "?";
}

async function main(): Promise<void> {
  localizePage();
  render(await getStatus());
  onBroadcast((message) => {
    if (message.type === "wf:stateChanged") render(message.status);
  });
}

void main();
