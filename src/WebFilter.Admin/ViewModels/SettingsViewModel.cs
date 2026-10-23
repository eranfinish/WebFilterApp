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
/// Family members' filter and YouTube modes (<see cref="SetMember"/>), the family's Windows features for children
/// (PROTECTION-SPEC §2) and baseline protection for the parents and adults (§13), both through
/// <see cref="FamilyContext.UpdateSettingsAsync"/>, this computer's requirement checks and the service connection
/// diagnostics.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    public static readonly IReadOnlyList<Option> FilterModes = Texts.Options("FilterMode_", "unrestricted", "blocklist", "allowlist");

    public static readonly IReadOnlyList<Option> YouTubeModes = Texts.Options("YouTubeMode_", "off", "restricted", "allowlist", "unrestricted");

    public SettingsViewModel(FamilyContext context) : base(context) => PipeName = Client.PipeName;

    public override string Title => Strings.Nav_Settings;

    public override string Intro => Strings.Settings_Intro;

    public ObservableCollection<MemberSettingsViewModel> Members { get; } = [];

    public string PipeName { get; }

    [ObservableProperty] private string _connectionStateText = "";
    [ObservableProperty] private string _settingsVersionText = Strings.Common_None;
    [ObservableProperty] private string _deviceText = Strings.Common_None;
    [ObservableProperty] private string _isHubText = Strings.Common_None;
    [ObservableProperty] private string _serviceTimeText = Strings.Common_None;
    [ObservableProperty] private string _timeZoneText = Strings.Common_None;

    /// <summary>An agent computer: no members to edit here; <see cref="HubText"/> says which hub manages it.</summary>
    [ObservableProperty] private bool _isAgent;

    [ObservableProperty] private string? _hubText;

    /// <summary>The Windows features restricted accounts may use (hub only; null on an agent).</summary>
    [ObservableProperty] private WindowsFeaturesEditor? _windowsFeatures;

    /// <summary>The baseline protection of the parents' own accounts (hub only; null on an agent).</summary>
    [ObservableProperty] private BaselineEditor? _baseline;

    /// <summary>What this computer has and lacks (<see cref="GetDiagnostics"/>).</summary>
    public ObservableCollection<CheckItem> Checks { get; } = [];

    [ObservableProperty] private bool _hasChecks;

    protected override async Task LoadAsync()
    {
        try
        {
            // Every settings change bumps the version, so the cached settings are current unless the version moved.
            var status = await Context.RefreshStatusAsync();
            Checks.Clear();
            foreach (var check in await CheckItem.LoadAsync(Client)) Checks.Add(check);
            HasChecks = Checks.Count > 0;
            SettingsVersionText = status.SettingsVersion.ToString(CultureInfo.CurrentCulture);
            DeviceText = status.DeviceId;
            IsHubText = status.IsHub ? Strings.Common_Yes : Strings.Common_No;
            ServiceTimeText = Context.FormatDateTime(status.Now);
            IsAgent = !status.IsHub;
            Members.Clear();
            if (IsAgent)
            {
                // Members are managed on the hub; the agent's status still names the family's zone and its hub.
                TimeZoneText = status.TimeZone ?? Strings.Common_None;
                HubText = Context.DescribeHub(status.Hub).Text;
                WindowsFeatures = null;
                Baseline = null;
                return;
            }
            var settings = Context.Settings ?? await Context.RefreshSettingsAsync();
            TimeZoneText = settings.TimeZone;
            HubText = null;
            foreach (var member in settings.Members) Members.Add(new MemberSettingsViewModel(member, this));
            // A bundle without the section reads as its defaults; switches the parent is still changing stay as they are.
            var features = settings.WindowsFeatures ?? new WindowsFeaturesDto();
            if (WindowsFeatures is { IsDirty: true } editing) editing.Rebase(features);
            else WindowsFeatures = new WindowsFeaturesEditor(this, features);
            // The same for the baseline (on when the bundle does not say), which also knows whether the adult list is there.
            var baseline = settings.Baseline ?? new BaselineDto();
            var hasAdultList = BaselineEditor.HasAdultList(settings);
            if (Baseline is { IsChanged: true } changing) changing.Rebase(baseline, hasAdultList);
            else Baseline = new BaselineEditor(this, baseline, hasAdultList);
        }
        finally
        {
            // Shown even (especially) when the service cannot be reached.
            ConnectionStateText = Texts.ConnectionState(Client.State);
        }
    }

    /// <summary>Sends only the fields the parent changed.</summary>
    internal Task SaveAsync(MemberSettingsViewModel member) =>
        ActAsync(new SetMember(member.Id,
                FilterMode: member.FilterMode != member.Member.FilterMode ? member.FilterMode : null,
                YouTubeMode: member.YouTubeMode != member.Member.YouTubeMode ? member.YouTubeMode : null),
            Texts.Format(Strings.Done_MemberSaved, member.DisplayName));

    /// <summary>Replaces only the Windows features part of the settings (read again if something changed meanwhile).</summary>
    internal async Task SaveWindowsFeaturesAsync(WindowsFeaturesEditor editor)
    {
        StatusMessage = null;
        var features = editor.ToDto();
        if (!await RunAsync(() => Context.UpdateSettingsAsync(s => s with { WindowsFeatures = features }))) return;
        editor.MarkSaved(features);
        StatusMessage = Strings.Done_WindowsFeaturesSaved;
        await RefreshAsync();
    }

    /// <summary>
    /// Replaces only the baseline part of the settings (read again if something changed meanwhile). With blockAdult on, the
    /// shipped adult list goes into the family's categories when the family has none, so there is something to block; a
    /// family that has its own keeps it. Turned off, the list stays (a child's settings may use it).
    /// </summary>
    internal async Task SaveBaselineAsync(BaselineEditor editor)
    {
        StatusMessage = null;
        var baseline = editor.ToDto();
        var adult = StarterLists.Find(StarterLists.Adult) ?? throw new InvalidOperationException("The adult list is not embedded.");
        if (!await RunAsync(() => Context.UpdateSettingsAsync(s => s with
            {
                Baseline = baseline,
                Categories = baseline.BlockAdult ? StarterLists.AddTo(s.Categories, [adult]) : s.Categories,
            })))
            return;
        editor.MarkSaved(baseline, hasAdultList: baseline.BlockAdult || editor.AdultListPresent);
        StatusMessage = Strings.Done_BaselineSaved;
        await RefreshAsync();
    }
}

