// The WFP engine behind WfpApi.h. See that header for the filter layout.
#define WEBFILTER_WFP_NATIVE_EXPORTS
#include "WfpApi.h"

#include <fwpmu.h>
#include <sddl.h>
#include <userenv.h>

#include <cstring>
#include <cwchar>
#include <functional>
#include <string>
#include <vector>

namespace {

// {F89A6E71-59E0-4C54-B98F-CEABF67463D0}
constexpr GUID ProviderKey = {0xf89a6e71, 0x59e0, 0x4c54, {0xb9, 0x8f, 0xce, 0xab, 0xf6, 0x74, 0x63, 0xd0}};
// {9710EAFC-B30C-4E32-A731-50424230DE15}
constexpr GUID SublayerKey = {0x9710eafc, 0xb30c, 0x4e32, {0xa7, 0x31, 0x50, 0x42, 0x42, 0x30, 0xde, 0x15}};

// Sublayers are evaluated from the highest weight down and a block is final, so a high weight lets our block decide
// before other sublayers' permits are consulted. Our permits are soft: other sublayers (Windows Firewall) can still block.
constexpr UINT16 SublayerWeight = 0xFFF0;

// Filter weights inside our sublayer (FWP_UINT8: a weight range 0-15; the highest matching filter decides).
constexpr UINT8 BlockWeight = 1;
constexpr UINT8 AllowWeight = 10;
constexpr UINT8 LoopbackWeight = 12;

const GUID* const Layers[] = {&FWPM_LAYER_ALE_AUTH_CONNECT_V4, &FWPM_LAYER_ALE_AUTH_CONNECT_V6};

constexpr UINT32 EnumPageSize = 256;

// ---- small RAII helpers ------------------------------------------------------------------------------------------

class Engine
{
public:
    Engine() = default;
    Engine(const Engine&) = delete;
    Engine& operator=(const Engine&) = delete;
    ~Engine()
    {
        if (handle_ != nullptr) FwpmEngineClose0(handle_);
    }

    DWORD Open()
    {
        FWPM_SESSION0 session{};
        session.displayData.name = const_cast<wchar_t*>(L"WebFilter");
        // Another writer holds the engine at most briefly; do not wait forever for it.
        session.txnWaitTimeoutInMSec = 30000;
        return FwpmEngineOpen0(nullptr, RPC_C_AUTHN_WINNT, nullptr, &session, &handle_);
    }

    HANDLE get() const { return handle_; }

private:
    HANDLE handle_ = nullptr;
};

class Transaction
{
public:
    explicit Transaction(HANDLE engine) : engine_(engine) {}
    Transaction(const Transaction&) = delete;
    Transaction& operator=(const Transaction&) = delete;
    ~Transaction()
    {
        if (active_) FwpmTransactionAbort0(engine_);
    }

    DWORD Begin()
    {
        const DWORD result = FwpmTransactionBegin0(engine_, 0);
        active_ = result == ERROR_SUCCESS;
        return result;
    }

    DWORD Commit()
    {
        active_ = false;
        return FwpmTransactionCommit0(engine_);
    }

private:
    HANDLE engine_;
    bool active_ = false;
};

struct LocalMemory
{
    HLOCAL value = nullptr;
    LocalMemory() = default;
    LocalMemory(const LocalMemory&) = delete;
    LocalMemory& operator=(const LocalMemory&) = delete;
    ~LocalMemory()
    {
        if (value != nullptr) LocalFree(value);
    }
};

/// App ids returned by FwpmGetAppIdFromFileName0, freed together.
struct AppIds
{
    std::vector<FWP_BYTE_BLOB*> blobs;
    std::vector<std::wstring> paths;
    AppIds() = default;
    AppIds(const AppIds&) = delete;
    AppIds& operator=(const AppIds&) = delete;
    ~AppIds()
    {
        for (auto& blob : blobs) FwpmFreeMemory0(reinterpret_cast<void**>(&blob));
    }

