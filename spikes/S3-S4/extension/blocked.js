// Formats handled:
//   blocked.html?reason=failclosed            (static ruleset, extensionPath)
//   blocked.html#<original URL>                (dynamic rule, regexSubstitution "\0")
//   blocked.html#yt:<reason>:<encoded URL>     (YouTube content script)
const params = new URLSearchParams(location.search);
const hash = location.hash.slice(1); // raw: the original URL is not encoded by DNR
let reason = params.get("reason") ?? "rule";
let url = "";

if (hash.startsWith("yt:")) {
  const [, ytReason, ...rest] = hash.split(":");
  reason = ytReason;
  url = decodeURIComponent(rest.join(":"));
} else if (hash) {
  url = hash;
}

document.getElementById("reason").textContent = reason;
document.getElementById("url").textContent = url || "—";
document.documentElement.dataset.reason = reason;
document.documentElement.dataset.url = url;
