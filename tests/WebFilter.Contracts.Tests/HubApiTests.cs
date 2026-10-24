using System.Text.Json;
using WebFilter.Contracts.Hub;

namespace WebFilter.Contracts.Tests;

public class HubApiTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-24T23:26:44+03:00");

    /// <summary>MVP-PLAN §10.3: the snapshot's recent decisions, by these names; <c>until</c> only when the approval ends.</summary>
    [Fact]
    public void A_snapshot_carries_its_decisions_by_the_wire_names()
    {
        var snapshot = new SnapshotDto(20, new FamilyBundle { TimeZone = "Asia/Jerusalem" }, new Dictionary<string, RuntimeDto>(), [],
        [
            new DecisionDto("ar-1", "noa", "lab-kids", "url", "ynet.co.il", "approved", At),
            new DecisionDto("lab-kids-q1", "noa", "lab-kids", "app", @"c:\games\game.exe", "approved", At, At.AddMinutes(30)),
        ]);

        var json = JsonSerializer.Serialize(snapshot, HubApiJsonContext.Default.SnapshotDto);

        Assert.Contains("""
            "decisions":[{"id":"ar-1","member":"noa","device":"lab-kids","kind":"url","targetKey":"ynet.co.il","status":"approved","decidedAt":"2026-10-24T23:26:44+03:00"},
            """, json, StringComparison.Ordinal);
        Assert.Contains("\"until\":\"2026-10-24T23:56:44+03:00\"", json, StringComparison.Ordinal);
        Assert.Equal(snapshot.Decisions, JsonSerializer.Deserialize(json, HubApiJsonContext.Default.SnapshotDto)!.Decisions);
    }

    /// <summary>
    /// Either side may be older: a snapshot without decisions (an older hub) reads as none, and writes none; an older agent
    /// reads a newer snapshot because names it does not know are skipped.
    /// </summary>
    [Fact]
    public void Snapshots_with_and_without_decisions_are_read_by_either_version()
    {
        var older = JsonSerializer.Deserialize("""{"version":19,"settings":{"timeZone":"Asia/Jerusalem"},"runtime":{},"usage":[]}""", HubApiJsonContext.Default.SnapshotDto)!;
        Assert.Equal(19, older.Version);
        Assert.Null(older.Decisions);
        Assert.DoesNotContain("decisions", JsonSerializer.Serialize(older, HubApiJsonContext.Default.SnapshotDto), StringComparison.Ordinal);

        var newer = JsonSerializer.Deserialize("""{"version":21,"settings":{"timeZone":"Asia/Jerusalem"},"runtime":{},"usage":[],"somethingNewer":[{"x":1}]}""", HubApiJsonContext.Default.SnapshotDto)!;
        Assert.Equal(21, newer.Version);
    }
}
