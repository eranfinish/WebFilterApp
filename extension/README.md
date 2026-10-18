# WebFilter browser extension

Manifest V3 extension for Edge and Chrome, force-installed by policy from the Chrome Web Store and Microsoft Edge Add-ons (unlisted). Built in M3; design in [MVP-PLAN.md §4.4](../docs/MVP-PLAN.md).

Planned layout:

```
extension/
  src/          background service worker, content script (YouTube), native messaging client
  rulesets/     failclosed.json (static, enabled by default)
  pages/        blocked.html, timeup.html, my-channels.html
  manifest.json
```

Tooling: TypeScript, esbuild, Vitest; level-3 simulations with Playwright (see [SIMULATION-PLAN.md](../docs/SIMULATION-PLAN.md)).