/// <summary>
/// The family's baseline protection for the parents and other adults (PROTECTION-SPEC §13): what administrator accounts
/// and unrestricted members get — adult sites blocked, the jump guard, ad blocking and SafeSearch. Saved together. It needs
/// the adult list in the family's categories to block anything: while blockAdult is on and the list is missing, the
/// section says so and can be saved as it is, which adds the list.
/// </summary>
public sealed partial class BaselineEditor : ObservableObject
{
    private readonly SettingsViewModel? _owner;

    internal BaselineEditor(SettingsViewModel? owner, BaselineDto stored, bool hasAdultList)
    {
        _owner = owner;
        Stored = stored ?? throw new ArgumentNullException(nameof(stored));
        AdultListPresent = hasAdultList;
        Load(stored);
    }

    /// <summary>Whether <paramref name="settings"/> has the category the baseline blocks (<see cref="StarterLists.Adult"/>).</summary>
    internal static bool HasAdultList(FamilyBundle settings) =>
        settings.Categories.Keys.Any(k => string.Equals(k, StarterLists.Adult, StringComparison.OrdinalIgnoreCase));

    public BaselineDto Stored { get; private set; }

    /// <summary>The family's categories have the adult list (copied from the shipped one, or its own).</summary>
    public bool AdultListPresent { get; private set; }

    /// <summary>Adult sites (the adult list) are blocked for the parents, unless a parent's own allow rule matches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsDirty), nameof(NeedsAdultList))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _blockAdult;

    /// <summary>A site may not open another site by itself; "open anyway" stays available.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _jumpGuard;

    /// <summary>Requests to known ad servers are blocked in the browser.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _blockAds;

    /// <summary>Google and Bing SafeSearch are forced in Chrome and Edge (per-user browser policies).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChanged), nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _safeSearch;

    /// <summary>The switches differ from what is stored.</summary>
    public bool IsChanged => ToDto() != Stored;

    /// <summary>Adult sites should be blocked, but the family has no adult list yet: a save adds it.</summary>
    public bool NeedsAdultList => BlockAdult && !AdultListPresent;