    bool Contains(const FWP_BYTE_BLOB& blob) const
    {
        for (const auto* known : blobs)
            if (known->size == blob.size && std::memcmp(known->data, blob.data, blob.size) == 0) return true;
        return false;
    }
};

// ---- values --------------------------------------------------------------------------------------------------------

/// The canonical string form of a SID ("S-1-5-21-..."), or an error for anything that is not a SID.
DWORD NormalizeSid(LPCWSTR text, std::wstring& normalized)
{
    if (text == nullptr || *text == L'\0') return ERROR_INVALID_PARAMETER;
    LocalMemory sid;
    if (!ConvertStringSidToSidW(text, reinterpret_cast<PSID*>(&sid.value))) return GetLastError();
    LocalMemory canonical;
    if (!ConvertSidToStringSidW(static_cast<PSID>(sid.value), reinterpret_cast<LPWSTR*>(&canonical.value))) return GetLastError();
    normalized = static_cast<LPCWSTR>(canonical.value);
    return ERROR_SUCCESS;
}

/// A Win32 code for an HRESULT (the Win32 code itself when it wraps one).
DWORD FromHResult(HRESULT hr)
{
    if (HRESULT_FACILITY(hr) == FACILITY_WIN32) return HRESULT_CODE(hr);
    return hr == E_INVALIDARG ? ERROR_INVALID_PARAMETER : static_cast<DWORD>(hr);
}

/// The SID of a Store package: a package SID given as is, or derived from a package family name (the AppContainer
/// name of a packaged app). The package does not need to be installed.
DWORD PackageSid(LPCWSTR value, std::vector<BYTE>& sid)
{
    if (value == nullptr || *value == L'\0') return ERROR_INVALID_PARAMETER;
    if (_wcsnicmp(value, L"S-1-15-2-", 9) == 0)
    {
        LocalMemory parsed;
        if (!ConvertStringSidToSidW(value, reinterpret_cast<PSID*>(&parsed.value))) return GetLastError();
        const DWORD length = GetLengthSid(static_cast<PSID>(parsed.value));
        sid.assign(static_cast<BYTE*>(parsed.value), static_cast<BYTE*>(parsed.value) + length);
        return ERROR_SUCCESS;
    }
    PSID derived = nullptr;
    const HRESULT hr = DeriveAppContainerSidFromAppContainerName(value, &derived);
    if (FAILED(hr)) return FromHResult(hr);
    const DWORD length = GetLengthSid(derived);
    sid.assign(static_cast<BYTE*>(derived), static_cast<BYTE*>(derived) + length);
    FreeSid(derived);
    return ERROR_SUCCESS;
}

/// The security descriptor of the user condition: it grants FWP_ACTRL_MATCH_FILTER ("CC") to the account, so only that
/// account's processes match. A Store app's token is checked a second time against its package, so the descriptor also
/// grants ALL APPLICATION PACKAGES and ALL RESTRICTED APPLICATION PACKAGES; with only the account granted, the account's
/// Store apps would match no filter at all. Other accounts' Store apps still fail the first check.
DWORD UserSecurityDescriptor(const std::wstring& sid, LocalMemory& descriptor, ULONG& size)
{
    const std::wstring sddl = L"O:SYG:SYD:(A;;CC;;;" + sid + L")(A;;CC;;;AC)(A;;CC;;;S-1-15-2-2)";
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1,
                                                              reinterpret_cast<PSECURITY_DESCRIPTOR*>(&descriptor.value), &size))
        return GetLastError();
    return ERROR_SUCCESS;
}

/// The account SID stored in a filter's provider data (empty when there is none).
std::wstring SidOf(const FWPM_FILTER0& filter)
{
    if (filter.providerData.data == nullptr || filter.providerData.size < sizeof(wchar_t)) return {};
    std::wstring sid(filter.providerData.size / sizeof(wchar_t), L'\0');
    std::memcpy(sid.data(), filter.providerData.data, sid.size() * sizeof(wchar_t));
    while (!sid.empty() && sid.back() == L'\0') sid.pop_back();
    return sid;
}

bool IsOurs(const FWPM_FILTER0& filter)
{
    return (filter.providerKey != nullptr && IsEqualGUID(*filter.providerKey, ProviderKey)) || IsEqualGUID(filter.subLayerKey, SublayerKey);
}

bool SameSid(const std::wstring& a, const std::wstring& b) { return _wcsicmp(a.c_str(), b.c_str()) == 0; }

// ---- WFP objects ---------------------------------------------------------------------------------------------------

