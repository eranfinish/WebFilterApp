using WebFilter.Admin.Resources;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

/// <summary>The checks and conversions behind the member page and the setup wizard.</summary>
public sealed class InputTests
{
    [Theory]
    [InlineData("9:30", false, 570)]
    [InlineData(" 09:30 ", false, 570)]
    [InlineData("00:00", false, 0)]
    [InlineData("23:59", false, 1439)]
    [InlineData("24:00", true, 1440)]
    public void Clock_times_are_read_as_minutes(string text, bool endOfDay, int minute)
    {
        Assert.True(ClockTime.TryParse(text, endOfDay, out var parsed));
        Assert.Equal(minute, parsed);
    }

    [Theory]
    [InlineData("24:00", false)]
    [InlineData("24:30", true)]
    [InlineData("12:60", true)]
    [InlineData("9", true)]
    [InlineData("9.30", true)]
    [InlineData("", true)]
    [InlineData(null, true)]
    public void Other_text_is_not_a_clock_time(string? text, bool endOfDay) => Assert.False(ClockTime.TryParse(text, endOfDay, out _));

    [Fact]
    public void A_members_week_is_read_day_by_day()
    {
        var (restricts, days) = WeeklyPlan.From(TestData.Family(), "noa");

        Assert.True(restricts);
        Assert.Equal(7, days.Count);
        Assert.Equal([(900, 1140)], days[0].Windows);
        Assert.Equal(120, days[4].LimitMinutes);
        Assert.Equal([(540, 780), (960, 1200)], days[5].Windows);
        Assert.Equal(180, days[5].LimitMinutes);
        Assert.Equal([(540, 1200)], days[6].Windows);

        var (danaRestricts, danaDays) = WeeklyPlan.From(TestData.Family(), "dana");
        Assert.False(danaRestricts);
        Assert.All(danaDays, d => Assert.True(d.Windows.Count == 0 && d.LimitMinutes is null));
    }

    [Fact]
    public void Several_limits_on_one_day_count_as_the_smallest()
    {
        var settings = TestData.Family() with
        {
            DailyLimits = [new DailyLimitDto("noa", [1, 2], 90), new DailyLimitDto("noa", [2], 60)],
        };

        var (_, days) = WeeklyPlan.From(settings, "noa");

        Assert.Equal([null, 90, 60, null, null, null, null], days.Select(d => d.LimitMinutes));
    }

    [Fact]
    public void Writing_a_week_back_groups_the_days_that_share_hours_or_a_limit()
    {
        var (restricts, days) = WeeklyPlan.From(TestData.Family(), "noa");

        var (schedules, limits) = WeeklyPlan.To("noa", restricts, days);

        Assert.Equal(TestData.Family().Schedules.Where(s => s.Member == "noa").Select(Text), schedules.Select(Text));
        Assert.Equal(TestData.Family().DailyLimits.Where(l => l.Member == "noa").Select(Text), limits.Select(Text));
    }

    [Fact]
    public void Replacing_a_week_leaves_everyone_else_alone_and_without_restriction_drops_the_hours()
    {
        var family = TestData.Family();
        var (_, days) = WeeklyPlan.From(family, "noa");

        var replaced = WeeklyPlan.Replace(family, "noa", restrictsHours: false, days);

        Assert.DoesNotContain(replaced.Schedules, s => s.Member == "noa");
        Assert.Equal(family.Schedules.Where(s => s.Member == "yoav"), replaced.Schedules);
        Assert.Equal(family.DailyLimits.Select(Text).Order(), replaced.DailyLimits.Select(Text).Order());
    }

    [Theory]
    [InlineData(@"C:\Games\Roblox\roblox.exe", @"C:\Games\Roblox\roblox.exe")]
    [InlineData(@"  ""C:\Program Files\App\..\App\App.EXE""  ", @"C:\Program Files\App\App.EXE")]
    [InlineData(@"\\server\share\tool.exe", @"\\server\share\tool.exe")]
    public void A_program_is_approved_by_its_full_path(string input, string stored)
    {
        Assert.True(AppRuleValidator.TryNormalize(input, "path", out var value, out var error), error);
        Assert.Equal(stored, value);
    }