    /// <summary>There is something to save: a changed switch, or the missing list.</summary>
    public bool IsDirty => IsChanged || NeedsAdultList;

    internal BaselineDto ToDto() => new(BlockAdult, JumpGuard, BlockAds, SafeSearch);

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task Save() => _owner?.SaveBaselineAsync(this) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsChanged))]
    private void Revert() => Load(Stored);

    internal void MarkSaved(BaselineDto saved, bool hasAdultList) => Rebase(saved, hasAdultList);

    /// <summary>Keeps the switches as they are but compares them with <paramref name="stored"/>.</summary>
    internal void Rebase(BaselineDto stored, bool hasAdultList)
    {
        Stored = stored;
        AdultListPresent = hasAdultList;
        OnPropertyChanged(nameof(AdultListPresent));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(NeedsAdultList));
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    private void Load(BaselineDto baseline)
    {
        BlockAdult = baseline.BlockAdult;
        JumpGuard = baseline.JumpGuard;
        BlockAds = baseline.BlockAds;
        SafeSearch = baseline.SafeSearch;
    }
}

/// <summary>
/// The family-wide Windows features for children (PROTECTION-SPEC §2): what restricted and unlinked accounts on every
/// computer may use besides the browsers. Saved together.
/// </summary>
public sealed partial class WindowsFeaturesEditor : ObservableObject
{
    private readonly SettingsViewModel? _owner;

    internal WindowsFeaturesEditor(SettingsViewModel? owner, WindowsFeaturesDto stored)
    {
        _owner = owner;
        Stored = stored ?? throw new ArgumentNullException(nameof(stored));
        Load(stored);
    }

    public WindowsFeaturesDto Stored { get; private set; }

    /// <summary>Per-user Windows services (notifications, settings sync) may use the network, also when time is up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _notificationsAndSync;

    /// <summary>The OneDrive installed for all users may sync, also when time is up.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _oneDrive;

    /// <summary>Bing results in the Start menu's search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private bool _webSearchInStart;

    public bool IsDirty => ToDto() != Stored;

    internal WindowsFeaturesDto ToDto() => new(NotificationsAndSync, OneDrive, WebSearchInStart);

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task Save() => _owner?.SaveWindowsFeaturesAsync(this) ?? Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert() => Load(Stored);

    internal void MarkSaved(WindowsFeaturesDto saved) => Rebase(saved);

    /// <summary>Keeps the switches as they are but compares them with <paramref name="stored"/>.</summary>
    internal void Rebase(WindowsFeaturesDto stored)
    {
        Stored = stored;
        OnPropertyChanged(nameof(IsDirty));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    private void Load(WindowsFeaturesDto features)
    {
        NotificationsAndSync = features.NotificationsAndSync;
        OneDrive = features.OneDrive;
        WebSearchInStart = features.WebSearchInStart;
    }
}

public sealed partial class MemberSettingsViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public MemberSettingsViewModel(MemberDto member, SettingsViewModel owner)
    {
        Member = member ?? throw new ArgumentNullException(nameof(member));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        FilterMode = member.FilterMode;
        YouTubeMode = member.YouTubeMode;
    }

    public MemberDto Member { get; }

    public string Id => Member.Id;

    public string DisplayName => Member.DisplayName;

    public string RoleText => Texts.Code("Role_", Member.Role);

    public string AgeText => Member.Age?.ToString(CultureInfo.CurrentCulture) ?? Strings.Common_None;

    public IReadOnlyList<Option> FilterModeOptions => SettingsViewModel.FilterModes;

    public IReadOnlyList<Option> YouTubeModeOptions => SettingsViewModel.YouTubeModes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private string? _filterMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(RevertCommand))]
    private string? _youTubeMode;

    public bool IsDirty => FilterMode != Member.FilterMode || YouTubeMode != Member.YouTubeMode;

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private Task Save() => _owner.SaveAsync(this);

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Revert()
    {
        FilterMode = Member.FilterMode;
        YouTubeMode = Member.YouTubeMode;
    }

    /// <summary>The member page: hours, limits, sites, channels, apps and categories.</summary>
    [RelayCommand]
    private void OpenDetails() => _owner.Navigator?.ShowMember(Id);
}
