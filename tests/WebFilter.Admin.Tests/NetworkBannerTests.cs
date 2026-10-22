using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

/// <summary>
/// The hub on a network Windows marks Public cannot be reached by the children's computers (its firewall rule is for
/// Private networks only — found in the first lab install). The main window warns on every page and offers the fix.
/// </summary>
public sealed class NetworkBannerTests
{
    private readonly FakeAdminClient _client = new();
    private readonly ManualTimer _timer = new();
    private readonly FakeDialogs _dialogs = new();

    private MainViewModel Create() => new(_client, _timer, _dialogs, new FakeClipboard(), new ManualTime(TestData.Now));

    private void OnPublicNetwork(string names = "Home-WiFi")
    {
        var index = _client.Checks.FindIndex(c => c.Id == "network");
        _client.Checks[index] = new CheckResult("network", "warn", names);
    }

    [Fact]
    public async Task A_hub_on_a_private_network_shows_no_warning()
    {
        using var main = Create();
        main.Start();
        await main.CurrentLoad;
        Assert.Single(_client.SentOf<GetDiagnostics>());
        Assert.False(main.HasPublicNetwork);
    }

    [Fact]
    public async Task A_hub_on_a_public_network_is_warned_on_every_page()
    {
        OnPublicNetwork();
        using var main = Create();
        main.Start();
        await main.CurrentLoad;
        Assert.True(main.HasPublicNetwork);
        Assert.Contains("Home-WiFi", main.PublicNetworkText, StringComparison.Ordinal);
        Assert.Contains("ציבורית", main.PublicNetworkText, StringComparison.Ordinal);

        main.SelectedPage = main.Rules;
        await main.CurrentLoad;
        Assert.True(main.HasPublicNetwork); // the banner belongs to the window, not to a page
    }

    [Fact]
    public async Task Confirming_switches_the_network_to_private_and_the_warning_goes_away()
    {
        OnPublicNetwork();
        using var main = Create();
        main.Start();
        await main.CurrentLoad;

        await main.MakeNetworkPrivateCommand.ExecuteAsync(null);

        Assert.Contains("Home-WiFi", Assert.Single(_dialogs.Asked), StringComparison.Ordinal);
        Assert.Contains("הבית", _dialogs.Asked[0], StringComparison.Ordinal); // only for the home network
        Assert.Single(_client.SentOf<SetNetworksPrivate>());
        Assert.False(main.HasPublicNetwork);
    }

    [Fact]
    public async Task Cancelling_changes_nothing()
    {
        OnPublicNetwork();
        _dialogs.Answer = false;
        using var main = Create();
        main.Start();
        await main.CurrentLoad;

        await main.MakeNetworkPrivateCommand.ExecuteAsync(null);

        Assert.Empty(_client.SentOf<SetNetworksPrivate>());
        Assert.True(main.HasPublicNetwork);
    }

    [Fact]
    public async Task When_windows_refuses_the_banner_says_so()
    {
        OnPublicNetwork();
        _client.Reply = m => m is SetNetworksPrivate ? new AdminError("bad_request", "Access is denied.") : null;
        using var main = Create();
        main.Start();
        await main.CurrentLoad;

        await main.MakeNetworkPrivateCommand.ExecuteAsync(null);

        Assert.True(main.HasPublicNetwork);
        Assert.Contains("Access is denied.", main.PublicNetworkText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_network_is_checked_again_about_once_a_minute()
    {
        using var main = Create();
        main.Start();
        await main.CurrentLoad;
        Assert.False(main.HasPublicNetwork);

        OnPublicNetwork();
        for (var i = 0; i < MainViewModel.NetworkCheckTicks; i++) await _timer.FireAsync();

        Assert.True(main.HasPublicNetwork);
    }

    [Fact]
    public async Task An_agent_computer_is_not_asked_about_the_hub_network()
    {
        OnPublicNetwork();
        _client.IsHub = false;
        using var main = Create();
        main.Start();
        await main.CurrentLoad;
        Assert.Empty(_client.SentOf<GetDiagnostics>());
        Assert.False(main.HasPublicNetwork);
    }
}
