using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;

namespace WebFilter.Core.Tests.Engine;

/// <summary>
/// An unlinked account the parents chose to keep as a guest: the flag records their answer and survives the device's
/// account reports, but filtering is that of every unlinked account (MVP-PLAN §5.3, tests/simulation/README.md §8).
/// </summary>
public class GuestAccountTests
{
    private const string Visitor = "S-1-5-21-2000-1005";
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00");

    [Fact]
    public void A_report_keeps_a_known_accounts_guest_flag_and_never_brings_one()
    {
        var hub = new HubCore(WithVisitorAsGuest());

        hub.ReportAccounts("pc-kids",
        [
            new DeviceAccount(Visitor, "visitor-renamed", null, IsAdmin: false),
            new DeviceAccount("S-1-5-21-2000-1009", "cousin", "noa", IsAdmin: false, Guest: true),
        ], Now);

        var accounts = hub.Config.Device("pc-kids").Accounts;
        Assert.Equal(new DeviceAccount(Visitor, "visitor-renamed", null, false, Guest: true), accounts.Single(a => a.AccountId == Visitor));
        // A new account is unlinked and not a guest, whatever the computer sent: both are the parents' answers.
        Assert.Equal(new DeviceAccount("S-1-5-21-2000-1009", "cousin", null, false, Guest: false), accounts.Single(a => a.Name == "cousin"));
    }

    [Fact]
    public void A_guest_is_filtered_exactly_like_any_unlinked_account()
    {
        var plain = new HubCore(Fixture());
        var guest = new HubCore(WithVisitorAsGuest());

        Assert.Equal(
            NetworkPolicy.Desired(plain.View(), "pc-kids", Now).Single(p => p.UserSid == Visitor),
            NetworkPolicy.Desired(guest.View(), "pc-kids", Now).Single(p => p.UserSid == Visitor),
            PolicyComparer.Instance);

        var (before, after) = (Agent(plain), Agent(guest));
        foreach (var url in new[] { "https://www.wikipedia.org/", "https://mashov.info/", "https://www.youtube.com/" })
        {
            var expected = before.DecideUrl(before.AccountNamed("visitor"), url, Now);
            var actual = after.DecideUrl(after.AccountNamed("visitor"), url, Now);
            Assert.Equal((expected.Decision, expected.Reason), (actual.Decision, actual.Reason));
        }
        Assert.Equal(ReasonCode.BlockedNotInAllowlist, after.DecideUrl(after.AccountNamed("visitor"), "https://www.wikipedia.org/", Now).Reason);
        Assert.Equal(ReasonCode.BlockedAppNetwork, after.DecideApp(after.AccountNamed("visitor"), new AppIdentity(Path: @"C:\Games\Minecraft\minecraft.exe"), Now).Reason);
        Assert.Equal(
            ExtensionPolicy.For(before.View(), before.AccountNamed("visitor"), Now) with { UrlPolicy = null },
            ExtensionPolicy.For(after.View(), after.AccountNamed("visitor"), Now) with { UrlPolicy = null });
    }

    private static AgentCore Agent(HubCore hub)
    {
        var agent = new AgentCore("pc-kids");
        agent.Apply(hub.Snapshot());
        return agent;
    }

    private static FamilyConfig WithVisitorAsGuest()
    {
        var config = Fixture();
        var index = config.Devices.FindIndex(d => d.Id == "pc-kids");
        config.Devices[index] = config.Devices[index] with
        {
            Accounts = [.. config.Devices[index].Accounts.Select(a => a.AccountId == Visitor ? a with { Guest = true } : a)],
        };
        return config;
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);

    private sealed class PolicyComparer : IEqualityComparer<UserNetworkPolicy>
    {
        public static readonly PolicyComparer Instance = new();

        public bool Equals(UserNetworkPolicy? x, UserNetworkPolicy? y) => x is not null && y is not null && x.UserSid == y.UserSid && x.Equivalent(y);

        public int GetHashCode(UserNetworkPolicy obj) => obj.UserSid.GetHashCode(StringComparison.Ordinal);
    }
}
