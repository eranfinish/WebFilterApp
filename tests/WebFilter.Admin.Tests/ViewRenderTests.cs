using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

/// <summary>
/// Renders the real main window off-screen, with the app's resources and a fake service, on every page and every step
/// of the setup wizard, and fails on any WPF binding error (a misspelt path, a wrong DataContext). Set
/// WEBFILTER_RENDER_DIR to a folder to also get a PNG of each state, full height.
/// </summary>
public sealed class ViewRenderTests
{
    [Fact]
    public Task Every_page_renders_without_binding_errors() => UiThread.RunAsync(async () =>
    {
        var client = new FakeAdminClient();
        client.Devices.AddRange(TestData.Devices());
        client.Requests.Add(TestData.Request("ar-1", "url", "https://games.example/", "games.example"));
        client.Events.Add(new EventDto("blocked", TestData.Now.AddMinutes(-3), "noa", "pc-kids", "blocked_not_in_allowlist", "https://games.example/", null, null, null));
        client.Events.Add(new EventDto("search", TestData.Now.AddMinutes(-4), "yoav", "laptop-yoav", null, "https://www.google.com/search?q=lego", null, null, "google:lego"));
        client.Events.Add(new EventDto("override", TestData.Now.AddMinutes(-5), "yoav", "laptop-yoav", "blocked_jump", "https://news.example/", null, null, null));
        client.Events.Add(new EventDto("tamper", TestData.Now.AddMinutes(-6), null, "pc-kids", null, null, null, null, "unclean_stop"));
        client.Settings = client.Settings with { Rules = [.. client.Settings.Rules, .. SchoolSignIn.Rules] };
        client.Checks.Add(new CheckResult("onedrive", "warn", "OneDriveSetup.exe /allusers"));
        await using var shown = await ShownAsync(client);

        foreach (var page in shown.Main.Pages.ToList())
        {
            shown.Main.SelectedPage = page;
            await shown.Main.CurrentLoad;
            await shown.RenderAsync(page.GetType().Name);
        }
        shown.Main.Settings.WindowsFeatures!.OneDrive = false;
        await shown.RenderAsync("SettingsViewModel-features-changed");
        shown.Main.SelectedPage = shown.Main.Rules;
        await shown.Main.CurrentLoad;
        shown.Main.Rules.NewPattern = "games";
        await shown.Main.Rules.AddCommand.ExecuteAsync(null);
        await shown.RenderAsync("RulesViewModel-invalid");
    });

    [Fact]
    public Task Every_step_of_the_setup_wizard_renders_without_binding_errors() => UiThread.RunAsync(async () =>
    {
        var client = new FakeAdminClient
        {
            Settings = SetupWizardViewModelTests.EmptyFamily(),
            Status = TestData.Status(1) with { Members = [] },
        };
        client.LocalAccounts.AddRange(
        [
            new("S-1-5-21-9-1001", "Dad", null, true),
            new("S-1-5-21-9-1002", "noa", null, false),
        ]);
        await using var shown = await ShownAsync(client);
        var setup = shown.Main.Setup;
        Assert.Same(setup, shown.Main.SelectedPage);
        await shown.RenderAsync("Setup-1-members-empty");

        await setup.NextCommand.ExecuteAsync(null);
        await shown.RenderAsync("Setup-1-members-invalid");
        setup.Members[0].Name = "נועה";
        setup.Members[0].AgeText = "8";
        setup.AddMemberCommand.Execute(null);
        setup.Members[1].Name = "אבא";
        setup.Members[1].Role = "adult";
        await shown.RenderAsync("Setup-1-members");

        await setup.NextCommand.ExecuteAsync(null);
        setup.Accounts.Single(a => a.Name == "Dad").MemberId = setup.Members[0].Id;
        await shown.RenderAsync("Setup-2-accounts");

        await setup.NextCommand.ExecuteAsync(null);
        await shown.RenderAsync("Setup-3-time");

        await setup.NextCommand.ExecuteAsync(null);
        setup.StarterCategories[1].Include = false;
        await shown.RenderAsync("Setup-4-categories");

        await setup.NextCommand.ExecuteAsync(null);
        await shown.RenderAsync("Setup-5-review");

        await setup.ApplyCommand.ExecuteAsync(null);
        Assert.True(setup.IsDone);
        await shown.RenderAsync("Setup-6-done");
    });

    [Fact]
    public Task The_member_page_renders_with_edits_and_messages_without_binding_errors() => UiThread.RunAsync(async () =>
    {
        var client = new FakeAdminClient();
        await using var shown = await ShownAsync(client);
        var main = shown.Main;

        main.ShowMember("noa");
        await main.CurrentLoad;
        await shown.RenderAsync("Member-noa");

        var page = main.MemberDetail;
        page.Week!.Days[1].Windows[0].End = "nine";
        page.Week.Days[3].AddWindowCommand.Execute(null);
        await page.Week.SaveCommand.ExecuteAsync(null);
        page.Profile!.DisplayName = "";
        await page.Profile.SaveCommand.ExecuteAsync(null);
        page.Categories!.Items[0].Choice = "block";
        page.NewPattern = "games.example/x";
        await page.AddRuleCommand.ExecuteAsync(null);
        page.NewChannel = "@khankids";
        await page.AddChannelCommand.ExecuteAsync(null);
        page.NewAppValue = "roblox.exe";
        await page.AddAppCommand.ExecuteAsync(null);
        await shown.RenderAsync("Member-noa-edits");

        main.ShowMember("yoav");
        await main.CurrentLoad;
        await shown.RenderAsync("Member-yoav");

        main.ShowMember("savta");
        await main.CurrentLoad;
        main.MemberDetail.Week!.RestrictsHours = true;
        await shown.RenderAsync("Member-savta-unrestricted");
    });

