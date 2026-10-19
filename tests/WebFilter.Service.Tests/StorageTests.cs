using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
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
        Assert.Equal(3, Db("hub.db", "hub").SchemaVersion);
        Assert.Equal(3, Db("hub.db", "hub").SchemaVersion);
        Assert.Equal(1, Db("agent.db", "agent").SchemaVersion);
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

    [Fact]
    public void Hub_state_survives_a_restart()
    {
        var hub = new HubCore(Fixture());
        hub.AddRule(new Rule("r-new", "yoav", "chess.example", PatternType.Domain, RuleAction.Block, ExpiresAt: Now.AddHours(1)), Now);
        hub.Pause("noa", Now.AddMinutes(30), Now);
        hub.ExtendTime("yoav", 15, Now);
        hub.Usage.Mark("noa", "pc-kids", Now);
        hub.Usage.Mark("noa", "pc-kids", Now.AddMinutes(1));
        hub.CreateRequest(new AccessRequestDraft("noa", "pc-kids", RequestKind.YouTube, "UCnatgeoKids000000000004", "UCnatgeoKids000000000004", null, "please", Now), out var request);

        var store = new HubStore(Db("hub.db", "hub"));
        store.Save(hub.Export(), Now);

        var loaded = new HubStore(Db("hub.db", "hub")).Load();
        Assert.NotNull(loaded);
        var restored = HubCore.Restore(loaded);
        Assert.Equal(hub.Version, restored.Version);
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
}
