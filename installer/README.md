# WebFilter installer

WiX MSI (x64, per-machine) with a Hub / Agent role choice. Design: [MVP-PLAN.md §4.6](../docs/MVP-PLAN.md). Lab test: [docs/LAB-INSTALLER-CHECKLIST.md](../docs/LAB-INSTALLER-CHECKLIST.md).

```bash
dotnet build installer/WebFilter.Installer.wixproj -c Release
```

Output: `installer/bin/Release/he-IL/WebFilter.msi` (Hebrew UI) and `installer/bin/Release/en-US/WebFilter.msi` (English, for the lab VMs). The project publishes the service, the native host and the admin app (Release, self-contained, `win-x64`) into `obj/payload` first, so the target PC needs no .NET runtime. It is not in `WebFilter.slnx`, so `dotnet test` does not build an MSI. Simulation builds are refused.

WiX comes from NuGet (`WixToolset.Sdk` 5.0.2 and its Util, Firewall and UI extensions); nothing is installed system-wide. 5.0.2 is the last release under plain MS-RL: WiX 6 and 7 require accepting the Open Source Maintenance Fee EULA, which is the project owner's call.

Never run the MSI, or `WebFilter.Service.exe cleanup`, on the development PC: both change the machine (service, HKLM, firewall, every user's registry, `%ProgramData%\WebFilter`). Use the Hyper-V lab.

## Properties

| Property | Values | Notes |
|---|---|---|
| `ROLE` | `hub` (default) or `agent` | Chosen in the setup dialog. Remembered in `HKLM\SOFTWARE\WebFilter`; a repair or upgrade keeps it. Changing it means uninstalling (which deletes the data) and installing again. |
| `HUB`, `PAIRCODE` | `host[:port]`, `XXXX-XXXX-XXXX` | Agent only, optional. Both set → `WebFilter.Service.exe pair` runs. A failed pairing does not fail the install (see the MSI log); pair later from an elevated prompt with the command the hub's Devices screen shows. |
| `CHROME_EXTENSION_ID`, `EDGE_EXTENSION_ID` | 32 letters `a`–`p` | Store ids. Precedence: command line, then the build's (`-p:ChromeExtensionId=… -p:EdgeExtensionId=…`), then the remembered ones. Without them no force-install policy is written and the native host admits no extension. |

```powershell
msiexec /i WebFilter.msi /qn ROLE=hub /l*v hub-install.log
msiexec /i WebFilter.msi /qn ROLE=agent HUB=lab-hub.local:47800 PAIRCODE=XXXX-XXXX-XXXX /l*v agent-install.log
msiexec /x WebFilter.msi /qn /l*v uninstall.log
```

## What it does

Install, repair and upgrade:

- Files to `%ProgramFiles%\WebFilter` (fixed: the service trusts only the native host there). Start-menu shortcut to the admin app.
- Service `WebFilter`: LocalSystem, automatic start, restart on failure (10 s; failure count reset daily), also registered for Safe Mode with Networking.
- `WebFilter.Service.exe configure` (custom action, before the service starts) writes `appsettings.json` (role, `ManageBrowserPolicies=true`, store ids) and the native host manifest `com.webfilter.host.json` (allowed origins = the store ids).
- Native host keys `HKLM\SOFTWARE\{Google\Chrome,Microsoft\Edge}\NativeMessagingHosts\com.webfilter.host`.
- `%ProgramData%\WebFilter` created with a protected ACL: SYSTEM and Administrators only (each profile cache file grants its own user read access).
- Hub only: inbound firewall rule "WebFilter Hub", TCP 47800, the service executable, Private profile.
- The browser policies themselves (force-install, `NativeMessagingUserLevelHosts`, per-child restrictions) are written by the running service, not by the MSI.

Uninstall (not the removal of the old version during an upgrade):

- The service is stopped, then `WebFilter.Service.exe cleanup` removes every browser policy value the service wrote — in HKLM and in every user's hive, loading the hives of users who are not logged on — the WFP filters (once the native engine exists), and `%ProgramData%\WebFilter`. Best effort: what it could not remove is listed in the MSI log and the uninstall continues.
- On a paired agent, before the data folder (and with it the client key) is deleted, `cleanup` unregisters the computer from its hub: `POST /v1/unregister` over the existing mutual-TLS link, waiting at most 5 seconds. The hub revokes the computer, as a parent's revoke would, and keeps it listed: the Devices screen shows it among the revoked computers with the status "Uninstalled" ("הסינון הוסר"), and the activity log gets "WebFilter was uninstalled from a computer". It is not deleted, so the parent sees that the filter was removed, and pairing the same computer again after a reinstall restores it with its account links. When the hub is off or does not answer in time, the MSI log says so (`Hub: this computer could not be unregistered …`), the uninstall still completes, and the computer stays listed as active until a parent revokes it.
- The MSI removes the service, the files, the generated files, the host keys, `HKLM\SOFTWARE\WebFilter`, the Safe Mode key and the firewall rule.

## Known gaps

- An agent uninstalled while its hub is off (or silent for 5 seconds) is not unregistered; revoke it on the hub's Devices screen. Uninstalling the hub tells its agents nothing: they keep enforcing their last settings until WebFilter is uninstalled from them too.
- A background service that throws stops the service cleanly, and Windows restarts only services that end without a clean stop.
- The MSI is not signed (SmartScreen warns). The native host is not NativeAOT yet (needs Visual Studio's C++ workload; then build with `-p:NativeHostAot=true`).
- The dialogs use WixUI's left-to-right layout, also in Hebrew.
