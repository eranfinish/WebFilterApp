using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier.Tests;

/// <summary>The balloons in Hebrew: one per notification, or a summary when a poll brings several.</summary>
public sealed class NotificationTextsTests
{
    private static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");

    private static Balloon One(NotificationDto item) => NotificationTexts.For([item], Israel);

    [Theory]
    [InlineData("url:games.example", "pc-kids", "בקשה לגשת לאתר games.example (מהמחשב pc-kids). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("time:30", "pc-kids", "בקשה לעוד 30 דקות גלישה (מהמחשב pc-kids). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData(@"app:C:\Games\Roblox\roblox.exe", "laptop-yoav", "בקשה להשתמש בתוכנה roblox.exe (מהמחשב laptop-yoav). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("youtube:UCkhanKids00000000000001", null, "בקשה לצפות בערוץ YouTube. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("time:soon", null, "בקשה חדשה מאחד הילדים. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData(null, null, "בקשה חדשה מאחד הילדים. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    public void A_request_says_what_was_asked_for_and_from_which_computer(string? detail, string? device, string text)
    {
        var balloon = One(new NotificationDto("r1", NotificationKinds.Request, TestData.Now, "m-1a2b3c4d", device, detail));

        Assert.Equal("בקשה חדשה", balloon.Title);
        Assert.Equal(text, Bidi.Plain(balloon.Text));
        Assert.False(balloon.IsWarning);
        // Member ids are not names; the notifier cannot read the names without elevating, so it never shows an id.
        Assert.DoesNotContain("m-1a2b3c4d", balloon.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_url_keeps_its_left_to_right_host_in_one_piece()
    {
        var text = One(TestData.Request(1, "url:www.games.example")).Text;

        Assert.Contains("\u200Ewww.games.example\u200E", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("policy_repaired", "הגדרות הסינון שונו", "במחשב pc-kids שונו הגדרות שהסינון מנהל, והסינון החזיר אותן.")]
    [InlineData("network_repaired", "מסנני הרשת הוסרו", "במחשב pc-kids הוסרו מסנני הרשת של חשבון, והסינון החזיר אותם.")]
    [InlineData("unclean_stop", "שירות הסינון נעצר", "במחשב pc-kids שירות הסינון נעצר באופן לא צפוי והופעל מחדש. ייתכן שהייתה הפסקת חשמל או כיבוי בכוח.")]
    [InlineData("extension_silent", "שינוי בסינון", "במחשב pc-kids קרה משהו שכדאי לבדוק ביומן הפעילות.")]
    public void Tamper_says_what_was_undone_and_where(string detail, string title, string text)
    {
        var balloon = One(TestData.Tamper(1, detail));

        Assert.Equal(title, balloon.Title);
        Assert.Equal(text + " לחצו כאן כדי לפתוח את ניהול המשפחה.", Bidi.Plain(balloon.Text));
        Assert.True(balloon.IsWarning);
    }

    [Fact]
    public void An_unclean_stop_is_not_called_an_attack()
    {
        var text = One(TestData.Tamper(1, "unclean_stop", device: null)).Text;

        Assert.StartsWith("במחשב לא ידוע", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ניסיון", text, StringComparison.Ordinal);
        Assert.DoesNotContain("עקיפה", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_silent_computer_says_when_it_last_reported_in_family_time()
    {
        var balloon = One(TestData.Device(1, "2026-10-21T15:30:00.0000000+00:00"));

        Assert.Equal("מחשב לא מדווח", balloon.Title);
        Assert.StartsWith("המחשב laptop-yoav לא דיווח למחשב ההורים יותר מיום (לאחרונה ב-21.10 18:30).", Bidi.Plain(balloon.Text), StringComparison.Ordinal);
        Assert.True(balloon.IsWarning);

        var never = One(TestData.Device(2, null));
        Assert.StartsWith("המחשב laptop-yoav צומד, אבל לא דיווח", Bidi.Plain(never.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_at_once_make_one_summary()
    {
        var balloon = NotificationTexts.For([TestData.Request(1), TestData.Request(2), TestData.Request(3), TestData.Tamper(4, "policy_repaired")]);

        Assert.Equal("4 התראות חדשות מ-WebFilter", balloon.Title);
        Assert.Equal("3 בקשות חדשות\nהתראה על הסינון\nלחצו כאן כדי לפתוח את ניהול המשפחה.", balloon.Text);
        Assert.True(balloon.IsWarning);

        var requests = NotificationTexts.For([TestData.Request(1), TestData.Device(2, null), TestData.Device(3, null)]);
        Assert.Equal("בקשה חדשה\n2 מחשבים שלא מדווחים\nלחצו כאן כדי לפתוח את ניהול המשפחה.", requests.Text);
    }

    [Fact]
    public void An_unknown_kind_from_a_newer_service_still_points_to_the_admin_app()
    {
        var balloon = One(new NotificationDto("x", "quota", TestData.Now));

        Assert.Equal(("התראה חדשה", "יש התראה חדשה בניהול המשפחה. לחצו כאן כדי לפתוח."), (balloon.Title, balloon.Text));
    }

    [Fact]
    public void Texts_are_cut_to_what_Windows_shows()
    {
        var host = new string('a', 300) + ".example";

        var balloon = One(TestData.Request(1, "url:" + host));

        Assert.Equal(NotificationTexts.MaxText, balloon.Text.Length);
        Assert.EndsWith("…", balloon.Text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => NotificationTexts.For([]));
    }
}
