// Isolated-world bridge: hides the page in allowlist mode until a decision
// arrives, forwards detections to the background, and enforces the result.
(() => {
  let mode = "observe";
  const veil = document.createElement("style");
  veil.textContent = "html { visibility: hidden !important; }";
  const hide = () => { if (mode === "allowlist" && !veil.isConnected) document.documentElement.appendChild(veil); };
  const reveal = () => veil.remove();

  chrome.storage.local.get("ytMode").then(({ ytMode }) => { mode = ytMode ?? "observe"; hide(); });
  document.addEventListener("yt-navigate-start", hide);

  window.addEventListener("message", async (event) => {
    if (event.source !== window || event.data?.__wf !== "yt-detect") return;
    const detection = event.data.detection;
    let result;
    try {
      result = await chrome.runtime.sendMessage({ type: "yt-detect", detection });
    } catch {
      return; // background unavailable; production would fail closed here
    }
    if (!result || result.decision === "allow") { reveal(); return; }
    location.replace(`${chrome.runtime.getURL("blocked.html")}#yt:${result.reason}:${encodeURIComponent(detection.url)}`);
  });
})();
