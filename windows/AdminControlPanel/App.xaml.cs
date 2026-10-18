using System;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AdminControlPanel.Services;
using AdminControlPanel.Views;

namespace AdminControlPanel
{
    public partial class App : Application
    {
        private ServiceProvider _serviceProvider;

        public ServiceProvider? ServiceProvider => _serviceProvider;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // IMPORTANT: Prevent app from shutting down when dialogs close
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Global exception handlers
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                var ex = args.ExceptionObject as Exception;
                MessageBox.Show($"Unhandled Exception:\n{ex?.Message}\n\n{ex?.StackTrace}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"UI Exception:\n{args.Exception.Message}\n\n{args.Exception.StackTrace}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };

            // Step 1: Check if running as Windows Administrator
            if (!IsRunningAsAdmin())
            {
                // Try to restart as admin
                var result = MessageBox.Show(
                    "The URL Filter Control Panel requires administrator privileges.\n\n" +
                    "Click OK to restart as administrator, or Cancel to exit.",
                    "Administrator Required",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                if (result == MessageBoxResult.OK)
                {
                    RestartAsAdmin();
                }

                Shutdown();
                return;
            }

            // Configure dependency injection
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider();

            // Step 2: Check authentication
            var authService = _serviceProvider.GetRequiredService<AdminAuthenticationService>();

            if (authService.IsFirstTimeSetup())
            {
                // First time setup - create admin credentials
                var setupDialog = new AdminSetupDialog();
                if (setupDialog.ShowDialog() != true)
                {
                    // User cancelled setup
                    Shutdown();
                    return;
                }
            }
            else
            {
                // Require login
                var loginDialog = new LoginDialog();
                if (loginDialog.ShowDialog() != true || !loginDialog.IsAuthenticated)
                {
                    // Login failed or cancelled
                    Shutdown();
                    return;
                }
            }

            // Step 3: Show main window after successful authentication
            try
            {
                var viewModel = _serviceProvider.GetRequiredService<ViewModels.MainWindowViewModel>();
                var mainWindow = new Views.MainWindow(viewModel);

                // Now switch to normal shutdown mode - close when main window closes
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                MainWindow = mainWindow;

                mainWindow.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error creating main window:\n{ex.Message}\n\n{ex.InnerException?.Message}\n\n{ex.StackTrace}",
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown();
            }
        }

        private void ConfigureServices(IServiceCollection services)
        {
            // Logging - required for ViewModels and Services
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Debug);
            });

            // Services
            services.AddSingleton<AdminAuthenticationService>();
            services.AddSingleton<UrlFilterServiceClient>();
            services.AddSingleton<UserDatabaseService>();

            // Views and ViewModels
            services.AddTransient<Views.MainWindow>();
            services.AddTransient<ViewModels.MainWindowViewModel>();
        }

        /// <summary>
        /// Check if the application is running with administrator privileges
        /// </summary>
        private bool IsRunningAsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Restart the application with administrator privileges (UAC prompt)
        /// </summary>
        private void RestartAsAdmin()
        {
            try
            {
                var exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath))
                    return;

                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas" // This triggers UAC elevation
                };

                Process.Start(startInfo);
            }
            catch (Exception)
            {
                // User cancelled UAC or other error
                MessageBox.Show(
                    "Failed to restart as administrator.\n\n" +
                    "Please right-click the application and select 'Run as administrator'.",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _serviceProvider?.Dispose();
            base.OnExit(e);
        }
    }
}
