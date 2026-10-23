using WebFilter.Admin.Resources;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

/// <summary>On an agent computer (MVP-PLAN §4.5): "managed by the hub ‹name›", the status, and the local emergency pause.</summary>
public sealed class AgentModeTests
{
    private readonly FakeAdminClient _client = new() { IsHub = false };
    private readonly ManualTimer _timer = new();

    private MainViewModel Create() => new(_client, _timer, new FakeDialogs(), new FakeClipboard(), new ManualTime(TestData.Now));

    [Fact]
    public async Task Only_the_dashboard_and_the_settings_page_are_shown_and_nothing_hub_only_is_asked()
    {
        using var main = Create();

        main.Start();
        await main.CurrentLoad;
        main.SelectedPage = main.Settings;
        await main.CurrentLoad;
        await _timer.FireAsync();

        Assert.Equal([main.Dashboard, main.Settings], main.Pages);
        Assert.Empty(_client.SentOf<GetSettings>());
        Assert.Empty(_client.SentOf<GetRequests>());
        Assert.Null(main.Dashboard.ErrorMessage);
        Assert.Null(main.Settings.ErrorMessage);
    }

    [Fact]
    public async Task The_dashboard_says_which_hub_manages_this_computer_and_offers_only_the_local_pause()
    {
        var vm = new DashboardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await vm.RefreshAsync();

        Assert.True(vm.IsAgent);
        Assert.False(vm.HubProblem);
        Assert.Equal("המחשב הזה מנוהל על ידי מחשב ההורים pc-parents. קשר אחרון: 15:59.", Bidi.Plain(vm.HubText));
        Assert.False(vm.ShowsRequests);
        Assert.False(vm.HasNewAccounts);
        Assert.Empty(vm.NewAccounts);
        var noa = vm.Members.Single(m => m.MemberId == "noa");
        Assert.True(noa.IsFiltered);
        Assert.Equal((false, true, false), (noa.CanExtend, noa.CanPause, noa.CanStartHomework));
        Assert.Equal("השהיית חירום במחשב הזה עד סוף היום", noa.PauseText);
        Assert.False(vm.Members.Single(m => m.MemberId == "dana").IsFiltered);

        await noa.PauseCommand.ExecuteAsync(null);

        // Until the end of the family's day: the zone comes with the agent's status.
        Assert.Equal(new Pause("noa", new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.FromHours(3))), _client.Single<Pause>());
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task A_paused_member_is_resumed_from_the_hub()
    {
        _client.Status = TestData.Status(7, new MemberStatus("noa", "נועה", "ok", 45, 120, 75, "19:00", TestData.Now.AddHours(2), null));
        var vm = new DashboardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await vm.RefreshAsync();

        var noa = Assert.Single(vm.Members);
        Assert.Equal((true, false, false, true), (noa.IsPaused, noa.CanPause, noa.CanResume, noa.ResumesOnHub));
    }

    [Theory]
    [InlineData(false, "2026-10-19T15:30:00+03:00", "המחשב הזה מנוהל על ידי מחשב ההורים pc-parents. אין כרגע קשר איתו, והסינון ממשיך לפי ההגדרות האחרונות שהתקבלו. קשר אחרון: 15:30.")]
    [InlineData(false, null, "המחשב הזה מנוהל על ידי מחשב ההורים pc-parents. אין כרגע קשר איתו, והסינון ממשיך לפי ההגדרות האחרונות שהתקבלו. עדיין לא היה קשר מאז שהשירות התחיל.")]
    public async Task An_unreachable_hub_is_a_warning(bool reachable, string? lastContact, string text)
    {
        _client.AgentHub = new HubConnection("pc-parents", "192.168.1.20:47800", reachable, lastContact is null ? null : DateTimeOffset.Parse(lastContact));
        var vm = new DashboardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await vm.RefreshAsync();

        Assert.True(vm.HubProblem);
        Assert.Equal(text, Bidi.Plain(vm.HubText));
    }

    [Fact]
    public async Task An_agent_that_is_not_paired_says_how_to_pair_it()
    {
        _client.AgentHub = new HubConnection(null, null, false, null);
        _client.Status = TestData.Status(0) with { Members = [] };
        var vm = new DashboardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await vm.RefreshAsync();

        Assert.True(vm.HubProblem);
        Assert.Equal(Strings.Agent_NotPaired, vm.HubText);
        Assert.True(vm.HasNoMembers);
    }

    [Fact]
    public async Task The_settings_page_shows_the_diagnostics_and_the_hub_but_no_members()
    {
        var vm = new SettingsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await vm.RefreshAsync();

        Assert.True(vm.IsAgent);
        Assert.Empty(vm.Members);
        Assert.Equal(("לא", "Asia/Jerusalem"), (vm.IsHubText, vm.TimeZoneText));
        Assert.StartsWith("המחשב הזה מנוהל על ידי מחשב ההורים pc-parents.", Bidi.Plain(vm.HubText));
        Assert.Null(vm.ErrorMessage);
        Assert.Empty(_client.SentOf<GetSettings>());
    }
}
