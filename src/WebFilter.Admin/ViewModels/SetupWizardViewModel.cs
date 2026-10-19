using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

public enum WizardStep
{
    Members,
    Accounts,
    Time,
    Review,
    Done,
}

/// <summary>
/// First-time setup of a hub whose family is empty (MVP-PLAN §4.5): the family members, each starting from its role's
/// template; this computer's Windows accounts (<see cref="ScanAccounts"/>, then the device in the settings) linked to
/// them; hours and a daily limit for the filtered ones; then all of it in one <see cref="ReplaceSettings"/>. It ends
/// with how to pair the children's other computers.
/// </summary>
public sealed partial class SetupWizardViewModel : PageViewModel
{
    /// <summary>Account link value: not linked to anyone.</summary>
    public const string NotLinked = "";

    public SetupWizardViewModel(FamilyContext context) : base(context)
    {
        Members.Add(new WizardMemberViewModel(NewMemberId(), "child"));
        UpdateSteps();
    }

    public override string Title => Strings.Nav_Setup;

    public override string Intro => Strings.Setup_Intro;

    /// <summary>Creates ids for new members (the rules, hours and accounts refer to them).</summary>
    internal Func<string> NewMemberId { get; set; } = () => $"m-{Guid.NewGuid():N}"[..10];

    public ObservableCollection<WizardMemberViewModel> Members { get; } = [];

    public ObservableCollection<WizardAccountViewModel> Accounts { get; } = [];

    /// <summary>"Not linked" and the members, for each account's choice.</summary>
    public ObservableCollection<Option> LinkOptions { get; } = [];

    /// <summary>The members whose browsing is filtered: only they get hours and a limit.</summary>
    public ObservableCollection<WizardMemberViewModel> TimedMembers { get; } = [];

