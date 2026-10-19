"use strict";

// WebFilter 0.1.0 (spike S1): reports whether the extension was force-installed by policy.
// chrome.management.getSelf() needs no permission; installType "admin" means installed by policy.

const t = (key) => chrome.i18n.getMessage(key);
const byId = (id) => document.getElementById(id);

function localize() {
  const root = document.documentElement;
  root.lang = t("langCode") || "en";
  root.dir = t("textDirection") === "rtl" ? "rtl" : "ltr";
  for (const el of document.querySelectorAll("[data-i18n]")) {
    el.textContent = t(el.dataset.i18n);
  }
}

function setStatus(state, titleKey, detailKey) {
  byId("status").dataset.state = state;
  byId("status-title").textContent = t(titleKey);
  byId("status-detail").textContent = t(detailKey);
}

function showInfo(info) {
  const managed = info.installType === "admin";
  setStatus(
    managed ? "managed" : "unmanaged",
    managed ? "statusManaged" : "statusNotManaged",
    managed ? "statusManagedDetail" : "statusNotManagedDetail",
  );
  byId("ext-id").textContent = info.id;
  byId("ext-version").textContent = info.version;
  byId("install-type").textContent = info.installType;
  byId("may-disable").textContent = t(info.mayDisable ? "valueYes" : "valueNo");
}

function showError() {
  setStatus("error", "statusError", "");
  byId("ext-id").textContent = chrome.runtime.id;
  byId("ext-version").textContent = chrome.runtime.getManifest().version;
  byId("install-type").textContent = "?";
  byId("may-disable").textContent = "?";
}

localize();
chrome.management.getSelf((info) => {
  if (chrome.runtime.lastError || !info) {
    showError();
  } else {
    showInfo(info);
  }
});
