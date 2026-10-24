using System.Text.Json;
using System.Threading.Channels;
using WebFilter.Contracts.Agent;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
using WebFilter.Core.Urls;
using WebFilter.Service.Guard;

namespace WebFilter.Service.Pipes;

/// <summary>
/// One native host connection: answers agent-protocol requests and pushes settings and time changes. From its hello on, it
/// tells <paramref name="connections"/> that the extension of that browser is connected for the client's account, and when
/// it was last heard from (the extension guard, PROTECTION-SPEC §14).
/// </summary>
internal sealed class AgentSession(Stream pipe, ClientIdentity client, FamilyService family, ILogger log, ExtensionConnections? connections = null)
{
    private readonly Channel<AgentMessage> _outgoing = Channel.CreateUnbounded<AgentMessage>(new UnboundedChannelOptions { SingleReader = true });
    private ExtensionConnections.Session? _connection;
    private string? _extensionId;
    private string? _lastTimeState;
    // When the rules last sent stop being right on their own (a temporary rule expires), in UTC ticks.
    private long _rulesValidUntil = long.MaxValue;

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteLoopAsync(linked.Token);
        void OnSettings(int version) => _outgoing.Writer.TryWrite(new RulesChangedMessage(version));
        void OnTick(DateTimeOffset now)
        {
            PushTimeIfChanged();
            if (now.UtcTicks >= Interlocked.Read(ref _rulesValidUntil) && Interlocked.Exchange(ref _rulesValidUntil, long.MaxValue) != long.MaxValue)
                _outgoing.Writer.TryWrite(new RulesChangedMessage(family.WithAgent((agent, _) => agent.SnapshotVersion ?? 0)));
        }
        void OnDecided(AccessRequest request) => PushDecision(request);
        family.SettingsChanged += OnSettings;
        family.Ticked += OnTick;
        family.RequestDecided += OnDecided;
        try
        {
            while (await AgentProtocol.ReadLenientAsync(pipe, linked.Token) is { } message)
            {
                _connection?.Heard(family.Now);
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
            _connection?.Close();
            family.SettingsChanged -= OnSettings;
            family.Ticked -= OnTick;
            family.RequestDecided -= OnDecided;
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
        ReportBlockRequest block => ReportBlock(block),
        ReportSearchRequest search => ReportSearch(search),
        ReportOverrideRequest overridden => family.WithAgent<AgentMessage>((agent, now) =>
        {
            var reason = ReasonCodeExtensions.ParseOrNone(overridden.Reason);
            // A parent's own "open anyway" (PROTECTION-SPEC §13) is acknowledged and not recorded.
            var recorded = agent.ReportOverride(Account(agent), Cap(overridden.Url ?? "", MaxUrlLength), reason == ReasonCode.None ? ReasonCode.BlockedJump : reason, now);
            return new ErrorMessage("ok", recorded ? "recorded" : "not recorded");
        }),
        ErrorMessage { Code: "dnr_rules_dropped" } dropped => family.WithAgent<AgentMessage>((agent, now) =>
        {
            var detail = "dnr_rules_dropped: " + dropped.Message;
            agent.ReportProblem(Account(agent), detail.Length > 500 ? detail[..500] : detail, now);
            return new ErrorMessage("ok", "recorded");
        }),
        UnknownMessage unknown => new ErrorMessage("unsupported", $"Unknown message type '{unknown.Type}'."),
        DecideUrlRequest decide => DecideUrl(decide.Url),
        RequestAccessRequest request => RequestAccess(request),
        ResolveYouTubeRequest resolve => family.WithAgent<AgentMessage>((agent, _) =>
            new ResolveYouTubeResponse(agent.View().Config.YouTubeChannels.FirstOrDefault(c =>
                resolve.Handle is not null && string.Equals(c.Handle, resolve.Handle, StringComparison.OrdinalIgnoreCase))?.ChannelId)),
        _ => new ErrorMessage("unsupported", $"Unsupported message type {message.GetType().Name}."),
    };

    private AgentMessage Hello(HelloRequest hello)
    {
        _extensionId = hello.ExtensionId;
        // The account is the OS's (the client's token); the browser and the extension id are what the hello says.
        if (connections is not null && client.UserSid is { Length: > 0 } sid && BrowserKinds.TryParse(hello.Browser, out var browser))
        {
            _connection?.Close();
            _connection = connections.Open(sid, browser, hello.ExtensionId, family.Now);
        }
        return family.WithAgent<AgentMessage>((agent, now) =>
        {
            var account = Account(agent);
            var member = account.MemberId is null ? null : agent.View().Config.Member(account.MemberId);
            return new HelloResponse(
                new AgentUser(account.AccountId, client.UserName ?? account.Name, member is null ? "unlinked" : Lower(member.Role), account.IsAdmin),
                Mode(account, member, ExtensionPolicy.For(agent.View(), account, now)), "service", true, agent.SnapshotVersion ?? 0,
                typeof(AgentSession).Assembly.GetName().Version?.ToString() ?? "0");
        });
    }

    private AgentMessage Rules() => family.WithAgent<AgentMessage>((agent, now) =>
    {
        var view = agent.View();
        var account = Account(agent);
        var effective = ExtensionPolicy.For(view, account, now);
        var options = new DnrCompileOptions(_extensionId ?? "unknown-extension-id");
        var rules = effective.UrlPolicy is null ? new DnrRuleSet([], []) : DnrCompiler.Compile(effective.UrlPolicy, options);
        Interlocked.Exchange(ref _rulesValidUntil, effective.ValidUntil == DateTimeOffset.MaxValue ? long.MaxValue : effective.ValidUntil.UtcTicks);

        // One record per channel with its effective action: the member's own record beats a global one.
        var channels = account.MemberId is not { } memberId ? [] : view.Config.YouTubeChannels
            .Where(c => c.MemberId is null || c.MemberId == memberId)
            .GroupBy(c => c.ChannelId, StringComparer.Ordinal)
            .Select(g => g.FirstOrDefault(c => c.MemberId is not null) ?? g.First())
            .ToList();
        YouTubeChannelRef Ref(YouTubeChannel c) => new(c.ChannelId, c.Handle, c.Title);
        var youtube = new YouTubePolicy(Lower(effective.YouTube),
            channels.Where(c => c.Action == RuleAction.Allow).Select(Ref).ToList(),
            channels.Where(c => c.Action == RuleAction.Block).Select(Ref).ToList());

        var features = effective.Features;
        var member = account.MemberId is null ? null : view.Config.Member(account.MemberId);
        return new RulesResponse(agent.SnapshotVersion ?? 0,
            rules.DynamicRules.Select(r => JsonSerializer.SerializeToElement(r)).ToList(),
            rules.TimeUpSessionRules.Select(r => JsonSerializer.SerializeToElement(r)).ToList(),
            youtube, TimeStateOf(agent, account, now),
            new AgentFeatures(features.JumpGuard, features.BlockYouTubeShorts, features.LogSearches, features.CanOverrideJump, features.BlockAds),
            Mode(account, member, effective));
    });

    private const int MaxUrlLength = 2048;
    private const int MaxQueryLength = 200;

    private static string Cap(string text, int length) => text.Length > length ? text[..length] : text;

    /// <summary>
    /// A search from the extension (PROTECTION-SPEC §9): recorded only when the member's searches are logged, always
    /// acknowledged. The engine must be a short lower-case name, since the event's detail is "engine:query".
    /// </summary>
    private AgentMessage ReportSearch(ReportSearchRequest search)
    {
        var engine = search.Engine ?? "";
        if (engine.Length is 0 or > 32 || !engine.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            return new ErrorMessage("bad_request", "engine must be a lower-case name such as google.");
        var query = Cap((search.Query ?? "").Trim(), MaxQueryLength);
        if (query.Length == 0) return new ErrorMessage("ok", "not recorded");
        var recorded = family.WithAgent((agent, now) =>
            agent.ReportSearch(Account(agent), engine, query, search.Url is { } url ? Cap(url, MaxUrlLength) : null, now));
        return new ErrorMessage("ok", recorded ? "recorded" : "not recorded");
    }

    private AgentMessage DecideUrl(string url) => family.WithAgent<AgentMessage>((agent, now) =>
    {
        var e = Explain(agent, Account(agent), url, now);
        return new DecideUrlResponse(e.Decision, e.Reason, e.RuleId, e.Category, e.CanRequest, e.RequestKind);
    });

    /// <summary>
    /// Records a block the extension enforced and answers with the service's own reason. YouTube channel and Shorts
    /// blocks are decided in the page and jump blocks by how the page was opened, so the extension's reason stands for
    /// those. If the service would allow the URL, the extension's rules are stale: nothing is recorded and rulesChanged
    /// is pushed. A parent's own block (PROTECTION-SPEC §13) is answered and never recorded (AgentCore.ReportBlock).
    /// </summary>
    private AgentMessage ReportBlock(ReportBlockRequest block)
    {
        var stale = false;
        var reply = family.WithAgent<AgentMessage>((agent, now) =>
        {
            var account = Account(agent);
            var reported = ReasonCodeExtensions.ParseOrNone(block.Reason);
            var e = reported switch
            {
                ReasonCode.BlockedYouTubeChannel or ReasonCode.BlockedYouTubeUnknown =>
                    new Explanation("block", reported.ToWire(), block.RuleId, null, account.MemberId is not null && !agent.IsParent(account), "youtube"),
                // A parent's approval could not lift these: Shorts and jumps are blocked whatever the site rules say.
                ReasonCode.BlockedYouTubeShorts => new Explanation("block", reported.ToWire(), block.RuleId, null, false, "youtube"),
                ReasonCode.BlockedJump => new Explanation("block", reported.ToWire(), block.RuleId, null, false, "url"),
                _ => Explain(agent, account, block.Url, now),
            };
            if (e.Decision == "allow") stale = true;
            else agent.ReportBlock(account, block.Url, ReasonCodes.Parse(e.Reason), now);
            return new ReportBlockResult(e.Decision, e.Reason, e.RuleId, e.Category, e.CanRequest, e.RequestKind);
        });
        if (stale) _outgoing.Writer.TryWrite(new RulesChangedMessage(family.WithAgent((agent, _) => agent.SnapshotVersion ?? 0)));
        return reply;
    }

    private sealed record Explanation(string Decision, string Reason, string? RuleId, string? Category, bool CanRequest, string RequestKind);

    private static Explanation Explain(AgentCore agent, DeviceAccount account, string url, DateTimeOffset now)
    {
        var result = agent.ExplainUrl(account, url, now);
        string? category = null;
        if (result.Reason == ReasonCode.BlockedCategory && account.MemberId is { } memberId && UrlNormalizer.TryNormalize(url, out var normalized))
        {
            var config = agent.View().Config;
            category = config.MemberCategories
                .Where(mc => mc.MemberId == memberId && mc.Action == RuleAction.Block)
                .Select(mc => mc.Category)
                .FirstOrDefault(c => RuleEvaluator.IsInCategory(config.Categories, c, normalized));
        }
        var aboutTime = result.Reason is ReasonCode.BlockedTimeWindow or ReasonCode.BlockedDailyLimit;
        // An approved site cannot lift a built-in block (image search, AI Mode), so there is nothing to request; a parent
        // does not ask anyone (PROTECTION-SPEC §13: the protection is turned off in the admin app's settings).
        var canRequest = account.MemberId is not null && result.Decision != Decision.Allow && !BuiltinRules.IsBuiltinBlock(result.RuleId)
            && !agent.IsParent(account);
        return new Explanation(result.Decision == Decision.Allow ? "allow" : "block", result.Reason.ToWire(), result.RuleId, category,
            canRequest, aboutTime ? "time" : "url");
    }

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
        // Unlinked accounts have no one to ask for; parents' own accounts are never offered a request (PROTECTION-SPEC §13).
        if (account.MemberId is null || agent.IsParent(account)) return new RequestAccessResponse("unavailable", null);
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

    private void PushDecision(AccessRequest request)
    {
        var (mine, until) = family.WithAgent((agent, _) => (Account(agent).MemberId == request.MemberId,
            request.CreatedRuleId is { } ruleId ? agent.View().Config.Rules.FirstOrDefault(r => r.Id == ruleId)?.ExpiresAt : null));
        if (!mine) return;
        _outgoing.Writer.TryWrite(new AccessDecidedMessage(request.Id, request.Status.ToString().ToLowerInvariant(), until));
        if (request.Kind == RequestKind.Time) PushTimeIfChanged();
    }

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
        // Time does not apply while paused or in homework (MVP-PLAN §6), so the extension must not install its time-up rules.
        if (account.MemberId is null || !ExtensionPolicy.For(agent.View(), account, now).TimeApplies) return new TimeStateMessage("unlimited", null, null);
        var status = agent.TimeStatus(account.MemberId, now);
        return new TimeStateMessage(
            status.State switch { TimeState.Ok => "ok", TimeState.Warn => "warn", TimeState.Expired => "expired", TimeState.OutsideWindow => "outside_window", _ => "unlimited" },
            status.RemainingMinutes,
            status.WindowEndsAtMinute is { } w ? $"{w / 60:00}:{w % 60:00}" : null);
    }

    /// <summary>
    /// The mode the hello and rules replies give: baseline for a parent's account while the family's baseline protection is
    /// on (PROTECTION-SPEC §13, <see cref="ExtensionPolicy.Baseline"/>), else the member's filter mode, unrestricted for an
    /// administrator linked to nobody, allowlist for an unlinked standard account.
    /// </summary>
    private static string Mode(DeviceAccount account, FamilyMember? member, ExtensionPolicy effective) =>
        effective.Baseline ? "baseline" : member is not null ? Lower(member.FilterMode) : account.IsAdmin ? "unrestricted" : "allowlist";

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
