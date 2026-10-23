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
/// Linking each computer's Windows accounts to family members (MVP-PLAN §4.5, devices screen). The accounts come from
/// the settings: every computer reports its own (an agent with its uploads, the hub by itself, or now with
/// <see cref="ScanAccounts"/>). A standard account nobody answered for shows the new-account card
/// (<see cref="NewAccountViewModel"/>); a linked one a choice of member, saved at once through
/// <see cref="FamilyContext.UpdateSettingsAsync"/>; a guest says so, with a way to change it. Administrators are never
/// filtered: they get no choice of children, only — optionally — which adult they belong to.
/// </summary>
public sealed partial class DevicesViewModel
{
    private readonly Dictionary<string, DeviceAccountsViewModel> _groups = new(StringComparer.Ordinal);

    /// <summary>This computer first, then the paired computers that still get settings, by name.</summary>
    public ObservableCollection<DeviceAccountsViewModel> AccountGroups { get; } = [];

    /// <summary>"Not linked" and the members, for a linked standard account's choice.</summary>
    public ObservableCollection<Option> LinkOptions { get; } = [];

    /// <summary>"Not specified" and the unfiltered members, for an administrator account: whose it is, which changes no filtering.</summary>
    public ObservableCollection<Option> AdminLinkOptions { get; } = [];

    [RelayCommand]
    private async Task ScanAccountsAsync()
    {
        StatusMessage = null;
        string? added = null;
        if (!await RunAsync(async () => added = (await Client.ExecuteAsync(new ScanAccounts())).Detail)) return;
        StatusMessage = added switch
        {
            null or "0" => Strings.Devices_NoNewAccounts,
            "1" => Strings.Devices_NewAccount_One,
            _ => Texts.Format(Strings.Devices_NewAccounts, added),
        };
        await RefreshAsync();
    }

    /// <summary>Saves one account's link; on failure the choice goes back to what is stored.</summary>
    internal async Task LinkAsync(DeviceAccountItemViewModel account, DeviceAccountsViewModel device)
    {
        var member = account.MemberId == SetupWizardViewModel.NotLinked ? null : account.MemberId;
        StatusMessage = null;
        var saved = await RunAsync(() => Context.UpdateSettingsAsync(s => AccountLinks.Link(s, device.DeviceId, account.AccountId, member)));
        if (!saved)
        {
            account.Revert();
            return;
        }
        account.Saved();
        var name = Texts.Ltr(account.Name);
        StatusMessage = member is null
            ? Texts.Format(Strings.Devices_AccountUnlinked, name, device.Title)
            : Texts.Format(Strings.Devices_AccountLinked, name, device.Title, Context.MemberName(member));
        await RefreshAsync();
    }

    /// <summary>
    /// Updates the account lists in place, so the refresh every few seconds neither rebuilds a combo box the parent is
    /// using nor undoes a choice that is being saved.
    /// </summary>
    private void SyncAccounts(FamilyBundle settings, string thisDeviceId, IReadOnlyList<DeviceInfo> paired)
    {
        // Rebuilding the choices clears the combo boxes' selections; they are put back below.
        var choices = _groups.Values.SelectMany(g => g.Accounts).ToDictionary(a => a, a => a.MemberId);
        Replace(LinkOptions, [new Option(SetupWizardViewModel.NotLinked, Strings.Setup_NotLinked), .. settings.Members.Select(m => new Option(m.Id, m.DisplayName))]);
        Replace(AdminLinkOptions, [new Option(SetupWizardViewModel.NotLinked, Strings.Devices_AdminOwnerNone),
            .. settings.Members.Where(m => m.FilterMode == "unrestricted").Select(m => new Option(m.Id, m.DisplayName))]);
        foreach (var (account, choice) in choices) account.Restore(choice);

        var names = paired.Where(d => !d.Revoked).ToDictionary(d => d.Id, FamilyContext.DeviceName, StringComparer.Ordinal);
        var byName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        var shown = settings.Devices
            .Where(d => d.Id == thisDeviceId || names.ContainsKey(d.Id))
            .OrderBy(d => d.Id == thisDeviceId ? 0 : 1).ThenBy(d => names.GetValueOrDefault(d.Id, d.Id), byName)
            .ToList();

        foreach (var gone in _groups.Keys.Where(id => shown.All(d => d.Id != id)).ToList()) _groups.Remove(gone);
        var ordered = new List<DeviceAccountsViewModel>();
        foreach (var device in shown)
        {
            var isThis = device.Id == thisDeviceId;
            if (!_groups.TryGetValue(device.Id, out var group)) _groups[device.Id] = group = new DeviceAccountsViewModel(this, device.Id, isThis);
            group.Name = isThis ? null : names[device.Id];
            group.Title = isThis ? Texts.Format(Strings.Devices_ThisComputer, Texts.Ltr(device.Id)) : Texts.Ltr(names[device.Id])!;
            group.Sync(device.Accounts, settings.Members);
            ordered.Add(group);
        }
        Place(AccountGroups, ordered);
    }

