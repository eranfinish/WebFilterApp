using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>The settings changes behind a Windows account's answer: whom it belongs to, or that it stays a guest.</summary>
internal static class AccountLinks
{
    /// <summary>A standard account nobody answered for: not linked and not kept as a guest. Browsing there is blocked meanwhile.</summary>
    public static bool IsNew(AccountDto account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.Member is null && !account.IsAdmin && !account.Guest;
    }

    /// <summary>Links the account to <paramref name="memberId"/>, or unlinks it (null); either way it is no longer a guest.</summary>
    public static FamilyBundle Link(FamilyBundle settings, string deviceId, string accountId, string? memberId) =>
        Change(settings, deviceId, accountId, a => a with { Member = memberId, Guest = false });

    /// <summary>The parents' answer "keep it blocked": unlinked, as a guest (restricted like every unlinked account).</summary>
    public static FamilyBundle KeepAsGuest(FamilyBundle settings, string deviceId, string accountId) =>
        Change(settings, deviceId, accountId, a => a with { Member = null, Guest = true });

    private static FamilyBundle Change(FamilyBundle settings, string deviceId, string accountId, Func<AccountDto, AccountDto> change)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with
        {
            Devices = [.. settings.Devices.Select(d => d.Id != deviceId ? d : d with
            {
                Accounts = [.. d.Accounts.Select(a => string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase) ? change(a) : a)],
            })],
        };
    }
}

/// <summary>Which of a new account's answers the parent opened.</summary>
public enum NewAccountAnswer
{
    None,
    Existing,
    NewChild,
    Adult,
}

/// <summary>
/// "A new Windows account was found": a standard account on some computer that nobody answered for, so browsing there is
/// blocked. Windows accounts are made in Windows and found by WebFilter; family members are made here. The parent answers
/// with one of four: an existing family member; a new child or teen, set up from the role's template as the setup wizard
/// does (<see cref="MemberTemplate.AddMember"/>); an adult without filtering, existing or new; or keeping it blocked as a
/// guest. Each answer is one settings save through the page that shows the card (the dashboard or the devices page),
/// which then reloads. A reload updates the card in place, so what the parent is typing stays.
/// </summary>
public sealed partial class NewAccountViewModel : ObservableObject
{
    /// <summary>The "new adult" choice among the adults (member ids are never empty).</summary>
    public const string NewAdult = "";

    private readonly PageViewModel _owner;
    private readonly Action? _close;
    private IReadOnlyList<MemberDto> _members = [];
    private bool _saving;

