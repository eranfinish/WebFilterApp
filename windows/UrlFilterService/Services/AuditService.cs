using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Service for auditing and logging system activities
    /// </summary>
    public class AuditService
    {
        private readonly ILogger<AuditService> _logger;
        private readonly DatabaseService _databaseService;

        public AuditService(ILogger<AuditService> logger, DatabaseService databaseService)
        {
            _logger = logger;
            _databaseService = databaseService;
        }

        /// <summary>
        /// Log an audit event
        /// </summary>
        public async Task LogEventAsync(string eventType, string description, DeviceUser user = null)
        {
            try
            {
                _logger.LogInformation($"Audit Event [{eventType}]: {description} - User: {user?.Username ?? "System"}");
                
                // Log audit event to database
                var auditEntry = new AuditLog
                {
                    EventType = eventType,
                    Description = description,
                    UserId = user?.Id,
                    Username = user?.Username ?? "System",
                    Timestamp = DateTime.UtcNow,
                    IpAddress = GetCurrentUserIpAddress(),
                    DeviceInfo = Environment.MachineName,
                    Severity = DetermineSeverity(eventType)
                };

                await _databaseService.InsertAuditLogAsync(auditEntry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to log audit event");
            }
        }

        /// <summary>
        /// Log user activity
        /// </summary>
        public async Task LogUserActivityAsync(DeviceUser user, string activity, string details = null)
        {
            await LogEventAsync("UserActivity", $"{activity} - {details}", user);
        }

        /// <summary>
        /// Log system event
        /// </summary>
        public async Task LogSystemEventAsync(string eventName, string details = null)
        {
            await LogEventAsync("SystemEvent", $"{eventName} - {details}");
        }

        /// <summary>
        /// Log security event
        /// </summary>
        public async Task LogSecurityEventAsync(string eventName, string details = null, DeviceUser user = null)
        {
            await LogEventAsync("SecurityEvent", $"{eventName} - {details}", user);
        }

        /// <summary>
        /// Log admin action
        /// </summary>
        public async Task LogAdminActionAsync(string action, string details, AdminUser admin, DeviceUser targetUser = null)
        {
            var description = $"Admin {admin.DisplayName} performed action: {action}";
            if (!string.IsNullOrEmpty(details))
                description += $" - {details}";
            if (targetUser != null)
                description += $" - Target: {targetUser.Username}";

            await LogEventAsync("AdminAction", description);
        }

        /// <summary>
        /// Get current user's IP address
        /// </summary>
        private string GetCurrentUserIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var ip = host.AddressList.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return ip?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }

        /// <summary>
        /// Determine severity level based on event type
        /// </summary>
        private string DetermineSeverity(string eventType)
        {
            return eventType.ToLower() switch
            {
                "securityevent" => "Warning",
                "systemerror" => "Error",
                "criticalfailure" => "Critical",
                "bypassattempt" => "Warning",
                "unauthorizedaccess" => "Critical",
                _ => "Information"
            };
        }
    }
}