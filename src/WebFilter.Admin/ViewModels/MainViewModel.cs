using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WebFilter.Admin.Services;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// The main window: navigation between the pages, the connection banner, and the periodic refresh (every
/// <see cref="RefreshInterval"/> the shown page reloads if it auto-refreshes; otherwise only the pending-request count
/// does). A failed refresh is retried on the next tick, which is also how the app reconnects after the service restarts.
/// While the hub's family is empty the setup wizard joins the navigation and opens once by itself.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, INavigator, IDisposable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    private readonly IUiTimer _timer;
    private readonly SynchronizationContext? _ui;
    private IDisposable? _ticker;
    private bool _setupOpened;

    public MainViewModel(IAdminClient client, IUiTimer timer, IDialogService dialogs, IClipboardService clipboard, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
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
        Pages = [Dashboard, Requests, MemberDetail, Rules, Activity, Devices, Settings];
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
            UpdateSetup();
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
        UpdateSetup();
    }

    /// <summary>
    /// While the hub's family is empty, the wizard is first in the navigation and opens once by itself; once the family has
    /// members it leaves the navigation (not while it is shown, so its last step stays on screen).
    /// </summary>
    private void UpdateSetup()
    {
        var needed = Context.Status is { IsHub: true } && Context.Settings is { Members.Count: 0 };
        if (needed)
        {
            if (!Pages.Contains(Setup)) Pages.Insert(0, Setup);
            if (_setupOpened) return;
            _setupOpened = true;
            SelectedPage = Setup;
        }
        else if (SelectedPage != Setup)
        {
            Pages.Remove(Setup);
        }
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
