# Repair Status — Cross-Platform URL Filtering System

Branch: `fix/stabilization`
Last update: Phase 2 + Phase 3 Windows complete. Phase 1 Android scaffold **done**, `assembleDebug` **fails at 72 javac errors** (all business-logic classes never written) — stopped at attempt 2, awaiting user direction.

---

## Toolchain detected

| Tool | Detected | Notes |
| --- | --- | --- |
| .NET SDK | `10.0.201` (default), also 3.1, 7.0, 8.0, 10.0-rc | No .NET 6 SDK installed. SDK 10 builds `net6.0` / `net6.0-windows` targets with EOL warning `NETSDK1138`. |
| MSBuild | via `dotnet build` (no standalone in PATH) | Used `dotnet build` throughout. |
| JDK | Java 8 JRE only in PATH (no `javac`) | Android Studio bundles JBR at `C:\Program Files\Android\Android Studio\jbr` — not on PATH. |
| Android SDK | `C:\Users\finis\AppData\Local\Android\Sdk` | Platforms 33–36.1, build-tools 33–37 present. `ANDROID_HOME` / `ANDROID_SDK_ROOT` not set. |
| Gradle | none in PATH; no wrapper in repo | See Android section below. |

---

## Component map & build status

Solution: `WebFilterApp.sln` (Windows only). Android sources live under `android/` but are **not** wired into any build system.

### Windows / .NET — all 8 projects: **build succeeded, 0 errors**

| # | Component | Type / TFM | Errors | Warnings | Log |
| - | --------- | ---------- | -----: | -------: | --- |
| 1 | `shared/models/UrlFilter.Shared.Models.csproj`   | classlib, `net6.0` | 0 | 3  | `build-logs/01-shared-models-initial.log` |
| 2 | `shared/logging/UrlFilter.Shared.Logging.csproj` | classlib, `net6.0` | 0 | 29 | `build-logs/02-shared-logging-initial.log` |
| 3 | `windows/Common/Common.csproj`                   | classlib, `net6.0` | 0 | 11 | `build-logs/03-common-initial.log` |
| 4 | `windows/WFPIntegration/WFPIntegration.csproj`   | classlib, `net6.0-windows`, `AllowUnsafeBlocks` | 0 | 8  | `build-logs/04-wfp-initial.log` |
| 5 | `windows/UrlFilterService/UrlFilterService.csproj` | Worker service, `net6.0-windows` | 0 | 25 | `build-logs/05-service-initial.log` |
| 6 | `windows/DevTools/DevTools.csproj`               | exe, `net6.0-windows` | 0 | 22 | `build-logs/06-devtools-initial.log` |
| 7 | `windows/Installer/UrlFilterInstaller.csproj`    | exe, `net6.0-windows` | 0 | 18 | `build-logs/07-installer-initial.log` |
| 8 | `windows/AdminControlPanel/AdminControlPanel.csproj` | WPF exe, `net6.0-windows` | 0 | 63 | `build-logs/08-admin-initial.log` |

**All warnings are cosmetic** (nullable-annotation contexts CS8632, non-nullable init CS8618/8625, EOL-framework warning NETSDK1138, and `9.0.0` NuGet packages hitched to `net6.0`). None block compilation.

### Android — **scaffold built, javac fails at 72 errors (was 100)** — stopped at attempt 2, needs user direction

Post-Phase 1 Android scaffold status:

