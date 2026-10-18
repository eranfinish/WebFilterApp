// Opening this page starts the service worker again if it was terminated.
chrome.runtime.sendMessage({ type: "wake" }).catch(() => {});