    [Theory]
    [InlineData("", nameof(Strings.Validation_Empty))]
    [InlineData("roblox.exe", nameof(Strings.Validation_AppPathForm))]
    [InlineData(@"Games\roblox.exe", nameof(Strings.Validation_AppPathForm))]
    [InlineData(@"%LOCALAPPDATA%\Roblox\roblox.exe", nameof(Strings.Validation_AppPathForm))]
    [InlineData(@"C:\Games\*.exe", nameof(Strings.Validation_AppPathForm))]
    [InlineData(@"C:\Games\Roblox", nameof(Strings.Validation_AppPathExe))]
    [InlineData(@"C:\Games\readme.txt", nameof(Strings.Validation_AppPathExe))]
    public void A_path_that_is_not_a_full_program_path_is_explained(string input, string messageKey)
    {
        Assert.False(AppRuleValidator.TryNormalize(input, "path", out _, out var error));
        Assert.Equal(Strings.ResourceManager.GetString(messageKey, Strings.Culture), error);
    }

    [Theory]
    [InlineData("Microsoft.MinecraftUWP_8wekyb3d8bbwe", "Microsoft.MinecraftUWP_8wekyb3d8bbwe")]
    [InlineData("Microsoft.MinecraftUWP_1.21.4401.0_x64__8wekyb3d8bbwe", "Microsoft.MinecraftUWP_8wekyb3d8bbwe")]
    [InlineData(" SpotifyAB.SpotifyMusic_zpdnekdrzrea0 ", "SpotifyAB.SpotifyMusic_zpdnekdrzrea0")]
    public void A_store_app_is_approved_by_its_package_family_name(string input, string stored)
    {
        Assert.True(AppRuleValidator.TryNormalize(input, "package", out var value, out var error), error);
        Assert.Equal(stored, value);
    }

    [Theory]
    [InlineData("Minecraft")]
    [InlineData("Microsoft.MinecraftUWP_8wekyb3d8bbw")]
    [InlineData(@"C:\Games\minecraft.exe")]
    public void Other_text_is_not_a_package_name(string input)
    {
        Assert.False(AppRuleValidator.TryNormalize(input, "package", out _, out var error));
        Assert.Equal(Strings.Validation_AppPackageForm, error);
    }

    [Theory]
    [InlineData("UCkhanKids00000000000001")]
    [InlineData("https://www.youtube.com/channel/UCkhanKids00000000000001")]
    [InlineData("youtube.com/channel/UCkhanKids00000000000001/videos")]
    [InlineData("m.youtube.com/channel/UCkhanKids00000000000001?view=0")]
    public void A_channel_is_read_from_its_id_or_address(string input)
    {
        Assert.True(YouTubeChannelInput.TryParseChannelId(input, out var id, out _));
        Assert.Equal("UCkhanKids00000000000001", id);
    }

    [Theory]
    [InlineData("@khankids", nameof(Strings.Validation_ChannelHandleOnly))]
    [InlineData("https://www.youtube.com/@khankids", nameof(Strings.Validation_ChannelHandleOnly))]
    [InlineData("UCshort", nameof(Strings.Validation_ChannelId))]
    [InlineData("https://www.youtube.com/watch?v=abc", nameof(Strings.Validation_ChannelId))]
    [InlineData(" ", nameof(Strings.Validation_Empty))]
    public void A_handle_alone_or_other_text_is_not_a_channel_id(string input, string messageKey)
    {
        Assert.False(YouTubeChannelInput.TryParseChannelId(input, out _, out var error));
        Assert.Equal(Strings.ResourceManager.GetString(messageKey, Strings.Culture), error);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("KhanKids", "@khankids")]
    [InlineData("@Khan.Kids-2", "@khan.kids-2")]
    [InlineData("https://www.youtube.com/@KhanKids/videos", "@khankids")]
    public void Handles_are_stored_with_their_at_sign_in_lower_case(string input, string? handle)
    {
        Assert.True(YouTubeChannelInput.TryNormalizeHandle(input, out var normalized, out _));
        Assert.Equal(handle, normalized);
    }

    [Theory]
    [InlineData("@ab")]
    [InlineData("@has space")]
    [InlineData("@שלום")]
    public void Other_handles_are_explained(string input)
    {
        Assert.False(YouTubeChannelInput.TryNormalizeHandle(input, out _, out var error));
        Assert.Equal(Strings.Validation_ChannelHandle, error);
    }

    private static string Text(ScheduleDto s) => $"{s.Member}:{string.Join(",", s.Days)}:{s.Start}-{s.End}";

    private static string Text(DailyLimitDto l) => $"{l.Member}:{string.Join(",", l.Days)}:{l.Minutes}";
}
