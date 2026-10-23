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
/// The computers paired with this hub (<see cref="GetDevices"/>), revoking one (<see cref="RevokeDevice"/>), and pairing a
/// new one with a one-time code (<see cref="CreatePairingCode"/>; MVP-PLAN §4.5, §10.3). On an agent computer the service
/// answers "not_hub" and the page only says where devices are managed.
/// </summary>
public sealed partial class DevicesViewModel : PageViewModel, IDisposable
{
    public const string NotHub = "not_hub";
    public const string NotFound = "not_found";

    /// <summary>Agents upload every 10 seconds; one heard from within this window counts as connected now.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);

    private readonly IDialogService _dialogs;
    private readonly IClipboardService _clipboard;
    private readonly IUiTimer _timer;
    private readonly Dictionary<string, DeviceItemViewModel> _items = new(StringComparer.Ordinal);

    public DevicesViewModel(FamilyContext context, IDialogService dialogs, IClipboardService clipboard, IUiTimer timer) : base(context)
    {
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        // The navigation shows how many standard accounts wait for an answer (browsing there is blocked meanwhile).
        UpdateBadge();
        Context.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FamilyContext.UnlinkedAccountCount)) UpdateBadge();
        };
    }

    public override string Title => Strings.Nav_Devices;

    public override string Intro => Strings.Devices_Intro;

    public override bool AutoRefresh => true;

    public override string BadgeToolTip => Strings.Devices_BadgeToolTip;

    /// <summary>Paired computers that still receive settings, by name.</summary>
    public ObservableCollection<DeviceItemViewModel> Active { get; } = [];

    /// <summary>Computers whose pairing was revoked, by name; the view lists them apart and greyed out.</summary>
    public ObservableCollection<DeviceItemViewModel> Revoked { get; } = [];

    /// <summary>The service answered as the hub: the lists and the pairing button show.</summary>
    [ObservableProperty]
    private bool _isHub;

    /// <summary>The service answered "not_hub" (an agent computer): only an explanation shows, no buttons.</summary>
    [ObservableProperty]
    private bool _isNotHub;

    [ObservableProperty]
    private bool _hasNoActive;

    [ObservableProperty]
    private bool _hasRevoked;

    /// <summary>The open "pair a new computer" panel, or null. Refreshing the list never touches it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPairing))]
    private PairingViewModel? _pairing;

    public bool IsPairing => Pairing is not null;

    protected override async Task LoadAsync()
    {
        // Devices first: an agent answers "not_hub" here, while its GetStatus fails with a generic error.
        DevicesResult result;
        try
        {
            result = await Client.RequestAsync<DevicesResult>(new GetDevices());
        }
        catch (AdminServerException ex) when (ex.Code == NotHub)
        {
            ShowNotHub();
            return;
        }
        // Which computers still get settings, for the accounts waiting for an answer (the badge and the dashboard's cards).
        Context.UsePairedDevices(result.Devices);
        // The family's time zone and the service's clock, for "seen 5 minutes ago".
        await Context.RefreshStatusAsync();
        // The tick reloads only the shown page; this keeps the requests badge current meanwhile.
        await Context.RefreshPendingRequestsAsync();
        IsNotHub = false;
        IsHub = true;
        Sync(result.Devices, Context.ServiceNow);
        if (Context.Settings is { } settings && Context.Status is { } status) SyncAccounts(settings, status.DeviceId, result.Devices);
    }

    [RelayCommand]
    private Task StartPairingAsync() => CreateCodeAsync();

    [RelayCommand]
    private void ClosePairing()
    {
        Pairing?.Dispose();
        Pairing = null;
    }

    [RelayCommand]
    private async Task RevokeAsync(DeviceItemViewModel? device)
    {
        if (device is null || device.IsRevoked) return;
        var name = Texts.Ltr(device.Name);
        if (!_dialogs.Confirm(Texts.Format(Strings.Devices_RevokeConfirm, name), Strings.Devices_RevokeTitle)) return;
        StatusMessage = null;
        string? done = null;
        var answered = await RunAsync(async () =>
        {
            try
            {
                await Client.ExecuteAsync(new RevokeDevice(device.Id));
                done = Texts.Format(Strings.Done_DeviceRevoked, name);
            }
            catch (AdminServerException ex) when (ex.Code == NotFound)
            {
                // Revoked meanwhile (e.g. from another admin window): the outcome the parent asked for.
                done = Texts.Format(Strings.Devices_AlreadyRevoked, name);
            }
        });
        if (!answered) return;
        StatusMessage = done;
        await RefreshAsync();
    }

    /// <summary>Asks the hub for a new pairing code and shows it: in a new panel, or in the open one (keeping the address the parent picked).</summary>
    internal async Task CreateCodeAsync()
    {
        StatusMessage = null;
        PairingCodeResult? code = null;
        var notHub = false;
        await RunAsync(async () =>
        {
            try
            {
                code = await Client.RequestAsync<PairingCodeResult>(new CreatePairingCode());
            }
            catch (AdminServerException ex) when (ex.Code == NotHub)
            {
                notHub = true;
            }
        });
        if (notHub) ShowNotHub();
        if (code is null) return;
        Pairing ??= new PairingViewModel(this, Context, _clipboard, _timer);
        Pairing.Show(code);
    }

    public void Dispose() => ClosePairing();

    private void UpdateBadge() =>
        Badge = Context.UnlinkedAccountCount > 0 ? Context.UnlinkedAccountCount.ToString(CultureInfo.CurrentCulture) : null;

    private void ShowNotHub()
    {
        IsHub = false;
        IsNotHub = true;
        ClosePairing();
        _items.Clear();
        Active.Clear();
        Revoked.Clear();
        _groups.Clear();
        AccountGroups.Clear();
        HasNoActive = false;
        HasRevoked = false;
    }

    /// <summary>Updates the rows in place (by device id) so the refresh every few seconds does not rebuild the buttons under the mouse.</summary>
    private void Sync(IReadOnlyList<DeviceInfo> devices, DateTimeOffset now)
    {
        var ids = devices.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _items.Keys.Where(id => !ids.Contains(id)).ToList()) _items.Remove(gone);
        foreach (var device in devices)
        {
            if (!_items.TryGetValue(device.Id, out var item)) _items[device.Id] = item = new DeviceItemViewModel(device.Id);
            item.Update(device, Context, now);
        }

        var byName = StringComparer.Create(CultureInfo.CurrentCulture, ignoreCase: true);
        var ordered = _items.Values.OrderBy(d => d.Name, byName).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        Place(Active, [.. ordered.Where(d => !d.IsRevoked)]);
        Place(Revoked, [.. ordered.Where(d => d.IsRevoked)]);
        HasNoActive = Active.Count == 0;
        HasRevoked = Revoked.Count > 0;
    }

    /// <summary>Makes <paramref name="target"/> hold exactly <paramref name="items"/> in order, moving rather than recreating rows.</summary>
    private static void Place(ObservableCollection<DeviceItemViewModel> target, IReadOnlyList<DeviceItemViewModel> items)
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

