# WebFilter.Wfp.Native

C++20 DLL that owns all Windows Filtering Platform calls. The service talks to it through a flat `extern "C"` API with simple types only, so no WFP structure is ever declared by hand in C#.

Planned API (validated in spike S5, see [MVP-PLAN.md §5.3](../../docs/MVP-PLAN.md)):

```c
int  WfAddUserAppPolicy(const wchar_t* userSid, const wchar_t** allowedAppPaths, int count, int mode);
int  WfRemoveAll(void);
int  WfSubscribeBlockedEvents(void (*callback)(const wchar_t* userSid, const wchar_t* appPath));
```

- Persistent filters under our own provider and sublayer, conditions `ALE_USER_ID` + `ALE_APP_ID` / `ALE_PACKAGE_ID`
- Built with MSBuild (`.vcxproj`) against `fwpuclnt.lib`; added to the solution in S5