    [Fact]
    public Task Pages_render_on_an_agent_computer_without_binding_errors() => UiThread.RunAsync(async () =>
    {
        var client = new FakeAdminClient { IsHub = false };
        await using var shown = await ShownAsync(client);
        Assert.Equal([shown.Main.Dashboard, shown.Main.Settings], shown.Main.Pages);

        await shown.RenderAsync("Agent-dashboard");
        shown.Main.SelectedPage = shown.Main.Settings;
        await shown.Main.CurrentLoad;
        await shown.RenderAsync("Agent-settings");

        // A hub-only page (say, from a stale link) is not kept: the window goes back to the dashboard.
        shown.Main.SelectedPage = shown.Main.MemberDetail;
        await shown.Main.CurrentLoad;
        Assert.Same(shown.Main.Dashboard, shown.Main.SelectedPage);

        client.AgentHub = new HubConnection("pc-parents", "192.168.1.20:47800", false, TestData.Now.AddHours(-3));
        shown.Main.SelectedPage = shown.Main.Dashboard;
        await shown.Main.CurrentLoad;
        await shown.RenderAsync("Agent-dashboard-unreachable");
    });

    [Fact]
    public Task The_check_sees_a_broken_binding() => UiThread.RunAsync(async () =>
    {
        UiThread.BindingErrors.Clear();
        var text = new System.Windows.Controls.TextBlock { DataContext = new FakeDialogs() };
        text.SetBinding(System.Windows.Controls.TextBlock.TextProperty, "NoSuchProperty");
        text.Measure(new Size(100, 100));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Contains(UiThread.BindingErrors, e => e.Contains("NoSuchProperty", StringComparison.Ordinal));
        UiThread.BindingErrors.Clear();
    });

    private static async Task<ShownWindow> ShownAsync(FakeAdminClient client)
    {
        var main = new MainViewModel(client, new ManualTimer(), new FakeDialogs(), new FakeClipboard(), new ManualTime(TestData.Now));
        var full = UiThread.RenderDirectory is not null;
        var window = new MainWindow
        {
            DataContext = main,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Height = full ? 2400 : 760,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        UiThread.BindingErrors.Clear();
        window.Show();
        main.Start();
        await main.CurrentLoad;
        return new ShownWindow(window, main);
    }

    private sealed class ShownWindow(Window window, MainViewModel main) : IAsyncDisposable
    {
        public MainViewModel Main { get; } = main;

        /// <summary>Lets layout, data binding and rendering finish, renders the window, and checks for binding errors.</summary>
        public async Task RenderAsync(string state)
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var content = (FrameworkElement)window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);
            if (UiThread.RenderDirectory is { } directory)
            {
                // Rendered alone, right-to-left content comes out mirrored (the window applies the mirroring); flip it back.
                BitmapSource image = window.FlowDirection == FlowDirection.RightToLeft ? new TransformedBitmap(bitmap, new ScaleTransform(-1, 1)) : bitmap;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                await using var file = File.Create(Path.Combine(directory, state + ".png"));
                encoder.Save(file);
            }

            var errors = UiThread.BindingErrors.ToArray();
            UiThread.BindingErrors.Clear();
            Assert.True(errors.Length == 0, $"{state}: {errors.Length} binding error(s):\n{string.Join("\n", errors)}");
        }

        public async ValueTask DisposeAsync()
        {
            Main.Dispose();
            window.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }
}

/// <summary>
/// One STA thread for the whole test run with the admin app's <see cref="App"/> resources (styles, brushes, converters)
/// loaded, as WPF allows one application per process. Binding errors are collected from WPF's trace source.
/// </summary>
internal static class UiThread
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);

    public static ConcurrentQueue<string> BindingErrors { get; } = new();

    public static string? RenderDirectory { get; } = Environment.GetEnvironmentVariable("WEBFILTER_RENDER_DIR") is { Length: > 0 } dir
        ? Directory.CreateDirectory(dir).FullName
        : null;

    public static Task RunAsync(Func<Task> work) => Ui.Value.InvokeAsync(work).Task.Unwrap();

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiCulture.Apply(UiCulture.Hebrew);
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
                PresentationTraceSources.Refresh();
                PresentationTraceSources.DataBindingSource.Listeners.Add(new Collector());
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
                ready.SetResult(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }
            System.Windows.Threading.Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WPF render tests",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }

    private sealed class Collector : TraceListener
    {
        private readonly System.Text.StringBuilder _line = new();

        public override void Write(string? message) => _line.Append(message);

        public override void WriteLine(string? message)
        {
            _line.Append(message);
            BindingErrors.Enqueue(_line.ToString());
            _line.Clear();
        }
    }
}
