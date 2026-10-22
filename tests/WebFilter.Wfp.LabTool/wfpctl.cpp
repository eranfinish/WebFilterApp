// wfpctl: drives WebFilter.Wfp.Native.dll from the command line, for the lab harness
// (tests/simulation/lab/Test-LabWfp.ps1). Run it only inside a lab VM, elevated: it changes the machine's WFP policy.
//
//   wfpctl status                         provider=yes|no sublayer=yes|no filters=N
//   wfpctl list                           the accounts that have filters, one SID per line
//   wfpctl set <sid> [--time-up] [--app <path>]... [--package <name>]...
//                                         replaces that account's filters; one line per program/package with its result
//   wfpctl remove <sid>
//   wfpctl remove-all
//   wfpctl appcontainer-run <name> <command line>
//                                         runs a command inside an AppContainer named <name> (created when missing, with
//                                         the internetClient capability), as a stand-in for a Store app; prints its exit code
//   wfpctl appcontainer-delete <name>
//
// The exit code is the Win32 error code of the call (0 = success).
#include "WfpApi.h"

#include <sddl.h>
#include <userenv.h>

#include <cstdio>
#include <cwchar>
#include <string>
#include <vector>

namespace {

int Report(LPCWSTR what, DWORD result)
{
    if (result != ERROR_SUCCESS) std::fwprintf(stderr, L"%s failed: %lu (0x%08lX)\n", what, result, result);
    return static_cast<int>(result);
}

int Status()
{
    BOOL provider = FALSE, sublayer = FALSE;
    UINT32 filters = 0;
    const DWORD result = WfGetStatus(&provider, &sublayer, &filters);
    if (result == ERROR_SUCCESS) std::wprintf(L"provider=%s sublayer=%s filters=%u\n", provider ? L"yes" : L"no", sublayer ? L"yes" : L"no", filters);
    return Report(L"WfGetStatus", result);
}

int List()
{
    UINT32 required = 0;
    DWORD result = WfListUsers(nullptr, 0, &required);
    if (result != ERROR_INSUFFICIENT_BUFFER) return Report(L"WfListUsers", result);
    std::vector<wchar_t> buffer(required);
    result = WfListUsers(buffer.data(), required, &required);
    if (result != ERROR_SUCCESS) return Report(L"WfListUsers", result);
    for (const wchar_t* sid = buffer.data(); *sid != L'\0'; sid += std::wcslen(sid) + 1) std::wprintf(L"%s\n", sid);
    return 0;
}

int Set(int argc, wchar_t** argv)
{
    if (argc < 3) return Report(L"set", ERROR_INVALID_PARAMETER);
    const wchar_t* sid = argv[2];
    std::vector<LPCWSTR> apps, packages;
    BOOL timeUp = FALSE;
    for (int i = 3; i < argc; ++i)
    {
        if (std::wcscmp(argv[i], L"--time-up") == 0) timeUp = TRUE;
        else if (std::wcscmp(argv[i], L"--app") == 0 && i + 1 < argc) apps.push_back(argv[++i]);
        else if (std::wcscmp(argv[i], L"--package") == 0 && i + 1 < argc) packages.push_back(argv[++i]);
        else return Report(L"set: unknown argument", ERROR_INVALID_PARAMETER);
    }
    std::vector<DWORD> appErrors(apps.size()), packageErrors(packages.size());
    const DWORD result = WfSetUserPolicy(sid, apps.data(), static_cast<UINT32>(apps.size()), appErrors.data(),
                                         packages.data(), static_cast<UINT32>(packages.size()), packageErrors.data(), timeUp);
    if (result == ERROR_SUCCESS)
    {
        for (size_t i = 0; i < apps.size(); ++i) std::wprintf(L"app %s: %s (%lu)\n", apps[i], appErrors[i] == 0 ? L"allowed" : L"skipped", appErrors[i]);
        for (size_t i = 0; i < packages.size(); ++i) std::wprintf(L"package %s: %s (%lu)\n", packages[i], packageErrors[i] == 0 ? L"allowed" : L"skipped", packageErrors[i]);
    }
    return Report(L"WfSetUserPolicy", result);
}

struct LocalSid
{
    PSID value = nullptr;
    ~LocalSid()
    {
        if (value != nullptr) LocalFree(value);
    }
};

int AppContainerRun(const wchar_t* name, const wchar_t* commandLine)
{
    PSID container = nullptr;
    HRESULT hr = CreateAppContainerProfile(name, name, L"WebFilter lab test", nullptr, 0, &container);
    if (hr == HRESULT_FROM_WIN32(ERROR_ALREADY_EXISTS)) hr = DeriveAppContainerSidFromAppContainerName(name, &container);
    if (FAILED(hr)) return Report(L"CreateAppContainerProfile", static_cast<DWORD>(hr));
    LPWSTR containerText = nullptr;
    if (ConvertSidToStringSidW(container, &containerText))
    {
        std::wprintf(L"appcontainer=%s\n", containerText);
        LocalFree(containerText);
    }

    LocalSid internetClient;
    if (!ConvertStringSidToSidW(L"S-1-15-3-1", &internetClient.value)) return Report(L"ConvertStringSidToSid", GetLastError());
    SID_AND_ATTRIBUTES capability{internetClient.value, SE_GROUP_ENABLED};
    SECURITY_CAPABILITIES capabilities{};
    capabilities.AppContainerSid = container;
    capabilities.Capabilities = &capability;
    capabilities.CapabilityCount = 1;

    SIZE_T size = 0;
    InitializeProcThreadAttributeList(nullptr, 1, 0, &size);
    std::vector<BYTE> attributes(size);
    auto* list = reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(attributes.data());
    if (!InitializeProcThreadAttributeList(list, 1, 0, &size)) return Report(L"InitializeProcThreadAttributeList", GetLastError());
    if (!UpdateProcThreadAttribute(list, 0, PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES, &capabilities, sizeof(capabilities), nullptr, nullptr))
        return Report(L"UpdateProcThreadAttribute", GetLastError());

    STARTUPINFOEXW startup{};
    startup.StartupInfo.cb = sizeof(startup);
    startup.lpAttributeList = list;
    PROCESS_INFORMATION process{};
    std::wstring mutableCommand = commandLine;
    const BOOL started = CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, FALSE, EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW,
                                        nullptr, nullptr, &startup.StartupInfo, &process);
    const DWORD startError = started ? ERROR_SUCCESS : GetLastError();
    DeleteProcThreadAttributeList(list);
    FreeSid(container);
    if (!started) return Report(L"CreateProcess", startError);
    WaitForSingleObject(process.hProcess, 120000);
    DWORD exitCode = 0;
    GetExitCodeProcess(process.hProcess, &exitCode);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    std::wprintf(L"exit=%lu\n", exitCode);
    return 0;
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2) return Report(L"usage: wfpctl status|list|set|remove|remove-all|appcontainer-run|appcontainer-delete", ERROR_INVALID_PARAMETER);
    const std::wstring command = argv[1];
    if (command == L"status") return Status();
    if (command == L"list") return List();
    if (command == L"set") return Set(argc, argv);
    if (command == L"remove" && argc == 3) return Report(L"WfRemoveUser", WfRemoveUser(argv[2]));
    if (command == L"remove-all") return Report(L"WfRemoveAll", WfRemoveAll());
    if (command == L"appcontainer-run" && argc == 4) return AppContainerRun(argv[2], argv[3]);
    if (command == L"appcontainer-delete" && argc == 3)
    {
        const HRESULT hr = DeleteAppContainerProfile(argv[2]);
        return Report(L"DeleteAppContainerProfile", SUCCEEDED(hr) ? ERROR_SUCCESS : static_cast<DWORD>(hr));
    }
    return Report(L"unknown command", ERROR_INVALID_PARAMETER);
}
