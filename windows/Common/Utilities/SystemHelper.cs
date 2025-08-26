using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace Common.Utilities
{
    /// <summary>
    /// System utility functions for URL Filter
    /// </summary>
    public static class SystemHelper
    {
        /// <summary>
        /// Check if current process is running as administrator
        /// </summary>
        public static bool IsRunningAsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Check if Windows service exists and is running
        /// </summary>
        public static bool IsServiceRunning(string serviceName)
        {
            try
            {
                using var service = new ServiceController(serviceName);
                return service.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Start Windows service
        /// </summary>
        public static bool StartService(string serviceName, ILogger logger = null)
        {
            try
            {
                using var service = new ServiceController(serviceName);
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMinutes(2));
                    logger?.LogInformation($"Service {serviceName} started successfully");
                    return true;
                }
                return true; // Already running
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, $"Failed to start service {serviceName}");
                return false;
            }
        }
        
        /// <summary>
        /// Stop Windows service
        /// </summary>
        public static bool StopService(string serviceName, ILogger logger = null)
        {
            try
            {
                using var service = new ServiceController(serviceName);
                if (service.Status == ServiceControllerStatus.Running)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromMinutes(2));
                    logger?.LogInformation($"Service {serviceName} stopped successfully");
                }
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, $"Failed to stop service {serviceName}");
                return false;
            }
        }
        
        /// <summary>
        /// Execute command with administrator privileges
        /// </summary>
        public static bool ExecuteAsAdmin(string fileName, string arguments = "", ILogger logger = null)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = true,
                    Verb = "runas", // Run as administrator
                    WindowStyle = ProcessWindowStyle.Normal
                };
                
                using var process = Process.Start(startInfo);
                process?.WaitForExit();
                
                logger?.LogInformation($"Executed {fileName} with admin privileges");
                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, $"Failed to execute {fileName} as admin");
                return false;
            }
        }
        
        /// <summary>
        /// Ensure directory exists with proper permissions
        /// </summary>
        public static void EnsureDirectoryExists(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }
            }
            catch
            {
                // Ignore directory creation errors
            }
        }
        
        /// <summary>
        /// Get application data directory for URL Filter
        /// </summary>
        public static string GetAppDataDirectory()
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UrlFilter");
            EnsureDirectoryExists(path);
            return path;
        }
        
        /// <summary>
        /// Check if port is available
        /// </summary>
        public static bool IsPortAvailable(int port)
        {
            try
            {
                var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}