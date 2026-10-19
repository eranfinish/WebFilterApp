using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// One family member in full (MVP-PLAN §4.5): name, role and modes (<see cref="SetMember"/>); weekly hours and daily
/// limits, YouTube channels, approved apps and categories (<see cref="ReplaceSettings"/> through
/// <see cref="FamilyContext.UpdateSettingsAsync"/>, which replaces only this member's part); and personal site rules
/// (<see cref="AddRule"/> / <see cref="RemoveRule"/>). A section with unsaved edits keeps them when the page reloads, and
/// a refusal from the service is shown next to the form it is about.
/// </summary>
public sealed partial class MemberDetailViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;
    private bool _building;

    public MemberDetailViewModel(FamilyContext context, IDialogService dialogs) : base(context) =>
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    public override string Title => Strings.Nav_Members;

    public override string Intro => Strings.Member_Intro;

    /// <summary>Creates ids for new site rules and app rules; the service stores the id it is given.</summary>
    internal Func<string> NewRuleId { get; set; } = () => $"r-{Guid.NewGuid():N}"[..14];

    internal Func<string> NewAppRuleId { get; set; } = () => $"a-{Guid.NewGuid():N}"[..14];

    public ObservableCollection<Option> MemberOptions { get; } = [];

    [ObservableProperty]
    private string? _selectedMemberId;

    /// <summary>A member is selected and shown.</summary>
    [ObservableProperty]
    private bool _hasMember;

    [ObservableProperty]
    private bool _hasNoMembers;

    /// <summary>The service answered "not_hub": this is an agent computer, members are managed on the hub.</summary>
    [ObservableProperty]
    private bool _isNotHub;

    /// <summary>The member's filter mode is "unrestricted": hours, sites and apps do not apply to them (the view says so).</summary>
    [ObservableProperty]
    private bool _isUnrestricted;

    [ObservableProperty]
    private MemberProfileEditor? _profile;

    [ObservableProperty]
    private WeekEditor? _week;

    [ObservableProperty]
    private CategoriesEditor? _categories;

    // ---- site rules -----------------------------------------------------------------------------------------------

    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];

    [ObservableProperty] private bool _hasNoRules;

    public IReadOnlyList<Option> RuleTypeOptions { get; } = Texts.Options("RuleType_", RulePatternValidator.Types);

    public IReadOnlyList<Option> RuleActionOptions { get; } = Texts.Options("RuleAction_", "allow", "block");

    public IReadOnlyList<Option> ExpiryOptions => RuleExpiry.Options;

    [ObservableProperty] private string _newPattern = "";
    [ObservableProperty] private string _newType = "domain";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeAlwaysAllowed))]
    private string _newAction = "allow";

    [ObservableProperty] private bool _newAlwaysAllowed;
    [ObservableProperty] private string _newExpiry = RuleExpiry.Permanent;
    [ObservableProperty] private string? _ruleMessage;

    /// <summary>"Always allowed" (even when time is up) only makes sense for an allow rule.</summary>
    public bool CanBeAlwaysAllowed => NewAction == "allow";

    // ---- YouTube channels -----------------------------------------------------------------------------------------

    public ObservableCollection<ChannelItemViewModel> Channels { get; } = [];

    [ObservableProperty] private bool _hasNoChannels;

    public IReadOnlyList<Option> ChannelActionOptions { get; } = Texts.Options("RuleAction_", "allow", "block");

    [ObservableProperty] private string _newChannel = "";
    [ObservableProperty] private string _newChannelHandle = "";
    [ObservableProperty] private string _newChannelTitle = "";
    [ObservableProperty] private string _newChannelAction = "allow";
    [ObservableProperty] private string? _channelMessage;

    // ---- apps -----------------------------------------------------------------------------------------------------

    public ObservableCollection<AppRuleItemViewModel> Apps { get; } = [];

    [ObservableProperty] private bool _hasNoApps;

    public IReadOnlyList<Option> AppMatchOptions { get; } = Texts.Options("AppMatch_", AppRuleValidator.Matches);

    [ObservableProperty] private string _newAppMatch = "path";
    [ObservableProperty] private string _newAppValue = "";
    [ObservableProperty] private bool _newAppAlwaysAllowed;
    [ObservableProperty] private string _newAppExpiry = RuleExpiry.Permanent;
    [ObservableProperty] private string? _appMessage;

    /// <summary>Shows <paramref name="memberId"/> (the main window navigates here afterwards).</summary>
    public void Show(string memberId) => SelectedMemberId = memberId;

    protected override async Task LoadAsync()
    {
        FamilyBundle settings;
        try
        {
            await Context.RefreshStatusAsync();
            settings = Context.Settings ?? await Context.RefreshSettingsAsync();
        }
        catch (AdminServerException ex) when (ex.Code == FamilyContext.NotHub)
        {
            IsNotHub = true;
            Build(null);
            return;
        }
        IsNotHub = false;

        _building = true;
        try
        {
            var wanted = SelectedMemberId;
            Replace(MemberOptions, [.. settings.Members.Select(m => new Option(m.Id, m.DisplayName))]);
            SelectedMemberId = settings.Members.Any(m => m.Id == wanted) ? wanted : settings.Members.FirstOrDefault()?.Id;
        }
        finally
        {
            _building = false;
        }
        HasNoMembers = settings.Members.Count == 0;
        Build(settings);
    }

    partial void OnSelectedMemberIdChanged(string? value)
    {
        // A refusal shown under a form was about the member shown before.
        RuleMessage = null;
        ChannelMessage = null;
        AppMessage = null;
        if (!_building && Context.Settings is { } settings) Build(settings);
    }

    partial void OnNewPatternChanged(string value) => RuleMessage = null;

    partial void OnNewTypeChanged(string value) => RuleMessage = null;

    partial void OnNewActionChanged(string value)
    {
        if (value != "allow") NewAlwaysAllowed = false;
    }

    partial void OnNewChannelChanged(string value) => ChannelMessage = null;

    partial void OnNewChannelHandleChanged(string value) => ChannelMessage = null;

    partial void OnNewAppValueChanged(string value) => AppMessage = null;

    partial void OnNewAppMatchChanged(string value) => AppMessage = null;

    // ---- commands -------------------------------------------------------------------------------------------------

    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (CurrentMember() is not { } member) return;
        RuleMessage = null;
        var rule = RuleDrafts.TryCreate(NewPattern, NewType, NewAction, member.Id, NewAlwaysAllowed, NewExpiry,
            Context.Settings?.Rules ?? [], Context, NewRuleId, out var error);
        if (rule is null)
        {
            RuleMessage = error;
            return;
        }
        if (await SaveAsync(() => Client.ExecuteAsync(new AddRule(rule)), Texts.Format(Strings.Done_RuleAdded, Texts.Ltr(rule.Pattern)), m => RuleMessage = m))
        {
            NewPattern = "";
            NewAlwaysAllowed = false;
            NewExpiry = RuleExpiry.Permanent;
        }
    }

    [RelayCommand]
    private async Task RemoveRuleAsync(RuleItemViewModel? rule)
    {
        if (rule is null) return;
        if (!_dialogs.Confirm(Texts.Format(Strings.Rules_RemoveConfirm, Texts.Ltr(rule.Pattern)), Strings.Rules_RemoveTitle)) return;
        await SaveAsync(() => Client.ExecuteAsync(new RemoveRule(rule.Id)), Texts.Format(Strings.Done_RuleRemoved, Texts.Ltr(rule.Pattern)), m => RuleMessage = m);
    }

    [RelayCommand]
    private async Task AddChannelAsync()
    {
        if (CurrentMember() is not { } member) return;
        ChannelMessage = null;
        if (!YouTubeChannelInput.TryParseChannelId(NewChannel, out var channelId, out var error)
            || !YouTubeChannelInput.TryNormalizeHandle(NewChannelHandle, out var handle, out error))
        {
            ChannelMessage = error;
            return;
        }
        var action = NewChannelAction;
        if (Channels.Any(c => c.ChannelId == channelId && c.Channel.Action == action))
        {
            ChannelMessage = Strings.Validation_Duplicate;
            return;
        }
        // The same channel with the other action is replaced: a channel is either allowed or blocked for a member.
        var channel = new YouTubeChannelDto(channelId, handle, Texts.NullIfEmpty(NewChannelTitle.Trim()), member.Id, action);
        var name = channel.Title ?? channel.Handle ?? channelId;
        if (await UpdateAsync(s => s with { YouTubeChannels = [.. s.YouTubeChannels.Where(c => !(c.Member == member.Id && c.ChannelId == channelId)), channel] },
                Texts.Format(Strings.Done_ChannelAdded, Texts.Ltr(name)), m => ChannelMessage = m))
        {
            NewChannel = "";
            NewChannelHandle = "";
            NewChannelTitle = "";
        }
    }

    [RelayCommand]
    private async Task RemoveChannelAsync(ChannelItemViewModel? channel)
    {
        if (channel is null || CurrentMember() is not { } member) return;
        if (!_dialogs.Confirm(Texts.Format(Strings.Member_RemoveChannelConfirm, Texts.Ltr(channel.Name)), Strings.Member_RemoveTitle)) return;
        await UpdateAsync(s => s with { YouTubeChannels = [.. s.YouTubeChannels.Where(c => !(c.Member == member.Id && c.ChannelId == channel.ChannelId))] },
            Texts.Format(Strings.Done_ChannelRemoved, Texts.Ltr(channel.Name)), m => ChannelMessage = m);
    }

    [RelayCommand]
    private async Task AddAppAsync()
    {
        if (CurrentMember() is not { } member) return;
        AppMessage = null;
        if (!AppRuleValidator.TryNormalize(NewAppValue, NewAppMatch, out var value, out var error))
        {
            AppMessage = error;
            return;
        }
        var now = Context.ServiceNow;
        if (Apps.Any(a => a.Rule.Match == NewAppMatch && !(a.Rule.ExpiresAt <= now) && string.Equals(a.Rule.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            AppMessage = Strings.Validation_Duplicate;
            return;
        }
        var rule = new AppRuleDto(NewAppRuleId(), member.Id, NewAppMatch, value, "allow", NewAppAlwaysAllowed, RuleExpiry.ExpiresAt(NewAppExpiry, Context));
        if (await UpdateAsync(s => s with { AppRules = [.. s.AppRules, rule] }, Texts.Format(Strings.Done_AppAdded, Texts.Ltr(AppRuleItemViewModel.ShortName(rule))),
                m => AppMessage = m))
        {
            NewAppValue = "";
            NewAppAlwaysAllowed = false;
            NewAppExpiry = RuleExpiry.Permanent;
        }
    }

    [RelayCommand]
    private async Task RemoveAppAsync(AppRuleItemViewModel? app)
    {
        if (app is null) return;
        if (!_dialogs.Confirm(Texts.Format(Strings.Member_RemoveAppConfirm, Texts.Ltr(app.Name)), Strings.Member_RemoveTitle)) return;
        await UpdateAsync(s => s with { AppRules = [.. s.AppRules.Where(a => a.Id != app.Id)] }, Texts.Format(Strings.Done_AppRemoved, Texts.Ltr(app.Name)),
            m => AppMessage = m);
    }

    // ---- section saves (called by the editors) --------------------------------------------------------------------

    /// <summary>Sends only the profile fields the parent changed; clearing the age needs the whole member, so it goes through the settings.</summary>
    internal async Task SaveProfileAsync(MemberProfileEditor editor)
    {
        var others = Context.Settings?.Members.Where(m => m.Id != editor.MemberId).Select(m => m.DisplayName) ?? [];
        if (!editor.TryValidate(others, out var name, out var age)) return;
        var stored = editor.Member;
        var done = Texts.Format(Strings.Done_MemberSaved, name);
        if (age is null && stored.Age is not null)
        {
            var member = stored with { DisplayName = name, Role = editor.Role, Age = null, FilterMode = editor.FilterMode, YouTubeMode = editor.YouTubeMode };
            await UpdateAsync(s => s with { Members = [.. s.Members.Select(m => m.Id == member.Id ? member : m)] }, done, m => editor.Message = m, editor.MarkSaved);
            return;
        }
        var set = new SetMember(stored.Id,
            DisplayName: name != stored.DisplayName ? name : null,
            Role: editor.Role != stored.Role ? editor.Role : null,
            Age: age != stored.Age ? age : null,
            FilterMode: editor.FilterMode != stored.FilterMode ? editor.FilterMode : null,
            YouTubeMode: editor.YouTubeMode != stored.YouTubeMode ? editor.YouTubeMode : null);
        await SaveAsync(() => Client.ExecuteAsync(set), done, m => editor.Message = m, editor.MarkSaved);
    }

    internal async Task SaveWeekAsync(WeekEditor editor)
    {
        if (!editor.TryBuild(out var days)) return;
        var restricts = editor.RestrictsHours;
        await UpdateAsync(s => WeeklyPlan.Replace(s, editor.MemberId, restricts, days),
            Texts.Format(Strings.Done_WeekSaved, Context.MemberName(editor.MemberId)), m => editor.Message = m, editor.MarkSaved);
    }

    internal async Task SaveCategoriesAsync(CategoriesEditor editor)
    {
        var shown = editor.Items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var chosen = editor.Items.Where(i => i.Choice != CategoryChoiceViewModel.Default)
            .Select(i => new MemberCategoryDto(editor.MemberId, i.Name, i.Choice)).ToList();
        await UpdateAsync(s => s with
            {
                MemberCategories = [.. s.MemberCategories.Where(c => c.Member != editor.MemberId || !shown.Contains(c.Category)), .. chosen],
            },
            Texts.Format(Strings.Done_CategoriesSaved, Context.MemberName(editor.MemberId)), m => editor.Message = m, editor.MarkSaved);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private MemberDto? CurrentMember() => Context.Member(SelectedMemberId);

    private Task<bool> UpdateAsync(Func<FamilyBundle, FamilyBundle> change, string done, Action<string?> showError, Action? saved = null) =>
        SaveAsync(() => Context.UpdateSettingsAsync(change), done, showError, saved);

    /// <summary>
    /// Runs a change and reloads. A refusal from the service goes next to the section's form (where the parent is looking,
    /// the page may be scrolled far from its header); connection problems stay with the window's banner.
    /// </summary>
    private async Task<bool> SaveAsync(Func<Task> operation, string done, Action<string?> showError, Action? saved = null)
    {
        StatusMessage = null;
        showError(null);
        if (!await RunAsync(operation))
        {
            if (ErrorMessage is { } error)
            {
                showError(error);
                ErrorMessage = null;
            }
            return false;
        }
        saved?.Invoke();
        StatusMessage = done;
        await RefreshAsync();
        return true;
    }

    /// <summary>Shows the selected member's sections; an editor with unsaved edits for the same member is kept as it is.</summary>
    private void Build(FamilyBundle? settings)
    {
        var member = settings?.Members.FirstOrDefault(m => m.Id == SelectedMemberId);
        HasMember = member is not null;
        Rules.Clear();
        Channels.Clear();
        Apps.Clear();
        if (settings is null || member is null)
        {
            Profile = null;
            Week = null;
            Categories = null;
            IsUnrestricted = false;
            return;
        }

        IsUnrestricted = member.FilterMode == "unrestricted";
        if (Profile is not { IsDirty: true } profile || profile.MemberId != member.Id) Profile = new MemberProfileEditor(this, member);
        else profile.Rebase(member);
        if (Week is not { IsDirty: true } week || week.MemberId != member.Id)
        {
            var (restricts, days) = WeeklyPlan.From(settings, member.Id);
            Week = new WeekEditor(this, member.Id, restricts, days);
        }
        if (Categories is not { IsDirty: true } categories || categories.MemberId != member.Id) Categories = new CategoriesEditor(this, member.Id, settings);

        var now = Context.ServiceNow;
        foreach (var rule in settings.Rules.Where(r => r.Member == member.Id).OrderBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase))
            Rules.Add(new RuleItemViewModel(rule, Context, now));
        foreach (var channel in settings.YouTubeChannels.Where(c => c.Member == member.Id).OrderBy(c => c.Action).ThenBy(c => c.Title ?? c.Handle ?? c.ChannelId, StringComparer.CurrentCulture))
            Channels.Add(new ChannelItemViewModel(channel));
        foreach (var app in settings.AppRules.Where(a => a.Member == member.Id).OrderBy(a => AppRuleItemViewModel.ShortName(a), StringComparer.OrdinalIgnoreCase))
            Apps.Add(new AppRuleItemViewModel(app, Context, now));
        HasNoRules = Rules.Count == 0;
        HasNoChannels = Channels.Count == 0;
        HasNoApps = Apps.Count == 0;
    }

    /// <summary>Replaces the items only when they changed, so a bound combo box keeps its selection.</summary>
    private static void Replace(ObservableCollection<Option> target, IReadOnlyList<Option> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

/// <summary>Name, role, age and the two modes of one member, saved together.</summary>
public sealed partial class MemberProfileEditor : ObservableObject
{
    private readonly MemberDetailViewModel _owner;

    public MemberProfileEditor(MemberDetailViewModel owner, MemberDto member)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Member = member ?? throw new ArgumentNullException(nameof(member));
        Load(member);
    }

    public static IReadOnlyList<Option> RoleOptions { get; } = Texts.Options("Role_", "child", "teen", "adult");

    public MemberDto Member { get; private set; }

    public string MemberId => Member.Id;

    public IReadOnlyList<Option> Roles => RoleOptions;

    public IReadOnlyList<Option> FilterModeOptions => SettingsViewModel.FilterModes;

    public IReadOnlyList<Option> YouTubeModeOptions => SettingsViewModel.YouTubeModes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private string _displayName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private string _role = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private string _ageText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private string _filterMode = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private string _youTubeMode = "";

    /// <summary>Why the profile cannot be saved, or the service's refusal.</summary>
    [ObservableProperty]
    private string? _message;

    public bool IsDirty =>
        DisplayName != Member.DisplayName || Role != Member.Role || AgeText.Trim() != AgeOf(Member)
        || FilterMode != Member.FilterMode || YouTubeMode != Member.YouTubeMode;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task Save() => _owner.SaveProfileAsync(this);

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert()
    {
        Load(Member);
        Message = null;
    }

    /// <summary>Checks the name (required, not another member's) and the age (empty, or 1–120).</summary>
    internal bool TryValidate(IEnumerable<string> otherNames, out string name, out int? age)
    {
        name = DisplayName.Trim();
        age = null;
        Message = MemberInput.Validate(name, AgeText, otherNames, out age);
        return Message is null;
    }

    /// <summary>After a save: the stored member is what the editor shows, so it is no longer dirty.</summary>
    internal void MarkSaved()
    {
        Member = Member with
        {
            DisplayName = DisplayName.Trim(), Role = Role, FilterMode = FilterMode, YouTubeMode = YouTubeMode,
            Age = int.TryParse(AgeText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var age) ? age : null,
        };
        DisplayName = Member.DisplayName;
        AgeText = AgeOf(Member);
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Keeps the parent's edits but compares them with the member as now stored.</summary>
    internal void Rebase(MemberDto member)
    {
        Member = member;
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    partial void OnDisplayNameChanged(string value) => Message = null;

    partial void OnAgeTextChanged(string value) => Message = null;

    private void Load(MemberDto member)
    {
        DisplayName = member.DisplayName;
        Role = member.Role;
        AgeText = AgeOf(member);
        FilterMode = member.FilterMode;
        YouTubeMode = member.YouTubeMode;
    }

    private static string AgeOf(MemberDto member) => member.Age?.ToString(CultureInfo.InvariantCulture) ?? "";
}

/// <summary>Checks shared by the member page and the setup wizard.</summary>
internal static class MemberInput
{
    public const int MaxAge = 120;

    /// <summary>Null when <paramref name="name"/> and <paramref name="ageText"/> are fine; otherwise why not.</summary>
    public static string? Validate(string name, string? ageText, IEnumerable<string> otherNames, out int? age)
    {
        age = null;
        if (string.IsNullOrWhiteSpace(name)) return Strings.Validation_NameEmpty;
        if (otherNames.Any(n => string.Equals(n.Trim(), name.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            return Texts.Format(Strings.Validation_NameTaken, name.Trim());
        var text = ageText?.Trim() ?? "";
        if (text.Length == 0) return null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > MaxAge)
            return Strings.Validation_Age;
        age = value;
        return null;
    }
}

/// <summary>
/// A member's week: whether hours are restricted, and per day (Sunday first) the windows and the daily limit, edited as
/// text and checked on save.
/// </summary>
public sealed partial class WeekEditor : ObservableObject
{
    public const string DefaultStart = "08:00";
    public const string DefaultEnd = "20:00";

    private readonly MemberDetailViewModel? _owner;
    private bool _setting;
    private string _saved;
    private (bool Restricts, (string Start, string End)[][] Windows, string[] Limits) _savedValues;

    internal WeekEditor(MemberDetailViewModel? owner, string memberId, bool restrictsHours, IReadOnlyList<DayPlan> days)
    {
        _owner = owner;
        MemberId = memberId;
        Days = [.. Enumerable.Range(0, WeeklyPlan.Days).Select(d => new DayEditor(this, d))];
        Set(restrictsHours,
            [.. Enumerable.Range(0, WeeklyPlan.Days).Select(d => days.FirstOrDefault(p => p.Day == d)?.Windows.Select(w => (ClockTime.Format(w.Start), ClockTime.Format(w.End))).ToArray() ?? [])],
            [.. Enumerable.Range(0, WeeklyPlan.Days).Select(d => days.FirstOrDefault(p => p.Day == d)?.LimitMinutes?.ToString(CultureInfo.InvariantCulture) ?? "")]);
        _saved = Snapshot();
        _savedValues = Values();
    }

    public string MemberId { get; }

    public IReadOnlyList<DayEditor> Days { get; }

    [ObservableProperty]
    private bool _restrictsHours;

    /// <summary>Why the week cannot be saved, or the service's refusal.</summary>
    [ObservableProperty]
    private string? _message;

    public bool IsDirty => Snapshot() != _saved;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task Save() => _owner?.SaveWeekAsync(this) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert()
    {
        Set(_savedValues.Restricts, _savedValues.Windows, _savedValues.Limits);
        Message = null;
    }

    /// <summary>Copies one day's hours and limit to every day.</summary>
    [RelayCommand]
    private void CopyToAll(DayEditor? source)
    {
        if (source is null) return;
        var windows = source.Windows.Select(w => (w.Start, w.End)).ToArray();
        foreach (var day in Days.Where(d => d != source))
        {
            day.SetWindows(windows);
            day.LimitText = source.LimitText;
        }
        Changed();
    }

    /// <summary>The days as plans, or false with <see cref="Message"/> saying which day is wrong and why.</summary>
    internal bool TryBuild(out List<DayPlan> days)
    {
        days = [];
        var plans = new List<DayPlan>();
        foreach (var day in Days)
        {
            var windows = new List<(int, int)>();
            if (RestrictsHours)
            {
                foreach (var window in day.Windows)
                {
                    if (!ClockTime.TryParse(window.Start, allowEndOfDay: false, out var start) || !ClockTime.TryParse(window.End, allowEndOfDay: true, out var end))
                        return Fail(Texts.Format(Strings.Validation_TimeFormat, day.Name));
                    if (start >= end) return Fail(Texts.Format(Strings.Validation_TimeOrder, day.Name, window.Start.Trim(), window.End.Trim()));
                    windows.Add((start, end));
                }
            }
            int? limit = null;
            var text = day.LimitText.Trim();
            if (text.Length > 0)
            {
                if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes > ClockTime.EndOfDay)
                    return Fail(Texts.Format(Strings.Validation_Limit, day.Name));
                limit = minutes;
            }
            plans.Add(new DayPlan(day.Day, windows, limit));
        }
        Message = null;
        days = plans;
        return true;
    }

    private bool Fail(string message)
    {
        Message = message;
        return false;
    }

    internal void MarkSaved()
    {
        _saved = Snapshot();
        _savedValues = Values();
        Changed();
    }

    /// <summary>Called by the days and windows on every edit.</summary>
    internal void Changed()
    {
        Message = null;
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
        foreach (var day in Days) day.Refresh();
    }

    partial void OnRestrictsHoursChanged(bool value)
    {
        if (_setting) return;
        // Restricting hours with no hours on any day would allow none at all: start from a sensible window instead.
        if (value && Days.All(d => d.Windows.Count == 0))
            foreach (var day in Days) day.SetWindows([(DefaultStart, DefaultEnd)]);
        Changed();
    }

    private void Set(bool restricts, (string Start, string End)[][] windows, string[] limits)
    {
        _setting = true;
        RestrictsHours = restricts;
        _setting = false;
        for (var d = 0; d < WeeklyPlan.Days; d++)
        {
            Days[d].SetWindows(windows[d]);
            Days[d].LimitText = limits[d];
        }
        Changed();
    }

    private (bool, (string, string)[][], string[]) Values() =>
        (RestrictsHours, [.. Days.Select(d => d.Windows.Select(w => (w.Start, w.End)).ToArray())], [.. Days.Select(d => d.LimitText)]);

    private string Snapshot() =>
        string.Join("|", Days.Select(d => string.Join(",", d.Windows.Select(w => $"{w.Start.Trim()}-{w.End.Trim()}")) + "/" + d.LimitText.Trim()))
        + (RestrictsHours ? "|R" : "|-");
}

public sealed partial class DayEditor : ObservableObject
{
    private readonly WeekEditor _week;
    private bool _setting;

    public DayEditor(WeekEditor week, int day)
    {
        _week = week;
        Day = day;
        Name = CultureInfo.CurrentCulture.DateTimeFormat.DayNames[day];
    }

    public int Day { get; }

    public string Name { get; }

    public ObservableCollection<WindowEditor> Windows { get; } = [];

    [ObservableProperty]
    private string _limitText = "";

    /// <summary>Hours are restricted and this day has no window: no browsing at all that day (the view says so).</summary>
    public bool IsClosed => _week.RestrictsHours && Windows.Count == 0;

    /// <summary>The windows apply (hours are restricted); otherwise the day shows "any hour".</summary>
    public bool HoursEnabled => _week.RestrictsHours;

    public bool HasNoLimit => LimitText.Trim().Length == 0;

    [RelayCommand]
    private void AddWindow()
    {
        var last = Windows.LastOrDefault();
        Windows.Add(new WindowEditor(this, last is null ? WeekEditor.DefaultStart : last.End, last is null ? WeekEditor.DefaultEnd : "22:00"));
        _week.Changed();
    }

    [RelayCommand]
    private void RemoveWindow(WindowEditor? window)
    {
        if (window is null || !Windows.Remove(window)) return;
        _week.Changed();
    }

    internal void SetWindows(IEnumerable<(string Start, string End)> windows)
    {
        _setting = true;
        Windows.Clear();
        foreach (var (start, end) in windows) Windows.Add(new WindowEditor(this, start, end));
        _setting = false;
    }

    internal void Changed()
    {
        if (!_setting) _week.Changed();
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsClosed));
        OnPropertyChanged(nameof(HoursEnabled));
        OnPropertyChanged(nameof(HasNoLimit));
    }

    partial void OnLimitTextChanged(string value) => Changed();
}

public sealed partial class WindowEditor(DayEditor day, string start, string end) : ObservableObject
{
    [ObservableProperty]
    private string _start = start;

    [ObservableProperty]
    private string _end = end;

    partial void OnStartChanged(string value) => day.Changed();

    partial void OnEndChanged(string value) => day.Changed();
}

/// <summary>The member's choice for each category the family has: as the filter mode says, always allowed, or blocked.</summary>
public sealed partial class CategoriesEditor : ObservableObject
{
    private readonly MemberDetailViewModel? _owner;

    public CategoriesEditor(MemberDetailViewModel? owner, string memberId, FamilyBundle settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _owner = owner;
        MemberId = memberId;
        Items = [.. settings.Categories.OrderBy(c => Texts.Code("Category_", c.Key), StringComparer.CurrentCulture).Select(c => new CategoryChoiceViewModel(this, c.Key, c.Value,
            settings.MemberCategories.LastOrDefault(mc => mc.Member == memberId && string.Equals(mc.Category, c.Key, StringComparison.OrdinalIgnoreCase))?.Action))];
    }

    public string MemberId { get; }

    public IReadOnlyList<CategoryChoiceViewModel> Items { get; }

    public bool HasNoCategories => Items.Count == 0;

    [ObservableProperty]
    private string? _message;

    public bool IsDirty => Items.Any(i => i.IsDirty);

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task Save() => _owner?.SaveCategoriesAsync(this) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert()
    {
        foreach (var item in Items) item.Choice = item.Saved;
        Message = null;
    }

    internal void MarkSaved()
    {
        foreach (var item in Items) item.Saved = item.Choice;
        Changed();
    }

    internal void Changed()
    {
        Message = null;
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }
}

public sealed partial class CategoryChoiceViewModel : ObservableObject
{
    /// <summary>No entry for this member: the category follows the filter mode.</summary>
    public const string Default = "default";

    public static IReadOnlyList<Option> ChoiceOptions { get; } = Texts.Options("CategoryChoice_", Default, "allow", "block");

    private readonly CategoriesEditor _editor;
    private readonly bool _ready;

    public CategoryChoiceViewModel(CategoriesEditor editor, string name, IReadOnlyList<string> domains, string? action)
    {
        _editor = editor;
        Name = name;
        Label = Texts.Code("Category_", name);
        DomainsText = domains.Count <= 4 ? string.Join(", ", domains) : Texts.Format(Strings.Member_CategoryDomains, string.Join(", ", domains.Take(4)), domains.Count - 4);
        Saved = action is "allow" or "block" ? action : Default;
        Choice = Saved;
        _ready = true;
    }

    public string Name { get; }

    public string Label { get; }

    public string DomainsText { get; }

    public IReadOnlyList<Option> Options => ChoiceOptions;

    internal string Saved { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    private string _choice = Default;

    public bool IsDirty => Choice != Saved;

    partial void OnChoiceChanged(string value)
    {
        if (_ready) _editor.Changed();
    }
}

public sealed class ChannelItemViewModel
{
    public ChannelItemViewModel(YouTubeChannelDto channel)
    {
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        Name = channel.Title ?? channel.Handle ?? channel.ChannelId;
        DetailText = Texts.Join(" · ", channel.Title is null ? null : channel.Handle, channel.ChannelId);
        ActionText = Texts.Code("RuleAction_", channel.Action);
    }

    public YouTubeChannelDto Channel { get; }

    public string ChannelId => Channel.ChannelId;

    public string Name { get; }

    /// <summary><see cref="Name"/> as shown: a Latin name ("Khan Kids (sim)") keeps its parentheses in the right-to-left page.</summary>
    public string NameText => Texts.Natural(Name);

    public string DetailText { get; }

    public string ActionText { get; }

    public bool IsAllow => Channel.Action == "allow";
}

public sealed class AppRuleItemViewModel
{
    public AppRuleItemViewModel(AppRuleDto rule, FamilyContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        Rule = rule ?? throw new ArgumentNullException(nameof(rule));
        Name = ShortName(rule);
        MatchText = Texts.Code("AppMatch_", rule.Match);
        ActionText = Texts.Code("RuleAction_", rule.Action);
        IsExpired = rule.ExpiresAt is { } expires && expires <= now;
        ExpiryText = RuleDrafts.ExpiryText(rule.ExpiresAt, context, now);
    }

    public AppRuleDto Rule { get; }

    public string Id => Rule.Id;

    /// <summary>The program's file name, or the Store app's name without its publisher id.</summary>
    public string Name { get; }

    public string Value => Rule.Value;

    public string MatchText { get; }

    public string ActionText { get; }

    public bool IsAllow => Rule.Action == "allow";

    public bool AlwaysAllowed => Rule.AlwaysAllowed;

    public string ExpiryText { get; }

    public bool IsExpired { get; }

    public static string ShortName(AppRuleDto rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Match switch
        {
            "path" => System.IO.Path.GetFileName(rule.Value.TrimEnd('\\', '/')) is { Length: > 0 } file ? file : rule.Value,
            "package" when rule.Value.LastIndexOf('_') is > 0 and var cut => rule.Value[..cut],
            _ => rule.Value,
        };
    }
}
