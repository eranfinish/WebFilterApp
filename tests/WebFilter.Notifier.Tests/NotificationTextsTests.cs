using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier.Tests;

/// <summary>The toasts' wording in Hebrew: one per notification, or a summary when a poll brings several.</summary>
public sealed class NotificationTextsTests
{
    private static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");

    private static NotificationText One(NotificationDto item) => NotificationTexts.For([item], Israel);

    [Theory]
    [InlineData("url:games.example", "pc-kids", "בקשה לגשת לאתר games.example (מהמחשב pc-kids). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("time:30", "pc-kids", "בקשה לעוד 30 דקות גלישה (מהמחשב pc-kids). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData(@"app:C:\Games\Roblox\roblox.exe", "laptop-yoav", "בקשה להשתמש בתוכנה roblox.exe (מהמחשב laptop-yoav). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("youtube:UCkhanKids00000000000001", null, "בקשה לצפות בערוץ YouTube. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    // PROTECTION-SPEC §15: the channel by its name when the request knew it; a name may hold a ':' of its own.
    [InlineData("youtube:UCkhanKids00000000000001:Khan Kids (@khankids)", "pc-kids", "בקשה לצפות בערוץ YouTube Khan Kids (@khankids) (מהמחשב pc-kids). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("youtube:UCkhanKids00000000000001:Science: live (@sci)", null, "בקשה לצפות בערוץ YouTube Science: live (@sci). לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("youtube:UCkhanKids00000000000001:ערוץ הילדים", null, "בקשה לצפות בערוץ YouTube ערוץ הילדים. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("youtube:UCkhanKids00000000000001: ", null, "בקשה לצפות בערוץ YouTube. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData("time:soon", null, "בקשה חדשה מאחד הילדים. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    [InlineData(null, null, "בקשה חדשה מאחד הילדים. לחצו כאן כדי לפתוח את ניהול המשפחה.")]
    public void A_request_says_what_was_asked_for_and_from_which_computer(string? detail, string? device, string text)
    {
        var shown = One(new NotificationDto("r1", NotificationKinds.Request, TestData.Now, "m-1a2b3c4d", device, detail));

        Assert.Equal("בקשה חדשה", shown.Title);
        Assert.Equal(text, Bidi.Plain(shown.Text));
        // Member ids are not names; the notifier cannot read the names without elevating, so it never shows an id.
        Assert.DoesNotContain("m-1a2b3c4d", shown.Text, StringComparison.Ordinal);
    }

    /// <summary>A Latin channel name keeps its parentheses in the Hebrew sentence; a Hebrew one needs no marks.</summary>
    [Fact]
    public void A_channel_name_keeps_its_own_direction()
    {
        Assert.Contains("‎Khan Kids (@khankids)‎", One(TestData.Request(1, "youtube:UCkhanKids00000000000001:Khan Kids (@khankids)")).Text, StringComparison.Ordinal);
        Assert.Contains("YouTube ערוץ הילדים (", One(TestData.Request(2, "youtube:UCkhanKids00000000000001:ערוץ הילדים")).Text, StringComparison.Ordinal);
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
    [InlineData("extension_missing:chrome", "דפדפן נחסם: התוסף של WebFilter לא פעל",
        "במחשב pc-kids הדפדפן Chrome פעל בחשבון מסונן בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר.")]
    [InlineData("extension_missing:edge", "דפדפן נחסם: התוסף של WebFilter לא פעל",
        "במחשב pc-kids הדפדפן Edge פעל בחשבון מסונן בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר.")]
    [InlineData("extension_missing:firefox", "דפדפן נחסם: התוסף של WebFilter לא פעל",
        "במחשב pc-kids הדפדפן firefox פעל בחשבון מסונן בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר.")]
    [InlineData("extension_missing", "דפדפן נחסם: התוסף של WebFilter לא פעל",
        "במחשב pc-kids דפדפן פעל בחשבון מסונן בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר.")]
    public void Tamper_says_what_was_undone_and_where(string detail, string title, string text)
    {
        var shown = One(TestData.Tamper(1, detail));

        Assert.Equal(title, shown.Title);
        Assert.Equal(text + " לחצו כאן כדי לפתוח את ניהול המשפחה.", Bidi.Plain(shown.Text));
    }

    [Fact]
    public void An_unclean_stop_is_not_called_an_attack()
    {
        var text = One(TestData.Tamper(1, "unclean_stop", device: null)).Text;

        Assert.StartsWith("במחשב לא ידוע", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ניסיון", text, StringComparison.Ordinal);
        Assert.DoesNotContain("עקיפה", text, StringComparison.Ordinal);
    }

    /// <summary>PROTECTION-SPEC §14: the browser is named, in one piece and inside a Hebrew sentence; the child is not.</summary>
    [Fact]
    public void A_browser_blocked_without_the_extension_names_the_browser_and_the_computer_not_the_child()
    {
        var shown = new NotificationDto("tamper:1", NotificationKinds.Tamper, TestData.Now, "m-1a2b3c4d", "pc-kids", "extension_missing:chrome");

        var text = One(shown).Text;

        Assert.Contains("‎Chrome‎", text, StringComparison.Ordinal);
        Assert.StartsWith("במחשב", text, StringComparison.Ordinal);
        Assert.DoesNotContain("m-1a2b3c4d", text, StringComparison.Ordinal);
        Assert.Contains("נחסם עד שהתוסף יתחבר", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_silent_computer_says_when_it_last_reported_in_family_time()
    {
        var shown = One(TestData.Device(1, "2026-10-21T15:30:00.0000000+00:00"));

        Assert.Equal("מחשב לא מדווח", shown.Title);
        Assert.StartsWith("המחשב laptop-yoav לא דיווח למחשב ההורים יותר מיום (לאחרונה ב-21.10 18:30).", Bidi.Plain(shown.Text), StringComparison.Ordinal);

        var never = One(TestData.Device(2, null));
        Assert.StartsWith("המחשב laptop-yoav צומד, אבל לא דיווח", Bidi.Plain(never.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_account_says_which_account_on_which_computer_and_why_it_matters()
    {
        var shown = One(TestData.Account(1, "tom", "pc-kids"));

        Assert.Equal("נמצא חשבון Windows חדש", shown.Title);
        Assert.Equal("החשבון tom במחשב pc-kids: הגלישה בו חסומה עד שתבחרו לאיזה בן משפחה הוא שייך. לחצו כאן כדי לפתוח את ניהול המשפחה.",
            Bidi.Plain(shown.Text));
        // The Latin name stays in one piece, and the sentence starts in Hebrew, so the toast reads right to left.
        Assert.Contains("‎tom‎", shown.Text, StringComparison.Ordinal);
        Assert.StartsWith("החשבון", shown.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_account_without_a_name_or_a_computer_still_reads_well()
    {
        Assert.StartsWith("חשבון חדש במחשב pc-kids: הגלישה בו חסומה", Bidi.Plain(One(TestData.Account(1, name: null)).Text), StringComparison.Ordinal);
        Assert.StartsWith("החשבון tom במחשב לא ידוע: הגלישה בו חסומה", Bidi.Plain(One(TestData.Account(1, device: null)).Text), StringComparison.Ordinal);
    }

    [Fact]
    public void Several_at_once_make_one_summary()
    {
        var shown = NotificationTexts.For([TestData.Request(1), TestData.Request(2), TestData.Request(3), TestData.Tamper(4, "policy_repaired")]);

        Assert.Equal("4 התראות חדשות מ-WebFilter", shown.Title);
        Assert.Equal("3 בקשות חדשות\nהתראה על הסינון\nלחצו כאן כדי לפתוח את ניהול המשפחה.", shown.Text);

        var requests = NotificationTexts.For([TestData.Request(1), TestData.Device(2, null), TestData.Device(3, null)]);
        Assert.Equal("בקשה חדשה\n2 מחשבים שלא מדווחים\nלחצו כאן כדי לפתוח את ניהול המשפחה.", requests.Text);

        var accounts = NotificationTexts.For([TestData.Account(1), TestData.Request(2), TestData.Account(3, "maya")]);
        Assert.Equal("בקשה חדשה\n2 חשבונות Windows חדשים\nלחצו כאן כדי לפתוח את ניהול המשפחה.", accounts.Text);
        var account = NotificationTexts.For([TestData.Account(1), TestData.Tamper(2, "unclean_stop")]);
        Assert.Equal("התראה על הסינון\nחשבון Windows חדש\nלחצו כאן כדי לפתוח את ניהול המשפחה.", account.Text);
    }

    [Fact]
    public void An_unknown_kind_from_a_newer_service_still_points_to_the_admin_app()
    {
        var shown = One(new NotificationDto("x", "quota", TestData.Now));

        Assert.Equal(("התראה חדשה", "יש התראה חדשה בניהול המשפחה. לחצו כאן כדי לפתוח."), (shown.Title, shown.Text));
    }

    /// <summary>A summary counts every item: kinds this notifier does not know share a line, so the lines add up to the title.</summary>
    [Fact]
    public void Unknown_kinds_in_a_summary_have_a_line_of_their_own()
    {
        var one = NotificationTexts.For([TestData.Device(1, null), new NotificationDto("x", "quota", TestData.Now.AddMinutes(2))]);
        var two = NotificationTexts.For([TestData.Request(1), new NotificationDto("x", "quota", TestData.Now.AddMinutes(2)), new NotificationDto("y", "", TestData.Now.AddMinutes(3))]);

        Assert.Equal(("2 התראות חדשות מ-WebFilter", "מחשב שלא מדווח\nהתראה אחרת\nלחצו כאן כדי לפתוח את ניהול המשפחה."), (one.Title, one.Text));
        Assert.Equal(("3 התראות חדשות מ-WebFilter", "בקשה חדשה\n2 התראות אחרות\nלחצו כאן כדי לפתוח את ניהול המשפחה."), (two.Title, two.Text));
    }

    [Fact]
    public void Texts_are_cut_to_what_Windows_shows()
    {
        var host = new string('a', 300) + ".example";

        var shown = One(TestData.Request(1, "url:" + host));

        Assert.Equal(NotificationTexts.MaxText, shown.Text.Length);
        Assert.EndsWith("…", shown.Text, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => NotificationTexts.For([]));
    }
}
