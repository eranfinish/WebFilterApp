using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;
using static WebFilter.Core.Tests.Dnr.DnrTestPolicies;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// The rules the extension gets (<see cref="ExtensionPolicy"/> compiled to DNR) must agree with <see cref="Decider.Url"/>
/// in every member state — normal, paused, homework — so the browser blocks exactly what the service would.
/// </summary>
public class ExtensionPolicyTests
{
    private static readonly DateTimeOffset InWindow = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00"); // Monday, inside both windows
    private static readonly DeviceAccount Noa = new("S-1-5-21-2000-1001", "noa", "noa", false);
    private static readonly DeviceAccount Yoav = new("S-1-5-21-2000-1002", "yoav", "yoav", false);

    private static readonly string[] Urls =
    [
        "https://www.khanacademy.org/math", "https://en.wikipedia.org/wiki/Cat", "https://mashov.info/grades", "https://www.instagram.com/",
        "https://www.roblox.com/games", "https://chess.example/", "https://www.youtube.com/watch?v=abc", "https://youtu.be/abc",
        "https://scratch.mit.edu/projects", "https://mit.edu/", "http://192.168.1.10/", "https://news.example/today",
    ];

    public static TheoryData<string> States() => ["normal", "paused", "homework"];

    [Theory]
    [MemberData(nameof(States))]
    public void Compiled_rules_agree_with_the_service_decision(string state)
    {
        var hub = new HubCore(Fixture());
        foreach (var member in new[] { "noa", "yoav" })
        {
            if (state == "paused") hub.Pause(member, InWindow.AddHours(1), InWindow);
            if (state == "homework") hub.StartHomework(member, 60, InWindow);
        }
        var view = hub.View();

        foreach (var account in new[] { Noa, Yoav })
        {
            var effective = ExtensionPolicy.For(view, account, InWindow);
            var rules = effective.UrlPolicy is null ? [] : DnrCompiler.Compile(effective.UrlPolicy, Options).DynamicRules;
            foreach (var url in Urls)
            {
                var service = Decider.Url(view, account.MemberId!, url, InWindow).Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                Assert.True(service == DnrSimulator.Evaluate(rules, url), $"{state} {account.Name} {url}: service {service}");
            }
        }
    }

    [Fact]
    public void Paused_and_homework_turn_time_off_and_say_when_they_end()
    {
        var hub = new HubCore(Fixture());
        hub.Pause("yoav", InWindow.AddMinutes(45), InWindow);
        hub.StartHomework("noa", 30, InWindow);
        var view = hub.View();

        var yoav = ExtensionPolicy.For(view, Yoav, InWindow);
        Assert.Equal((null, YouTubeMode.Unrestricted, false, InWindow.AddMinutes(45)), (yoav.UrlPolicy, yoav.YouTube, yoav.TimeApplies, yoav.ValidUntil));

        var noa = ExtensionPolicy.For(view, Noa, InWindow);
        Assert.Equal((YouTubeMode.Off, false, InWindow.AddMinutes(30)), (noa.YouTube, noa.TimeApplies, noa.ValidUntil));

        var normal = ExtensionPolicy.For(hub.View(), Noa, InWindow.AddMinutes(31));
        Assert.Equal((YouTubeMode.Allowlist, true), (normal.YouTube, normal.TimeApplies));
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}