/// Visits every filter the template selects (every filter on the machine when it is null).
DWORD EnumerateFilters(HANDLE engine, const FWPM_FILTER_ENUM_TEMPLATE0* filterTemplate, const std::function<void(const FWPM_FILTER0&)>& visit)
{
    HANDLE enumHandle = nullptr;
    DWORD result = FwpmFilterCreateEnumHandle0(engine, filterTemplate, &enumHandle);
    if (result == FWP_E_PROVIDER_NOT_FOUND) return ERROR_SUCCESS; // nothing of ours exists
    if (result != ERROR_SUCCESS) return result;
    for (;;)
    {
        FWPM_FILTER0** entries = nullptr;
        UINT32 count = 0;
        result = FwpmFilterEnum0(engine, enumHandle, EnumPageSize, &entries, &count);
        if (result != ERROR_SUCCESS) break;
        for (UINT32 i = 0; i < count; ++i) visit(*entries[i]);
        if (entries != nullptr) FwpmFreeMemory0(reinterpret_cast<void**>(&entries));
        if (count < EnumPageSize) break;
    }
    FwpmFilterDestroyEnumHandle0(engine, enumHandle);
    return result;
}

/// Visits our filters on the layers we use.
DWORD EnumerateOurLayers(HANDLE engine, const std::function<void(const FWPM_FILTER0&)>& visit)
{
    for (const GUID* layer : Layers)
    {
        FWPM_FILTER_ENUM_TEMPLATE0 filterTemplate{};
        filterTemplate.providerKey = const_cast<GUID*>(&ProviderKey);
        filterTemplate.layerKey = *layer;
        filterTemplate.enumType = FWP_FILTER_ENUM_OVERLAPPING;
        filterTemplate.actionMask = 0xFFFFFFFF;
        const DWORD result = EnumerateFilters(engine, &filterTemplate, [&](const FWPM_FILTER0& filter) {
            if (IsOurs(filter)) visit(filter);
        });
        if (result != ERROR_SUCCESS) return result;
    }
    return ERROR_SUCCESS;
}

DWORD ProviderExists(HANDLE engine, bool& exists)
{
    FWPM_PROVIDER0* provider = nullptr;
    const DWORD result = FwpmProviderGetByKey0(engine, &ProviderKey, &provider);
    exists = result == ERROR_SUCCESS;
    if (provider != nullptr) FwpmFreeMemory0(reinterpret_cast<void**>(&provider));
    return result == ERROR_SUCCESS || result == FWP_E_PROVIDER_NOT_FOUND ? ERROR_SUCCESS : result;
}

DWORD SublayerExists(HANDLE engine, bool& exists)
{
    FWPM_SUBLAYER0* sublayer = nullptr;
    const DWORD result = FwpmSubLayerGetByKey0(engine, &SublayerKey, &sublayer);
    exists = result == ERROR_SUCCESS;
    if (sublayer != nullptr) FwpmFreeMemory0(reinterpret_cast<void**>(&sublayer));
    return result == ERROR_SUCCESS || result == FWP_E_SUBLAYER_NOT_FOUND ? ERROR_SUCCESS : result;
}

/// Adds the persistent provider and sublayer when they are missing (inside the caller's transaction). No service name
/// is set on the provider: BFE would disable the filters of a provider whose service is disabled, and stopping the
/// service must not lift the filters (fail closed).
DWORD EnsureProviderAndSublayer(HANDLE engine)
{
    bool exists = false;
    DWORD result = ProviderExists(engine, exists);
    if (result != ERROR_SUCCESS) return result;
    if (!exists)
    {
        FWPM_PROVIDER0 provider{};
        provider.providerKey = ProviderKey;
        provider.displayData.name = const_cast<wchar_t*>(L"WebFilter");
        provider.displayData.description = const_cast<wchar_t*>(L"WebFilter: network access per Windows account and program");
        provider.flags = FWPM_PROVIDER_FLAG_PERSISTENT;
        result = FwpmProviderAdd0(engine, &provider, nullptr);
        if (result != ERROR_SUCCESS) return result;
    }

    result = SublayerExists(engine, exists);
    if (result != ERROR_SUCCESS) return result;
    if (!exists)
    {
        FWPM_SUBLAYER0 sublayer{};
        sublayer.subLayerKey = SublayerKey;
        sublayer.displayData.name = const_cast<wchar_t*>(L"WebFilter");
        sublayer.displayData.description = const_cast<wchar_t*>(L"WebFilter: allowed programs per Windows account");
        sublayer.flags = FWPM_SUBLAYER_FLAG_PERSISTENT;
        sublayer.providerKey = const_cast<GUID*>(&ProviderKey);
        sublayer.weight = SublayerWeight;
        result = FwpmSubLayerAdd0(engine, &sublayer, nullptr);
        if (result != ERROR_SUCCESS) return result;
    }
    return ERROR_SUCCESS;
}

