using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>Family members' filter and YouTube modes (<see cref="SetMember"/>) and the service connection diagnostics.</summary>
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

    protected override async Task LoadAsync()
    {
        try
        {
            // Every settings change bumps the version, so the cached settings are current unless the version moved.
            var status = await Context.RefreshStatusAsync();
            var settings = Context.Settings ?? await Context.RefreshSettingsAsync();
            SettingsVersionText = status.SettingsVersion.ToString(CultureInfo.CurrentCulture);
            DeviceText = status.DeviceId;
            IsHubText = status.IsHub ? Strings.Common_Yes : Strings.Common_No;
            ServiceTimeText = Context.FormatDateTime(status.Now);
            TimeZoneText = settings.TimeZone;

            Members.Clear();
            foreach (var member in settings.Members) Members.Add(new MemberSettingsViewModel(member, this));
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
}
