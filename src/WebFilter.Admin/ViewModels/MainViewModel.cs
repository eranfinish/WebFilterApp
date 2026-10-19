using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Services;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// The main window: navigation between the pages, the connection banner, and the periodic refresh (every
/// <see cref="RefreshInterval"/> the shown page reloads if it auto-refreshes; otherwise only the pending-request count
/// does). A failed refresh is retried on the next tick, which is also how the app reconnects after the service restarts.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, INavigator, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IUiTimer _timer;
    private readonly SynchronizationContext? _ui;
    private IDisposable? _ticker;

    public MainViewModel(IAdminClient client, IUiTimer timer, IDialogService dialogs, IClipboardService clipboard, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        _ui = SynchronizationContext.Current;
        Context = new FamilyContext(client, time);
        Dashboard = new DashboardViewModel(Context);
        Requests = new RequestsViewModel(Context);
        Rules = new RulesViewModel(Context, dialogs);
        Activity = new ActivityViewModel(Context);
        Devices = new DevicesViewModel(Context, dialogs, clipboard, timer);
        Settings = new SettingsViewModel(Context);
        Pages = [Dashboard, Requests, Rules, Activity, Devices, Settings];
        foreach (var page in Pages) page.Navigator = this;
        client.StateChanged += OnClientStateChanged;
        UpdateConnection();
    }

    public FamilyContext Context { get; }

    public DashboardViewModel Dashboard { get; }
    public RequestsViewModel Requests { get; }
    public RulesViewModel Rules { get; }
    public ActivityViewModel Activity { get; }
    public DevicesViewModel Devices { get; }
    public SettingsViewModel Settings { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

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

    /// <summary>The load started by the last page change (tests await it).</summary>
    public Task CurrentLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Shows the dashboard and starts the periodic refresh.</summary>
    public void Start()
    {
        SelectedPage ??= Dashboard;
        _ticker ??= _timer.Start(RefreshInterval, TickAsync);
    }

    public async Task TickAsync()
    {
        if (SelectedPage is { AutoRefresh: true } page)
        {
            await page.RefreshAsync();
            return;
        }
        try
        {
            await Context.RefreshPendingRequestsAsync();
        }
        catch (AdminClientException)
        {
            // The banner reports connection problems; the next tick tries again.
        }
    }

    public void NavigateTo<TPage>() where TPage : PageViewModel => SelectedPage = Pages.OfType<TPage>().First();

    [RelayCommand]
    private Task Retry() => SelectedPage?.RefreshAsync() ?? Task.CompletedTask;

    partial void OnSelectedPageChanged(PageViewModel? value)
    {
        if (value is not null) CurrentLoad = value.RefreshAsync();
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
