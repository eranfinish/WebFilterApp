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
using WebFilter.Service.Mapping;

namespace WebFilter.Service.Pipes;

/// <summary>
/// <c>\\.\pipe\WebFilter.Admin</c>: the DACL grants only Administrators, SYSTEM and the service account, so an
/// unelevated token cannot even open it; each client is also checked to be elevated (or the service account).
/// </summary>
public sealed class AdminPipeServer(FamilyService family, IOptions<ServiceOptions> options, TimeProvider time, ILogger<AdminPipeServer> log) : BackgroundService
{
    private readonly ServiceOptions _options = options.Value;
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
            _ = Task.Run(() => ServeAsync(pipe, stoppingToken), stoppingToken);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using var owned = pipe;
        using var client = ClientIdentity.Of(pipe);
        if (client.Elevation != "full" && client.UserSid != Self.Value)
        {
            log.LogWarning("Rejected unelevated admin pipe client: {Client}", client);
            await AdminProtocol.WriteAsync(pipe, new AdminError("not_elevated", "Run the admin app as administrator."), ct);
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
                catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    reply = new AdminError("bad_request", ex.Message);
                }
                await AdminProtocol.WriteAsync(pipe, reply with { Id = request.Id }, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Malformed admin message from {Client}", client);
        }
    }

    private AdminMessage Handle(AdminMessage request, string adminName) => request switch
    {
        GetStatus => family.WithHub<AdminMessage>((hub, now) => new StatusResult(hub.Version, family.DeviceId, family.IsHub, now,
            hub.Config.Members.Select(m => MemberStatusOf(hub, m, now)).ToList())),
        GetSettings => family.WithHub<AdminMessage>((hub, _) => new SettingsResult(BundleMapper.ToBundle(hub.Config, hub.Version))),
        ReplaceSettings replace => Ok((hub, now) => hub.ReplaceSettings(BundleMapper.ToConfig(replace.Settings), now)),
        AddRule add => Ok((hub, now) => hub.AddRule(ToRule(add.Rule), now)),
        RemoveRule remove => Ok((hub, now) => hub.RemoveRule(remove.RuleId, now)),
        SetMember set => Ok((hub, now) => hub.SetMember(Patch(hub.Config.Member(set.MemberId), set), now)),
        ExtendTime extend => Ok((hub, now) => hub.ExtendTime(extend.MemberId, extend.Minutes, now)),
        Pause pause => Ok((hub, now) => hub.Pause(pause.MemberId, pause.Until, now)),
        Resume resume => Ok((hub, now) => hub.Resume(resume.MemberId, now)),
        StartHomework homework => Ok((hub, now) => hub.StartHomework(homework.MemberId, homework.Minutes, now)),
        StopHomework stop => Ok((hub, now) => hub.StopHomework(stop.MemberId, now)),
        GetEvents query => family.WithHub<AdminMessage>((hub, _) => new EventsResult(hub.Events
            .Where(e => (query.Since is null || e.At >= query.Since) && (query.MemberId is null || e.MemberId == query.MemberId)
                        && (query.EventType is null || Wire(e.Type) == query.EventType))
            .TakeLast(Math.Clamp(query.Max, 1, 5000))
            .Select(e => new EventDto(Wire(e.Type), e.At, e.MemberId, e.DeviceId, e.Reason == Core.Decisions.ReasonCode.None ? null : Core.Decisions.ReasonCodes.ToWire(e.Reason),
                e.Url, e.App, e.RemainingMinutes, e.Detail)).ToList())),
        GetRequests query => family.WithHub<AdminMessage>((hub, _) => new RequestsResult(hub.Requests
            .Where(r => (query.Status is null || r.Status.ToString().Equals(query.Status, StringComparison.OrdinalIgnoreCase)) && (query.MemberId is null || r.MemberId == query.MemberId))
            .Select(r => new RequestDto(r.Id, r.MemberId, r.DeviceId, r.Kind.ToString().ToLowerInvariant(), r.Target, r.TargetKey, r.Minutes, r.Message,
                r.CreatedAt, r.Status.ToString().ToLowerInvariant(), r.DecidedAt)).ToList())),
        DecideRequest decide => DecideRequest(decide),
        SetClock set => SetClock(set),
        _ => new AdminError("unsupported", $"Unsupported message type {request.GetType().Name}."),
    };

    private AdminMessage Ok(Action<HubCore, DateTimeOffset> action) =>
        family.WithHub<AdminMessage>((hub, now) =>
        {
            action(hub, now);
            return new AdminOk();
        });

    private AdminMessage DecideRequest(DecideRequest decide)
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
        family.Decide(decide.RequestId, approve, decide.Scope == "page" ? ApproveScope.Page : ApproveScope.Host, duration, decide.Minutes);
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

    private static MemberStatus MemberStatusOf(HubCore hub, FamilyMember member, DateTimeOffset now)
    {
        var status = hub.TimeStatus(member.Id, now);
        var runtime = hub.RuntimeOf(member.Id);
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
