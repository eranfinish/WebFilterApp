using System;
using System.ServiceProcess;
//using System.Configuration.Install;
using System.Reflection;
using System.IO;

namespace UrlFilterService
{
    /// <summary>
    /// Main program entry point for the URL Filter Windows Service
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                switch (args[0].ToLower())
                {
                    case "/install":
                    case "-install":
                        InstallService();
                        return;
                    case "/uninstall":
                    case "-uninstall":
                        UninstallService();
                        return;
                    case "/console":
                    case "-console":
                        RunAsConsole();
                        return;
                    case "/help":
                    case "-help":
                    case "/?":
                        ShowHelp();
                        return;
                }
            }

            // Run as Windows Service by default
            ServiceBase[] ServicesToRun;
            ServicesToRun = new ServiceBase[]
            {
                new UrlFilterWindowsService()
            };
            ServiceBase.Run(ServicesToRun);
        }

        private static void InstallService()
        {
            try
            {
                Console.WriteLine("Installing URL Filter Service...");
                Console.WriteLine("Service installation using sc.exe...");
                // Use sc.exe to install service instead of ManagedInstallerClass
                var exePath = Assembly.GetExecutingAssembly().Location;
                var result = System.Diagnostics.Process.Start("sc", $"create UrlFilterService binPath=\"{exePath}\" start=auto");
                result?.WaitForExit();
                Console.WriteLine("Service installed successfully.");
                Console.WriteLine("Use 'net start UrlFilterService' to start the service.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error installing service: {ex.Message}");
                Console.WriteLine("Make sure you are running as administrator.");
            }
        }

        private static void UninstallService()
        {
            try
            {
                Console.WriteLine("Uninstalling URL Filter Service...");
                ManagedInstallerClass.InstallHelper(new string[] { "/u", Assembly.GetExecutingAssembly().Location });
                Console.WriteLine("Service uninstalled successfully.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uninstalling service: {ex.Message}");
                Console.WriteLine("Make sure you are running as administrator.");
            }
        }

        private static void RunAsConsole()
        {
            Console.WriteLine("Starting URL Filter Service in console mode...");
            Console.WriteLine("Press Ctrl+C to stop.");

            var service = new UrlFilterWindowsService();
            
            // Handle console ctrl+c
            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                Console.WriteLine("\nShutting down service...");
                service.TestStop();
                Environment.Exit(0);
            };

            // Start service
            service.TestStart();

            // Keep running until interrupted
            while (true)
            {
                System.Threading.Thread.Sleep(1000);
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("URL Filter Service - Cross-Platform URL Filtering System");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  UrlFilterService.exe             - Run as Windows service");
            Console.WriteLine("  UrlFilterService.exe /install    - Install Windows service");
            Console.WriteLine("  UrlFilterService.exe /uninstall  - Uninstall Windows service");
            Console.WriteLine("  UrlFilterService.exe /console    - Run in console mode for testing");
            Console.WriteLine("  UrlFilterService.exe /help       - Show this help");
            Console.WriteLine();
            Console.WriteLine("Administrator privileges are required for install/uninstall operations.");
        }
    }
}