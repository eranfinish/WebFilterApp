using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>Global and per-member URL rules from <see cref="GetSettings"/>, with add (<see cref="AddRule"/>) and remove (<see cref="RemoveRule"/>).</summary>
public sealed partial class RulesViewModel : PageViewModel
{
    /// <summary>Filter value: every rule.</summary>
    public const string AllRules = "*";

    /// <summary>Filter / "applies to" value: rules for the whole family (<see cref="RuleDto.Member"/> = null).</summary>
    public const string Global = "";

    private readonly IDialogService _dialogs;
    private List<RuleItemViewModel> _all = [];

    public RulesViewModel(FamilyContext context, IDialogService dialogs) : base(context) =>
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));

    public override string Title => Strings.Nav_Rules;

    public override string Intro => Strings.Rules_Intro;

    /// <summary>Creates ids for new rules; the service stores the id it is given.</summary>
    internal Func<string> NewRuleId { get; set; } = () => $"r-{Guid.NewGuid():N}"[..14];

    public ObservableCollection<RuleItemViewModel> Rules { get; } = [];

    public ObservableCollection<Option> FilterOptions { get; } = [];

    public ObservableCollection<Option> AppliesToOptions { get; } = [];

    public IReadOnlyList<Option> TypeOptions { get; } = Texts.Options("RuleType_", RulePatternValidator.Types);

    public IReadOnlyList<Option> ActionOptions { get; } = Texts.Options("RuleAction_", "block", "allow");

    [ObservableProperty]
    private string? _selectedFilter = AllRules;

    [ObservableProperty]
    private bool _hasNoRules;

    [ObservableProperty]
    private string _newPattern = "";

    [ObservableProperty]
    private string _newType = "domain";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeAlwaysAllowed))]
    private string _newAction = "block";

    [ObservableProperty]
    private string? _newAppliesTo = Global;

    [ObservableProperty]
    private bool _newAlwaysAllowed;

    [ObservableProperty]
    private string? _validationMessage;

    /// <summary>"Always allowed" (even when time is up) only makes sense for an allow rule.</summary>
    public bool CanBeAlwaysAllowed => NewAction == "allow";

    protected override async Task LoadAsync()
    {
        await Context.RefreshStatusAsync();
        var settings = Context.Settings ?? await Context.RefreshSettingsAsync();
        var now = Context.ServiceNow;

        var members = settings.Members.Select(m => new Option(m.Id, m.DisplayName)).ToList();
        Replace(FilterOptions, [new Option(AllRules, Strings.Common_All), new Option(Global, Strings.Rules_FilterGlobal), .. members]);
        Replace(AppliesToOptions, [new Option(Global, Strings.Common_WholeFamily), .. members]);
        if (FilterOptions.All(o => o.Value != SelectedFilter)) SelectedFilter = AllRules;
        if (AppliesToOptions.All(o => o.Value != NewAppliesTo)) NewAppliesTo = Global;

        _all = [.. settings.Rules.Select(r => new RuleItemViewModel(r, Context, now))];
        ApplyFilter();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        ValidationMessage = null;
        StatusMessage = null;
        if (!RulePatternValidator.TryNormalize(NewPattern, NewType, out var pattern, out var error))
        {
            ValidationMessage = error;
            return;
        }
        var member = string.IsNullOrEmpty(NewAppliesTo) ? null : NewAppliesTo;
        if (_all.Any(r => r.Rule.Member == member && r.Rule.Type == NewType && r.Rule.Action == NewAction
                          && string.Equals(r.Rule.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            ValidationMessage = Strings.Validation_Duplicate;
            return;
        }

        var rule = new RuleDto(NewRuleId(), member, pattern, NewType, NewAction, AlwaysAllowed: NewAction == "allow" && NewAlwaysAllowed);
        if (await ActAsync(new AddRule(rule), Texts.Format(Strings.Done_RuleAdded, Texts.Ltr(pattern))))
        {
            NewPattern = "";
            NewAlwaysAllowed = false;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(RuleItemViewModel? rule)
    {
        if (rule is null) return;
        if (!_dialogs.Confirm(Texts.Format(Strings.Rules_RemoveConfirm, Texts.Ltr(rule.Pattern)), Strings.Rules_RemoveTitle)) return;
        await ActAsync(new RemoveRule(rule.Id), Texts.Format(Strings.Done_RuleRemoved, Texts.Ltr(rule.Pattern)));
    }

    partial void OnSelectedFilterChanged(string? value) => ApplyFilter();

    partial void OnNewPatternChanged(string value) => ValidationMessage = null;

    partial void OnNewTypeChanged(string value) => ValidationMessage = null;

    partial void OnNewActionChanged(string value)
    {
        if (value != "allow") NewAlwaysAllowed = false;
    }

    private void ApplyFilter()
    {
        var filter = SelectedFilter ?? AllRules;
        Rules.Clear();
        foreach (var rule in _all.Where(r => filter == AllRules || (r.Rule.Member ?? Global) == filter)
                     .OrderBy(r => r.Rule.Member is null ? 0 : 1).ThenBy(r => r.AppliesToText, StringComparer.CurrentCulture)
                     .ThenBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase))
            Rules.Add(rule);
        HasNoRules = Rules.Count == 0;
    }

    /// <summary>Replaces the items only when they changed, so a bound combo box keeps its selection.</summary>
    private static void Replace(ObservableCollection<Option> target, IReadOnlyList<Option> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }
}

public sealed class RuleItemViewModel
{
    public RuleItemViewModel(RuleDto rule, FamilyContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(context);
        Rule = rule;
        TypeText = Texts.Code("RuleType_", rule.Type);
        ActionText = Texts.Code("RuleAction_", rule.Action);
        AppliesToText = context.MemberName(rule.Member);
        IsExpired = rule.ExpiresAt is { } expires && expires <= now;
        ExpiryText = rule.ExpiresAt switch
        {
            null => Strings.Rule_NoExpiry,
            _ when IsExpired => Strings.Rule_Expired,
            { } expiresAt => Texts.Format(Strings.Rule_ExpiresAt, context.FormatUntil(expiresAt)),
        };
    }

    public RuleDto Rule { get; }

    public string Id => Rule.Id;

    public string Pattern => Rule.Pattern;

    public bool IsAllow => Rule.Action == "allow";

    public bool AlwaysAllowed => Rule.AlwaysAllowed;

    public string TypeText { get; }

    public string ActionText { get; }

    public string AppliesToText { get; }

    public string ExpiryText { get; }

    public bool IsExpired { get; }
}