    private static void Replace(ObservableCollection<Option> target, IReadOnlyList<Option> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private static void Place(ObservableCollection<DeviceAccountsViewModel> target, IReadOnlyList<DeviceAccountsViewModel> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var at = target.IndexOf(items[i]);
            if (at < 0) target.Insert(i, items[i]);
            else if (at != i) target.Move(at, i);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }
}

/// <summary>One computer's Windows accounts: standard accounts first, then administrators.</summary>
public sealed partial class DeviceAccountsViewModel(DevicesViewModel owner, string deviceId, bool isThisComputer) : ObservableObject
{
    public string DeviceId { get; } = deviceId;

    /// <summary>The hub computer itself: it can look for new accounts at once.</summary>
    public bool IsThisComputer { get; } = isThisComputer;

    public ObservableCollection<DeviceAccountItemViewModel> Accounts { get; } = [];

    [ObservableProperty] private string _title = "";

    [ObservableProperty] private bool _hasNoAccounts;

    /// <summary>The paired computer's name, for the new-account cards; null for this computer.</summary>
    internal string? Name { get; set; }

    internal DevicesViewModel Owner { get; } = owner;

    internal void Sync(IReadOnlyList<AccountDto> accounts, IReadOnlyList<MemberDto> members)
    {
        var wanted = accounts.OrderBy(a => a.IsAdmin).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var items = new List<DeviceAccountItemViewModel>();
        foreach (var account in wanted)
        {
            var item = Accounts.FirstOrDefault(a => string.Equals(a.AccountId, account.AccountId, StringComparison.OrdinalIgnoreCase))
                       ?? new DeviceAccountItemViewModel(this, account.AccountId);
            item.Update(account, members);
            items.Add(item);
        }
        for (var i = 0; i < items.Count; i++)
        {
            var at = Accounts.IndexOf(items[i]);
            if (at < 0) Accounts.Insert(i, items[i]);
            else if (at != i) Accounts.Move(at, i);
        }
        while (Accounts.Count > items.Count) Accounts.RemoveAt(Accounts.Count - 1);
        HasNoAccounts = Accounts.Count == 0;
    }
}

/// <summary>
/// One Windows account and the parents' answer for it, as stored. A standard account is linked to a member (a choice that
/// saves at once), not answered yet (the new-account card, <see cref="Card"/>), or kept as a guest (said so, with a way
/// to change it). An administrator is never filtered: no choice of children, only — optionally — which adult it belongs
/// to; one linked to a filtered member (older settings) is warned about, with a way to unlink it.
/// </summary>
public sealed partial class DeviceAccountItemViewModel(DeviceAccountsViewModel device, string accountId) : ObservableObject
{
    private bool _quiet;
    private string _stored = SetupWizardViewModel.NotLinked;
    private IReadOnlyList<MemberDto> _members = [];
    private AccountDto? _account;

    public string AccountId { get; } = accountId;

    [ObservableProperty] private string _name = "";

    [ObservableProperty] private bool _isAdmin;