    public ObservableCollection<WizardStepItem> Steps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMembersStep), nameof(IsAccountsStep), nameof(IsTimeStep), nameof(IsReviewStep), nameof(IsDone), nameof(CanGoBack),
        nameof(CanGoNext), nameof(ShowsWizard), nameof(ShowsAlreadySetUp))]
    private WizardStep _step = WizardStep.Members;

    public bool IsMembersStep => Step == WizardStep.Members;
    public bool IsAccountsStep => Step == WizardStep.Accounts;
    public bool IsTimeStep => Step == WizardStep.Time;
    public bool IsReviewStep => Step == WizardStep.Review;
    public bool IsDone => Step == WizardStep.Done;
    public bool CanGoBack => Step is WizardStep.Accounts or WizardStep.Time or WizardStep.Review;
    public bool CanGoNext => Step is WizardStep.Members or WizardStep.Accounts or WizardStep.Time;

    /// <summary>The steps show only on a hub whose family is still empty (or right after this wizard set it up).</summary>
    public bool ShowsWizard => !IsNotHub && (!IsAlreadySetUp || IsDone);

    /// <summary>The family already has members (set up here or elsewhere): the page points to the member page instead.</summary>
    public bool ShowsAlreadySetUp => !IsNotHub && IsAlreadySetUp && !IsDone;

    /// <summary>Why the parent cannot go on from this step.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWizard), nameof(ShowsAlreadySetUp))]
    private bool _isAlreadySetUp;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsWizard), nameof(ShowsAlreadySetUp))]
    private bool _isNotHub;

    [ObservableProperty]
    private bool _hasNoAccounts;

    [ObservableProperty]
    private bool _hasNoTimedMembers;

    /// <summary>On the review step: which accounts go to whom.</summary>
    [ObservableProperty]
    private string? _accountsSummary;

    protected override async Task LoadAsync()
    {
        StatusResult status;
        FamilyBundle settings;
        try
        {
            status = await Context.RefreshStatusAsync();
            if (!status.IsHub)
            {
                IsNotHub = true;
                return;
            }
            settings = Context.Settings ?? await Context.RefreshSettingsAsync();
        }
        catch (AdminServerException ex) when (ex.Code == FamilyContext.NotHub)
        {
            IsNotHub = true;
            return;
        }
        IsNotHub = false;
        IsAlreadySetUp = settings.Members.Count > 0;
        if (Step == WizardStep.Accounts) SyncAccounts(settings);
    }

    [RelayCommand]
    private void AddMember()
    {
        Members.Add(new WizardMemberViewModel(NewMemberId(), Members.Count == 0 ? "child" : "teen"));
        Message = null;
    }

    [RelayCommand]
    private void RemoveMember(WizardMemberViewModel? member)
    {
        if (member is null || Members.Count <= 1) return;
        Members.Remove(member);
        Message = null;
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        Message = null;
        switch (Step)
        {
            case WizardStep.Members:
                if (ValidateMembers() is { } membersProblem)
                {
                    Message = membersProblem;
                    return;
                }
                GoTo(WizardStep.Accounts);
                // Rebuilding the choices clears the combo boxes' selections, so the links are put back afterwards; links to
                // a member who was removed meanwhile are dropped.
                var links = Accounts.ToDictionary(a => a, a => a.MemberId);
                Replace(LinkOptions, [new Option(NotLinked, Strings.Setup_NotLinked), .. Members.Select(m => new Option(m.Id, m.Name.Trim()))]);
                foreach (var (account, link) in links) account.MemberId = LinkOptions.Any(o => o.Value == link) ? link : NotLinked;
                UpdateAccountWarnings();
                await ScanAsync();
                break;

            case WizardStep.Accounts:
                GoTo(WizardStep.Time);
                Replace(TimedMembers, [.. Members.Where(m => m.IsFiltered)]);
                HasNoTimedMembers = TimedMembers.Count == 0;
                break;

            case WizardStep.Time:
                if (ValidateTimes() is { } timeProblem)
                {
                    Message = timeProblem;
                    return;
                }
                foreach (var member in Members) member.Summarize(Accounts.Where(a => a.MemberId == member.Id).Select(a => a.Name));
                var unlinked = Accounts.Where(a => a.MemberId == NotLinked).Select(a => a.Name).ToList();
                AccountsSummary = unlinked.Count == 0 ? null : Texts.Format(Strings.Setup_UnlinkedAccounts, string.Join(", ", unlinked));
                GoTo(WizardStep.Review);
                break;
        }
    }

    [RelayCommand]
    private void Back()
    {
        Message = null;
        if (CanGoBack) GoTo(Step - 1);
    }

    /// <summary>Looks for Windows accounts again (e.g. after the parent created one for a child).</summary>
    [RelayCommand]
    private Task RescanAsync() => ScanAsync();

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (Step != WizardStep.Review) return;
        Message = null;
        StatusMessage = null;
        var deviceId = Context.Status?.DeviceId;
        var members = Members.Select(m => m.ToDto()).ToList();
        var links = Accounts.Where(a => a.MemberId != NotLinked).ToDictionary(a => a.AccountId, a => a.MemberId, StringComparer.OrdinalIgnoreCase);
        var allDays = Enumerable.Range(0, WeeklyPlan.Days).ToList();
        var timed = Members.Where(m => m.IsFiltered && m.LimitTime).ToList();
        var schedules = timed.Select(m => new ScheduleDto(m.Id, allDays, m.StartTime, m.EndTime)).ToList();
        var limits = timed.Where(m => m.LimitMinutes is not null).Select(m => new DailyLimitDto(m.Id, allDays, m.LimitMinutes!.Value)).ToList();

        var applied = await RunAsync(() => Context.UpdateSettingsAsync(s => s with
        {
            Members = [.. s.Members, .. members],
            Devices = [.. s.Devices.Select(d => d.Id != deviceId ? d : d with
            {
                Accounts = [.. d.Accounts.Select(a => links.TryGetValue(a.AccountId, out var member) ? a with { Member = member } : a)],
            })],
            Schedules = [.. s.Schedules, .. schedules],
            DailyLimits = [.. s.DailyLimits, .. limits],
        }));
        if (!applied) return;
        GoTo(WizardStep.Done);
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenDashboard() => Navigator?.NavigateTo<DashboardViewModel>();

    [RelayCommand]
    private void OpenDevices() => Navigator?.NavigateTo<DevicesViewModel>();

    [RelayCommand]
    private void OpenMembers() => Navigator?.NavigateTo<MemberDetailViewModel>();

    internal void UpdateAccountWarnings()
    {
        foreach (var account in Accounts)
            account.Warning = account.IsAdmin && Members.FirstOrDefault(m => m.Id == account.MemberId) is { IsFiltered: true } member
                ? Texts.Format(Strings.Setup_AdminLinked, account.Name, member.Name.Trim())
                : null;
    }

    private async Task ScanAsync()
    {
        await RunAsync(async () =>
        {
            try
            {
                await Client.ExecuteAsync(new ScanAccounts());
            }
            catch (AdminServerException ex) when (ex.Code is "unsupported" or "not_found")
            {
                // An older service, or this computer missing from the settings: show the accounts the settings have.
            }
            SyncAccounts(await Context.RefreshSettingsAsync());
        });
    }

    /// <summary>This computer's accounts from the settings; choices already made are kept, standard accounts first.</summary>
    private void SyncAccounts(FamilyBundle settings)
    {
        var deviceId = Context.Status?.DeviceId;
        var device = settings.Devices.FirstOrDefault(d => d.Id == deviceId);
        var chosen = Accounts.ToDictionary(a => a.AccountId, a => a.MemberId, StringComparer.OrdinalIgnoreCase);
        Accounts.Clear();
        foreach (var account in (device?.Accounts ?? []).OrderBy(a => a.IsAdmin).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var link = chosen.TryGetValue(account.AccountId, out var picked) ? picked
                : Members.Any(m => m.Id == account.Member) ? account.Member! : NotLinked;
            Accounts.Add(new WizardAccountViewModel(this, account, link));
        }
        HasNoAccounts = Accounts.Count == 0;
        UpdateAccountWarnings();
    }

    private string? ValidateMembers()
    {
        var existing = Context.Settings?.Members.Select(m => m.DisplayName) ?? [];
        foreach (var member in Members)
        {
            var others = existing.Concat(Members.Where(m => m != member && Members.IndexOf(m) < Members.IndexOf(member)).Select(m => m.Name));
            if (MemberInput.Validate(member.Name, member.AgeText, others, out var age) is { } problem) return problem;
            member.Age = age;
        }
        return null;
    }

    private string? ValidateTimes()
    {
        foreach (var member in TimedMembers.Where(m => m.LimitTime))
        {
            var who = member.Name.Trim();
            if (!ClockTime.TryParse(member.Start, allowEndOfDay: false, out var start) || !ClockTime.TryParse(member.End, allowEndOfDay: true, out var end))
                return Texts.Format(Strings.Validation_TimeFormat, who);
            if (start >= end) return Texts.Format(Strings.Validation_TimeOrder, who, member.Start.Trim(), member.End.Trim());
            var text = member.LimitText.Trim();
            int? minutes = null;
            if (text.Length > 0)
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > ClockTime.EndOfDay)
                    return Texts.Format(Strings.Validation_Limit, who);
                minutes = value;
            }
            member.SetTimes(ClockTime.Format(start), ClockTime.Format(end), minutes);
        }
        return null;
    }

    private void GoTo(WizardStep step)
    {
        Step = step;
        UpdateSteps();
    }

    private void UpdateSteps()
    {
        WizardStep[] shown = [WizardStep.Members, WizardStep.Accounts, WizardStep.Time, WizardStep.Review];
        Steps.Clear();
        foreach (var step in shown)
            Steps.Add(new WizardStepItem(Texts.Format(Strings.Setup_StepNumber, (int)step + 1, Texts.Code("Setup_Step_", step.ToString())), step == Step, step < Step));
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

/// <summary>A step in the wizard's progress bar.</summary>
public sealed record WizardStepItem(string Label, bool IsCurrent, bool IsComplete);

/// <summary>Where a role starts (MVP-PLAN §4.5 templates: young children / teens / adults); the parent can change each part.</summary>
internal sealed record MemberTemplate(string FilterMode, string YouTubeMode, bool LimitTime, string Start, string End, int LimitMinutes)
{
    public static MemberTemplate For(string role) => role switch
    {
        "child" => new("allowlist", "allowlist", true, "15:00", "19:00", 120),
        "teen" => new("blocklist", "restricted", true, "14:00", "21:30", 240),
        _ => new("unrestricted", "unrestricted", false, "08:00", "22:00", 180),
    };
}

public sealed partial class WizardMemberViewModel : ObservableObject
{
    public WizardMemberViewModel(string id, string role)
    {
        Id = id;
        Role = role;
    }

    public string Id { get; }

    public IReadOnlyList<Option> RoleOptions => MemberProfileEditor.RoleOptions;

    public IReadOnlyList<Option> FilterModeOptions => SettingsViewModel.FilterModes;

    public IReadOnlyList<Option> YouTubeModeOptions => SettingsViewModel.YouTubeModes;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _role = "";
    [ObservableProperty] private string _ageText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFiltered))]
    private string _filterMode = "";

    [ObservableProperty] private string _youTubeMode = "";
    [ObservableProperty] private bool _limitTime;
    [ObservableProperty] private string _start = "";
    [ObservableProperty] private string _end = "";
    [ObservableProperty] private string _limitText = "";

    /// <summary>On the review step: role, modes, hours and accounts in one line.</summary>
    [ObservableProperty] private string? _summaryText;

    public bool IsFiltered => FilterMode != "unrestricted";

    internal int? Age { get; set; }

    /// <summary>The checked hours ("HH:MM") and limit, set when the time step is left.</summary>
    internal string StartTime { get; private set; } = "";

    internal string EndTime { get; private set; } = "";

    internal int? LimitMinutes { get; private set; }

    internal void SetTimes(string start, string end, int? minutes) => (StartTime, EndTime, LimitMinutes) = (start, end, minutes);

    /// <summary>"15:00–19:00", kept left to right inside the Hebrew summary (a dash between two times would otherwise flip them).</summary>
    private string Hours => Texts.Ltr($"{StartTime}–{EndTime}")!;

    internal MemberDto ToDto() => new(Id, Name.Trim(), Role, Age, FilterMode, YouTubeMode);

    internal void Summarize(IEnumerable<string> accounts)
    {
        var time = !IsFiltered ? Strings.Setup_Summary_NotFiltered
            : !LimitTime ? Strings.Setup_Summary_NoTimeLimit
            : LimitMinutes is { } minutes ? Texts.Format(Strings.Setup_Summary_Time, Hours, Texts.Minutes(minutes))
            : Texts.Format(Strings.Setup_Summary_Hours, Hours);
        var linked = accounts.ToList();
        SummaryText = Texts.Join(" · ",
            Texts.Code("Role_", Role) + (Age is { } age ? Texts.Format(Strings.Setup_Summary_Age, age) : ""),
            Texts.Code("FilterMode_", FilterMode),
            Texts.Format(Strings.Setup_Summary_YouTube, Texts.Code("YouTubeMode_", YouTubeMode)),
            time,
            linked.Count == 0 ? Strings.Setup_Summary_NoAccount : Texts.Format(Strings.Setup_Summary_Accounts, string.Join(", ", linked)));
    }

    /// <summary>A new role brings its template's modes and times.</summary>
    partial void OnRoleChanged(string value)
    {
        var template = MemberTemplate.For(value);
        FilterMode = template.FilterMode;
        YouTubeMode = template.YouTubeMode;
        LimitTime = template.LimitTime;
        Start = template.Start;
        End = template.End;
        LimitText = template.LimitMinutes.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed partial class WizardAccountViewModel : ObservableObject
{
    private readonly SetupWizardViewModel _owner;

    public WizardAccountViewModel(SetupWizardViewModel owner, AccountDto account, string memberId)
    {
        _owner = owner;
        AccountId = account.AccountId;
        Name = account.Name;
        IsAdmin = account.IsAdmin;
        MemberId = memberId;
    }

    public string AccountId { get; }

    public string Name { get; }

    public bool IsAdmin { get; }

    public ObservableCollection<Option> Options => _owner.LinkOptions;

    [ObservableProperty]
    private string _memberId = SetupWizardViewModel.NotLinked;

    /// <summary>An administrator account linked to a filtered member: administrators are never filtered.</summary>
    [ObservableProperty]
    private string? _warning;

    partial void OnMemberIdChanged(string value) => _owner.UpdateAccountWarnings();
}
