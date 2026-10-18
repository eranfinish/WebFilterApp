// Runs without any automation attached. Reports to the pipe server through the native host.
//  keepalive: keep the port open and send a beat every 10 s from a timer (timers alone do not keep a worker alive).
//  control:   close the port after 3 s, then try to report again 90 s later. If the worker was
//             terminated while idle, that report never arrives.
const BOOT = crypto.randomUUID().slice(0, 8);

(async () => {
  const { mode } = await (await fetch(chrome.runtime.getURL("mode.json"))).json();
  const open = () => chrome.runtime.connectNative("com.webfilter.spike");
  let port = open();
  port.postMessage({ type: "hello" });
  port.postMessage({ type: "beat", boot: BOOT, phase: `${mode}-start` });

  if (mode === "keepalive") {
    setInterval(() => port.postMessage({ type: "beat", boot: BOOT, phase: "keepalive" }), 10_000);
  } else {
    setTimeout(() => port.disconnect(), 3_000);
    setTimeout(() => {
      port = open();
      port.postMessage({ type: "beat", boot: BOOT, phase: "control-after-idle" });
    }, 90_000);
  }
})();
