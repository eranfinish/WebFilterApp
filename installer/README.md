# WebFilter installer

WiX MSI (x64, per-machine) with a Hub / Agent role choice. Design: [MVP-PLAN.md §4.6](../docs/MVP-PLAN.md). Lab test: [docs/LAB-INSTALLER-CHECKLIST.md](../docs/LAB-INSTALLER-CHECKLIST.md).

```bash
dotnet build installer/WebFilter.Installer.wixproj -c Release
```

Output: `installer/bin/Release/he-IL/WebFilter.msi` (Hebrew UI) and `installer/bin/Release/en-US/WebFilter.msi` (English, for the lab VMs). The project publishes the service, the native host, the admin app and the notifier (Release, self-contained, `win-x64`) into `obj/payload` first, so the target PC needs no .NET runtime. It is not in `WebFilter.slnx`, so `dotnet test` does not build an MSI. Simulation builds are refused.

The WFP engine `WebFilter.Wfp.Native.dll` is C++ (`src/WebFilter.Wfp.Native`), which `dotnet build` cannot compile. The `BuildWfpNative` target of the `.wixproj` therefore runs Visual Studio's MSBuild on the `.vcxproj` (Release|x64) and copies the DLL into the payload. It finds MSBuild with `vswhere` and needs the "Desktop development with C++" workload; it clears the SDK paths `dotnet build` sets in its environment (`MSBuildExtensionsPath`, `MSBuildSDKsPath`, …) so the child build uses Visual Studio's own. Without the workload the build stops with a message saying so. Alternatives:

```bash
dotnet build installer/WebFilter.Installer.wixproj -c Release -p:VsMSBuildExe="C:\...\MSBuild\Current\Bin\MSBuild.exe"   # a specific MSBuild
dotnet build installer/WebFilter.Installer.wixproj -c Release -p:WfpNativeDll="C:\...\WebFilter.Wfp.Native.dll"           # a DLL built elsewhere
```

