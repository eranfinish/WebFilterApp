using System.Xml.Linq;
using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier.Tests;

/// <summary>
/// The toasts as Windows gets them (their XML): the wording of NotificationTexts, how long each stays on the screen, the
/// buttons, and what a click carries. Nothing here shows a toast or registers anything.
/// </summary>
public sealed class NotificationToastsTests
{
    private static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");

    private static XElement Toast(params NotificationDto[] items) => ToastXml.Of(NotificationToasts.For(items, Israel));

    private static void HasWording(XElement toast, params NotificationDto[] items)
    {
        var text = NotificationTexts.For(items, Israel);
        Assert.Equal([text.Title, text.Text], ToastXml.Texts(toast));
        Assert.Equal("ToastGeneric", (string?)toast.Descendants("binding").Single().Attribute("template"));
        // The texts' language, whatever the Windows display language.
        Assert.Equal("he-IL", (string?)toast.Element("visual")!.Attribute("lang"));
    }

    /// <summary>A click on the toast itself: foreground (the click comes to the notifier), asking for the admin app.</summary>
    private static void OpensWhenClicked(XElement toast)
    {
        Assert.Equal("action=open", (string?)toast.Attribute("launch"));
        Assert.Null(toast.Attribute("activationType"));
    }

    [Theory]
    [InlineData("url:games.example")]
    [InlineData("time:30")]
    [InlineData(null)]
    public void A_request_shows_for_the_normal_time_and_opens_family_management(string? detail)
    {
        var item = TestData.Request(1, detail!);

        var toast = Toast(item);

        HasWording(toast, item);
        OpensWhenClicked(toast);
        // The default scenario and duration: a few seconds on the screen, then kept in the notification center.
        Assert.Null(toast.Attribute("scenario"));
        Assert.Null(toast.Attribute("duration"));
        Assert.Equal([("פתח את ניהול המשפחה", "action=open", null)], ToastXml.Buttons(toast));
    }

    [Theory]
    [InlineData("policy_repaired")]
    [InlineData("network_repaired")]
    [InlineData("unclean_stop")]
    [InlineData("extension_silent")]
    public void Tamper_stays_on_the_screen_until_the_parent_opens_or_closes_it(string detail)
    {
        var item = TestData.Tamper(1, detail);

        var toast = Toast(item);

        HasWording(toast, item);
        OpensWhenClicked(toast);
        Assert.Equal("reminder", (string?)toast.Attribute("scenario"));
        // A reminder needs a button; "close" is Windows' own dismissal, which never starts or reaches the notifier.
        Assert.Equal([("פתח את ניהול המשפחה", "action=open", null), ("סגור", "dismiss", "system")], ToastXml.Buttons(toast));
    }

    [Fact]
    public void A_silent_computer_shows_for_the_normal_time()
    {
        var item = TestData.Device(1, "2026-10-21T15:30:00.0000000+00:00");

        var toast = Toast(item);

        HasWording(toast, item);
        OpensWhenClicked(toast);
        Assert.Null(toast.Attribute("scenario"));
        Assert.Equal([("פתח את ניהול המשפחה", "action=open", null)], ToastXml.Buttons(toast));
    }

    [Fact]
    public void A_summary_stays_on_the_screen_when_it_holds_tamper()
    {
        NotificationDto[] withTamper = [TestData.Request(1), TestData.Tamper(2, "policy_repaired"), TestData.Device(3, null)];
        NotificationDto[] without = [TestData.Request(1), TestData.Request(2), TestData.Device(3, null)];

        var urgent = Toast(withTamper);
        var calm = Toast(without);

        HasWording(urgent, withTamper);
        Assert.Equal("3 התראות חדשות מ-WebFilter", ToastXml.Texts(urgent)[0]);
        Assert.Equal("reminder", (string?)urgent.Attribute("scenario"));
        Assert.Equal(2, ToastXml.Buttons(urgent).Length);

        HasWording(calm, without);
        OpensWhenClicked(calm);
        Assert.Null(calm.Attribute("scenario"));
        Assert.Single(ToastXml.Buttons(calm));
    }

    [Fact]
    public void An_unknown_kind_from_a_newer_service_is_an_ordinary_toast()
    {
        var item = new NotificationDto("x", "quota", TestData.Now);

        var toast = Toast(item);

        HasWording(toast, item);
        OpensWhenClicked(toast);
        Assert.Null(toast.Attribute("scenario"));
    }

    [Theory]
    [InlineData("action=open", true)]
    [InlineData("kind=tamper;action=open", true)]
    [InlineData("action=close", false)]
    [InlineData("dismiss", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_click_that_asks_for_it_opens_family_management(string? arguments, bool opens) =>
        Assert.Equal(opens, NotificationToasts.Opens(arguments));
}
