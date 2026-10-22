using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
using WebFilter.Service.Accounts;
using WebFilter.Service.Mapping;
using WebFilter.Service.Network;
using WebFilter.Service.Setup;

namespace WebFilter.Service.Pipes;

/// <summary>
/// <c>\\.\pipe\WebFilter.Admin</c>: the DACL grants only Administrators, SYSTEM and the service account, so an
/// unelevated token cannot even open it; each client is also checked to be elevated (or the service account).
/// </summary>
public sealed class AdminPipeServer(FamilyService family, IOptions<ServiceOptions> options, TimeProvider time, ILogger<AdminPipeServer> log,
    ILocalAccounts? accounts = null, IMachineProbe? machine = null, INetworkProfiles? networks = null) : BackgroundService
{
    private readonly ServiceOptions _options = options.Value;
    private readonly ILocalAccounts _accounts = accounts ?? new WindowsLocalAccounts();
    private readonly IMachineProbe _machine = machine ?? new WindowsMachineProbe();
    private readonly INetworkProfiles _networks = networks ?? new WindowsNetworkProfiles();
    private static readonly SecurityIdentifier Self = WindowsIdentity.GetCurrent().User!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Admin pipe listening on {Pipe}", _options.AdminPipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = NamedPipeServerStreamAcl.Create(_options.AdminPipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, Security());
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }
            _ = Task.Run(async () =>
            {
                // A session must never fail silently: anything unexpected is logged (the pipe is closed by ServeAsync).
                try { await ServeAsync(pipe, stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Pipe session failed"); }
            }, stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var owned = pipe;
        using var client = ClientIdentity.Of(pipe);
        if (client.Elevation != "full" && client.UserSid != Self.Value)
        {
            log.LogWarning("Rejected unelevated admin pipe client: {Client}", client);
            // Answer the client's first request (so the refusal arrives as its reply, before we hang up); a client that
            // sends nothing within 5 s gets the refusal without an id.
            string? firstId = null;
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(TimeSpan.FromSeconds(5));
                firstId = (await AdminProtocol.ReadAsync(pipe, wait.Token))?.Id;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or NotSupportedException or InvalidDataException) { }
            try { await AdminProtocol.WriteAsync(pipe, new AdminError("not_elevated", "Run the admin app as administrator.") { Id = firstId }, ct); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            return;
        }
        var adminName = client.UserName ?? client.UserSid ?? "admin";
        try
        {
            while (await AdminProtocol.ReadAsync(pipe, ct) is { } request)
            {
                AdminMessage reply;
                try
                {
                    reply = Handle(request, adminName);
                }
                catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException or NotSupportedException
                                               or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    reply = new AdminError("bad_request", ex.Message);
                }
                await AdminProtocol.WriteAsync(pipe, reply with { Id = request.Id }, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            log.LogWarning(ex, "Malformed admin message from {Client}", client);
        }
    }

    internal AdminMessage Handle(AdminMessage request, string adminName) => family.IsHub ? HandleOnHub(request, adminName) : HandleOnAgent(request);

    /// <summary>
    /// On an agent computer (MVP-PLAN §4.5): status from the last settings received, and the local emergency pause;
    /// everything else is managed on the hub.
    /// </summary>
    private AdminMessage HandleOnAgent(AdminMessage request) => request switch
    {
        GetStatus => family.WithAgent<AdminMessage>((agent, now) => AgentStatus(agent, now)),
        Pause pause => family.WithAgent<AdminMessage>((agent, now) => { agent.LocalPause(pause.MemberId, pause.Until, now); return new AdminOk(); }),
        GetDiagnostics => new DiagnosticsResult(MachineChecks.Run(_options, isHub: false, _machine)),
        SetClock set => SetClock(set),
        _ => new AdminError("not_hub", "This computer is managed by the hub computer; make this change there."),
    };

    private AdminMessage HandleOnHub(AdminMessage request, string adminName) => request switch
    {
        GetStatus => family.WithHub<AdminMessage>((hub, now) => new StatusResult(hub.Version, family.DeviceId, family.IsHub, now,
            hub.Config.Members.Select(m => MemberStatusOf(m, hub.TimeStatus(m.Id, now), hub.RuntimeOf(m.Id), now)).ToList(), BundleMapper.ZoneId(hub.Config.TimeZone))),
        GetSettings => family.WithHub<AdminMessage>((hub, _) => new SettingsResult(BundleMapper.ToBundle(hub.Config, hub.Version))),
        ReplaceSettings replace => Replace(replace, adminName),
        AddRule add => Ok(adminName, (hub, now) => hub.AddRule(ToRule(add.Rule), now)),
        RemoveRule remove => Ok(adminName, (hub, now) => hub.RemoveRule(remove.RuleId, now)),
        SetMember set => Ok(adminName, (hub, now) => hub.SetMember(Patch(hub.Config.Member(set.MemberId), set), now)),
        ExtendTime extend => Ok(adminName, (hub, now) => hub.ExtendTime(extend.MemberId, extend.Minutes, now)),
        Pause pause => Ok(adminName, (hub, now) => hub.Pause(pause.MemberId, pause.Until, now)),
        Resume resume => Ok(adminName, (hub, now) => hub.Resume(resume.MemberId, now)),
        StartHomework homework => Ok(adminName, (hub, now) => hub.StartHomework(homework.MemberId, homework.Minutes, now)),
        StopHomework stop => Ok(adminName, (hub, now) => hub.StopHomework(stop.MemberId, now)),
        GetEvents query => family.WithHub<AdminMessage>((hub, _) => new EventsResult(hub.Events
            .Where(e => (query.Since is null || e.At >= query.Since) && (query.MemberId is null || e.MemberId == query.MemberId)
                        && (query.EventType is null || Wire(e.Type) == query.EventType))
            .TakeLast(Math.Clamp(query.Max, 1, 5000))
            .Select(e => new EventDto(Wire(e.Type), e.At, e.MemberId, e.DeviceId, e.Reason == Core.Decisions.ReasonCode.None ? null : Core.Decisions.ReasonCodes.ToWire(e.Reason),
                e.Url, e.App, e.RemainingMinutes, e.Detail, e.Admin)).ToList())),
        GetRequests query => family.WithHub<AdminMessage>((hub, _) => new RequestsResult(hub.Requests
            .Where(r => (query.Status is null || r.Status.ToString().Equals(query.Status, StringComparison.OrdinalIgnoreCase)) && (query.MemberId is null || r.MemberId == query.MemberId))
            .Select(r => new RequestDto(r.Id, r.MemberId, r.DeviceId, r.Kind.ToString().ToLowerInvariant(), r.Target, r.TargetKey, r.Minutes, r.Message,
                r.CreatedAt, r.Status.ToString().ToLowerInvariant(), r.DecidedAt)).ToList())),
        DecideRequest decide => DecideRequest(decide, adminName),
        SetClock set => SetClock(set),
        CreatePairingCode => new PairingCodeResult(family.Pairing.Create(family.Now), family.Now + Hub.HubPairing.Lifetime, family.Options.HubPort, LocalAddresses()),
        GetDevices => new DevicesResult(family.Devices!.All().Select(d => new DeviceInfo(d.Id, d.Name, d.Os, d.PairedAt, d.LastSeenAt, d.RevokedAt is not null)).ToList()),
        RevokeDevice revoke => family.Devices!.Revoke(revoke.DeviceId, family.Now)
            ? new AdminOk()
            : new AdminError("not_found", "No such paired device."),
        ScanAccounts => ScanLocalAccounts(adminName),
        SetNetworksPrivate => MakeNetworksPrivate(adminName),
        GetDiagnostics => new DiagnosticsResult(MachineChecks.Run(_options, isHub: true, _machine)),
        _ => new AdminError("unsupported", $"Unsupported message type {request.GetType().Name}."),
    };

    /// <summary>
    /// The members as this agent sees them (none before its first settings arrive) and the hub that manages it, for the
    /// admin app's "managed by" view.
    /// </summary>
    private StatusResult AgentStatus(AgentCore agent, DateTimeOffset now)
    {
        var config = agent.SnapshotVersion is null ? null : agent.View().Config;
        var members = config?.Members.Select(m => MemberStatusOf(m, agent.TimeStatus(m.Id, now), agent.View().RuntimeOf(m.Id), now)).ToList() ?? [];
        var hub = new HubConnection(config?.Devices.FirstOrDefault(d => d.Role == "hub")?.Id, family.HubAddress,
            family.LastHubContactAt is not null && agent.HubReachable, family.LastHubContactAt);
        return new StatusResult(agent.SnapshotVersion ?? 0, family.DeviceId, false, now, members, config is null ? null : BundleMapper.ZoneId(config.TimeZone), hub);
    }

    private AdminMessage Ok(string adminName, Action<HubCore, DateTimeOffset> action) =>
        family.WithHub<AdminMessage>((hub, now) =>
        {
            action(hub, now);
            return new AdminOk();
        }, actor: adminName);

    /// <summary>The version check and the change happen under the same gate, so nothing can slip in between.</summary>
    private AdminMessage Replace(ReplaceSettings replace, string adminName)
    {
        var config = BundleMapper.ToConfig(replace.Settings);
        return family.WithHub<AdminMessage>((hub, now) =>
        {
            if (replace.IfVersion is { } expected && hub.Version != expected)
                return new AdminError(ReplaceSettings.ConflictCode,
                    $"The settings changed meanwhile (version {hub.Version}, expected {expected}); read them again.");
            hub.ReplaceSettings(config, now);
            return new AdminOk();
        }, actor: adminName);
    }

    /// <summary>Reads the Windows accounts outside the gate (it may take a moment), then records the new ones.</summary>
    /// <summary>A machine setting, not a family one: logged with the admin's name, the settings version does not change.</summary>
    private AdminMessage MakeNetworksPrivate(string adminName)
    {
        var changed = _networks.MakePublicNetworksPrivate();
        if (changed.Count > 0) log.LogInformation("{Admin} set the network(s) {Networks} to Private", adminName, string.Join(", ", changed));
        return new AdminOk(string.Join(", ", changed));
    }

    private AdminMessage ScanLocalAccounts(string adminName)
    {
        var seen = _accounts.List();
        return family.WithHub<AdminMessage>((hub, now) =>
        {
            var before = hub.Config.Devices.Find(d => d.Id == family.DeviceId)?.Accounts.Count;
            if (before is null) return new AdminError("not_found", $"This computer ({family.DeviceId}) is not in the settings.");
            hub.ReportAccounts(family.DeviceId, seen, now);
            var added = hub.Config.Device(family.DeviceId).Accounts.Count - before.Value;
            return new AdminOk(added.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }, actor: adminName);
    }

    private AdminMessage DecideRequest(DecideRequest decide, string adminName)
    {
        var approve = decide.Decision switch
        {
            "approve" => true,
            "deny" => false,
            _ => throw new ArgumentException("decision must be approve or deny"),
        };
        var duration = decide.Duration switch
        {
            "today" => ApprovalDuration.Today,
            "always" => ApprovalDuration.Always,
            null when decide.Minutes is { } m => ApprovalDuration.For(m),
            null => ApprovalDuration.Always,
            _ => throw new ArgumentException("duration must be today or always"),
        };
        family.Decide(decide.RequestId, approve, decide.Scope == "page" ? ApproveScope.Page : ApproveScope.Host, duration, decide.Minutes, adminName);
        return new AdminOk();
    }

    private AdminMessage SetClock(SetClock set)
    {
#if SIMULATION
        if (time is SimulationClock clock)
        {
            clock.Set(set.Now);
            return new AdminOk(clock.GetUtcNow().ToString("o"));
        }
#endif
        _ = set;
        _ = time;
        return new AdminError("unsupported", "The clock can only be set in Simulation builds.");
    }

    /// <summary>This computer's IPv4 addresses; adapters with a default gateway (the home network) first, so the first
    /// address is the one agents should use rather than a Hyper-V or VPN adapter.</summary>
    private static List<string> LocalAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .OrderByDescending(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses)
            .Where(u => u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(u => u.Address.ToString())
            .Distinct()
            .ToList();

    private static MemberStatus MemberStatusOf(FamilyMember member, TimeStatus status, MemberRuntime runtime, DateTimeOffset now)
    {
        return new MemberStatus(member.Id, member.DisplayName,
            status.State switch { TimeState.Ok => "ok", TimeState.Warn => "warn", TimeState.Expired => "expired", TimeState.OutsideWindow => "outside_window", _ => "unlimited" },
            status.UsedMinutes, status.LimitMinutes, status.RemainingMinutes,
            status.WindowEndsAtMinute is { } w ? $"{w / 60:00}:{w % 60:00}" : null,
            runtime.IsPaused(now) ? runtime.PausedUntil : null, runtime.IsInHomework(now) ? runtime.HomeworkUntil : null);
    }

    private static Rule ToRule(RuleDto dto) =>
        new(dto.Id, dto.Member, dto.Pattern, Enum.Parse<PatternType>(dto.Type, true), Enum.Parse<RuleAction>(dto.Action, true), dto.AlwaysAllowed, dto.ExpiresAt);

    private static FamilyMember Patch(FamilyMember member, SetMember set) => member with
    {
        DisplayName = set.DisplayName ?? member.DisplayName,
        Role = set.Role is { } role ? Enum.Parse<Role>(role, true) : member.Role,
        Age = set.Age ?? member.Age,
        FilterMode = set.FilterMode is { } mode ? Enum.Parse<FilterMode>(mode, true) : member.FilterMode,
        YouTubeMode = set.YouTubeMode is { } yt ? Enum.Parse<YouTubeMode>(yt, true) : member.YouTubeMode,
    };

    private static string Wire(EventType type) => type switch
    {
        EventType.TimeWarning => "time_warning", EventType.TimeExpired => "time_expired", EventType.Blocked => "blocked",
        EventType.AppBlocked => "app_blocked", EventType.AdminChange => "admin_change", EventType.AccessRequest => "access_request",
        EventType.Tamper => "tamper", _ => "service",
    };

    private static PipeSecurity Security()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(Self, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
