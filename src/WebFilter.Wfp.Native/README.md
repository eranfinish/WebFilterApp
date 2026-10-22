# WebFilter.Wfp.Native

C++20 DLL (x64) that owns every Windows Filtering Platform call of WebFilter (CLAUDE.md rule 5, [MVP-PLAN §5.3](../../docs/MVP-PLAN.md)). The service talks to it through a flat `extern "C"` API with UTF-16 strings, string arrays and Win32 error codes only, so no WFP structure is ever declared in C# (`src/WebFilter.Service/Network/NativeWfpEngine.cs`).

## Build

Visual Studio's MSBuild with the "Desktop development with C++" workload (toolset v145, Windows SDK 10.0.26100). `dotnet build` cannot build a `.vcxproj`, so the project is not in `WebFilter.slnx`:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" src\WebFilter.Wfp.Native\WebFilter.Wfp.Native.vcxproj -p:Configuration=Release -p:Platform=x64
```

Output: `bin\x64\Release\WebFilter.Wfp.Native.dll`. Warnings are errors (`/W4 /WX`); the CRT is linked statically, so the target PC needs no Visual C++ redistributable. The MSI build runs this step itself ([installer/README.md](../../installer/README.md)).

## API (`WfpApi.h`)

| Function | Does | `IWfpEngine` |
|---|---|---|
| `WfSetUserPolicy(sid, appPaths[], n, appPathErrors[], packages[], m, packageErrors[], timeUp)` | Replaces one account's filters in **one transaction**; creates the provider and sublayer when missing. A program or package that cannot get a filter is skipped and its error returned in the array (for example 2/3 for a program that is not installed); the rest is applied. | `SetUserPolicy` (returns the skipped ones) |
| `WfRemoveUser(sid)` | Removes one account's filters (one transaction). | `RemoveUser` |
| `WfListUsers(buffer, chars, &required)` | The accounts that have filters, as `S-1-5-…\0S-1-5-…\0\0` (`ERROR_INSUFFICIENT_BUFFER` + size when too small). Read from WFP, so it works after a reboot. | `FilteredUsers` |
| `WfRemoveAll()` | Every filter of our provider or in our sublayer (any layer), then the sublayer and the provider, in one transaction. Succeeds when nothing exists. | `RemoveAll` (uninstall) |
| `WfGetStatus(&provider, &sublayer, &filters)` | What exists now; read-only (diagnostics, lab tests). | `NativeWfpEngine.Status()` |

Each call opens its own WFP session (stateless, any thread) and needs LocalSystem or an administrator.

## Filters

Persistent, under provider `{F89A6E71-59E0-4C54-B98F-CEABF67463D0}` and sublayer `{9710EAFC-B30C-4E32-A731-50424230DE15}` (weight `0xFFF0`, both named "WebFilter"), at `FWPM_LAYER_ALE_AUTH_CONNECT_V4` and `_V6`. For each restricted account, on each layer:

| Filter | Conditions | Action | Weight |
|---|---|---|---|
| WebFilter: block account | `ALE_USER_ID` | BLOCK, hard (action right cleared) | 1 |
| WebFilter: allow loopback | `ALE_USER_ID` + `FLAGS` has `IS_LOOPBACK` | PERMIT | 12 |
| WebFilter: allow program | `ALE_USER_ID` + `ALE_APP_ID` (`FwpmGetAppIdFromFileName0`) | PERMIT | 10 |
| WebFilter: allow package | `ALE_USER_ID` + `ALE_PACKAGE_ID` (package SID, or derived from the package family name) | PERMIT | 10 |

- The user condition is a security descriptor granting `FWP_ACTRL_MATCH_FILTER` to the account's SID **and** to ALL APPLICATION PACKAGES / ALL RESTRICTED APPLICATION PACKAGES. A Store app's token is access-checked a second time against its package; without those two entries the account's Store apps (AppContainers) match no filter and reach the network freely — measured in the lab. Other accounts' AppContainers still fail the first check.
- The account's SID is in each filter's provider data (and description), which is how `WfListUsers` finds the accounts after a reboot. The description also says "(time up)" when the account's time is up; the caller has already reduced the allowed lists.
- Our permits are soft: they only lift our own block, and Windows Firewall can still block. SYSTEM, services and every other account match no filter. Name resolution goes through the DNS Client service, so blocked programs still resolve names but cannot connect.
- The provider has no service name: BFE would disable the filters of a provider whose service is disabled, and stopping the service must not lift the filters (fail closed).
- A copy of an allowed program at another path has another app id and is blocked.

## Lab results

`tests/simulation/lab/Test-LabWfp.ps1` (with `wfpctl.exe`, `tests/WebFilter.Wfp.LabTool`) and `Test-LabWfpService.ps1` run in the Hyper-V VM `lab-kids` (Windows 11 26300); results in `tests/simulation/lab/results/`. Never on the development PC.

- **Engine (36/36):** with only `curl.exe` allowed for `labadmin`, `powershell.exe` gets socket error 10013 over IPv4 and IPv6 while `curl.exe` connects; a copy of `curl.exe` elsewhere is blocked; loopback works; SYSTEM is unaffected; a program that is not installed is skipped with error 3. An AppContainer process of the account is blocked unless allowed, and allowed by its package name (any case) while the same program outside it stays blocked. Allowing `powershell.exe` makes it work again. After a reboot the 6 filters are still there, `list` returns the SID and `powershell.exe` is still blocked. `RemoveAll` leaves nothing in the API or in `netsh wfp show state` / `show filters`.
- **Service (13/13):** the published service with `ManageNetwork=true` filters every non-admin account (one linked restricted child, three unlinked) and no administrator; a program installed later gets its filters on the next tick; filters deleted by hand come back with a warning; pausing a member through the admin pipe lifts her filters within 3 seconds and resuming restores them; the filters stay after the service process ends.

## Not done yet

- **Blocked-connection events** ("an app tried to connect", MVP-PLAN §5.3): subscribe with `FwpmNetEventSubscribe` to `CLASSIFY_DROP` events whose filter id is one of ours, and pass user SID + app path to a callback. The lab already shows the drop events of our filters in `netsh wfp show netevents`. Planned API: `WfSubscribeBlockedEvents(callback)`.
- **`ALE_AUTH_RECV_ACCEPT`:** not filtered. A blocked program can still accept inbound connections where Windows Firewall lets them in (a standard user cannot add firewall rules); outbound, which is what reaches the web, is blocked.
- Real Store apps and Edge WebView2 were not run in the lab: the AppContainer stand-in covers the package and user conditions. WebView2 (`msedgewebview2.exe`) is a separate program, so an app that embeds it is blocked unless WebView2 is allowed too — and its path has the runtime version in it, so an allowed path stops matching after a WebView2 update. Allowing it needs the service to resolve the current path (or ProcessGuard), not a fixed rule.
