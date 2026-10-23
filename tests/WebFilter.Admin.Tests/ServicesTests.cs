using WebFilter.Admin.Services;

namespace WebFilter.Admin.Tests;

public sealed class FamilyTimeTests
{
    private static readonly TimeZoneInfo Israel = FamilyTime.FindZone("Asia/Jerusalem");

    [Theory]
    [InlineData("2026-10-19T16:00:00+03:00", "2026-10-20T00:00:00+03:00")]
    [InlineData("2026-10-19T21:30:00+00:00", "2026-10-21T00:00:00+03:00")] // 00:30 local on the 20th
    [InlineData("2026-10-24T23:00:00+03:00", "2026-10-25T00:00:00+03:00")] // night before the switch to winter time
    [InlineData("2026-10-25T12:00:00+02:00", "2026-10-26T00:00:00+02:00")] // after the switch
    public void End_of_day_is_the_next_local_midnight(string now, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected), FamilyTime.EndOfDay(DateTimeOffset.Parse(now), Israel));

    [Fact]
    public void Unknown_zones_fall_back_to_this_pc()
    {
        Assert.Same(TimeZoneInfo.Local, FamilyTime.FindZone("Mars/Olympus_Mons"));
        Assert.Same(TimeZoneInfo.Local, FamilyTime.FindZone(null));
        Assert.Equal(TimeSpan.FromHours(3), Israel.GetUtcOffset(new DateTime(2026, 10, 19, 12, 0, 0)));
    }
}

public sealed class AdminAppOptionsTests
{
    [Theory]
    [InlineData(new string[0], "WebFilter.Admin")]
    [InlineData(new[] { "--pipe", "WebFilter.Test.Admin" }, "WebFilter.Test.Admin")]
    [InlineData(new[] { "--pipe=Other" }, "Other")]
    [InlineData(new[] { "--PIPE", " Spaced " }, "Spaced")]
    [InlineData(new[] { "--pipe" }, "WebFilter.Admin")]
    [InlineData(new[] { "--pipe=" }, "WebFilter.Admin")]
    [InlineData(new[] { "--unknown", "x" }, "WebFilter.Admin")]
    public void Parses_the_pipe_name(string[] args, string expected) =>
        Assert.Equal(expected, AdminAppOptions.Parse(args).PipeName);

    /// <summary>The notifier opens the admin app with <c>--page devices</c> for a new Windows account (PROTECTION-SPEC §10).</summary>
    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "--page", "devices" }, "devices")]
    [InlineData(new[] { "--PAGE=Devices" }, "devices")]
    [InlineData(new[] { "--pipe", "WebFilter.Test.Admin", "--page", " requests " }, "requests")]
    [InlineData(new[] { "--page" }, null)]
    [InlineData(new[] { "--page=" }, null)]
    public void Parses_the_start_page(string[] args, string? expected) =>
        Assert.Equal(expected, AdminAppOptions.Parse(args).StartPage);

    [Fact]
    public void The_page_and_the_pipe_are_read_together() =>
        Assert.Equal(new AdminAppOptions("Other", "devices"), AdminAppOptions.Parse(["--page=devices", "--pipe=Other"]));
}