`Package.wxs` names the DLL as its own file (not through the payload wildcard), so an MSI cannot be built without it.

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
- Service `WebFilter`: LocalSystem, automatic start, restart on failure (10 s; failure count reset daily), also registered for Safe Mode with Networking. A failed background service counts as a failure: the service then ends its process with exit code 1 instead of stopping cleanly (`src/WebFilter.Service/FaultExit.cs`).
- `WebFilter.Service.exe configure` (custom action, before the service starts) writes `appsettings.json` (role, `ManageBrowserPolicies=true`, `ManageNetwork=true`, store ids) and the native host manifest `com.webfilter.host.json` (allowed origins = the store ids).
- Native host keys `HKLM\SOFTWARE\{Google\Chrome,Microsoft\Edge}\NativeMessagingHosts\com.webfilter.host`.
- `%ProgramData%\WebFilter` created with a protected ACL: SYSTEM and Administrators only (each profile cache file grants its own user read access).
- Hub only: inbound firewall rule "WebFilter Hub", TCP 47800, the service executable, Private profile.
- The parents' notifier `WebFilter.Notifier.exe` (PROTECTION-SPEC §10; tray icon, unelevated) is installed on every computer, and on the hub only it starts at logon for every user: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run`, value `WebFilter Notifier` = `"C:\Program Files\WebFilter\WebFilter.Notifier.exe"` (component `NotifierAutostart`, conditioned on `ROLE~="hub"` like the firewall rule). It polls `\\.\pipe\WebFilter.Notify` and exits at once for a user the service does not answer as an administrator, so children's accounts on the hub see nothing; it keeps its place in `%LOCALAPPDATA%\WebFilter\notifier-cursor.txt` (first run: from now) and logs to `notifier.log` there. What arrives is shown as Windows toast notifications, which stay in the notification center (Win+N); clicking one, its "פתח את ניהול המשפחה" button, or the same item in the tray menu starts the admin app, which asks for elevation (UAC).
- Toasts for an app without a package (`Microsoft.Toolkit.Uwp.Notifications` 7.1.3, MIT): the notifier is built for `net10.0-windows10.0.17763.0`, the lowest Windows TFM that library supports, so the payload also holds the WinRT projection (`Microsoft.Windows.SDK.NET.dll`, about 23 MB, and `WinRT.Runtime.dll`) and the library; the `Files` wildcard picks them up. The MSI writes nothing for toasts. The notifier registers itself, in the user's own HKCU, the first time the service answers it as a parent (children's accounts never register): `HKCU\Software\Classes\AppUserModelId\WebFilter.Notifier` (`DisplayName` "WebFilter", `IconUri` = the exe's shield saved as `%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\WebFilter.Notifier\Icon.png`, `CustomActivator` = `{10ba0946-57a5-4d37-7c5f-89f39a01c46b}`), and the COM server `HKCU\Software\Classes\CLSID\{10ba0946-57a5-4d37-7c5f-89f39a01c46b}\LocalServer32` = `"C:\Program Files\WebFilter\WebFilter.Notifier.exe" -ToastActivated`. A click reaches the running notifier through COM; with none running (after "יציאה", or after a reboot before it starts) Windows starts the exe with `-ToastActivated -Embedding`, and that one opens the admin app and stays as the tray.
- The browser policies themselves (force-install, `NativeMessagingUserLevelHosts`, per-child restrictions) are written by the running service, not by the MSI. So are the WFP filters (MVP-PLAN §5.3): every non-admin account gets network access for its allowed programs only, through `WebFilter.Wfp.Native.dll` next to the service. The filters are persistent: they stay when the service stops and after a reboot.

Uninstall (not the removal of the old version during an upgrade):

- The service is stopped, then `WebFilter.Service.exe cleanup` removes every browser policy value the service wrote — in HKLM and in every user's hive, loading the hives of users who are not logged on — our WFP provider, sublayer and filters (`WFP: provider, sublayer and filters removed.` in the log), and `%ProgramData%\WebFilter`. Best effort: what it could not remove is listed in the MSI log and the uninstall continues.
- On a paired agent, before the data folder (and with it the client key) is deleted, `cleanup` unregisters the computer from its hub: `POST /v1/unregister` over the existing mutual-TLS link, waiting at most 5 seconds. The hub revokes the computer, as a parent's revoke would, and keeps it listed: the Devices screen shows it among the revoked computers with the status "Uninstalled" ("הסינון הוסר"), and the activity log gets "WebFilter was uninstalled from a computer". It is not deleted, so the parent sees that the filter was removed, and pairing the same computer again after a reinstall restores it with its account links. When the hub is off or does not answer in time, the MSI log says so (`Hub: this computer could not be unregistered …`), the uninstall still completes, and the computer stays listed as active until a parent revokes it.
- The MSI removes the service, the files, the generated files, the host keys, `HKLM\SOFTWARE\WebFilter`, the Safe Mode key, the firewall rule and the notifier's Run value. A notifier running in any signed-in session is ended first (`util:CloseApplication` with `TerminateProcess`, also on repair and upgrade, after which the hub starts it again at the next logon), so its files are not left for a reboot. Its per-user cursor and log under `%LOCALAPPDATA%\WebFilter` stay.
- On the hub, before the files go, `WebFilter.Notifier.exe --unregister` runs as the user who uninstalls (custom action `UnregisterNotifier`: deferred and impersonated, so it reaches that user's HKCU; not during an upgrade; best effort). It is the library's own clean-up: it clears that user's WebFilter toasts from the notification center and deletes both keys above and the icon folder (the empty `ToastNotificationManagerCompat\Apps` folder stays). The MSI cannot do this for the other users: it is their HKCU, whose `Software\Classes` part lives in each profile's `UsrClass.dat`. So another parent who used the notifier keeps the two keys and the icon. They are harmless — they only name the deleted exe, and a click on one of that parent's old toasts does nothing — and can be removed by hand (`reg delete HKCU\Software\Classes\AppUserModelId\WebFilter.Notifier /f`, `reg delete "HKCU\Software\Classes\CLSID\{10ba0946-57a5-4d37-7c5f-89f39a01c46b}" /f`, and the folder), or before the uninstall by running the notifier with `--unregister` as that parent. With UAC turned off a notifier runs elevated, and the library then also writes `HKLM\SOFTWARE\Classes\CLSID\{10ba0946-…}` and `HKLM\SOFTWARE\Classes\AppID\{10ba0946-…}`; `--unregister` removes those only when it runs elevated too.

## Known gaps

- An agent uninstalled while its hub is off (or silent for 5 seconds) is not unregistered; revoke it on the hub's Devices screen. Uninstalling the hub tells its agents nothing: they keep enforcing their last settings until WebFilter is uninstalled from them too.
- The restart after a failed background service is covered by unit tests (`FaultExitTests`) but not yet checked on an installed service (`docs/LAB-INSTALLER-CHECKLIST.md`, section א).
- The notifier's toasts are covered by unit tests on their content and click handling only (no test may register with Windows on the development PC); kept in Win+N, the tamper reminder staying on the screen, and a click with the notifier not running are not yet checked in the lab (`docs/LAB-INSTALLER-CHECKLIST.md`, section א), nor is `--unregister` on uninstall (section ג).
- The MSI is not signed (SmartScreen warns). The native host is not NativeAOT yet (needs Visual Studio's C++ workload; then build with `-p:NativeHostAot=true`).
- The dialogs use WixUI's left-to-right layout, also in Hebrew.
