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
        if (Settings?.Version != status.SettingsVersion) await RefreshSettingsAsync(ct);
        return status;
    }

    public async Task<FamilyBundle> RefreshSettingsAsync(CancellationToken ct = default)
    {
        var result = await Client.RequestAsync<SettingsResult>(new GetSettings(), ct);
        Settings = result.Settings;
        TimeZone = FamilyTime.FindZone(result.Settings.TimeZone);
        return result.Settings;
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

    /// <summary>"the end of the day" for the coming midnight, the time for later today, otherwise date and time.</summary>
    public string FormatUntil(DateTimeOffset until)
    {
        var now = ServiceNow;
        if (until == FamilyTime.EndOfDay(now, TimeZone)) return Strings.Until_EndOfDay;
        return ToLocal(until).Date == ToLocal(now).Date ? FormatTime(until) : FormatDateTime(until);
    }
}