DWORD DeleteFilters(HANDLE engine, const std::vector<UINT64>& ids)
{
    for (const UINT64 id : ids)
    {
        const DWORD result = FwpmFilterDeleteById0(engine, id);
        if (result != ERROR_SUCCESS && result != FWP_E_FILTER_NOT_FOUND) return result;
    }
    return ERROR_SUCCESS;
}

DWORD DeleteUserFilters(HANDLE engine, const std::wstring& sid)
{
    std::vector<UINT64> ids;
    const DWORD result = EnumerateOurLayers(engine, [&](const FWPM_FILTER0& filter) {
        if (SameSid(SidOf(filter), sid)) ids.push_back(filter.filterId);
    });
    if (result != ERROR_SUCCESS) return result;
    return DeleteFilters(engine, ids);
}

struct FilterOwner
{
    const std::wstring& sid;
    FWPM_FILTER_CONDITION0 user;
    bool timeUp;
};

DWORD AddFilter(HANDLE engine, const GUID& layer, const FilterOwner& owner, LPCWSTR name, const std::wstring& what,
                FWP_ACTION_TYPE action, UINT8 weight, const FWPM_FILTER_CONDITION0* extra)
{
    FWPM_FILTER_CONDITION0 conditions[2] = {owner.user};
    UINT32 count = 1;
    if (extra != nullptr) conditions[count++] = *extra;

    std::wstring description = what + L" for account " + owner.sid;
    if (owner.timeUp) description += L" (time up)";

    FWPM_FILTER0 filter{};
    filter.displayData.name = const_cast<wchar_t*>(name);
    filter.displayData.description = description.data();
    // A cleared action right makes our block hard: a permit from a lower-priority sublayer cannot lift it.
    filter.flags = FWPM_FILTER_FLAG_PERSISTENT | (action == FWP_ACTION_BLOCK ? FWPM_FILTER_FLAG_CLEAR_ACTION_RIGHT : 0);
    filter.providerKey = const_cast<GUID*>(&ProviderKey);
    filter.providerData.size = static_cast<UINT32>((owner.sid.size() + 1) * sizeof(wchar_t));
    filter.providerData.data = reinterpret_cast<UINT8*>(const_cast<wchar_t*>(owner.sid.c_str()));
    filter.layerKey = layer;
    filter.subLayerKey = SublayerKey;
    filter.weight.type = FWP_UINT8;
    filter.weight.uint8 = weight;
    filter.numFilterConditions = count;
    filter.filterCondition = conditions;
    filter.action.type = action;
    UINT64 id = 0;
    return FwpmFilterAdd0(engine, &filter, nullptr, &id);
}

} // namespace

// ---- exported API --------------------------------------------------------------------------------------------------

