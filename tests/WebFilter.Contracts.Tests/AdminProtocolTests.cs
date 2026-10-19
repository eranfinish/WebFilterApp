using System.Text;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Contracts.Tests;

public class AdminProtocolTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-01T16:00:00+02:00");

    public static TheoryData<AdminMessage> Messages() => new()
    {
        new GetStatus { Id = "1" },
        new StatusResult(4, "pc-kids", true, At, [new MemberStatus("noa", "נועה", "ok", 30, 120, 90, "19:00", null, null)]),
        new GetSettings(),
        new SettingsResult(new FamilyBundle { TimeZone = "Asia/Jerusalem", Version = 4 }),
        new ReplaceSettings(new FamilyBundle { TimeZone = "Asia/Jerusalem" }),
        new ReplaceSettings(new FamilyBundle { TimeZone = "Asia/Jerusalem" }, IfVersion: 12),
        new AddRule(new RuleDto("r1", "noa", "pbskids.org", "domain", "allow", ExpiresAt: At)),
        new RemoveRule("r1"),
        new SetMember("yoav", YouTubeMode: "off"),
        new ExtendTime("noa", 15),
        new Pause("noa", At),
        new Resume("noa"),
        new StartHomework("noa", 45),
        new StopHomework("noa"),
        new GetEvents(At, "time_warning", "noa", 50),
        new EventsResult([new EventDto("time_warning", At, "noa", null, null, null, null, 10, null)]),
        new GetRequests("pending"),
        new RequestsResult([new RequestDto("ar-1", "noa", "pc-kids", "url", "https://pbskids.org/", "pbskids.org", null, "please", At, "pending", null)]),
        new DecideRequest("ar-1", "approve", "page", "today"),
        new SetClock(At),
        new ScanAccounts(),
        new AdminOk("done"),
        new AdminError("bad_request", "nope"),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Every_message_round_trips(AdminMessage message)
    {
        var json = AdminProtocol.Serialize(message);
        var back = AdminProtocol.Deserialize(json);
        Assert.Equal(message.GetType(), back.GetType());
        Assert.Equal(Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(AdminProtocol.Serialize(back)));
    }
}
