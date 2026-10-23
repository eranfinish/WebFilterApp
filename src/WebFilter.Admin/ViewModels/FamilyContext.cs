using CommunityToolkit.Mvvm.ComponentModel;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// What every page needs to know about the family, cached between pages: the last status, the settings (refetched
/// only when the service's settings version changes), the family's time zone and the pending-request count.
/// </summary>
public sealed partial class FamilyContext : ObservableObject
{
    /// <summary>How often <see cref="UpdateSettingsAsync"/> reads again after the settings changed under it.</summary>
    public const int UpdateAttempts = 3;

    public const string NotHub = "not_hub";

    private readonly TimeProvider _time;
    private DateTimeOffset _statusReceivedAt;

    public FamilyContext(IAdminClient client, TimeProvider? time = null)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        _time = time ?? TimeProvider.System;
    }

    public IAdminClient Client { get; }

    public StatusResult? Status { get; private set; }

    public FamilyBundle? Settings { get; private set; }

    public TimeZoneInfo TimeZone { get; private set; } = TimeZoneInfo.Local;

    [ObservableProperty]
    private int _pendingRequestCount;

    /// <summary>How many <see cref="NewAccounts"/> there are (the devices page's badge).</summary>
    [ObservableProperty]
    private int _unlinkedAccountCount;

    /// <summary>
    /// Standard Windows accounts nobody answered for yet — not linked to a family member and not kept as a guest
    /// (<see cref="AccountLinks.IsNew"/>), so browsing there is blocked — on this computer and on the paired computers that
    /// still get settings; this computer first, then by computer and account name. The dashboard and the devices page
    /// show a card for each. Until the paired computers were read (<see cref="UsePairedDevices"/>) every computer in the
    /// settings counts.
    /// </summary>
    public IReadOnlyList<NewAccount> NewAccounts { get; private set; } = [];

    /// <summary>The paired computers as last read, for their names and whether they still get settings; null until read.</summary>
    public IReadOnlyList<DeviceInfo>? PairedDevices { get; private set; }

    /// <summary>Some computer in the settings has an account nobody answered for (the dashboard then reads the computers' names).</summary>
    public bool MayHaveNewAccounts => Settings?.Devices.Any(d => d.Accounts.Any(AccountLinks.IsNew)) == true;

    /// <summary>Creates ids for the members a new account's card adds.</summary>
    internal Func<string> NewMemberId { get; set; } = MemberTemplate.NewId;

    /// <summary>The service answered as an agent computer, managed by a hub: there only the status and the local pause work.</summary>
    public bool IsAgent => Status is { IsHub: false };

    /// <summary>
    /// The service's clock now: the last <see cref="StatusResult.Now"/> plus the time since it arrived. Simulation
    /// services run a settable clock, so "end of the day" and "last 24 hours" follow the service, not this PC.
    /// </summary>
    public DateTimeOffset ServiceNow => Status is { } status ? status.Now + (_time.GetUtcNow() - _statusReceivedAt) : _time.GetUtcNow();

    public async Task<StatusResult> RefreshStatusAsync(CancellationToken ct = default)
    {
        var status = await Client.RequestAsync<StatusResult>(new GetStatus(), ct);
        Status = status;
        _statusReceivedAt = _time.GetUtcNow();
        if (!status.IsHub)
        {
            // An agent answers "not_hub" for the settings; its status carries what the pages need there.
            Settings = null;
            TimeZone = FamilyTime.FindZone(status.TimeZone);
            UpdateNewAccounts();
            return status;
        }
        if (Settings?.Version != status.SettingsVersion) await RefreshSettingsAsync(ct);
        return status;
    }

    public async Task<FamilyBundle> RefreshSettingsAsync(CancellationToken ct = default)
    {
        var result = await Client.RequestAsync<SettingsResult>(new GetSettings(), ct);
        Settings = result.Settings;
        TimeZone = FamilyTime.FindZone(result.Settings.TimeZone);
        UpdateNewAccounts();
        return result.Settings;
    }

    /// <summary>Reads the paired computers (hub only) for <see cref="NewAccounts"/>.</summary>
    public async Task RefreshDevicesAsync(CancellationToken ct = default) =>
        UsePairedDevices((await Client.RequestAsync<DevicesResult>(new GetDevices(), ct)).Devices);

    /// <summary>Takes the paired computers a page read (<see cref="GetDevices"/>).</summary>
    public void UsePairedDevices(IReadOnlyList<DeviceInfo> devices)
    {
        PairedDevices = devices ?? throw new ArgumentNullException(nameof(devices));
        UpdateNewAccounts();
    }

    /// <summary>
    /// Changes part of the settings with <see cref="ReplaceSettings"/>: reads the current settings, applies
    /// <paramref name="change"/>, and replaces them only if nothing changed in between (<see cref="ReplaceSettings.IfVersion"/>).
    /// When something did (a request approved, a computer paired, usage counted), it reads again and reapplies the
    /// change, so a page that edits one member never undoes anything else.
    /// </summary>
    public async Task UpdateSettingsAsync(Func<FamilyBundle, FamilyBundle> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        for (var attempt = 1; ; attempt++)
        {
            var current = await RefreshSettingsAsync(ct);
            try
            {
                await Client.ExecuteAsync(new ReplaceSettings(change(current), current.Version), ct);
                return;
            }
            catch (AdminServerException ex) when (ex.Code == ReplaceSettings.ConflictCode && attempt < UpdateAttempts)
            {
            }
        }
    }

    public async Task<IReadOnlyList<RequestDto>> RefreshPendingRequestsAsync(CancellationToken ct = default)
    {
        var result = await Client.RequestAsync<RequestsResult>(new GetRequests("pending"), ct);
        PendingRequestCount = result.Requests.Count;
        return result.Requests;
    }

    public MemberDto? Member(string? memberId) =>
        memberId is null ? null : Settings?.Members.FirstOrDefault(m => m.Id == memberId);

    /// <summary>The member's display name; "whole family" for null (global rules), the raw id when unknown.</summary>
    public string MemberName(string? memberId) =>
        memberId is null
            ? Strings.Common_WholeFamily
            : Member(memberId)?.DisplayName ?? Status?.Members.FirstOrDefault(m => m.MemberId == memberId)?.DisplayName ?? memberId;

    public DateTimeOffset ToLocal(DateTimeOffset instant) => FamilyTime.ToLocal(instant, TimeZone);

    public string FormatTime(DateTimeOffset instant) => ToLocal(instant).ToString(Strings.Format_Time, System.Globalization.CultureInfo.CurrentCulture);

    public string FormatDateTime(DateTimeOffset instant) => ToLocal(instant).ToString(Strings.Format_DateTime, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The day with its year, for dates that may be long ago (when a computer was paired).</summary>
    public string FormatDate(DateTimeOffset instant) => ToLocal(instant).ToString(Strings.Format_Date, System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// For an agent computer: "managed by the parents' computer ‹name›" and when it was last heard from; whether that is a
    /// problem (not paired, or not reachable now — it keeps filtering by the last settings either way).
    /// </summary>
    public (string Text, bool Problem) DescribeHub(HubConnection? hub)
    {
        if (hub?.Address is null) return (Strings.Agent_NotPaired, true);
        var managedBy = Texts.Format(Strings.Agent_ManagedBy, Texts.Ltr(hub.Name ?? hub.Address));
        var contact = hub.LastContactAt is { } at ? Texts.Format(Strings.Agent_LastContact, FormatUntil(at)) : Strings.Agent_NoContactYet;
        return hub.Reachable
            ? (Texts.Join(" ", managedBy, contact), false)
            : (Texts.Join(" ", managedBy, Strings.Agent_Unreachable, contact), true);
    }

    /// <summary>"the end of the day" for the coming midnight, the time for later today, otherwise date and time.</summary>
    public string FormatUntil(DateTimeOffset until)
    {
        var now = ServiceNow;
        if (until == FamilyTime.EndOfDay(now, TimeZone)) return Strings.Until_EndOfDay;
        return ToLocal(until).Date == ToLocal(now).Date ? FormatTime(until) : FormatDateTime(until);
    }

    /// <summary>A paired computer's name as the parent gave it at pairing; its id when it has none.</summary>
    public static string DeviceName(DeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return string.IsNullOrWhiteSpace(device.Name) ? device.Id : device.Name.Trim();
    }

    private void UpdateNewAccounts()
    {
        var thisComputer = Status?.DeviceId;
        var paired = PairedDevices?.Where(d => !d.Revoked).ToDictionary(d => d.Id, DeviceName, StringComparer.Ordinal);
        var byName = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true);
        NewAccounts = Settings is not { } settings || IsAgent ? [] : [.. settings.Devices
            .Where(d => d.Id == thisComputer || paired is null || paired.ContainsKey(d.Id))
            .Select(d => (Device: d, Name: d.Id == thisComputer ? null : paired?.GetValueOrDefault(d.Id) ?? d.Id))
            .OrderBy(d => d.Name is null ? 0 : 1).ThenBy(d => d.Name, byName)
            .SelectMany(d => d.Device.Accounts.Where(AccountLinks.IsNew).OrderBy(a => a.Name, byName).Select(a => new NewAccount(d.Device.Id, d.Name, a)))];
        UnlinkedAccountCount = NewAccounts.Count;
    }
}

/// <summary>A standard Windows account nobody answered for yet, on <see cref="DeviceId"/>; <see cref="DeviceName"/> is null for this computer.</summary>
public sealed record NewAccount(string DeviceId, string? DeviceName, AccountDto Account);