WF_API WfSetUserPolicy(LPCWSTR userSid,
                       const LPCWSTR* appPaths, UINT32 appPathCount, DWORD* appPathErrors,
                       const LPCWSTR* packages, UINT32 packageCount, DWORD* packageErrors,
                       BOOL timeUp)
{
    if ((appPathCount > 0 && (appPaths == nullptr || appPathErrors == nullptr)) ||
        (packageCount > 0 && (packages == nullptr || packageErrors == nullptr)))
        return ERROR_INVALID_PARAMETER;
    std::wstring sid;
    DWORD result = NormalizeSid(userSid, sid);
    if (result != ERROR_SUCCESS) return result;

    // Resolve everything before touching WFP. A program that cannot be resolved (not installed, a bad path) is skipped
    // and reported; it stays blocked.
    AppIds apps;
    for (UINT32 i = 0; i < appPathCount; ++i)
    {
        FWP_BYTE_BLOB* blob = nullptr;
        appPathErrors[i] = appPaths[i] == nullptr || *appPaths[i] == L'\0' ? ERROR_INVALID_PARAMETER : FwpmGetAppIdFromFileName0(appPaths[i], &blob);
        if (blob == nullptr) continue;
        if (appPathErrors[i] != ERROR_SUCCESS || apps.Contains(*blob))
        {
            FwpmFreeMemory0(reinterpret_cast<void**>(&blob));
            continue;
        }
        apps.blobs.push_back(blob);
        apps.paths.emplace_back(appPaths[i]);
    }
    std::vector<std::vector<BYTE>> packageSids;
    std::vector<std::wstring> packageNames;
    for (UINT32 i = 0; i < packageCount; ++i)
    {
        std::vector<BYTE> packageSid;
        packageErrors[i] = PackageSid(packages[i], packageSid);
        if (packageErrors[i] != ERROR_SUCCESS) continue;
        bool known = false;
        for (const auto& existing : packageSids) known = known || EqualSid(const_cast<BYTE*>(existing.data()), packageSid.data());
        if (known) continue;
        packageSids.push_back(std::move(packageSid));
        packageNames.emplace_back(packages[i]);
    }

    LocalMemory descriptor;
    ULONG descriptorSize = 0;
    result = UserSecurityDescriptor(sid, descriptor, descriptorSize);
    if (result != ERROR_SUCCESS) return result;
    FWP_BYTE_BLOB descriptorBlob{descriptorSize, static_cast<UINT8*>(descriptor.value)};

    FilterOwner owner{sid, {}, timeUp != FALSE};
    owner.user.fieldKey = FWPM_CONDITION_ALE_USER_ID;
    owner.user.matchType = FWP_MATCH_EQUAL;
    owner.user.conditionValue.type = FWP_SECURITY_DESCRIPTOR_TYPE;
    owner.user.conditionValue.sd = &descriptorBlob;

    Engine engine;
    if ((result = engine.Open()) != ERROR_SUCCESS) return result;
    Transaction transaction(engine.get());
    if ((result = transaction.Begin()) != ERROR_SUCCESS) return result;
    if ((result = EnsureProviderAndSublayer(engine.get())) != ERROR_SUCCESS) return result;
    if ((result = DeleteUserFilters(engine.get(), sid)) != ERROR_SUCCESS) return result;

    for (const GUID* layer : Layers)
    {
        result = AddFilter(engine.get(), *layer, owner, L"WebFilter: block account", L"Blocks the network", FWP_ACTION_BLOCK, BlockWeight, nullptr);
        if (result != ERROR_SUCCESS) return result;

        FWPM_FILTER_CONDITION0 loopback{};
        loopback.fieldKey = FWPM_CONDITION_FLAGS;
        loopback.matchType = FWP_MATCH_FLAGS_ALL_SET;
        loopback.conditionValue.type = FWP_UINT32;
        loopback.conditionValue.uint32 = FWP_CONDITION_FLAG_IS_LOOPBACK;
        result = AddFilter(engine.get(), *layer, owner, L"WebFilter: allow loopback", L"Allows loopback", FWP_ACTION_PERMIT, LoopbackWeight, &loopback);
        if (result != ERROR_SUCCESS) return result;

        for (size_t i = 0; i < apps.blobs.size(); ++i)
        {
            FWPM_FILTER_CONDITION0 app{};
            app.fieldKey = FWPM_CONDITION_ALE_APP_ID;
            app.matchType = FWP_MATCH_EQUAL;
            app.conditionValue.type = FWP_BYTE_BLOB_TYPE;
            app.conditionValue.byteBlob = apps.blobs[i];
            result = AddFilter(engine.get(), *layer, owner, L"WebFilter: allow program", L"Allows " + apps.paths[i], FWP_ACTION_PERMIT, AllowWeight, &app);
            if (result != ERROR_SUCCESS) return result;
        }

        for (size_t i = 0; i < packageSids.size(); ++i)
        {
            FWPM_FILTER_CONDITION0 package{};
            package.fieldKey = FWPM_CONDITION_ALE_PACKAGE_ID;
            package.matchType = FWP_MATCH_EQUAL;
            package.conditionValue.type = FWP_SID;
            package.conditionValue.sid = reinterpret_cast<SID*>(packageSids[i].data());
            result = AddFilter(engine.get(), *layer, owner, L"WebFilter: allow package", L"Allows package " + packageNames[i], FWP_ACTION_PERMIT, AllowWeight, &package);
            if (result != ERROR_SUCCESS) return result;
        }
    }
    return transaction.Commit();
}

