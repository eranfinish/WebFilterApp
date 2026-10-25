using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
using WebFilter.Service.Mapping;
using WebFilter.Service.Storage;

namespace WebFilter.Service.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-storage-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T16:00:00+02:00");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* WAL files may linger briefly */ }
    }

    private static FamilyConfig Fixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx")))
                return BundleMapper.ToConfig(FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(dir.FullName, "tests", "simulation", "fixtures", "family-base.json"))));
        throw new InvalidOperationException("WebFilter.slnx not found");
    }

    private SqliteDatabase Db(string name, string schema) => new(Path.Combine(_dir, name), schema);

    [Fact]
    public void Migrations_are_applied_once()
    {
        Assert.Equal(6, Db("hub.db", "hub").SchemaVersion);
        Assert.Equal(6, Db("hub.db", "hub").SchemaVersion);
        Assert.Equal(2, Db("agent.db", "agent").SchemaVersion);
    }

    [Fact]
    public void A_bundle_that_leaves_out_lists_reads_them_as_empty_and_the_service_starts_from_it()
    {
        const string minimal = """{"timezone":"Asia/Jerusalem","members":[{"id":"noa","displayName":"נועה","role":"child","age":8,"filterMode":"allowlist","youtubeMode":"off"}],"rules":null}""";
        var config = BundleMapper.ToConfig(FamilyBundleJson.Deserialize(minimal));
        Assert.Single(config.Members);
        Assert.Empty(config.Rules);
        Assert.Empty(config.Devices);
        Assert.Empty(config.Schedules);
        Assert.Empty(config.Categories);

        var bootstrap = Path.Combine(_dir, "minimal.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(bootstrap, minimal);
        using var family = new FamilyService(Microsoft.Extensions.Options.Options.Create(new ServiceOptions
        {
            Role = "hub", DeviceId = "pc", DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"), BootstrapBundlePath = bootstrap,
        }), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<FamilyService>.Instance);
        family.Start();
        Assert.Equal("noa", family.WithHub((hub, _) => hub.Config.Members.Single().Id));
    }

    [Fact]
    public void Bundle_mapping_round_trips_the_fixture()
    {
        var config = Fixture();
        var again = BundleMapper.ToConfig(BundleMapper.ToBundle(config, 3));
        Assert.Equal(
            FamilyBundleJson.Serialize(BundleMapper.ToBundle(config, 3)),
            FamilyBundleJson.Serialize(BundleMapper.ToBundle(again, 3)));
        Assert.Equal("Asia/Jerusalem", BundleMapper.ToBundle(config, 3).TimeZone);
    }

    /// <summary>PROTECTION-SPEC §1–§2 through the bundle: the flags and Windows features round-trip; an older bundle loads with the defaults.</summary>
    [Fact]
    public void Member_flags_and_windows_features_round_trip_and_default_when_missing()
    {
        var config = PolicyTests.WithFeatures(Fixture(), new WindowsFeatures(NotificationsAndSync: false, OneDrive: true, WebSearchInStart: true));
        config.Members[0] = config.Members[0] with { BlockImageSearch = true, JumpGuard = true, BlockYouTubeShorts = true, LogSearches = true, BlockAds = true, YouTubeChannelSearch = true };
        config.Members[1] = config.Members[1] with { JumpGuard = true };
        config.Members[2] = config.Members[2] with { BlockAds = true };

        var back = BundleMapper.ToConfig(FamilyBundleJson.Deserialize(FamilyBundleJson.Serialize(BundleMapper.ToBundle(config, 3))));

        Assert.Equal(config.Members, back.Members);
        Assert.Equal(new WindowsFeatures(NotificationsAndSync: false, OneDrive: true, WebSearchInStart: true), back.WindowsFeatures);
        var older = Fixture();
        Assert.Equal(WindowsFeatures.Default, older.WindowsFeatures);
        Assert.Equal((true, true, false), (older.WindowsFeatures.NotificationsAndSync, older.WindowsFeatures.OneDrive, older.WindowsFeatures.WebSearchInStart));
        Assert.All(older.Members, m => Assert.False(m.BlockImageSearch || m.JumpGuard || m.BlockYouTubeShorts || m.LogSearches || m.BlockAds || m.YouTubeChannelSearch));
    }

    /// <summary>PROTECTION-SPEC §13 through the bundle: the baseline switches round-trip; an older bundle has them all on.</summary>
    [Fact]
    public void The_baseline_round_trips_and_is_all_on_when_missing()
    {
        var config = PolicyTests.WithBaseline(Fixture(), new BaselineProtection(BlockAdult: false, JumpGuard: true, BlockAds: false, SafeSearch: true));
        var bundle = BundleMapper.ToBundle(config, 3);
        Assert.Equal(new BaselineDto(BlockAdult: false, JumpGuard: true, BlockAds: false, SafeSearch: true), bundle.Baseline);

        var back = BundleMapper.ToConfig(FamilyBundleJson.Deserialize(FamilyBundleJson.Serialize(bundle)));
        Assert.Equal(config.Baseline, back.Baseline);
        Assert.Equal(config.Baseline, back.Clone().Baseline);
        Assert.Equal(BaselineProtection.Default, Fixture().Baseline);
        Assert.Equal(new BaselineProtection(true, true, true, true), Fixture().Baseline);
    }

    /// <summary>A guest answer is carried like the link: through the bundle and a restart; a linked account is never a guest.</summary>
    [Fact]
    public void An_accounts_guest_flag_round_trips_survives_a_restart_and_is_dropped_once_linked()
    {
        var config = Fixture();
        var kids = config.Devices.FindIndex(d => d.Id == "pc-kids");
        config.Devices[kids] = config.Devices[kids] with { Accounts = [.. config.Devices[kids].Accounts.Select(a => a.Name == "visitor" ? a with { Guest = true } : a)] };

        var bundle = BundleMapper.ToBundle(config, 3);
        Assert.True(bundle.Devices.Single(d => d.Id == "pc-kids").Accounts.Single(a => a.Name == "visitor").Guest);
        var back = BundleMapper.ToConfig(FamilyBundleJson.Deserialize(FamilyBundleJson.Serialize(bundle)));
        Assert.Equal(config.Devices.SelectMany(d => d.Accounts), back.Devices.SelectMany(d => d.Accounts));

        var store = new HubStore(Db("hub.db", "hub"));
        store.Save(new HubCore(config).Export(), Now);
        var restored = HubCore.Restore(new HubStore(Db("hub.db", "hub")).Load()!);
        Assert.True(restored.Config.Device("pc-kids").Accounts.Single(a => a.Name == "visitor").Guest);

        // A bundle that links the account and still says guest (an older admin app, say): the link wins.
        var linked = bundle with
        {
            Devices = [.. bundle.Devices.Select(d => d.Id != "pc-kids" ? d : d with { Accounts = [.. d.Accounts.Select(a => a.Name == "visitor" ? a with { Member = "yoav" } : a)] })],
        };
        var visitor = BundleMapper.ToConfig(linked).Device("pc-kids").Accounts.Single(a => a.Name == "visitor");
        Assert.Equal(("yoav", false), (visitor.MemberId, visitor.Guest));
    }

    [Fact]
    public void Search_and_override_events_survive_a_restart_on_the_hub_and_in_the_agent_outbox()
    {
        var hub = new HubCore(Fixture());
        var agent = new AgentCore("pc-kids");
        var config = Fixture();
        config.Members[0] = config.Members[0] with { LogSearches = true };
        agent.Apply(new HubCore(config).Snapshot());
        Assert.True(agent.ReportSearch(agent.AccountOf(config.Members[0].Id), "google", "cats: and dogs", "https://www.google.com/search?q=cats", Now));
        agent.ReportOverride(agent.AccountOf("yoav"), "https://other.example/", Core.Decisions.ReasonCode.BlockedJump, Now);

        var agentStore = new AgentStore(Db("agent.db", "agent"));
        agentStore.Save(agent.Export());
        var outbox = new AgentStore(Db("agent.db", "agent")).Load("pc-kids")!.Outbox;
        Assert.Equal([EventType.Search, EventType.Override], outbox.Select(e => e.Type));
        Assert.Equal("google:cats: and dogs", outbox[0].Detail);
        Assert.Equal(Core.Decisions.ReasonCode.BlockedJump, outbox[1].Reason);

        hub.Ingest(new AgentUpload("pc-kids", new UsageLedger(), outbox, []));
        new HubStore(Db("hub.db", "hub")).Save(hub.Export(), Now);
        var events = new HubStore(Db("hub.db", "hub")).Load()!.Events;
        Assert.Contains(events, e => e.Type == EventType.Search && e.Detail == "google:cats: and dogs" && e.Url == "https://www.google.com/search?q=cats");
        Assert.Contains(events, e => e.Type == EventType.Override && e.Reason == Core.Decisions.ReasonCode.BlockedJump);
    }

    [Fact]
    public void Notifications_have_stable_ids_and_strictly_increasing_times()
    {
        var store = new NotificationStore(Db("hub.db", "hub"));
        Assert.True(store.Add("request:ar-1", "request", Now, "noa", "pc-kids", "url:pbskids.org"));
        Assert.False(store.Add("request:ar-1", "request", Now.AddMinutes(1)));
        Assert.True(store.Add("tamper:x", "tamper", Now, null, "pc-kids", "unclean_stop")); // same clock reading
        Assert.True(store.Add("device:y", "device", Now.AddMinutes(-5), deviceId: "laptop-yoav")); // the clock went back

        var all = store.Since(null);
        Assert.Equal(["request:ar-1", "tamper:x", "device:y"], all.Select(n => n.Id));
        Assert.True(all[0].At < all[1].At && all[1].At < all[2].At);
        Assert.Equal(["tamper:x", "device:y"], store.Since(all[0].At).Select(n => n.Id));
        Assert.Empty(store.Since(all[2].At));

        // A restart continues after the last time, and retention drops the old ones.
        var again = new NotificationStore(Db("hub.db", "hub"));
        Assert.True(again.Add("request:ar-2", "request", Now.AddMinutes(-10)));
        Assert.True(again.Since(all[2].At).Single().At > all[2].At);
        again.Prune(Now.AddDays(40));
        Assert.Empty(again.Since(null));
    }

    [Fact]
    public void Hub_state_survives_a_restart()
    {
        var hub = new HubCore(Fixture());
        hub.AddRule(new Rule("r-new", "yoav", "chess.example", PatternType.Domain, RuleAction.Block, ExpiresAt: Now.AddHours(1)), Now);
        hub.Pause("noa", Now.AddMinutes(30), Now);
        hub.ExtendTime("yoav", 15, Now);
        hub.SetMember(hub.Config.Member("yoav") with { BlockAds = true }, Now);
        hub.Usage.Mark("noa", "pc-kids", Now);
        hub.Usage.Mark("noa", "pc-kids", Now.AddMinutes(1));
        hub.CreateRequest(new AccessRequestDraft("noa", "pc-kids", RequestKind.YouTube, "UCnatgeoKids000000000004", "UCnatgeoKids000000000004", null, "please", Now), out var request);

        var store = new HubStore(Db("hub.db", "hub"));
        store.Save(hub.Export(), Now);

        var loaded = new HubStore(Db("hub.db", "hub")).Load();
        Assert.NotNull(loaded);
        var restored = HubCore.Restore(loaded);
        Assert.Equal(hub.Version, restored.Version);
        Assert.Equal((true, false), (restored.Config.Member("yoav").BlockAds, restored.Config.Member("noa").BlockAds));
        Assert.Contains(restored.Config.Rules, r => r.Id == "r-new" && r.ExpiresAt == Now.AddHours(1));
        Assert.Equal(Now.AddMinutes(30), restored.RuntimeOf("noa").PausedUntil);
        Assert.Single(restored.RuntimeOf("yoav").Grants);
        Assert.Equal(2, restored.Usage.MinutesOn("noa", "pc-kids").Count);
        Assert.Equal(hub.Events.Count, restored.Events.Count);
        var restoredRequest = Assert.Single(restored.Requests);
        Assert.Equal(request!.Id, restoredRequest.Id);
        Assert.Equal("please", restoredRequest.Message);

        // Counters continue: the next request gets a fresh id.
        restored.CreateRequest(new AccessRequestDraft("yoav", "pc-kids", RequestKind.YouTube, "UCgamerX0000000000000003", "UCgamerX0000000000000003", null, null, Now), out var next);
        Assert.NotEqual(request.Id, next!.Id);
    }

    /// <summary>
    /// PROTECTION-SPEC §15: a YouTube request's channel names and search survive a restart on the hub (access_requests, 006)
    /// and in an agent's queue (its JSON); a request without them has none.
    /// </summary>
    [Fact]
    public void A_youtube_requests_names_survive_a_restart_on_the_hub_and_in_the_agent_queue()
    {
        const string channel = "UCnatgeoKids000000000004";
        var hub = new HubCore(Fixture());
        hub.CreateRequest(new AccessRequestDraft("noa", "pc-kids", RequestKind.YouTube, channel, channel, null, null, Now, "NatGeo Kids", "@natgeokids", "animals"), out _);
        hub.CreateRequest(new AccessRequestDraft("noa", "pc-kids", RequestKind.Url, "https://pbskids.org/", "pbskids.org", null, null, Now), out _);
        new HubStore(Db("hub.db", "hub")).Save(hub.Export(), Now);

        var requests = HubCore.Restore(new HubStore(Db("hub.db", "hub")).Load()!).Requests;
        Assert.Equal(("NatGeo Kids", "@natgeokids", "animals"), (requests[0].Title, requests[0].Handle, requests[0].SearchQuery));
        Assert.Equal(((string?)null, (string?)null, (string?)null), (requests[1].Title, requests[1].Handle, requests[1].SearchQuery));

        var agent = new AgentCore("pc-kids") { HubReachable = false };
        agent.Apply(hub.Snapshot());
        agent.RequestAccess(new AccessRequestDraft("noa", "pc-kids", RequestKind.YouTube, channel, channel, null, null, Now, "NatGeo Kids", "@natgeokids", "animals"),
            _ => RequestResult.Queued);
        new AgentStore(Db("agent.db", "agent")).Save(agent.Export());
        var queued = Assert.Single(AgentCore.Restore(new AgentStore(Db("agent.db", "agent")).Load("pc-kids")!).QueuedRequests);
        Assert.Equal(("NatGeo Kids", "@natgeokids", "animals"), (queued.Title, queued.Handle, queued.SearchQuery));
    }

    [Fact]
    public void Hub_saves_are_incremental()
    {
        var hub = new HubCore(Fixture());
        var store = new HubStore(Db("hub.db", "hub"));
        hub.Usage.Mark("noa", "pc-kids", Now);
        hub.Pause("noa", Now.AddMinutes(10), Now);
        store.Save(hub.Export(), Now);
        hub.Usage.Mark("noa", "pc-kids", Now.AddMinutes(1));
        hub.Resume("noa", Now.AddMinutes(1));
        store.Save(hub.Export(), Now.AddMinutes(1));

        var loaded = new HubStore(Db("hub.db", "hub")).Load()!;
        Assert.Equal(2, loaded.Usage.MinutesOn("noa", "pc-kids").Count);
        Assert.Equal(hub.Events.Count, loaded.Events.Count);
        Assert.Null(loaded.Runtime["noa"].PausedUntil);
    }

    [Fact]
    public void Agent_state_survives_a_restart_including_unsent_work()
    {
        var hub = new HubCore(Fixture());
        var agent = new AgentCore("laptop-yoav");
        agent.Apply(hub.Snapshot());
        agent.HubReachable = false;
        agent.LocalPause("yoav", Now.AddMinutes(20), Now);
        var draft = new AccessRequestDraft("yoav", "laptop-yoav", RequestKind.YouTube, "UCgamerX0000000000000003", "UCgamerX0000000000000003", null, null, Now);
        Assert.Equal(RequestResult.Queued, agent.RequestAccess(draft, _ => throw new InvalidOperationException("hub is offline")));

        var store = new AgentStore(Db("agent.db", "agent"));
        store.Save(agent.Export());

        var restored = AgentCore.Restore(new AgentStore(Db("agent.db", "agent")).Load("laptop-yoav")!);
        Assert.Equal(hub.Version, restored.SnapshotVersion);
        Assert.Single(restored.QueuedRequests);
        var upload = restored.TakeUpload();
        Assert.Contains(upload.Events, e => e.Detail == "local_pause");
        Assert.Single(upload.QueuedRequests);

        // After the upload is delivered and saved, nothing is pending any more.
        store.Save(restored.Export());
        var again = new AgentStore(Db("agent.db", "agent")).Load("laptop-yoav")!;
        Assert.Empty(again.Outbox);
        Assert.Empty(again.QueuedRequests);
        Assert.Equal(Now.AddMinutes(20), again.LocalPauses["yoav"]);
    }

    /// <summary>The decisions an agent announced stay announced after a restart, and go once the hub stops sending them.</summary>
    [Fact]
    public void The_decisions_an_agent_saw_survive_a_restart()
    {
        var hub = new HubCore(Fixture());
        var draft = new AccessRequestDraft("yoav", "laptop-yoav", RequestKind.Url, "https://www.instagram.com/", "instagram.com", null, null, Now);
        hub.CreateRequest(draft, out var request);
        hub.Approve(request!.Id, ApproveScope.Host, ApprovalDuration.For(30), null, Now);
        var agent = new AgentCore("laptop-yoav");
        Assert.Single(agent.Apply(hub.SnapshotForAgents(Now)));
        var store = new AgentStore(Db("agent.db", "agent"));
        store.Save(agent.Export());

        var restored = AgentCore.Restore(new AgentStore(Db("agent.db", "agent")).Load("laptop-yoav")!);

        Assert.Equal(Now, restored.DecisionsSeen[request.Id]);
        Assert.Empty(restored.Apply(hub.SnapshotForAgents(Now.AddMinutes(1))));
        Assert.Empty(restored.Apply(hub.SnapshotForAgents(Now.AddHours(49))));
        store.Save(restored.Export());
        Assert.Empty(new AgentStore(Db("agent.db", "agent")).Load("laptop-yoav")!.DecisionsSeen!);
    }
}
