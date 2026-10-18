// Spike background: the harness drives it through globalThis functions.
// BOOT changes whenever the service worker is restarted.
const BOOT = crypto.randomUUID();
const bootAt = Date.now();
const messages = [];
const disconnects = [];
let port = null;
let connectedAt = 0;

globalThis.info = () => ({ boot: BOOT, bootAt, connected: Boolean(port), messages: messages.length, disconnects });

globalThis.connect = () => {
  connectedAt = Date.now();
  port = chrome.runtime.connectNative("com.webfilter.spike");
  port.onMessage.addListener((m) => messages.push({ ...m, receivedAt: Date.now(), sinceConnect: Date.now() - connectedAt }));
  port.onDisconnect.addListener(() => {
    disconnects.push({ at: Date.now(), error: chrome.runtime.lastError?.message ?? null });
    port = null;
  });
  return true;
};

globalThis.send = (message) => {
  if (!port) return false;
  port.postMessage(message);
  return true;
};

chrome.runtime.onMessage.addListener((m, _sender, reply) => { if (m?.type === "wake") reply({ boot: BOOT }); });

globalThis.messages = () => messages;
globalThis.disconnect = () => { port?.disconnect(); port = null; };
