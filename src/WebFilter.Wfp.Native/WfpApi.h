// WebFilter.Wfp.Native: the only code that calls the Windows Filtering Platform (CLAUDE.md rule 5, MVP-PLAN §5.3).
//
// A flat C API with UTF-16 strings. Every function returns a Win32 error code (ERROR_SUCCESS = 0) or an FWP_E_* code
// from WFP itself. Each call opens its own WFP session, so the API is stateless and safe to call from any thread.
// Changing calls need an administrator or LocalSystem.
//
// All objects are persistent (they survive a service crash and a reboot) and live under our own provider and sublayer:
//   provider  {F89A6E71-59E0-4C54-B98F-CEABF67463D0}  "WebFilter"
//   sublayer  {9710EAFC-B30C-4E32-A731-50424230DE15}  "WebFilter"
// Each filtered account gets, at FWPM_LAYER_ALE_AUTH_CONNECT_V4 and _V6, filters whose user condition
// (FWPM_CONDITION_ALE_USER_ID) matches only that account's processes:
//   - a BLOCK filter (weight 1, hard: the action right is cleared),
//   - a PERMIT filter for loopback traffic (weight 12),
//   - a PERMIT filter for each allowed program (FWPM_CONDITION_ALE_APP_ID, weight 10),
//   - a PERMIT filter for each allowed Store package (FWPM_CONDITION_ALE_PACKAGE_ID, weight 10).
// The account's SID is stored in each filter's provider data, so the filtered accounts can be listed after a reboot.
// SYSTEM, services and every other account match no filter and are not affected.
#pragma once

#include <windows.h>

#ifdef WEBFILTER_WFP_NATIVE_EXPORTS
#define WF_API extern "C" __declspec(dllexport) DWORD WINAPI
#else
#define WF_API extern "C" __declspec(dllimport) DWORD WINAPI
#endif

// Replaces every filter of one account with a new set, in one WFP transaction: either the whole new set is in place
// or nothing changed. Creates the provider and sublayer when they are missing.
//
//   userSid        the account, as a SID string ("S-1-5-21-...").
//   appPaths       full paths of the programs that may connect (may be NULL when appPathCount is 0).
//   appPathErrors  out, one per path: ERROR_SUCCESS, or why that program was skipped (for example
//                  ERROR_FILE_NOT_FOUND). A skipped program stays blocked; the rest of the policy is still applied.
//   packages       Store packages that may connect: package family names ("Microsoft.WindowsCalculator_8wekyb3d8bbwe")
//                  or package SIDs ("S-1-15-2-..."). May be NULL when packageCount is 0.
//   packageErrors  out, one per package, like appPathErrors.
//   timeUp         nonzero when the account's time is up (outside its hours or over its daily limit). The caller has
//                  already reduced the allowed lists; the flag is recorded in the filters' descriptions.
WF_API WfSetUserPolicy(LPCWSTR userSid,
                       const LPCWSTR* appPaths, UINT32 appPathCount, DWORD* appPathErrors,
                       const LPCWSTR* packages, UINT32 packageCount, DWORD* packageErrors,
                       BOOL timeUp);

// Removes every filter of one account, in one transaction. Succeeds when the account has none.
WF_API WfRemoveUser(LPCWSTR userSid);

// Lists the accounts that have filters, as a double-NUL-terminated list of SID strings ("S-1-5-...\0S-1-5-...\0\0").
// *requiredChars receives the size needed, in characters, including the final NUL. Returns ERROR_INSUFFICIENT_BUFFER
// when bufferChars is smaller (buffer may then be NULL).
WF_API WfListUsers(LPWSTR buffer, UINT32 bufferChars, UINT32* requiredChars);

// Removes every filter of our provider or in our sublayer (on any layer), then the sublayer and the provider, in one
// transaction (uninstall). Succeeds when none of them exist.
WF_API WfRemoveAll(void);

// What exists now: our provider, our sublayer, and the number of filters that belong to either (on any layer).
// Read-only; for diagnostics and tests.
WF_API WfGetStatus(BOOL* providerPresent, BOOL* sublayerPresent, UINT32* filterCount);
