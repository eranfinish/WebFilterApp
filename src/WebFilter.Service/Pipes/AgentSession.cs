using System.Text.Json;
using System.Threading.Channels;
using WebFilter.Contracts.Agent;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;

namespace WebFilter.Service.Pipes;

/// <summary>One native host connection: answers agent-protocol requests and pushes settings and time changes.</summary>
internal sealed class AgentSession(Stream pipe, ClientIdentity client, FamilyService family, ILogger log)
{
    private readonly Channel<AgentMessage> _outgoing = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions { SingleReader = true });
    private string? _extensionId;
    private string? _lastTimeState;

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteLoopAsync(linked.Token);
        void OnSettings(int version) => _outgoing.Writer.TryWrite(new RulesChangedMessage(version));
        void OnTick(DateTimeOffset _) => PushTimeIfChanged();
        family.SettingsChanged += OnSettings;
        family.Ticked += OnTick;
        try
        {
            while (await AgentProtocol.ReadAsync(pipe, linked.Token) is { } message)
            {
                AgentMessage reply;
                try
                {
                    reply = Handle(message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Agent request {Type} failed", message.GetType().Name);
                    reply = new ErrorMessage("internal_error", "The request failed.");
                }
                _outgoing.Writer.TryWrite(reply with { Id = message.Id });
            }
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "Malformed agent message from {Client}", client);
        }
        finally
        {
            family.SettingsChanged -= OnSettings;
            family.Ticked -= OnTick;
            _outgoing.Writer.TryComplete();
            await linked.CancelAsync();
            try { await writer; } catch (OperationCanceledException) { }
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        await foreach (var message in _outgoing.Reader.ReadAllAsync(ct))
            await AgentProtocol.WriteAsync(pipe, message, ct);
    }

    private AgentMessage Handle(AgentMessage message) => message switch
    {
        HelloRequest hello => Hello(hello),
        GetRulesRequest => Rules(),
        HeartbeatRequest beat => Heartbeat(beat),
        ReportBlockRequest block => family.WithAgent<AgentMessage>((agent, now) =>
        {
            agent.ReportBlock(Account(agent), block.Url, ReasonCodeExtensions.ParseOrNone(block.Reason), now);
            return new ErrorMessage("ok", "recorded");
        }),
        RequestAccessRequest request => RequestAccess(request),
        ResolveYouTubeRequest resolve => family.WithAgent<AgentMessage>((agent, _) =>
            new ResolveYouTubeResponse(agent.View().Config.YouTubeChannels.FirstOrDefault(c =>
                resolve.Handle is not null && string.Equals(c.Handle, resolve.Handle, StringComparison.OrdinalIgnoreCase))?.ChannelId)),
        _ => new ErrorMessage("unsupported", $"Unsupported message type {message.GetType().Name}."),
    };

    private AgentMessage Hello(HelloRequest hello)
    {
        _extensionId = hello.ExtensionId;
        return family.WithAgent<AgentMessage>((agent, _) =>
        {
            var account = Account(agent);
            var member = account.MemberId is null ? null : agent.View().Config.Member(account.MemberId);
            return new HelloResponse(
                new AgentUser(account.AccountId, client.UserName ?? account.Name, member is null ? "unlinked" : Lower(member.Role), account.IsAdmin),
                Mode(account, member), "service", true, agent.SnapshotVersion ?? 0, typeof(AgentSession).Assembly.GetName().Version?.ToString() ?? "0");
        });
    }

    private AgentMessage Rules() => family.WithAgent<AgentMessage>((agent, now) =>
    {
        var view = agent.View();
        var account = Account(agent);
        var member = account.MemberId is null ? null : view.Config.Member(account.MemberId);
        var options = new DnrCompileOptions(_extensionId ?? "unknown-extension-id");

        EffectiveUrlPolicy? policy = member is not null ? view.UrlPolicy(member.Id, now)
            : account.IsAdmin ? null
            : new EffectiveUrlPolicy("", FilterMode.Allowlist, YouTubeMode.Off, [], view.Config.Rules.Where(r => r.MemberId is null && r.AlwaysAllowed && r.IsActiveAt(now)).ToList(), []);
        var rules = policy is null ? new DnrRuleSet([], []) : DnrCompiler.Compile(policy, options);

        var channels = member is null ? [] : view.Config.YouTubeChannels.Where(c => c.MemberId is null || c.MemberId == member.Id).ToList();
        YouTubeChannelRef Ref(YouTubeChannel c) => new(c.ChannelId, c.Handle, c.Title);
        var youtube = new YouTubePolicy(member is null ? (account.IsAdmin ? "unrestricted" : "off") : Lower(member.YouTubeMode),
            channels.Where(c => c.Action == RuleAction.Allow).Select(Ref).ToList(),
            channels.Where(c => c.Action == RuleAction.Block).Select(Ref).ToList());

        return new RulesResponse(agent.SnapshotVersion ?? 0,
            rules.DynamicRules.Select(r => JsonSerializer.SerializeToElement(r)).ToList(),
            rules.TimeUpSessionRules.Select(r => JsonSerializer.SerializeToElement(r)).ToList(),
            youtube, TimeStateOf(agent, account, now));
    });

    private AgentMessage Heartbeat(HeartbeatRequest beat) => family.WithAgent<AgentMessage>((agent, now) =>
    {
        var account = Account(agent);
        agent.Heartbeat(account, now, beat.Focused && beat.Idle == "active" && beat.HasWebTab);
        var state = TimeStateOf(agent, account, now);
        _lastTimeState = state.State + state.RemainingMinutes;
        return state;
    });

    private AgentMessage RequestAccess(RequestAccessRequest request) => family.WithAgent<AgentMessage>((agent, now) =>
    {
        var account = Account(agent);
        if (account.MemberId is null) return new RequestAccessResponse("unavailable", null);
        var kind = request.Kind switch { "youtube" => RequestKind.YouTube, "app" => RequestKind.App, "time" => RequestKind.Time, _ => RequestKind.Url };
        var target = request.Target ?? "";
        var draft = new AccessRequestDraft(account.MemberId, agent.DeviceId, kind, target,
            HubCore.TargetKeyFor(kind, target, TimeCalculator.LocalDate(now, agent.View().Config.TimeZone)), request.Minutes, request.Note, now);
        AccessRequest? created = null;
        var result = agent.RequestAccess(draft, d => family.SendRequestToHub(d, out created));
        var id = created?.Id ?? (result == RequestResult.Queued ? agent.QueuedRequests[^1].Id : null);
        return new RequestAccessResponse(result switch
        {
            RequestResult.Created => "created", RequestResult.Merged => "merged", RequestResult.RateLimited => "rate_limited",
            RequestResult.Cooldown => "cooldown", RequestResult.Queued => "queued", _ => "unavailable",
        }, id);
    });

    private void PushTimeIfChanged()
    {
        try
        {
            var state = family.WithAgent((agent, now) => TimeStateOf(agent, Account(agent), now));
            var key = state.State + state.RemainingMinutes;
            if (key == _lastTimeState) return;
            _lastTimeState = key;
            _outgoing.Writer.TryWrite(state);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Time push skipped");
        }
    }

    /// <summary>The account on this device whose SID the OS reported; unknown SIDs are unlinked accounts.</summary>
    private DeviceAccount Account(AgentCore agent) =>
        agent.Device.Accounts.FirstOrDefault(a => string.Equals(a.AccountId, client.UserSid, StringComparison.OrdinalIgnoreCase))
        ?? new DeviceAccount(client.UserSid ?? "", client.UserName ?? "", null, client.IsAdminAccount);

    private static TimeStateMessage TimeStateOf(AgentCore agent, DeviceAccount account, DateTimeOffset now)
    {
        if (account.MemberId is null) return new TimeStateMessage("unlimited", null, null);
        var status = agent.TimeStatus(account.MemberId, now);
        return new TimeStateMessage(
            status.State switch { TimeState.Ok => "ok", TimeState.Warn => "warn", TimeState.Expired => "expired", TimeState.OutsideWindow => "outside_window", _ => "unlimited" },
            status.RemainingMinutes,
            status.WindowEndsAtMinute is { } w ? $"{w / 60:00}:{w % 60:00}" : null);
    }

    private static string Mode(DeviceAccount account, FamilyMember? member) =>
        member is not null ? Lower(member.FilterMode) : account.IsAdmin ? "unrestricted" : "allowlist";

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