/// <summary>One paired computer; updated in place on every refresh.</summary>
public sealed partial class DeviceItemViewModel(string id) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _idText = "";
    [ObservableProperty] private string _osText = "";
    [ObservableProperty] private string _pairedText = "";
    [ObservableProperty] private string _lastSeenText = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isRevoked;

    /// <summary>Heard from within <see cref="DevicesViewModel.OnlineWindow"/>, and not revoked.</summary>
    [ObservableProperty] private bool _isOnline;

    internal void Update(DeviceInfo device, FamilyContext context, DateTimeOffset now)
    {
        Name = FamilyContext.DeviceName(device);
        IdText = Texts.Format(Strings.Device_Id, device.Id);
        OsText = Texts.Code("DeviceOs_", device.Os);
        PairedText = context.FormatDate(device.PairedAt);
        LastSeenText = LastSeen(device.LastSeenAt, now, context);
        IsRevoked = device.Revoked;
        IsOnline = !device.Revoked && device.LastSeenAt is { } seen && now - seen < DevicesViewModel.OnlineWindow;
        StatusText = !device.Revoked ? Strings.Device_Active
            : device.RevokedReason == UnregisterRequest.Uninstall ? Strings.Device_Uninstalled
            : Strings.Device_Revoked;
    }

    /// <summary>"N minutes ago" within the hour, "N hours ago" within the day, otherwise the date and time.</summary>
    internal static string LastSeen(DateTimeOffset? lastSeen, DateTimeOffset now, FamilyContext context)
    {
        if (lastSeen is not { } seen) return Strings.Device_NeverSeen;
        var ago = now - seen;
        // A clock a little ahead of the service's counts as "just now".
        if (ago < TimeSpan.FromMinutes(1)) return Strings.Device_SeenJustNow;
        if (ago < TimeSpan.FromHours(1))
        {
            var minutes = (int)ago.TotalMinutes;
            return minutes == 1 ? Strings.Device_SeenMinuteAgo : Texts.Format(Strings.Device_SeenMinutesAgo, minutes);
        }
        if (ago < TimeSpan.FromDays(1))
        {
            var hours = (int)ago.TotalHours;
            return hours == 1 ? Strings.Device_SeenHourAgo : Texts.Format(Strings.Device_SeenHoursAgo, hours);
        }
        return context.FormatDateTime(seen);
    }
}