| Item | Status |
| ---- | ------ |
| `settings.gradle`, `build.gradle` (root + `:app`), `gradle.properties`, `local.properties` | **Present.** AGP 8.7.2, `compileSdk 34`, `minSdk 26`, Java 17. |
| `gradlew`, `gradlew.bat`, `gradle/wrapper/gradle-wrapper.jar+properties` | **Present.** Generated via local Gradle 8.11.1. |
| `res/values/{strings,themes}.xml`, `res/drawable/{ic_shield,ic_stop}.xml`, `res/xml/{device_admin,data_extraction_rules,backup_rules,file_paths}.xml` | **Present.** All Manifest resource references resolve; `mergeDebugResources` and `processDebugResources` pass. |
| Manifest-referenced classes (9) | **Stubbed.** `UrlFilterApplication`, `admin.{MainActivity,AdminAuthActivity,AdminDeviceAdminReceiver}`, `vpn.MonitoringService`, `receivers.{BootReceiver,NetworkStateReceiver,AppUsageReceiver}`, `database.UrlFilterContentProvider`. |
| Top-level `models.DeviceUser`, `models.UserType` | **Added.** Fixes existing sources' broken imports of nested types inside `DataModels`. |
| Business classes referenced by existing sources but never written | **Still missing.** `database.DatabaseHelper`, `utils.{UserDetector, FilterEngine, NetworkUtils, YouTubeUrlParser, DomainClassifier}`, `vpn.LocalProxyServer`, DTOs `models.{UserDetectionResult, TrafficAnalysis}`. |
| Method/ctor signatures on existing classes | **Mismatched.** `IpPacket` ctor and `AndroidLogger.getInstance` are called with args they don't accept. |
| `./gradlew assembleDebug` | **FAILS.** 72 javac errors; log `build-logs/22-android-assemble-attempt2.log`. |
| JDK/SDK config | Uses `C:\Program Files\Android\Android Studio\jbr` (OpenJDK 21) as `JAVA_HOME` and `%LOCALAPPDATA%\Android\Sdk` as `ANDROID_HOME` at build time. |

---

## Categorised status

### Working (verified)

- **Phase 0** — All 8 Windows/.NET projects compile clean against SDK 10.0.201 targeting `net6.0` / `net6.0-windows`.
- **Phase 2** — Windows service starts, DB initializes from `schema.sql` idempotently, DNS interceptor binds `127.0.0.1:53`, IPC pipe active, PolicyEnforcementService running. Log: `build-logs/19-service-console-wfp-gated.log`. See `FIX_LOG.md` Phase 2 for fix details.
- **Phase 3** — DNS filtering end-to-end **PASS**: blocked domain → `0.0.0.0`; allowed domain → real upstream A record. Automated test: `tests/dns-filter-smoke.ps1`. Evidence: `build-logs/20-phase3-dns-smoke-PASS.log`.

### Blocked / needs Phase 1 investigation

- Windows: none at compile or run level for the DNS-filtering slice. Phase 4 will exercise remaining features (WFP filter add, browser policy enforcement, admin panel, installer) individually with elevated runs.

### Elevation-gated (per protocol rule 7 — not "bugs", preserved for production)

- WFP filter add (`FwpmFilterAdd0`) — requires admin. Gated with `IsProcessElevated()` in `WfpFilterService.Start`.
- Set adapter DNS to `127.0.0.1` — WMI `SetDNSServerSearchOrder` returns 91 for non-admin.
- DoH GPO registry writes under `HKLM\SOFTWARE\Policies\*` — requires admin.
- `PolicyEnforcementService.KillUnauthorizedBrowsers` — non-admin cannot terminate other-session processes.

### Disabled — needs work
- (none — nothing was disabled or removed in Phase 0)

### ARCHITECTURAL (platform/design limits, not simple bugs)
- **Android build system is entirely absent.** No Gradle files, no wrapper, no res/ tree, and 9 Manifest-referenced classes have no source files. This is not a "fix the compile error" problem — it is a missing build/scaffold + missing implementation. A repair per rule 1 ("repair, don't rewrite") is not possible here without creating substantial new infrastructure. Recommendation for user review: decide whether Android is in-scope for this stabilization pass, or defer.
- Even once buildable, the following features remain platform-constrained (per protocol rule 7):
  - `DevicePolicyManager` / device-owner: needs provisioning at first boot, not code-level.
  - WFP install/start on Windows: requires elevation, and production use needs a signed binary.
  - HTTPS inspection: pinned apps will break regardless of cert install correctness.
  - VpnService+filtering distribution: constrained by Play policy.

### Requires manual setup (deferred until we reach those features)
- **Azure AD / MSAL app registration** — `Microsoft.Identity.Client` is referenced in `UrlFilterService.csproj`; client-id/tenant will be needed at runtime.
- **YouTube Data API key** — not yet located in code; will be flagged when YouTube path is exercised.
- **Code-signing certificate** — for signed service binary.
- **Elevation / test administrator** — required to install/start the Windows service and load WFP filters.
- **`ANDROID_HOME` env var** + JDK 17+ on PATH — needed before Android can be built even after scaffolding.

---

## Files this protocol creates
- `REPAIR_STATUS.md` (this file) — living component table.
- `FIX_LOG.md` — append-only fix journal.
- `build-logs/*.log` — captured build output (evidence).
- `tests/` — regression scripts (Phase 3+).
