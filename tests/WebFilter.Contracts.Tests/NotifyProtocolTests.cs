using System.Text;
using System.Text.Json;
using WebFilter.Contracts.Notify;

namespace WebFilter.Contracts.Tests;

/// <summary>PROTECTION-SPEC §10: the notifier's read-only protocol.</summary>
public class NotifyProtocolTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-01T16:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);

    public static TheoryData<NotifyMessage> Messages() => new()
    {
        new GetNotifications { Id = "1" },
        new GetNotifications(At),
        new NotificationsResult(
        [
            new NotificationDto("request:ar-1:1", "request", At, "noa", "pc-kids", "url:pbskids.org"),
            new NotificationDto("tamper:AB12", "tamper", At.AddTicks(1), null, "laptop-yoav", "unclean_stop"),
            new NotificationDto("device:laptop-yoav:1", "device", At.AddTicks(2), DeviceId: "laptop-yoav"),
        ]),
        new NotifyError(NotifyError.NotAdmin, "no"),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Every_message_round_trips(NotifyMessage message)
    {
        var json = NotifyProtocol.Serialize(message);
        var back = NotifyProtocol.Deserialize(json);
        Assert.Equal(message.GetType(), back.GetType());
        Assert.Equal(Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(NotifyProtocol.Serialize(back)));
    }

    [Fact]
    public void Wire_shapes_follow_the_spec()
    {
        var request = Assert.IsType<GetNotifications>(NotifyProtocol.Deserialize("""{"type":"getNotifications","v":1,"id":"n1","since":"2026-11-01T16:00:00Z"}"""u8));
        Assert.Equal(At, request.Since);
        Assert.Null(Assert.IsType<GetNotifications>(NotifyProtocol.Deserialize("""{"type":"getNotifications","v":1}"""u8)).Since);

        var json = Encoding.UTF8.GetString(NotifyProtocol.Serialize(new NotificationsResult([new NotificationDto("device:x:1", "device", At, DeviceId: "x")]) { Id = "n1" }));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("notificationsResult", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("n1", doc.RootElement.GetProperty("id").GetString());
        var item = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(["id", "kind", "at", "deviceId"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal("WebFilter.Notify", NotifyProtocol.PipeName);
        Assert.Equal(["request", "tamper", "device"], new[] { NotificationKinds.Request, NotificationKinds.Tamper, NotificationKinds.Device });
    }

    [Fact]
    public async Task Frames_round_trip_over_a_stream()
    {
        using var stream = new MemoryStream();
        await NotifyProtocol.WriteAsync(stream, new GetNotifications(At) { Id = "a" });
        stream.Position = 0;
        Assert.Equal("a", (await NotifyProtocol.ReadAsync(stream))!.Id);
        Assert.Null(await NotifyProtocol.ReadAsync(stream));
    }
}
