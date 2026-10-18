using System;
using System.ServiceProcess;
using System.ComponentModel;
using System.Timers;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UrlFilterService.Services;
using UrlFilter.Shared.Models;

namespace UrlFilterService
{
    /// <summary>
    /// Windows Service implementation for URL filtering.
    /// Primary filtering: DNS interception (blocks domains by returning 0.0.0.0).
    /// Secondary filtering: WFP IP-based blocking (backup enforcement).
    /// IPC: Named pipe server for admin panel communication.
    /// </summary>
    public partial class UrlFilterWindowsService : ServiceBase
    {
        private ILogger<UrlFilterWindowsService> _logger;
        private IServiceProvider _serviceProvider;
        private WfpFilterService _wfpService;
        private DnsInterceptorService _dnsService;
        private ServiceCommandHandler _commandHandler;
        private UserDetectionService _userDetectionService;
        private FilterEngine _filterEngine;
        private DatabaseService _databaseService;
        private PolicyEnforcementService _policyService;
        private Timer _monitoringTimer;
        private bool _isRunning = false;

        public UrlFilterWindowsService()
        {
            SetupDependencyInjection();
        }

        private void SetupDependencyInjection()
        {
            var services = new ServiceCollection();

            // Logging
            services.AddLogging(builder =>
            {
                builder.AddEventLog(settings =>
                {
                    settings.SourceName = "URL Filter Service";
                    settings.LogName = "Application";
                });
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            // Core services
            services.AddSingleton<DatabaseService>();
            services.AddSingleton<WfpFilterService>();
            services.AddSingleton<DnsInterceptorService>();
            services.AddSingleton<ServiceCommandHandler>();
            services.AddSingleton<UserDetectionService>();
            services.AddSingleton<FilterEngine>();
            services.AddSingleton<AuditService>();
            services.AddSingleton<PolicyEnforcementService>();

            _serviceProvider = services.BuildServiceProvider();
            _logger = _serviceProvider.GetService<ILogger<UrlFilterWindowsService>>();
        }

        protected override void OnStart(string[] args)
        {
            _logger.LogInformation("URL Filter Service starting...");
            TestStart();
        }

        protected override void OnStop()
        {
            _logger.LogInformation("URL Filter Service stopping...");
            TestStop();
        }

        public void TestStart()
        {
            try
            {
                _isRunning = true;

                // Initialize core services
                _databaseService = _serviceProvider.GetService<DatabaseService>();
                _userDetectionService = _serviceProvider.GetService<UserDetectionService>();
                _filterEngine = _serviceProvider.GetService<FilterEngine>();
                _wfpService = _serviceProvider.GetService<WfpFilterService>();
                _dnsService = _serviceProvider.GetService<DnsInterceptorService>();
                _commandHandler = _serviceProvider.GetService<ServiceCommandHandler>();
                _policyService = _serviceProvider.GetService<PolicyEnforcementService>();

                // Initialize database
                InitializeDatabaseAsync().Wait();

                // Initialize user detection
                _logger.LogInformation("Initializing user detection service");
                _userDetectionService.InvalidateUserCache();

                // Start WFP service (IP-based blocking backup)
                _wfpService.Start();

                // Start DNS interceptor (primary filtering)
                _dnsService.StartAsync().Wait();

                // Start IPC command handler (admin panel communication)
                _commandHandler.Start();

                // Start user detection
                _userDetectionService.Start();

                // Start policy enforcement (DoH block, browser policies, process kill)
                _policyService.Start();

                // Setup periodic monitoring
                SetupMonitoring();

                _logger.LogInformation("URL Filter Service started successfully");
                _logger.LogInformation("  DNS filtering: ACTIVE on 127.0.0.1:53");
                _logger.LogInformation("  WFP IP blocking: ACTIVE");
                _logger.LogInformation("  IPC command handler: ACTIVE");

                LogServiceEvent("SERVICE_STARTED");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start URL Filter Service");
                // Attempt graceful cleanup
                TestStop();
                throw;
            }
        }

        public void TestStop()
        {
            try
            {
                _isRunning = false;

                // Stop monitoring timer
                _monitoringTimer?.Stop();
                _monitoringTimer?.Dispose();

                // Stop services in reverse order
                _policyService?.Stop();
                _commandHandler?.Stop();
                _userDetectionService?.Stop();
                _dnsService?.Stop();  // This restores original DNS settings
                _wfpService?.Stop();

                _logger.LogInformation("URL Filter Service stopped successfully");

                LogServiceEvent("SERVICE_STOPPED");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping URL Filter Service");
            }
        }

        private async Task InitializeDatabaseAsync()
        {
            try
            {
                await _databaseService.InitializeAsync();
                _logger.LogInformation("Database initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database");
                throw;
            }
        }

        private void SetupMonitoring()
        {
            _monitoringTimer = new Timer(60000); // 1 minute interval
            _monitoringTimer.Elapsed += OnMonitoringTimer;
            _monitoringTimer.AutoReset = true;
            _monitoringTimer.Start();

            _logger.LogInformation("Monitoring timer started");
        }

        private async void OnMonitoringTimer(object sender, ElapsedEventArgs e)
        {
            if (!_isRunning) return;

            try
            {
                await PerformMonitoringTasksAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during monitoring tasks");
            }
        }

        private async Task PerformMonitoringTasksAsync()
        {
            // Update user sessions
            await UpdateUserSessionsAsync();

            // Check time restrictions
            await CheckTimeRestrictionsAsync();

            // Update statistics
            await UpdateStatisticsAsync();

            // Cleanup old data
            await CleanupOldDataAsync();

            // Check service health
            CheckServiceHealth();
        }

        private async Task UpdateUserSessionsAsync()
        {
            try
            {
                var activeUsers = await _userDetectionService.GetActiveUsersAsync();
                
                foreach (var user in activeUsers)
                {
                    var currentSession = await _databaseService.GetCurrentSessionAsync(user.Id);
                    
                    if (currentSession != null)
                    {
                        // Update session activity
                        currentSession.LastActivityTime = DateTime.UtcNow;
                        await _databaseService.UpdateSessionAsync(currentSession);
                    }
                }

                _logger.LogDebug($"Updated sessions for {activeUsers.Count} active users");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating user sessions");
            }
        }

        private async Task CheckTimeRestrictionsAsync()
        {
            try
            {
                var activeUsers = await _userDetectionService.GetActiveUsersAsync();
                var currentTime = DateTime.Now;

                foreach (var user in activeUsers)
                {
                    var timeRestriction = await _databaseService.GetUserTimeRestrictionAsync(
                        user.Id, (int)currentTime.DayOfWeek);

                    if (timeRestriction != null)
                    {
                        var accessResult = _filterEngine.CheckTimeAccess(user, timeRestriction, currentTime);
                        
                        if (!accessResult.IsAllowed)
                        {
                            // Block user's internet access
                            await _wfpService.BlockUserTrafficAsync(user);
                            
                            // Log time restriction event
                            await LogTimeRestrictionEventAsync(user, accessResult.Reason);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking time restrictions");
            }
        }

        private async Task UpdateStatisticsAsync()
        {
            try
            {
                var today = DateTime.Today;
                var stats = await _databaseService.GetDailyStatisticsAsync(today);

                // Update system statistics
                await _databaseService.UpdateSystemStatisticsAsync(new SystemStatistics
                {
                    Date = today,
                    ActiveUsers = await _userDetectionService.GetActiveUserCountAsync(),
                    TotalRequests = stats.TotalRequests,
                    BlockedRequests = stats.BlockedRequests,
                    AllowedRequests = stats.AllowedRequests
                });

                _logger.LogDebug("Updated daily statistics");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating statistics");
            }
        }

        private async Task CleanupOldDataAsync()
        {
            try
            {
                // Cleanup data older than configured retention period
                var retentionDays = 90; // Default 90 days
                var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);

                await _databaseService.CleanupOldDataAsync(cutoffDate);

                _logger.LogDebug($"Cleaned up data older than {cutoffDate}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleaning up old data");
            }
        }

        private void CheckServiceHealth()
        {
            try
            {
                bool isHealthy = true;
                var healthIssues = new System.Collections.Generic.List<string>();

                // Check DNS interceptor
                if (!_dnsService.IsRunning)
                {
                    isHealthy = false;
                    healthIssues.Add("DNS Interceptor not running");
                }

                // Check WFP service
                if (!_wfpService.IsRunning)
                {
                    isHealthy = false;
                    healthIssues.Add("WFP Filter Service not running");
                }

                // Check database connectivity
                if (!_databaseService.IsConnected)
                {
                    isHealthy = false;
                    healthIssues.Add("Database not connected");
                }

                if (!isHealthy)
                {
                    _logger.LogWarning($"Service health issues detected: {string.Join(", ", healthIssues)}");
                    
                    // Attempt to restart failed services
                    Task.Run(async () => await RestartFailedServicesAsync());
                }
                else
                {
                    _logger.LogDebug("Service health check passed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
            }
        }

        private async Task RestartFailedServicesAsync()
        {
            try
            {
                if (!_dnsService.IsRunning)
                {
                    _logger.LogInformation("Restarting DNS Interceptor Service");
                    _dnsService.Stop();
                    await Task.Delay(2000);
                    await _dnsService.StartAsync();
                }

                if (!_wfpService.IsRunning)
                {
                    _logger.LogInformation("Restarting WFP Filter Service");
                    _wfpService.Stop();
                    await Task.Delay(2000);
                    _wfpService.Start();
                }

                if (!_databaseService.IsConnected)
                {
                    _logger.LogInformation("Reconnecting to database");
                    await _databaseService.ReconnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error restarting failed services");
            }
        }

        private void LogServiceEvent(string eventType)
        {
            Task.Run(async () =>
            {
                try
                {
                    var auditService = _serviceProvider.GetService<AuditService>();
                    await auditService.LogEventAsync(eventType, $"URL Filter Service event: {eventType}");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error logging service event");
                }
            });
        }

        private async Task LogTimeRestrictionEventAsync(DeviceUser user, string reason)
        {
            try
            {
                var blockedAttempt = new BlockedAttempt
                {
                    UserId = user.Id,
                    Url = "time_restriction",
                    ContentType = "system",
                    BlockReason = reason,
                    Timestamp = DateTime.UtcNow,
                    DeviceInfo = Environment.MachineName,
                    SeverityLevel = "medium"
                };

                await _databaseService.InsertBlockedAttemptAsync(blockedAttempt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error logging time restriction event");
            }
        }

        protected override void OnPause()
        {
            _logger.LogInformation("URL Filter Service paused");
            _monitoringTimer?.Stop();
        }

        protected override void OnContinue()
        {
            _logger.LogInformation("URL Filter Service resumed");
            _monitoringTimer?.Start();
        }

        protected override void OnShutdown()
        {
            _logger.LogInformation("URL Filter Service shutting down due to system shutdown");
            TestStop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _monitoringTimer?.Dispose();
                (_serviceProvider as IDisposable)?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // Service installer - Commented out as modern .NET services use different installation approach
    /*
    [RunInstaller(true)]
    public partial class UrlFilterServiceInstaller : Installer
    {
        private ServiceProcessInstaller serviceProcessInstaller;
        private ServiceInstaller serviceInstaller;

        public UrlFilterServiceInstaller()
        {
            serviceProcessInstaller = new ServiceProcessInstaller();
            serviceInstaller = new ServiceInstaller();

            // Service will run under LocalSystem account
            serviceProcessInstaller.Account = ServiceAccount.LocalSystem;
            serviceProcessInstaller.Username = null;
            serviceProcessInstaller.Password = null;

            // Service properties
            serviceInstaller.ServiceName = "UrlFilterService";
            serviceInstaller.DisplayName = "URL Filter Service";
            serviceInstaller.Description = "Cross-platform URL filtering and parental control service";
            serviceInstaller.StartType = ServiceStartMode.Automatic;

            // Add installers to collection
            Installers.Add(serviceProcessInstaller);
            Installers.Add(serviceInstaller);
        }
    }
    */
}