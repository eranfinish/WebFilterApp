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
/// <see cref="ScanAccounts"/>). A choice is saved at once through <see cref="FamilyContext.UpdateSettingsAsync"/>.
/// </summary>
public sealed partial class DevicesViewModel
{
    private readonly Dictionary<string, DeviceAccountsViewModel> _groups = new(StringComparer.Ordinal);

    /// <summary>This computer first, then the paired computers that still get settings, by name.</summary>
    public ObservableCollection<DeviceAccountsViewModel> AccountGroups { get; } = [];

    /// <summary>"Not linked" and the members, for every account's choice.</summary>
    public ObservableCollection<Option> LinkOptions { get; } = [];

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
        var saved = await RunAsync(() => Context.UpdateSettingsAsync(s => s with
        {
            Devices = [.. s.Devices.Select(d => d.Id != device.DeviceId ? d : d with
            {
                Accounts = [.. d.Accounts.Select(a => string.Equals(a.AccountId, account.AccountId, StringComparison.OrdinalIgnoreCase) ? a with { Member = member } : a)],
            })],
        }));
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
        foreach (var (account, choice) in choices) account.Restore(choice);

        var names = paired.Where(d => !d.Revoked).ToDictionary(d => d.Id, d => string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name.Trim(), StringComparer.Ordinal);
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

/// <summary>One Windows account and the member it is linked to; choosing another member saves at once.</summary>
public sealed partial class DeviceAccountItemViewModel(DeviceAccountsViewModel device, string accountId) : ObservableObject
{
    private bool _quiet;
    private string _stored = SetupWizardViewModel.NotLinked;
    private IReadOnlyList<MemberDto> _members = [];

    public string AccountId { get; } = accountId;

    public ObservableCollection<Option> Options => device.Owner.LinkOptions;

    [ObservableProperty] private string _name = "";

    [ObservableProperty] private bool _isAdmin;

    [ObservableProperty] private string _memberId = SetupWizardViewModel.NotLinked;

    /// <summary>Why this account needs attention: a standard account no one linked (browsing blocked), or an administrator linked to a filtered member.</summary>
    [ObservableProperty] private string? _warning;

    /// <summary>Takes the stored account; the parent's choice is kept while it is being saved.</summary>
    internal void Update(AccountDto account, IReadOnlyList<MemberDto> members)
    {
        _members = members;
        Name = account.Name;
        IsAdmin = account.IsAdmin;
        var stored = account.Member is { } member && members.Any(m => m.Id == member) ? member : SetupWizardViewModel.NotLinked;
        if (MemberId == _stored) Restore(stored);
        _stored = stored;
        UpdateWarning();
    }

    /// <summary>Sets the choice without saving it (a refresh, or putting back a selection the combo box dropped).</summary>
    internal void Restore(string? memberId)
    {
        _quiet = true;
        MemberId = memberId ?? SetupWizardViewModel.NotLinked;
        _quiet = false;
        UpdateWarning();
    }

    internal void Revert() => Restore(_stored);

    internal void Saved() => _stored = MemberId;

    partial void OnMemberIdChanged(string value)
    {
        // A combo box whose choices are being replaced sets null for a moment: not the parent's choice.
        if (_quiet || value is null) return;
        UpdateWarning();
        if (value != _stored) _ = device.Owner.LinkAsync(this, device);
    }

    private void UpdateWarning()
    {
        var member = _members.FirstOrDefault(m => m.Id == MemberId);
        Warning = member is null
            ? (IsAdmin ? null : Strings.Devices_UnlinkedWarning)
            : IsAdmin && member.FilterMode != "unrestricted" ? Texts.Format(Strings.Setup_AdminLinked, Name, member.DisplayName) : null;
    }
}
