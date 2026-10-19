using System.Windows;
using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;

namespace WebFilter.Admin;

/// <summary>Composition root only: builds the services and the main view model, shows the window, cleans up on exit.</summary>
public partial class App : Application
{
    private AdminClient? _client;
    private MainViewModel? _main;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        UiCulture.Apply(UiCulture.Hebrew);
        var options = AdminAppOptions.Parse(e.Args);
        _client = new AdminClient(new AdminClientOptions { PipeName = options.PipeName });
        _main = new MainViewModel(_client, new DispatcherUiTimer(), new MessageBoxDialogService(), new WpfClipboardService());
        MainWindow = new MainWindow { DataContext = _main };
        MainWindow.Show();
        _main.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _main?.Dispose();
        _client?.Dispose();
        base.OnExit(e);
    }
}
