using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.ViewModels;

/// <summary>Pending access requests with approve/deny (MVP-PLAN §8.1), and the most recent decisions.</summary>
public sealed partial class RequestsViewModel : PageViewModel
{
    public const int RecentCount = 20;

    public RequestsViewModel(FamilyContext context) : base(context) => Context.PropertyChanged += OnContextChanged;

    public override string Title => Strings.Nav_Requests;

    public override string Intro => Strings.Requests_Intro;

    public override bool AutoRefresh => true;

    public ObservableCollection<RequestItemViewModel> Pending { get; } = [];

    public ObservableCollection<RequestItemViewModel> Recent { get; } = [];

    [ObservableProperty]
    private bool _hasNoPending;

    [ObservableProperty]
    private bool _hasNoRecent;

    protected override async Task LoadAsync()
    {
        await Context.RefreshStatusAsync();
        var pending = await Context.RefreshPendingRequestsAsync();
        var all = await Client.RequestAsync<RequestsResult>(new GetRequests());

        // Keep existing items (and the scope the parent may have picked) across refreshes.
        var keep = pending.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in Pending.Where(p => !keep.Contains(p.Id)).ToList()) Pending.Remove(gone);
        foreach (var request in pending.OrderBy(r => r.CreatedAt))
            if (Pending.All(p => p.Id != request.Id)) Pending.Add(new RequestItemViewModel(request, Context, this));
        HasNoPending = Pending.Count == 0;

        Recent.Clear();
        foreach (var request in all.Requests.Where(r => r.Status != "pending").OrderByDescending(r => r.DecidedAt ?? r.CreatedAt).Take(RecentCount))
            Recent.Add(new RequestItemViewModel(request, Context, owner: null));
        HasNoRecent = Recent.Count == 0;
    }

    internal Task ApproveAsync(RequestItemViewModel item, string choice) =>
        ActAsync(item.BuildApproval(choice), Texts.Format(Strings.Done_Approved, item.MemberName));

    internal Task DenyAsync(RequestItemViewModel item) =>
        ActAsync(item.BuildDenial(), Texts.Format(Strings.Done_Denied, item.MemberName));

    private void OnContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FamilyContext.PendingRequestCount))
            Badge = Context.PendingRequestCount > 0 ? Context.PendingRequestCount.ToString(CultureInfo.CurrentCulture) : null;
    }
}

/// <summary>One way to approve a request, shown as its own button ("Allow for 15 minutes", "Always allow", …).</summary>
public sealed record ApprovalChoice(string Label, string Value, ICommand Command);

public sealed partial class RequestItemViewModel : ObservableObject
{
    public const string Host = "host";
    public const string Page = "page";
    public const string Today = "today";
    public const string Always = "always";

    public RequestItemViewModel(RequestDto request, FamilyContext context, RequestsViewModel? owner)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        Request = request;
        MemberName = context.MemberName(request.MemberId);
        KindText = Texts.Code("RequestKind_", request.Kind);
        TargetText = request.Kind == "time"
            ? Texts.Format(Strings.Request_TimeTarget, Texts.Minutes(request.Minutes ?? 15))
            : request.Target;
        MessageText = string.IsNullOrWhiteSpace(request.Message) ? null : Texts.Format(Strings.Request_Message, request.Message.Trim());
        SentText = Texts.Format(Strings.Request_SentAt, context.FormatDateTime(request.CreatedAt));
        StatusText = request.DecidedAt is { } decided
            ? Texts.Format(Strings.Request_Decided, Texts.Code("RequestStatus_", request.Status), context.FormatDateTime(decided))
            : Texts.Code("RequestStatus_", request.Status);
        IsApproved = request.Status == "approved";
        ScopeOptions = [new Option(Host, Texts.Format(Strings.Request_ScopeSite, Texts.Ltr(request.TargetKey))), new Option(Page, Strings.Request_ScopePage)];

        var approve = new AsyncRelayCommand<string>(choice => owner?.ApproveAsync(this, choice!) ?? Task.CompletedTask);
        DenyCommand = new AsyncRelayCommand(() => owner?.DenyAsync(this) ?? Task.CompletedTask);
        Choices = owner is null ? [] : [.. ChoicesFor(request).Select(c => new ApprovalChoice(c.Label, c.Value, approve))];
    }

    public RequestDto Request { get; }

    public string Id => Request.Id;

    public string Kind => Request.Kind;

    public bool IsUrl => Request.Kind == "url";

    public string MemberName { get; }

    public string KindText { get; }

    public string TargetText { get; }

    public string? MessageText { get; }

    public string SentText { get; }

    /// <summary>For decided requests: "approved at …" / "denied at …".</summary>
    public string StatusText { get; }

    public bool IsApproved { get; }

    /// <summary>url requests: allow the whole site (host rule, the default) or only this page (prefix rule).</summary>
    public IReadOnlyList<Option> ScopeOptions { get; }

    [ObservableProperty]
    private string _selectedScope = Host;

    public IReadOnlyList<ApprovalChoice> Choices { get; }

    public ICommand DenyCommand { get; }

    /// <summary>
    /// The <see cref="DecideRequest"/> for an approval <paramref name="choice"/>: a number of minutes, "today" or "always".
    /// Time requests always carry minutes (what the parent grants, possibly less than asked).
    /// </summary>
    public DecideRequest BuildApproval(string choice)
    {
        ArgumentException.ThrowIfNullOrEmpty(choice);
        int? minutes = int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m > 0 ? m : null;
        if (minutes is null && choice is not (Today or Always))
            throw new ArgumentException($"Unknown approval choice '{choice}'.", nameof(choice));
        var duration = minutes is null ? choice : null;
        return Request.Kind switch
        {
            "time" => new DecideRequest(Id, "approve", Minutes: minutes ?? Request.Minutes ?? 15),
            "url" => new DecideRequest(Id, "approve", Scope: SelectedScope, Duration: duration, Minutes: minutes),
            // The service adds an approved channel without an expiry, so a channel approval is always permanent.
            "youtube" => new DecideRequest(Id, "approve", Duration: Always),
            _ => new DecideRequest(Id, "approve", Duration: duration, Minutes: minutes),
        };
    }

    public DecideRequest BuildDenial() => new(Id, "deny");

    private static IEnumerable<(string Label, string Value)> ChoicesFor(RequestDto request)
    {
        switch (request.Kind)
        {
            case "time":
                // 15 and 30 are what the child can ask for (§8.1); the parent may grant less than asked, never forced to grant more.
                foreach (var minutes in new[] { 15, 30, request.Minutes ?? 15 }.Where(m => m > 0).Distinct().Order())
                    yield return (Texts.Format(Strings.Approve_Time, Texts.Minutes(minutes)), minutes.ToString(CultureInfo.InvariantCulture));
                break;
            case "youtube":
                yield return (Strings.Approve_Channel, Always);
                break;
            case "url":
            case "app":
                yield return (Texts.Format(Strings.Approve_Minutes, Texts.Minutes(15)), "15");
                yield return (Strings.Approve_Today, Today);
                yield return (Strings.Approve_Always, Always);
                break;
        }
    }
}