    /// <summary>Stored as a guest: the parents chose to keep it unlinked and blocked.</summary>
    [ObservableProperty] private bool _isGuest;

    /// <summary>The parent reopened a guest's card to give another answer; closing it puts the guest row back.</summary>
    [ObservableProperty] private bool _isChangingGuest;

    [ObservableProperty] private string _memberId = SetupWizardViewModel.NotLinked;

    /// <summary>The choices for <see cref="MemberId"/>: the adults for an administrator, every member for a standard account.</summary>
    [ObservableProperty] private ObservableCollection<Option> _choices = [];

    /// <summary>The choice of member shows: a linked standard account, or an administrator with adults to choose from.</summary>
    [ObservableProperty] private bool _showsChoice;

    /// <summary>The new-account card, for a standard account nobody linked (null otherwise); kept across refreshes.</summary>
    [ObservableProperty] private NewAccountViewModel? _card;

    [ObservableProperty] private bool _showsCard;

    [ObservableProperty] private bool _showsGuest;

    /// <summary>An administrator linked to a filtered member: administrators are never filtered, so the child needs a standard account.</summary>
    [ObservableProperty] private string? _warning;

    /// <summary>Takes the stored account; the parent's choice is kept while it is being saved.</summary>
    internal void Update(AccountDto account, IReadOnlyList<MemberDto> members)
    {
        _account = account;
        _members = members;
        Name = account.Name;
        IsAdmin = account.IsAdmin;
        IsGuest = account.Guest;
        var stored = account.Member is { } member && members.Any(m => m.Id == member) ? member : SetupWizardViewModel.NotLinked;
        if (MemberId == _stored) Restore(stored);
        _stored = stored;
        UpdateState();
    }

    /// <summary>Sets the choice without saving it (a refresh, or putting back a selection the combo box dropped).</summary>
    internal void Restore(string? memberId)
    {
        _quiet = true;
        MemberId = memberId ?? SetupWizardViewModel.NotLinked;
        _quiet = false;
    }

    internal void Revert() => Restore(_stored);

    internal void Saved()
    {
        _stored = MemberId;
        UpdateState();
    }

    [RelayCommand]
    private void ChangeGuest() => IsChangingGuest = true;

    /// <summary>Unlinks an administrator from a filtered member.</summary>
    [RelayCommand]
    private void Unlink() => MemberId = SetupWizardViewModel.NotLinked;

    partial void OnMemberIdChanged(string value)
    {
        // A combo box whose choices are being replaced sets null for a moment: not the parent's choice.
        if (_quiet || value is null) return;
        if (value != _stored) _ = device.Owner.LinkAsync(this, device);
    }

    partial void OnIsChangingGuestChanged(bool value) => UpdateState();

    /// <summary>What the row shows, from the account as stored (not from a choice still being saved).</summary>
    private void UpdateState()
    {
        var linked = _members.FirstOrDefault(m => m.Id == _stored);
        var adminProblem = IsAdmin && linked is { FilterMode: not "unrestricted" };
        Warning = adminProblem ? Texts.Format(Strings.Setup_AdminLinked, Name, linked!.DisplayName) : null;

        // Another list replaces the combo box's items, which drops its selection: put it back.
        var choice = MemberId;
        Choices = IsAdmin && !adminProblem ? device.Owner.AdminLinkOptions : device.Owner.LinkOptions;
        if (MemberId != choice) Restore(choice);
        ShowsChoice = IsAdmin ? !adminProblem && (linked is not null || device.Owner.AdminLinkOptions.Count > 1) : linked is not null;

        var unanswered = !IsAdmin && linked is null;
        if (!unanswered)
        {
            Card = null;
            IsChangingGuest = false;
        }
        else if (_account is { } account)
        {
            Card ??= new NewAccountViewModel(device.Owner, device.DeviceId, AccountId, () => IsChangingGuest = false);
            Card.Update(account, device.Name, _members);
        }
        ShowsCard = unanswered && (!IsGuest || IsChangingGuest);
        ShowsGuest = unanswered && IsGuest && !IsChangingGuest;
    }
}
