using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// One card per family member (from <see cref="GetStatus"/>) with the parent's quick actions (MVP-PLAN §4.5, §8), and
/// above them a card for each new Windows account nobody answered for yet (<see cref="FamilyContext.NewAccounts"/>).
/// On an agent computer: which hub manages it, and only the local emergency pause.
/// </summary>
public sealed partial class DashboardViewModel : PageViewModel
{
    public const int HomeworkMinutes = 60;

    public DashboardViewModel(FamilyContext context) : base(context) => Context.PropertyChanged += OnContextChanged;

    public override string Title => Strings.Nav_Dashboard;

    public override string Intro => Strings.Dashboard_Intro;

    public override bool AutoRefresh => true;

    public ObservableCollection<MemberCardViewModel> Members { get; } = [];

    [ObservableProperty]
    private bool _hasNoMembers;

    [ObservableProperty]
    private string? _lastUpdatedText;

    /// <summary>An agent computer: the cards offer only the local emergency pause, and <see cref="HubText"/> says who manages it.</summary>
    [ObservableProperty]
    private bool _isAgent;

    /// <summary>On an agent: "managed by the parents' computer ‹name›" and when it was last heard from, or that it is not paired.</summary>
    [ObservableProperty]
    private string? _hubText;

    /// <summary>On an agent: the hub is not reachable now (or this computer is not paired): shown as a warning.</summary>
    [ObservableProperty]
    private bool _hubProblem;

    /// <summary>The pending-requests line: once loaded, and only on the hub (requests are decided there).</summary>
    [ObservableProperty]
    private bool _showsRequests;

    public bool HasPendingRequests => Context.PendingRequestCount > 0;

    /// <summary>Standard Windows accounts nobody answered for (browsing there is blocked), each with its four answers.</summary>
    public ObservableCollection<NewAccountViewModel> NewAccounts { get; } = [];

    [ObservableProperty]
    private bool _hasNewAccounts;

    public string PendingRequestsText => Context.PendingRequestCount switch
    {
        0 => Strings.Dashboard_NoPendingRequests,
        1 => Strings.Dashboard_PendingRequests_One,
        var n => Texts.Format(Strings.Dashboard_PendingRequests_Many, n),
    };

    [RelayCommand]
    private void OpenRequests() => Navigator?.NavigateTo<RequestsViewModel>();

    [RelayCommand]
    private void OpenSetup() => Navigator?.NavigateTo<SetupWizardViewModel>();

    internal void OpenMember(MemberCardViewModel member) => Navigator?.ShowMember(member.MemberId);

    protected override async Task LoadAsync()
    {
        var status = await Context.RefreshStatusAsync();
        IsAgent = !status.IsHub;
        if (!IsAgent)
        {
            await Context.RefreshPendingRequestsAsync();
            // The computers' names for the new accounts' cards, and which computers still get settings.
            if (Context.MayHaveNewAccounts) await Context.RefreshDevicesAsync();
        }
        SyncNewAccounts();
        Sync(status);
        (HubText, HubProblem) = IsAgent ? Context.DescribeHub(status.Hub) : (null, false);
        ShowsRequests = !IsAgent;
        LastUpdatedText = Texts.Format(Strings.Dashboard_LastUpdated, Context.FormatTime(status.Now));
    }

    internal Task ExtendAsync(MemberCardViewModel member, int minutes) =>
        ActAsync(new ExtendTime(member.MemberId, minutes), Texts.Format(Strings.Done_Extend, Texts.Minutes(minutes), member.DisplayName));

    internal Task PauseForTodayAsync(MemberCardViewModel member) =>
        ActAsync(new Pause(member.MemberId, FamilyTime.EndOfDay(Context.ServiceNow, Context.TimeZone)), Texts.Format(Strings.Done_Pause, member.DisplayName));

    internal Task ResumeAsync(MemberCardViewModel member) =>
        ActAsync(new Resume(member.MemberId), Texts.Format(Strings.Done_Resume, member.DisplayName));

    internal Task StartHomeworkAsync(MemberCardViewModel member) =>
        ActAsync(new StartHomework(member.MemberId, HomeworkMinutes), Texts.Format(Strings.Done_StartHomework, member.DisplayName));

    internal Task StopHomeworkAsync(MemberCardViewModel member) =>
        ActAsync(new StopHomework(member.MemberId), Texts.Format(Strings.Done_StopHomework, member.DisplayName));

    /// <summary>Updates the cards in place (by member id) so a refresh every few seconds does not rebuild the buttons under the mouse.</summary>
    private void Sync(StatusResult status)
    {
        var index = 0;
        foreach (var member in status.Members)
        {
            var card = Members.FirstOrDefault(c => c.MemberId == member.MemberId);
            if (card is null)
            {
                card = new MemberCardViewModel(this, member.MemberId);
                Members.Insert(index, card);
            }
            else if (Members.IndexOf(card) != index)
            {
                Members.Move(Members.IndexOf(card), index);
            }
            card.Update(member, Context);
            index++;
        }
        while (Members.Count > index) Members.RemoveAt(Members.Count - 1);
        HasNoMembers = Members.Count == 0;
    }

