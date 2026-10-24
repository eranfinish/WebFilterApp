using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

/// <summary>
/// PROTECTION-SPEC §15 in the admin app: a YouTube request names its channel ("NASA (@nasa)", else the id) and the search
/// it was found with, each pending site or channel request can be opened in the parent's own browser first, and the role
/// templates switch the channel search on for teens and off for children.
/// </summary>
public sealed class ChannelRequestTests
{
    private const string Nasa = "UCLA_DiQTUM8_Bp7gWl1D0RQ";
    private readonly FakeAdminClient _client = new();
    private readonly FakeBrowser _browser = new();

    private async Task<RequestsViewModel> LoadedAsync(IBrowserLauncher? browser, params RequestDto[] requests)
    {
        _client.Requests.AddRange(requests);
        var vm = new RequestsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)), browser);
        await vm.RefreshAsync();
        return vm;
    }

    private static RequestDto Channel(string id, string? title, string? handle, string? search = null, string status = "pending") =>
        TestData.Request(id, "youtube", Nasa, Nasa, status: status, decided: status == "pending" ? null : TestData.Now) with { Title = title, Handle = handle, SearchQuery = search };

    [Fact]
    public async Task A_channel_request_shows_the_channels_name_its_id_and_the_search_it_came_from()
    {
        var vm = await LoadedAsync(_browser, Channel("ar-1", "NASA", "@nasa", " space "), Channel("ar-2", null, null));

        var named = vm.Pending.Single(p => p.Id == "ar-1");
        Assert.True(named.IsYouTube);
        Assert.Equal("NASA (@nasa)", named.TargetText);
        Assert.Equal("NASA (@nasa)", Bidi.Plain(named.ChannelText));
        Assert.Equal($"מזהה הערוץ: {Nasa}", Bidi.Plain(named.ChannelIdText));
        Assert.Equal("נמצא בחיפוש 'space'", Bidi.Plain(named.FoundInSearchText));

        // An older extension sent the id only: the id, and nothing more.
        var bare = vm.Pending.Single(p => p.Id == "ar-2");
        Assert.Equal(Nasa, bare.TargetText);
        Assert.Null(bare.ChannelIdText);
        Assert.Null(bare.FoundInSearchText);
    }

    [Fact]
    public async Task A_hebrew_channel_name_keeps_its_own_direction()
    {
        var vm = await LoadedAsync(_browser, Channel("ar-1", "ערוץ הילדים", "@kids"));

        Assert.Equal("ערוץ הילדים (@kids)", vm.Pending[0].ChannelText); // no left-to-right marks around a Hebrew name
    }

    [Fact]
    public async Task Open_for_review_opens_the_channel_or_the_site_in_the_parents_browser()
    {
        var vm = await LoadedAsync(_browser,
            Channel("ar-1", "NASA", "@nasa"),
            TestData.Request("ar-2", "url", "https://games.example/play?id=7", "games.example"),
            TestData.Request("ar-3", "time", "time", "time:2026-10-19", minutes: 15));

        var channel = vm.Pending.Single(p => p.Id == "ar-1");
        Assert.True(channel.CanOpenForReview);
        channel.OpenForReviewCommand.Execute(null);
        vm.Pending.Single(p => p.Id == "ar-2").OpenForReviewCommand.Execute(null);

        Assert.Equal([new Uri($"https://www.youtube.com/channel/{Nasa}"), new Uri("https://games.example/play?id=7")], _browser.Opened);
        Assert.Equal(Strings.Request_Opened, vm.StatusMessage);
        Assert.False(vm.Pending.Single(p => p.Id == "ar-3").CanOpenForReview); // nothing to open for more time
    }

    [Fact]
    public async Task Only_a_channel_id_or_a_web_address_is_opened()
    {
        var vm = await LoadedAsync(_browser,
            TestData.Request("ar-1", "youtube", "https://evil.example/", "https://evil.example/"),
            TestData.Request("ar-2", "url", "javascript:alert(1)", "javascript:alert(1)"),
            TestData.Request("ar-3", "app", @"C:\Games\game.exe", @"c:\games\game.exe"));

        Assert.All(vm.Pending, p => Assert.False(p.CanOpenForReview));
        foreach (var item in vm.Pending) item.OpenForReviewCommand.Execute(null);
        Assert.Empty(_browser.Opened);
    }

    [Fact]
    public async Task A_browser_that_does_not_open_is_reported_and_without_a_launcher_there_is_no_button()
    {
        _browser.Succeeds = false;
        var vm = await LoadedAsync(_browser, Channel("ar-1", "NASA", "@nasa"));
        vm.Pending[0].OpenForReviewCommand.Execute(null);
        Assert.Equal(Strings.Request_OpenFailed, vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);

        var without = await LoadedAsync(null);
        Assert.False(without.CanOpenForReview);
        Assert.All(without.Pending, p => Assert.False(p.CanOpenForReview));
    }

    [Fact]
    public async Task Decided_requests_name_their_channel_too_and_offer_no_button()
    {
        var vm = await LoadedAsync(_browser, Channel("ar-1", "NASA", "@nasa", status: "approved"));

        var recent = Assert.Single(vm.Recent);
        Assert.Equal("NASA (@nasa)", recent.TargetText);
        Assert.False(recent.CanOpenForReview);
    }

    [Fact]
    public void The_shell_launcher_hands_the_address_to_explorer_by_its_full_path_as_one_argument()
    {
        var info = ShellBrowserLauncher.StartInfo(new Uri("https://games.example/a b,c?x=1&y=\"2\""))!;

        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), info.FileName);
        Assert.True(Path.IsPathFullyQualified(info.FileName));
        Assert.False(info.UseShellExecute);
        Assert.Equal(["https://games.example/a%20b%2Cc?x=1&y=%222%22"], info.ArgumentList);
        Assert.Null(ShellBrowserLauncher.StartInfo(new Uri("file:///C:/Windows/notepad.exe")));
        Assert.Null(ShellBrowserLauncher.StartInfo(new Uri("javascript:alert(1)")));
    }

    [Fact]
    public void The_shell_launcher_reports_what_did_not_start()
    {
        var started = new List<ProcessStartInfo>();
        Assert.False(new ShellBrowserLauncher(i => { started.Add(i); return null; }).TryOpen(new Uri("https://www.youtube.com/channel/" + Nasa)));
        Assert.False(new ShellBrowserLauncher(_ => throw new Win32Exception(2)).TryOpen(new Uri("https://example.com/")));
        Assert.False(new ShellBrowserLauncher(i => { started.Add(i); return null; }).TryOpen(new Uri("ftp://example.com/")));
        Assert.Single(started); // the ftp address never reached the shell
    }

    [Fact]
    public void Teens_start_with_the_channel_search_and_children_without_it()
    {
        Assert.True(MemberTemplate.NewMember("m-1", "יואב", "teen", 14).YouTubeChannelSearch);
        Assert.False(MemberTemplate.NewMember("m-2", "נועה", "child", 8).YouTubeChannelSearch);
        Assert.False(MemberTemplate.NewMember("m-3", "אבא", "adult", null).YouTubeChannelSearch);

        // Named in a summary only where it means something: YouTube's "approved channels only" mode.
        var teen = MemberTemplate.For("teen").Protection;
        Assert.DoesNotContain(Strings.Member_YouTubeChannelSearch, teen.Text("restricted") ?? "", StringComparison.Ordinal);
        Assert.Contains(Strings.Member_YouTubeChannelSearch, teen.Text("allowlist"), StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.Member_YouTubeChannelSearch, MemberTemplate.For("child").Protection.Text("allowlist") ?? "", StringComparison.Ordinal);
    }
}
