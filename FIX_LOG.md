# Fix Log — append-only

Every attempted fix: **error → hypothesis → change → result**.
Consult before retrying an approach that has already failed (3-attempt stop rule).

---

## Phase 0 — 2026-07-03 — Environment & inventory

No code changes. Recorded state only.

**Observations that will guide Phase 1 (nothing acted on yet):**

1. All .NET projects target `net6.0` / `net6.0-windows`, but no .NET 6 SDK is installed locally. SDK `10.0.201` builds them via forward-compat with warning `NETSDK1138` (framework EOL). Builds succeed with 0 errors. → **Do not "fix" by upgrading TFM unless a real error demands it** (protocol rule: minimal correct fix, no unnecessary framework bumps).
2. NuGet 9.0.0 transitive packages (`System.Text.Json`, `System.Text.Encodings.Web`, `System.IO.Pipelines`, `Microsoft.Bcl.AsyncInterfaces`) emit "does not support net6.0" warnings via `Common.csproj`'s `System.Text.Json 9.0.0` direct reference. Compile still succeeds → cosmetic for now, revisit only if runtime issues appear.
3. Every non-shared C# project has extensive `CS8632` warnings (`?` used outside `#nullable` context). Cosmetic, defer.
4. Android module has no Gradle build files, no `res/`, and the Manifest references 9 classes that have no source files. This is flagged **ARCHITECTURAL** in `REPAIR_STATUS.md`. Awaiting user decision on scope before Phase 1 touches it.

No fix attempts yet.