/// <summary>
/// "Pair a new computer": the one-time code (12 characters, valid 10 minutes; MVP-PLAN §10.3) with a live countdown, this
/// hub's addresses and port, and the agent's pair command ready to copy. When the countdown ends the code is marked
/// expired and a new one can be created.
/// </summary>
public sealed partial class PairingViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan CountdownInterval = TimeSpan.FromSeconds(1);

    private readonly FamilyContext _context;
    private readonly IClipboardService _clipboard;
    private readonly IUiTimer _timer;
    private IDisposable? _countdown;
    private DateTimeOffset _expiresAt;

    internal PairingViewModel(DevicesViewModel owner, FamilyContext context, IClipboardService clipboard, IUiTimer timer)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _timer = timer ?? throw new ArgumentNullException(nameof(timer));
        NewCodeCommand = new AsyncRelayCommand(owner.CreateCodeAsync);
        CloseCommand = owner.ClosePairingCommand;
    }

    public IAsyncRelayCommand NewCodeCommand { get; }

    public IRelayCommand CloseCommand { get; }

    /// <summary>As the hub formats it (XXXX-XXXX-XXXX), which is what the pair command takes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairCommandText))]
    private string _code = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairCommandText), nameof(PortText))]
    private int _port;

    /// <summary>This hub's IPv4 addresses; the parent picks the one on the home network (the first by default).</summary>
    public ObservableCollection<string> Addresses { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PairCommandText))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string? _selectedAddress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private bool _isExpired;

    [ObservableProperty]
    private string _countdownText = "";

    [ObservableProperty]
    private string? _copyMessage;

    [ObservableProperty]
    private bool _copyFailed;

    public bool HasAddresses => Addresses.Count > 0;

    public bool HasSeveralAddresses => Addresses.Count > 1;

    public string PortText => Port.ToString(CultureInfo.InvariantCulture);

    /// <summary>The command to run on the other computer for the selected address; null when the hub reported no address.</summary>
    public string? PairCommandText => SelectedAddress is { } address ? BuildCommand(address, Port, Code) : null;

    /// <summary>The agent's pair command (<c>WebFilter.Service pair --hub host:port --code XXXX-XXXX-XXXX</c>).</summary>
    public static string BuildCommand(string address, int port, string code) =>
        string.Create(CultureInfo.InvariantCulture, $"WebFilter.Service.exe pair --hub {address}:{port} --code {code}");

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy()
    {
        if (PairCommandText is not { } command) return;
        CopyFailed = !_clipboard.TrySetText(command);
        CopyMessage = CopyFailed ? Strings.Pairing_CopyFailed : Strings.Pairing_Copied;
    }

    private bool CanCopy() => !IsExpired && SelectedAddress is not null;

    /// <summary>Shows a fresh code and (re)starts the countdown.</summary>
    internal void Show(PairingCodeResult result)
    {
        var picked = SelectedAddress;
        Code = result.Code;
        Port = result.Port;
        _expiresAt = result.ExpiresAt;
        Addresses.Clear();
        foreach (var address in result.Addresses.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.Ordinal)) Addresses.Add(address);
        SelectedAddress = picked is not null && Addresses.Contains(picked) ? picked : Addresses.FirstOrDefault();
        OnPropertyChanged(nameof(HasAddresses));
        OnPropertyChanged(nameof(HasSeveralAddresses));
        CopyMessage = null;
        CopyFailed = false;
        IsExpired = false;
        UpdateCountdown();
        if (!IsExpired) _countdown ??= _timer.Start(CountdownInterval, Tick);
    }

    /// <summary>Counts down on the service's clock, which set the expiry; at zero the code is marked expired and the countdown stops.</summary>
    internal void UpdateCountdown()
    {
        var seconds = (int)Math.Ceiling((_expiresAt - _context.ServiceNow).TotalSeconds);
        if (seconds > 0)
        {
            CountdownText = Texts.Format(Strings.Pairing_ValidFor, string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}"));
            return;
        }
        IsExpired = true;
        CountdownText = Strings.Pairing_Expired;
        CopyMessage = null;
        StopCountdown();
    }

    public void Dispose() => StopCountdown();

    partial void OnSelectedAddressChanged(string? value) => CopyMessage = null;

    private Task Tick()
    {
        UpdateCountdown();
        return Task.CompletedTask;
    }

    private void StopCountdown()
    {
        _countdown?.Dispose();
        _countdown = null;
    }
}
