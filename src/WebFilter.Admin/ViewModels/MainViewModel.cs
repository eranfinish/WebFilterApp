using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// The main window: navigation between the pages (it opens on the dashboard, or on the page <c>--page</c> names), the
/// connection banner, and the periodic refresh (every <see cref="RefreshInterval"/> the shown page reloads if it
/// auto-refreshes; otherwise only the pending-request count does). A failed refresh is retried on the next tick, which is also how the app reconnects after the service restarts.
/// While the hub's family is empty the setup wizard joins the navigation and opens once by itself. On an agent computer
/// only the dashboard (status and the local emergency pause) and the settings page are shown (MVP-PLAN §4.5).
/// </summary>
public sealed partial class MainViewModel : ObservableObject, INavigator, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IUiTimer _timer;
    private readonly IDialogService _dialogs;
    private readonly SynchronizationContext? _ui;
    private readonly IReadOnlyList<PageViewModel> _hubPages;
    private IDisposable? _ticker;
    private bool _setupOpened;
    private int _ticks;
    private bool _networkChecked;
    private string _publicNetworks = "";

    public MainViewModel(IAdminClient client, IUiTimer timer, IDialogService dialogs, IClipboardService clipboard, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _ui = SynchronizationContext.Current;
        Context = new FamilyContext(client, time);
        Dashboard = new DashboardViewModel(Context);
        Requests = new RequestsViewModel(Context);
        MemberDetail = new MemberDetailViewModel(Context, dialogs);
        Rules = new RulesViewModel(Context, dialogs);
        Activity = new ActivityViewModel(Context);
        Devices = new DevicesViewModel(Context, dialogs, clipboard, timer);
        Settings = new SettingsViewModel(Context);
        Setup = new SetupWizardViewModel(Context);
        _hubPages = [Dashboard, Requests, MemberDetail, Rules, Activity, Devices, Settings];
        Pages = [.. _hubPages];
        foreach (var page in Pages.Append(Setup)) page.Navigator = this;
        client.StateChanged += OnClientStateChanged;
        UpdateConnection();
    }

    public FamilyContext Context { get; }

    public DashboardViewModel Dashboard { get; }
    public RequestsViewModel Requests { get; }
    public MemberDetailViewModel MemberDetail { get; }
    public RulesViewModel Rules { get; }
    public ActivityViewModel Activity { get; }
    public DevicesViewModel Devices { get; }
    public SettingsViewModel Settings { get; }

    /// <summary>The first-time setup wizard; in <see cref="Pages"/> only while the hub's family is empty.</summary>
    public SetupWizardViewModel Setup { get; }

    /// <summary>The navigation list.</summary>
    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty]
    private PageViewModel? _selectedPage;

    [ObservableProperty]
    private string _connectionText = "";

    /// <summary>A few words for the status box under the navigation (the banner carries the full explanation).</summary>
    [ObservableProperty]
    private string _connectionShortText = "";

    /// <summary>True when the service cannot be used (not running, not elevated, connection lost): the banner shows.</summary>
    [ObservableProperty]
    private bool _hasConnectionProblem;

    [ObservableProperty]
    private bool _isConnected;

    /// <summary>
    /// The hub is on a network Windows marks Public: its firewall rule is for Private networks only, so the children's
    /// computers cannot reach it. The banner says so on every page and offers to switch the network to Private.
    /// </summary>
    [ObservableProperty]
    private bool _hasPublicNetwork;

    [ObservableProperty]
    private string _publicNetworkText = "";

    /// <summary>Every 6th tick (about once a minute) the hub's network is checked again.</summary>
    public const int NetworkCheckTicks = 6;

    /// <summary>The load started by the last page change (tests await it).</summary>
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Shows the page <paramref name="startPage"/> names (<see cref="AdminAppOptions.StartPage"/>), or the dashboard, and starts the periodic refresh.</summary>
    public void Start(string? startPage = null)
    {
        SelectedPage ??= PageNamed(startPage) ?? Dashboard;
        _ticker ??= _timer.Start(RefreshInterval, TickAsync);
    }

    /// <summary>
    /// A hub page by its command-line name (the notifier opens "devices" for a new Windows account); anything else is null.
    /// On an agent a hub-only page gives way to the dashboard once the status says what this computer is.
    /// </summary>
    private PageViewModel? PageNamed(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "dashboard" => Dashboard,
        "requests" => Requests,
        "members" => MemberDetail,
        "rules" => Rules,
        "activity" => Activity,
        "devices" => Devices,
        "settings" => Settings,
        _ => null,
    };

    public async Task TickAsync()
    {
        if (++_ticks % NetworkCheckTicks == 0) await CheckNetworkAsync();
        if (SelectedPage is { AutoRefresh: true } page)
        {
            await page.RefreshAsync();
            UpdatePages();
            return;
        }
        try
        {
            // An agent has no requests to count; asking for the status keeps the connection banner current there.
            if (Context.IsAgent) await Context.RefreshStatusAsync();
            else await Context.RefreshPendingRequestsAsync();
        }
        catch (AdminClientException)
        {
            // The banner reports connection problems; the next tick tries again.
        }
    }

    public void NavigateTo<TPage>() where TPage : PageViewModel
    {
        if (typeof(TPage) == typeof(SetupWizardViewModel) && !Pages.Contains(Setup)) Pages.Insert(0, Setup);
        SelectedPage = Pages.OfType<TPage>().First();
    }

    public void ShowMember(string memberId)
    {
        MemberDetail.Show(memberId);
        SelectedPage = MemberDetail;
    }

    [RelayCommand]
    private Task Retry() => SelectedPage?.RefreshAsync() ?? Task.CompletedTask;

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        if (value is not null) CurrentLoad = LoadAsync(value);
    }

    private async Task LoadAsync(PageViewModel page)
    {
        await page.RefreshAsync();
        // A page that loaded without the status (a hub-only page an agent refused, opened by --page) would leave the hub's
        // navigation up: the status says which pages this computer has.
        if (Context.Status is null && page.HasLoaded)
        {
            try
            {
                await Context.RefreshStatusAsync();
            }
            catch (AdminClientException)
            {
                // The banner reports connection problems; the next tick tries again.
            }
        }
        UpdatePages();
        if (!_networkChecked) await CheckNetworkAsync();
    }

    /// <summary>Reads the hub's network check (<see cref="GetDiagnostics"/>); agents and services without the check show nothing.</summary>
    public async Task CheckNetworkAsync()
    {
        if (Context.Status is not { IsHub: true }) return;
        try
        {
            var checks = (await Context.Client.RequestAsync<DiagnosticsResult>(new GetDiagnostics())).Checks;
            _networkChecked = true;
            var network = checks.FirstOrDefault(c => c.Id == "network");
            HasPublicNetwork = network?.State == "warn";
            _publicNetworks = HasPublicNetwork ? network!.Detail ?? "" : "";
            PublicNetworkText = HasPublicNetwork ? Texts.Format(Strings.Banner_PublicNetwork, Texts.Ltr(network!.Detail)) : "";
        }
        catch (AdminClientException)
        {
            // Connection problems have their own banner; the next check tries again.
        }
    }

    /// <summary>After the parent confirms that this is the home network, asks the service to switch it to Private.</summary>
    [RelayCommand]
    private async Task MakeNetworkPrivate()
    {
        if (!_dialogs.Confirm(Texts.Format(Strings.Network_MakePrivate_Confirm, Texts.Ltr(_publicNetworks)), Strings.Network_MakePrivate_Title)) return;
        try
        {
            await Context.Client.RequestAsync<AdminOk>(new SetNetworksPrivate());
            await CheckNetworkAsync();
        }
        catch (AdminClientException ex)
        {
            PublicNetworkText = Texts.Format(Strings.Network_MakePrivate_Failed, ex.Message);
        }
    }

    /// <summary>
    /// The navigation for what the service answered as. A hub gets every page, and while its family is empty the wizard
    /// first, opening once by itself (it stays while shown, so its last step stays on screen). An agent gets the dashboard
    /// and the settings page.
    /// </summary>
    private void UpdatePages()
    {
        if (Context.Status is not { } status) return;
        List<PageViewModel> wanted = status.IsHub ? [.. _hubPages] : [Dashboard, Settings];
        var setupNeeded = status.IsHub && Context.Settings is { Members.Count: 0 };
        if (setupNeeded || (status.IsHub && SelectedPage == Setup)) wanted.Insert(0, Setup);
        Place(Pages, wanted);
        if (setupNeeded && !_setupOpened)
        {
            _setupOpened = true;
            SelectedPage = Setup;
        }
        else if (SelectedPage is { } shown && !Pages.Contains(shown))
        {
            SelectedPage = Dashboard;
        }
    }

    /// <summary>Makes <paramref name="target"/> hold exactly <paramref name="items"/> in order, so the selected page keeps its row.</summary>
    private static void Place(ObservableCollection<PageViewModel> target, IReadOnlyList<PageViewModel> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var at = target.IndexOf(items[i]);
            if (at < 0) target.Insert(i, items[i]);
            else if (at != i) target.Move(at, i);
        }
        while (target.Count > items.Count) target.RemoveAt(target.Count - 1);
    }

    public void Dispose()
    {
        _ticker?.Dispose();
        _ticker = null;
        Devices.Dispose();
        Context.Client.StateChanged -= OnClientStateChanged;
    }

    /// <summary>The client raises this on its reader thread too; bindings and commands are updated on the UI thread.</summary>
    private void OnClientStateChanged(object? sender, EventArgs e)
    {
        if (_ui is null || SynchronizationContext.Current == _ui) UpdateConnection();
        else _ui.Post(_ => UpdateConnection(), null);
    }

    private void UpdateConnection()
    {
        var state = Context.Client.State;
        IsConnected = state == AdminConnectionState.Connected;
        // A reconnect attempt (every tick while the service is down) keeps the problem banner up instead of flashing it.
        if (state == AdminConnectionState.Connecting && HasConnectionProblem) return;
        ConnectionText = Texts.ConnectionState(state);
        ConnectionShortText = Texts.ConnectionShort(state);
        HasConnectionProblem = state is AdminConnectionState.ServiceNotRunning or AdminConnectionState.NotElevated or AdminConnectionState.ConnectionLost;
    }
}