    /// <param name="close">On the devices page: puts a reopened guest's row back (<see cref="CanClose"/>). Null on the dashboard.</param>
    internal NewAccountViewModel(PageViewModel owner, string deviceId, string accountId, Action? close = null)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        DeviceId = deviceId;
        AccountId = accountId;
        _close = close;
    }

    public string DeviceId { get; }

    public string AccountId { get; }

    public static IReadOnlyList<Option> ChildRoleOptions { get; } = Texts.Options("Role_", "child", "teen");

    [ObservableProperty] private string _accountName = "";

    /// <summary>"New Windows account found: yinon (on LAB-KIDS)".</summary>
    [ObservableProperty] private string _title = "";

    /// <summary>The computer as the status messages name it.</summary>
    internal string DeviceText { get; private set; } = "";

    /// <summary>Already kept as a guest (the parent reopened it to change that): "keep blocked" then only closes the card.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanClose))]
    private bool _isGuest;

    /// <summary>A guest's card, reopened from its row on the devices page: it can be closed again without an answer.</summary>
    public bool CanClose => _close is not null && IsGuest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosingExisting), nameof(IsAddingChild), nameof(IsChoosingAdult))]
    private NewAccountAnswer _answer;

    public bool IsChoosingExisting => Answer == NewAccountAnswer.Existing;

    public bool IsAddingChild => Answer == NewAccountAnswer.NewChild;

    public bool IsChoosingAdult => Answer == NewAccountAnswer.Adult;

    // ---- an existing member ---------------------------------------------------------------------------------------

    /// <summary>Every family member: children, teens and adults.</summary>
    public ObservableCollection<Option> MemberOptions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LinkExistingCommand))]
    private string? _existingMemberId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseExistingCommand))]
    private bool _hasMembers;

    // ---- a new child or teen --------------------------------------------------------------------------------------

    [ObservableProperty] private string _childName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChildTemplateText))]
    private string _childRole = "child";

    [ObservableProperty] private string _childAgeText = "";

    /// <summary>What the chosen role starts with, as the setup wizard would set it.</summary>
    public string ChildTemplateText => MemberTemplate.For(ChildRole).Text();

    // ---- an adult -------------------------------------------------------------------------------------------------

    /// <summary>The unfiltered members, then "a new adult".</summary>
    public ObservableCollection<Option> AdultOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNewAdult), nameof(AdultActionText))]
    private string _adultChoice = NewAdult;

    [ObservableProperty] private string _adultName = "";

    /// <summary>There are unfiltered members to choose from (otherwise only the new adult's name is asked).</summary>
    [ObservableProperty] private bool _hasAdults;

    public bool IsNewAdult => AdultChoice == NewAdult;

    public string AdultActionText => IsNewAdult ? Strings.NewAccount_AddAndLink : Strings.NewAccount_Link;

    /// <summary>Why the answer cannot be saved (a name, an age), or the service's refusal.</summary>
    [ObservableProperty] private string? _message;

    /// <summary>Takes the account as stored now; the open answer and what the parent typed are kept.</summary>
    /// <param name="deviceName">The paired computer's name; null for this computer.</param>
    internal void Update(AccountDto account, string? deviceName, IReadOnlyList<MemberDto> members)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(members);
        var name = Texts.Ltr(account.Name)!;
        if (AccountName.Length == 0)
        {
            // The new member's name starts as the account's; the parent can type the child's real name instead.
            ChildName = account.Name;
            AdultName = account.Name;
        }
        AccountName = account.Name;
        IsGuest = account.Guest;
        Title = deviceName is null
            ? Texts.Format(Strings.NewAccount_TitleThisComputer, name)
            : Texts.Format(Strings.NewAccount_Title, name, Texts.Ltr(deviceName));
        DeviceText = deviceName is null ? Texts.Format(Strings.Devices_ThisComputer, Texts.Ltr(DeviceId)) : Texts.Ltr(deviceName)!;

        _members = members;
        var existing = ExistingMemberId;
        Replace(MemberOptions, [.. members.Select(m => new Option(m.Id, m.DisplayName))]);
        ExistingMemberId = MemberOptions.Any(o => o.Value == existing) ? existing : null;
        HasMembers = MemberOptions.Count > 0;

        var adult = AdultChoice;
        var adults = members.Where(m => m.FilterMode == "unrestricted").ToList();
        Replace(AdultOptions, [.. adults.Select(m => new Option(m.Id, m.DisplayName)), new Option(NewAdult, Strings.NewAccount_NewAdult)]);
        HasAdults = adults.Count > 0;
        AdultChoice = AdultOptions.Any(o => o.Value == adult) ? adult : NewAdult;
    }

    [RelayCommand(CanExecute = nameof(HasMembers))]
    private void ChooseExisting() => Open(NewAccountAnswer.Existing);

    [RelayCommand]
    private void ChooseNewChild() => Open(NewAccountAnswer.NewChild);

    [RelayCommand]
    private void ChooseAdult() => Open(NewAccountAnswer.Adult);

    [RelayCommand]
    private void CancelAnswer() => Open(NewAccountAnswer.None);

    [RelayCommand]
    private void Close() => _close?.Invoke();

    [RelayCommand(CanExecute = nameof(CanLinkExisting))]
    private Task LinkExistingAsync()
    {
        if (ExistingMemberId is not { } memberId) return Task.CompletedTask;
        var name = _members.FirstOrDefault(m => m.Id == memberId)?.DisplayName ?? memberId;
        return SaveAsync(s => AccountLinks.Link(s, DeviceId, AccountId, memberId), Texts.Format(Strings.Devices_AccountLinked, Texts.Ltr(AccountName), DeviceText, name));
    }

    private bool CanLinkExisting() => ExistingMemberId is not null;

    /// <summary>A new child or teen from the role's template (hours, limit, lists, protections), and the account linked to them.</summary>
    [RelayCommand]
    private Task AddChildAsync()
    {
        if (MemberInput.Validate(ChildName, ChildAgeText, _members.Select(m => m.DisplayName), out var age) is { } problem)
        {
            Message = problem;
            return Task.CompletedTask;
        }
        return AddMemberAsync(ChildName.Trim(), ChildRole is "teen" ? "teen" : "child", age);
    }

    /// <summary>The chosen adult, or a new one without filtering, and the account linked to them.</summary>
    [RelayCommand]
    private Task LinkAdultAsync()
    {
        if (!IsNewAdult)
        {
            var adult = AdultChoice;
            var name = _members.FirstOrDefault(m => m.Id == adult)?.DisplayName ?? adult;
            return SaveAsync(s => AccountLinks.Link(s, DeviceId, AccountId, adult), Texts.Format(Strings.Devices_AccountLinked, Texts.Ltr(AccountName), DeviceText, name));
        }
        if (MemberInput.Validate(AdultName, null, _members.Select(m => m.DisplayName), out _) is { } problem)
        {
            Message = problem;
            return Task.CompletedTask;
        }
        return AddMemberAsync(AdultName.Trim(), "adult", null);
    }

    [RelayCommand]
    private Task KeepAsGuestAsync()
    {
        if (IsGuest)
        {
            Close();
            return Task.CompletedTask;
        }
        return SaveAsync(s => AccountLinks.KeepAsGuest(s, DeviceId, AccountId), Texts.Format(Strings.NewAccount_KeptAsGuest, Texts.Ltr(AccountName), DeviceText));
    }

    partial void OnChildNameChanged(string value) => Message = null;

    partial void OnChildAgeTextChanged(string value) => Message = null;

    partial void OnAdultNameChanged(string value) => Message = null;

    partial void OnAdultChoiceChanged(string value) => Message = null;

    private Task AddMemberAsync(string name, string role, int? age)
    {
        // One id for every attempt: a save that meets a newer version is applied again to it (FamilyContext.UpdateSettingsAsync).
        var id = _owner.Context.NewMemberId();
        return SaveAsync(s => AccountLinks.Link(MemberTemplate.AddMember(s, id, name, role, age), DeviceId, AccountId, id),
            Texts.Format(Strings.NewAccount_MemberAdded, name, Texts.Ltr(AccountName), DeviceText));
    }

    private async Task SaveAsync(Func<FamilyBundle, FamilyBundle> change, string done)
    {
        if (_saving) return;
        _saving = true;
        try
        {
            await _owner.SaveSettingsAsync(change, done, m => Message = m);
        }
        finally
        {
            _saving = false;
        }
    }

    private void Open(NewAccountAnswer answer)
    {
        Answer = answer;
        Message = null;
    }

    /// <summary>Replaces the items only when they changed, so a bound combo box keeps its selection.</summary>
    private static void Replace(ObservableCollection<Option> target, IReadOnlyList<Option> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}