    /// <summary>The new accounts' cards, kept in place (by computer and account) so a refresh keeps what the parent is typing.</summary>
    private void SyncNewAccounts()
    {
        var members = Context.Settings?.Members ?? [];
        var cards = new List<NewAccountViewModel>();
        foreach (var account in Context.NewAccounts)
        {
            var card = NewAccounts.FirstOrDefault(c => c.DeviceId == account.DeviceId && string.Equals(c.AccountId, account.Account.AccountId, StringComparison.OrdinalIgnoreCase))
                       ?? new NewAccountViewModel(this, account.DeviceId, account.Account.AccountId);
            card.Update(account.Account, account.DeviceName, members);
            cards.Add(card);
        }
        for (var i = 0; i < cards.Count; i++)
        {
            var at = NewAccounts.IndexOf(cards[i]);
            if (at < 0) NewAccounts.Insert(i, cards[i]);
            else if (at != i) NewAccounts.Move(at, i);
        }
        while (NewAccounts.Count > cards.Count) NewAccounts.RemoveAt(NewAccounts.Count - 1);
        HasNewAccounts = NewAccounts.Count > 0;
    }

    private void OnContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FamilyContext.PendingRequestCount))
        {
            OnPropertyChanged(nameof(HasPendingRequests));
            OnPropertyChanged(nameof(PendingRequestsText));
        }
    }
}

public sealed partial class MemberCardViewModel : ObservableObject
{
    public MemberCardViewModel(DashboardViewModel owner, string memberId)
    {
        ArgumentNullException.ThrowIfNull(owner);
        MemberId = memberId;
        AddQuarterHourCommand = new AsyncRelayCommand(() => owner.ExtendAsync(this, 15));
        AddHalfHourCommand = new AsyncRelayCommand(() => owner.ExtendAsync(this, 30));
        PauseCommand = new AsyncRelayCommand(() => owner.PauseForTodayAsync(this));
        ResumeCommand = new AsyncRelayCommand(() => owner.ResumeAsync(this));
        StartHomeworkCommand = new AsyncRelayCommand(() => owner.StartHomeworkAsync(this));
        StopHomeworkCommand = new AsyncRelayCommand(() => owner.StopHomeworkAsync(this));
        OpenDetailsCommand = new RelayCommand(() => owner.OpenMember(this));
    }

    public string MemberId { get; }

    public IAsyncRelayCommand AddQuarterHourCommand { get; }
    public IAsyncRelayCommand AddHalfHourCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand ResumeCommand { get; }
    public IAsyncRelayCommand StartHomeworkCommand { get; }
    public IAsyncRelayCommand StopHomeworkCommand { get; }
    public IRelayCommand OpenDetailsCommand { get; }

    [ObservableProperty] private string _displayName = "";

    /// <summary>The wire state (ok | warn | expired | outside_window | unlimited); the view colors the badge by it.</summary>
    [ObservableProperty] private string _timeState = "";

    [ObservableProperty] private string _timeStateText = "";
    [ObservableProperty] private string _usageText = "";
    [ObservableProperty] private string? _remainingText;
    [ObservableProperty] private string? _windowEndText;
    [ObservableProperty] private bool _hasLimit;
    [ObservableProperty] private int _usedMinutes;
    [ObservableProperty] private int _limitMinutes;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string? _pausedText;
    [ObservableProperty] private bool _isInHomework;
    [ObservableProperty] private string? _homeworkText;

    /// <summary>False for members whose filter mode is "unrestricted": no badges, no quick actions.</summary>
    [ObservableProperty] private bool _isFiltered;

    [ObservableProperty] private bool _canExtend;
    [ObservableProperty] private bool _canPause;
    [ObservableProperty] private bool _canResume;
    [ObservableProperty] private bool _canStartHomework;

    /// <summary>"Pause filtering for today", or on an agent "emergency pause on this computer until the end of the day".</summary>
    [ObservableProperty] private string _pauseText = Strings.Action_PauseToday;

    /// <summary>On an agent while paused: resuming is done from the parents' computer.</summary>
    [ObservableProperty] private bool _resumesOnHub;

    internal void Update(MemberStatus status, FamilyContext context)
    {
        DisplayName = status.DisplayName;
        TimeState = status.TimeState;
        TimeStateText = Texts.Code("TimeState_", status.TimeState);
        UsageText = status.LimitMinutes is { } limit
            ? Texts.Format(Strings.Member_UsedOfLimit, Texts.Minutes(status.UsedMinutes), Texts.Minutes(limit))
            : Texts.Format(Strings.Member_Used, Texts.Minutes(status.UsedMinutes));
        HasLimit = status.LimitMinutes is > 0;
        UsedMinutes = status.UsedMinutes;
        LimitMinutes = status.LimitMinutes ?? 0;
        RemainingText = status.RemainingMinutes is { } remaining ? Texts.Format(Strings.Member_Remaining, Texts.Minutes(remaining)) : null;
        WindowEndText = status.WindowEndsAt is { } end ? Texts.Format(Strings.Member_WindowEnds, end) : null;

        IsPaused = status.PausedUntil is not null;
        PausedText = status.PausedUntil is { } paused ? Texts.Format(Strings.Member_PausedUntil, context.FormatUntil(paused)) : null;
        IsInHomework = status.HomeworkUntil is not null;
        HomeworkText = status.HomeworkUntil is { } homework ? Texts.Format(Strings.Member_HomeworkUntil, context.FormatUntil(homework)) : null;

        // An agent has no settings: it knows a member is unfiltered only from the time state ("unlimited").
        var agent = context.IsAgent;
        IsFiltered = agent ? status.TimeState != "unlimited" : context.Member(status.MemberId)?.FilterMode is not "unrestricted";
        CanExtend = IsFiltered && !agent && status.TimeState != "unlimited";
        CanPause = IsFiltered && !IsPaused;
        CanResume = IsPaused && !agent;
        ResumesOnHub = IsPaused && agent;
        CanStartHomework = IsFiltered && !agent && !IsInHomework;
        PauseText = agent ? Strings.Action_LocalPause : Strings.Action_PauseToday;
    }
}