WF_API WfRemoveUser(LPCWSTR userSid)
{
    std::wstring sid;
    DWORD result = NormalizeSid(userSid, sid);
    if (result != ERROR_SUCCESS) return result;
    Engine engine;
    if ((result = engine.Open()) != ERROR_SUCCESS) return result;
    Transaction transaction(engine.get());
    if ((result = transaction.Begin()) != ERROR_SUCCESS) return result;
    if ((result = DeleteUserFilters(engine.get(), sid)) != ERROR_SUCCESS) return result;
    return transaction.Commit();
}

WF_API WfListUsers(LPWSTR buffer, UINT32 bufferChars, UINT32* requiredChars)
{
    if (requiredChars == nullptr) return ERROR_INVALID_PARAMETER;
    *requiredChars = 0;
    Engine engine;
    DWORD result = engine.Open();
    if (result != ERROR_SUCCESS) return result;
    std::vector<std::wstring> users;
    result = EnumerateOurLayers(engine.get(), [&](const FWPM_FILTER0& filter) {
        auto sid = SidOf(filter);
        if (sid.empty()) return;
        for (const auto& known : users)
            if (SameSid(known, sid)) return;
        users.push_back(std::move(sid));
    });
    if (result != ERROR_SUCCESS) return result;

    size_t required = 1;
    for (const auto& user : users) required += user.size() + 1;
    *requiredChars = static_cast<UINT32>(required);
    if (buffer == nullptr || bufferChars < required) return ERROR_INSUFFICIENT_BUFFER;
    for (const auto& user : users)
    {
        std::memcpy(buffer, user.c_str(), (user.size() + 1) * sizeof(wchar_t));
        buffer += user.size() + 1;
    }
    *buffer = L'\0';
    return ERROR_SUCCESS;
}

WF_API WfRemoveAll(void)
{
    Engine engine;
    DWORD result = engine.Open();
    if (result != ERROR_SUCCESS) return result;
    Transaction transaction(engine.get());
    if ((result = transaction.Begin()) != ERROR_SUCCESS) return result;

    // Every layer, not only the ones in use: nothing of ours may stay behind after uninstall.
    std::vector<UINT64> ids;
    result = EnumerateFilters(engine.get(), nullptr, [&](const FWPM_FILTER0& filter) {
        if (IsOurs(filter)) ids.push_back(filter.filterId);
    });
    if (result != ERROR_SUCCESS) return result;
    if ((result = DeleteFilters(engine.get(), ids)) != ERROR_SUCCESS) return result;

    bool exists = false;
    if ((result = SublayerExists(engine.get(), exists)) != ERROR_SUCCESS) return result;
    if (exists && (result = FwpmSubLayerDeleteByKey0(engine.get(), &SublayerKey)) != ERROR_SUCCESS) return result;
    if ((result = ProviderExists(engine.get(), exists)) != ERROR_SUCCESS) return result;
    if (exists && (result = FwpmProviderDeleteByKey0(engine.get(), &ProviderKey)) != ERROR_SUCCESS) return result;
    return transaction.Commit();
}

WF_API WfGetStatus(BOOL* providerPresent, BOOL* sublayerPresent, UINT32* filterCount)
{
    if (providerPresent == nullptr || sublayerPresent == nullptr || filterCount == nullptr) return ERROR_INVALID_PARAMETER;
    Engine engine;
    DWORD result = engine.Open();
    if (result != ERROR_SUCCESS) return result;
    bool provider = false, sublayer = false;
    if ((result = ProviderExists(engine.get(), provider)) != ERROR_SUCCESS) return result;
    if ((result = SublayerExists(engine.get(), sublayer)) != ERROR_SUCCESS) return result;
    UINT32 count = 0;
    result = EnumerateFilters(engine.get(), nullptr, [&](const FWPM_FILTER0& filter) {
        if (IsOurs(filter)) ++count;
    });
    if (result != ERROR_SUCCESS) return result;
    *providerPresent = provider ? TRUE : FALSE;
    *sublayerPresent = sublayer ? TRUE : FALSE;
    *filterCount = count;
    return ERROR_SUCCESS;
}
