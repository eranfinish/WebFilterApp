# Repair Status — Cross-Platform URL Filtering System

Branch: `fix/stabilization`
Last update: Phase 0 complete (initial triage; no code changes)

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

### Android — **not buildable in current state (ARCHITECTURAL)**

| Item | Status |
| ---- | ------ |
| `android/app/src/main/java/**` | 9 `.java` source files exist under `com/urlfilter/{auth,devtools,models,services,utils,vpn}`. |
| `AndroidManifest.xml` | Present, references classes that **do not exist as source files** (see below). |
| `build.gradle` / `build.gradle.kts` (project + module) | **Missing.** |
| `settings.gradle` | **Missing.** |
| `gradle.properties`, `gradlew`, `gradlew.bat`, `gradle/wrapper/` | **Missing.** |
| `android/app/src/main/res/` (drawables, strings, styles, xml) | **Missing.** Manifest references `@drawable/ic_shield`, `@string/app_name`, `@style/Theme.UrlFilter`, `@xml/device_admin`, `@xml/data_extraction_rules`, `@xml/backup_rules`, `@xml/file_paths`. |
| Classes referenced in Manifest but not in source | `UrlFilterApplication`, `admin.MainActivity`, `admin.AdminAuthActivity`, `admin.AdminDeviceAdminReceiver`, `vpn.MonitoringService`, `receivers.BootReceiver`, `receivers.NetworkStateReceiver`, `receivers.AppUsageReceiver`, `database.UrlFilterContentProvider`. |
| JDK on PATH | Java 8 JRE only — no `javac`. AGP requires JDK 17+. |
| `ANDROID_HOME` env var | Not set. SDK exists at `%LOCALAPPDATA%\Android\Sdk`. |

---

## Categorised status

### Working (Phase 0 verified — builds only, not yet run)
- All 8 Windows/.NET projects compile clean against SDK 10.0.201 targeting `net6.0` / `net6.0-windows`.

### Blocked / needs Phase 1 investigation
- None found in Windows components at compile level. Phase 2 (run/init) is next test.

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
