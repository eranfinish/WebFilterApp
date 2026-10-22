using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.ViewModels;

/// <summary>The event log (<see cref="GetEvents"/>) filtered by member, event type and period; newest first.</summary>
public sealed partial class ActivityViewModel : PageViewModel
{
    public const int MaxEvents = 500;
    public const string AnyValue = "";
    public const string LastDay = "day";
    public const string LastWeek = "week";

    /// <summary>The event types of <see cref="GetEvents.EventType"/>.</summary>
    public static readonly string[] EventTypes = ["blocked", "app_blocked", "access_request", "time_warning", "time_expired", "admin_change", "tamper", "service", "search", "override"];

    private bool _syncingOptions;

    public ActivityViewModel(FamilyContext context) : base(context)
    {
    }

    public override string Title => Strings.Nav_Activity;

    public override string Intro => Strings.Activity_Intro;

    public ObservableCollection<Option> MemberOptions { get; } = [new Option(AnyValue, Strings.Common_All)];

    public IReadOnlyList<Option> TypeOptions { get; } = [new Option(AnyValue, Strings.Common_All), .. Texts.Options("EventType_", EventTypes)];

    public IReadOnlyList<Option> PeriodOptions { get; } = [new Option(LastDay, Strings.Period_Day), new Option(LastWeek, Strings.Period_Week)];

    public ObservableCollection<EventItemViewModel> Events { get; } = [];

    [ObservableProperty]
    private string? _selectedMember = AnyValue;

    [ObservableProperty]
    private string? _selectedType = AnyValue;

    [ObservableProperty]
    private string? _selectedPeriod = LastDay;

    [ObservableProperty]
    private string? _countText;

    [ObservableProperty]
    private bool _hasNoEvents;

    /// <summary>The query the current filters produce, relative to the service's clock.</summary>
    internal GetEvents BuildQuery() => new(
        Since: Context.ServiceNow - (SelectedPeriod == LastWeek ? TimeSpan.FromDays(7) : TimeSpan.FromHours(24)),
        EventType: Texts.NullIfEmpty(SelectedType),
        MemberId: Texts.NullIfEmpty(SelectedMember),
        Max: MaxEvents);

    protected override async Task LoadAsync()
    {
        await Context.RefreshStatusAsync();
        SyncMemberOptions();
        var result = await Client.RequestAsync<EventsResult>(BuildQuery());
        Events.Clear();
        foreach (var item in result.Events.OrderByDescending(e => e.At)) Events.Add(new EventItemViewModel(item, Context));
        HasNoEvents = Events.Count == 0;
        CountText = Texts.Format(Strings.Activity_Count, Events.Count);
    }

    partial void OnSelectedMemberChanged(string? value) => FilterChanged();

    partial void OnSelectedTypeChanged(string? value) => FilterChanged();

    partial void OnSelectedPeriodChanged(string? value) => FilterChanged();

    private void FilterChanged()
    {
        if (HasLoaded && !_syncingOptions) _ = RefreshAsync();
    }

    private void SyncMemberOptions()
    {
        var members = Context.Settings?.Members.Select(m => new Option(m.Id, m.DisplayName)).ToList() ?? [];
        if (MemberOptions.Skip(1).SequenceEqual(members)) return;
        _syncingOptions = true;
        try
        {
            var selected = SelectedMember;
            while (MemberOptions.Count > 1) MemberOptions.RemoveAt(1);
            foreach (var member in members) MemberOptions.Add(member);
            SelectedMember = MemberOptions.Any(o => o.Value == selected) ? selected : AnyValue;
        }
        finally
        {
            _syncingOptions = false;
        }
    }
}

public sealed class EventItemViewModel
{
    public EventItemViewModel(EventDto item, FamilyContext context)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(context);
        Event = item;
        TimeText = context.FormatDateTime(item.At);
        MemberName = item.MemberId is null ? "" : context.MemberName(item.MemberId);
        TypeText = Texts.Code("EventType_", item.Type);
        DeviceText = item.DeviceId ?? "";
        var reason = Texts.Code("Reason_", item.Reason);
        Details = item.Type switch
        {
            "blocked" => Texts.Join(" — ", Texts.Ltr(item.Url), reason),
            "app_blocked" => Texts.Join(" — ", Texts.Ltr(item.App), reason),
            "time_warning" => item.RemainingMinutes is { } left ? Texts.Format(Strings.Event_TimeLeft, Texts.Minutes(left)) : reason,
            "admin_change" => Texts.Join(" — ", Texts.Code("AdminChange_", item.Detail), Texts.Ltr(item.Url)),
            "access_request" => Texts.Join(": ", Texts.Code("RequestKind_", item.Detail), Texts.Ltr(item.Url)),
            _ => Texts.Join(" — ", item.Detail, reason, Texts.Ltr(item.Url), Texts.Ltr(item.App)),
        };
    }

    public EventDto Event { get; }

    public string Type => Event.Type;

    /// <summary>Blocks and bypass attempts, which the view highlights.</summary>
    public bool IsAlert => Event.Type is "blocked" or "app_blocked" or "tamper";

    public string TimeText { get; }

    public string MemberName { get; }

    public string TypeText { get; }

    public string Details { get; }

    public string DeviceText { get; }
}
