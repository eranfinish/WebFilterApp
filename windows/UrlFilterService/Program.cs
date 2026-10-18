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
                    case "/block":
                    case "-block":
                        if (args.Length < 2) { Console.WriteLine("Usage: UrlFilterService.exe /block <domain>"); return; }
                        AddBlockedDomain(args[1]);
                        return;
                    case "/unblock":
                    case "-unblock":
                        if (args.Length < 2) { Console.WriteLine("Usage: UrlFilterService.exe /unblock <domain>"); return; }
                        RemoveBlockedDomain(args[1]);
                        return;
                    case "/list":
                    case "-list":
                        ListBlockedDomains();
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
                // Use sc.exe to uninstall service instead of ManagedInstallerClass
                var result = System.Diagnostics.Process.Start("sc", "delete UrlFilterService");
                result?.WaitForExit();
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
            Console.WriteLine();

            UrlFilterWindowsService service = null;

            try
            {
                service = new UrlFilterWindowsService();

                // Handle console ctrl+c
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("\nShutting down service...");
                    service?.TestStop();
                    Environment.Exit(0);
                };

                // Start service
                service.TestStart();

                Console.WriteLine();
                Console.WriteLine("=== Service is running. Press Ctrl+C to stop. ===");
                Console.WriteLine();

                // Keep running until interrupted
                while (true)
                {
                    System.Threading.Thread.Sleep(1000);
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine();
                Console.WriteLine("=== SERVICE FAILED TO START ===");
                Console.WriteLine($"Error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"Inner: {ex.InnerException.Message}");
                }
                Console.WriteLine();
                Console.WriteLine("Stack trace:");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }

        private static void ShowHelp()
        {
            Console.WriteLine("URL Filter Service - Cross-Platform URL Filtering System");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  UrlFilterService.exe                 - Run as Windows service");
            Console.WriteLine("  UrlFilterService.exe /install        - Install Windows service");
            Console.WriteLine("  UrlFilterService.exe /uninstall      - Uninstall Windows service");
            Console.WriteLine("  UrlFilterService.exe /console        - Run in console mode for testing");
            Console.WriteLine("  UrlFilterService.exe /block <domain> - Add a domain to the blacklist");
            Console.WriteLine("  UrlFilterService.exe /unblock <domain> - Remove a domain from the blacklist");
            Console.WriteLine("  UrlFilterService.exe /list           - List all URL list entries");
            Console.WriteLine("  UrlFilterService.exe /help           - Show this help");
            Console.WriteLine();
            Console.WriteLine("Administrator privileges are required for install/uninstall operations.");
        }

        private static string GetDbPath()
        {
            // Mirror DatabaseService.ResolveDatabasePath: prefer ProgramData when writable,
            // else fall back to LocalAppData (non-admin CLI / console mode).
            var programData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UrlFilter", "Database");
            var pdDb = System.IO.Path.Combine(programData, "urlfilter.db");
            if (CanUseDbLocation(programData, pdDb))
                return pdDb;

            var localAppData = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UrlFilter", "Database");
            return System.IO.Path.Combine(localAppData, "urlfilter.db");
        }

        private static bool CanUseDbLocation(string dir, string dbPath)
        {
            try
            {
                System.IO.Directory.CreateDirectory(dir);
                var probe = System.IO.Path.Combine(dir, $".writetest-{Guid.NewGuid():N}");
                System.IO.File.WriteAllText(probe, "");
                System.IO.File.Delete(probe);
            }
            catch { return false; }

            if (System.IO.File.Exists(dbPath))
            {
                try
                {
                    using var fs = new System.IO.FileStream(dbPath, System.IO.FileMode.Open,
                        System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite);
                }
                catch { return false; }
            }
            return true;
        }

        private static void AddBlockedDomain(string domain)
        {
            try
            {
                var dbPath = GetDbPath();
                if (!System.IO.File.Exists(dbPath))
                {
                    Console.WriteLine($"Database not found at {dbPath}");
                    Console.WriteLine("Run the service once first (/console) to create it.");
                    return;
                }

                using var conn = new System.Data.SQLite.SQLiteConnection($"Data Source={dbPath}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO url_lists (url_pattern, list_type, is_active, match_type) VALUES (@d, 'blacklist', 1, 'domain')";
                cmd.Parameters.AddWithValue("@d", domain);
                int rows = cmd.ExecuteNonQuery();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Added '{domain}' to blacklist ({rows} row).");
                Console.ResetColor();
                Console.WriteLine("Restart the service (or wait up to 5 minutes) for changes to apply.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }

        private static void RemoveBlockedDomain(string domain)
        {
            try
            {
                using var conn = new System.Data.SQLite.SQLiteConnection($"Data Source={GetDbPath()}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM url_lists WHERE url_pattern = @d AND list_type = 'blacklist'";
                cmd.Parameters.AddWithValue("@d", domain);
                int rows = cmd.ExecuteNonQuery();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Removed {rows} row(s) for '{domain}'.");
                Console.ResetColor();
                Console.WriteLine("Restart the service (or wait up to 5 minutes) for changes to apply.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }

        private static void ListBlockedDomains()
        {
            try
            {
                using var conn = new System.Data.SQLite.SQLiteConnection($"Data Source={GetDbPath()}");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, url_pattern, list_type, is_active FROM url_lists ORDER BY list_type, url_pattern";
                using var r = cmd.ExecuteReader();
                Console.WriteLine();
                Console.WriteLine("ID  | Active | Type      | Pattern");
                Console.WriteLine("----+--------+-----------+---------------------------------------");
                int count = 0;
                while (r.Read())
                {
                    int id = r.GetInt32(0);
                    string url = r.GetString(1);
                    string type = r.GetString(2);
                    string active = r.GetBoolean(3) ? "YES" : "no";
                    Console.WriteLine($"{id,3} | {active,-6} | {type,-9} | {url}");
                    count++;
                }
                Console.WriteLine();
                Console.WriteLine($"Total: {count} entries");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}