using WebFilter.Core.Engine;
using WebFilter.Core.Model;

namespace WebFilter.Core.Tests.Engine;

/// <summary>
/// What a device's account report tells the host (<see cref="HubCore.AccountsAdded"/>): the accounts that are new, as
/// stored, and whether the device had none before — its first report, which the hub does not notify the parents about
/// (PROTECTION-SPEC §10).
/// </summary>
public class AccountReportTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00");
    private static readonly DeviceAccount Owner = new("S-1-5-21-4000-500", "owner", null, IsAdmin: true);
    private static readonly DeviceAccount Maya = new("S-1-5-21-4000-1001", "maya", null, IsAdmin: false);
    private static readonly DeviceAccount Tom = new("S-1-5-21-4000-1002", "tom", null, IsAdmin: false);

    [Fact]
    public void New_accounts_are_raised_once_with_whether_the_device_reported_before()
    {
        var hub = new HubCore(new FamilyConfig { TimeZone = TimeZoneInfo.Utc, Devices = [new Device("pc-hub", "windows", "hub", [])] });
        hub.AddDevice("pc-new", "windows", Now);
        var raised = new List<NewAccounts>();
        hub.AccountsAdded += raised.Add;

        hub.ReportAccounts("pc-new", [Owner, Maya], Now);
        hub.ReportAccounts("pc-new", [Owner, Maya with { Name = "maya-renamed", IsAdmin = true }], Now); // nothing new
        hub.ReportAccounts("pc-new", [Owner, Maya, Tom with { MemberId = "noa", Guest = true }], Now);
        hub.ReportAccounts("pc-unknown", [Tom], Now); // not in the settings: ignored

        Assert.Equal([("pc-new", true), ("pc-new", false)], raised.Select(r => (r.DeviceId, r.FirstReport)));
        Assert.Equal([Owner, Maya], raised[0].Accounts);
        // As stored: unlinked and not a guest, whatever the computer sent.
        Assert.Equal([Tom], raised[1].Accounts);
        Assert.Equal(3, hub.Config.Device("pc-new").Accounts.Count);
    }
}
