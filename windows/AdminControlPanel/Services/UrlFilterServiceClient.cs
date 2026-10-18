using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace AdminControlPanel.Services
{
    /// <summary>
    /// Client for communicating with the UrlFilterService
    /// </summary>
    public class UrlFilterServiceClient
    {
        private readonly ILogger<UrlFilterServiceClient> _logger;
        private const string ServiceName = "UrlFilterService";
        private const string PipeName = "UrlFilterServicePipe";

        public UrlFilterServiceClient(ILogger<UrlFilterServiceClient> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Check if the service is running
        /// </summary>
        public bool IsServiceRunning()
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                return service.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check service status");
                return false;
            }
        }

        /// <summary>
        /// Start the URL Filter Service
        /// </summary>
        public async Task<bool> StartServiceAsync()
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                if (service.Status == ServiceControllerStatus.Stopped)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                }
                return service.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start service");
                return false;
            }
        }

        /// <summary>
        /// Stop the URL Filter Service
        /// </summary>
        public async Task<bool> StopServiceAsync()
        {
            try
            {
                using var service = new ServiceController(ServiceName);
                if (service.Status == ServiceControllerStatus.Running)
                {
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                return service.Status == ServiceControllerStatus.Stopped;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop service");
                return false;
            }
        }

        /// <summary>
        /// Enable filtering (turn filtering ON)
        /// </summary>
        public async Task<bool> EnableFilteringAsync()
        {
            return await SendCommandAsync(new ServiceCommand { Action = "EnableFiltering" });
        }

        /// <summary>
        /// Disable filtering (turn filtering OFF)
        /// </summary>
        public async Task<bool> DisableFilteringAsync()
        {
            return await SendCommandAsync(new ServiceCommand { Action = "DisableFiltering" });
        }

        /// <summary>
        /// Get current filtering status
        /// </summary>
        public async Task<FilteringStatus> GetFilteringStatusAsync()
        {
            try
            {
                var command = new ServiceCommand { Action = "GetStatus" };
                var responseData = await SendCommandWithDataAsync(command);
                
                if (responseData != null && responseData.Success)
                {
                    return ParseFilteringStatus(responseData);
                }
                
                // Fallback to basic status if service communication fails
                return new FilteringStatus
                {
                    IsEnabled = IsServiceRunning(),
                    TotalRequestsToday = 0,
                    BlockedRequestsToday = 0,
                    ActiveUsers = 0,
                    ServiceVersion = "Unknown",
                    LastUpdated = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get filtering status");
                return new FilteringStatus { IsEnabled = false };
            }
        }

        /// <summary>
        /// Add time extension for a user
        /// </summary>
        public async Task<bool> ExtendUserTimeAsync(int userId, int additionalMinutes)
        {
            return await SendCommandAsync(new ServiceCommand 
            { 
                Action = "ExtendUserTime",
                Parameters = new Dictionary<string, object>
                {
                    ["UserId"] = userId,
                    ["AdditionalMinutes"] = additionalMinutes
                }
            });
        }

        /// <summary>
        /// Enable homework mode for a user
        /// </summary>
        public async Task<bool> EnableHomeworkModeAsync(int userId, int durationMinutes = 120)
        {
            return await SendCommandAsync(new ServiceCommand 
            { 
                Action = "EnableHomeworkMode",
                Parameters = new Dictionary<string, object>
                {
                    ["UserId"] = userId,
                    ["DurationMinutes"] = durationMinutes
                }
            });
        }

        /// <summary>
        /// Block internet for a user immediately
        /// </summary>
        public async Task<bool> BlockUserNowAsync(int userId)
        {
            return await SendCommandAsync(new ServiceCommand 
            { 
                Action = "BlockUserNow",
                Parameters = new Dictionary<string, object>
                {
                    ["UserId"] = userId
                }
            });
        }

        /// <summary>
        /// Send command to service via named pipe
        /// </summary>
        private async Task<bool> SendCommandAsync(ServiceCommand command)
        {
            try
            {
                var response = await SendCommandWithDataAsync(command);
                return response?.Success ?? false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to service: {Command}", command.Action);
                return false;
            }
        }

        /// <summary>
        /// Send command to service and return full response data
        /// </summary>
        private async Task<ServiceResponse?> SendCommandWithDataAsync(ServiceCommand command)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                await client.ConnectAsync(5000); // 5 second timeout
                
                var json = JsonSerializer.Serialize(command);
                var buffer = System.Text.Encoding.UTF8.GetBytes(json);
                
                await client.WriteAsync(buffer, 0, buffer.Length);
                await client.FlushAsync();
                
                var responseBuffer = new byte[4096]; // Increased buffer size for status data
                var bytesRead = await client.ReadAsync(responseBuffer, 0, responseBuffer.Length);
                
                if (bytesRead > 0)
                {
                    var responseJson = System.Text.Encoding.UTF8.GetString(responseBuffer, 0, bytesRead);
                    return JsonSerializer.Deserialize<ServiceResponse>(responseJson);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send command to service: {Command}", command.Action);
                return null;
            }
        }

        /// <summary>
        /// Parse filtering status from service response
        /// </summary>
        private FilteringStatus ParseFilteringStatus(ServiceResponse response)
        {
            var status = new FilteringStatus
            {
                IsEnabled = IsServiceRunning(),
                LastUpdated = DateTime.Now
            };

            if (response.Data != null)
            {
                // Parse statistics from service response
                if (response.Data.TryGetValue("TotalRequestsToday", out var totalRequests))
                {
                    if (totalRequests is JsonElement totalElement && totalElement.ValueKind == JsonValueKind.Number)
                    {
                        status.TotalRequestsToday = totalElement.GetInt32();
                    }
                }

                if (response.Data.TryGetValue("BlockedRequestsToday", out var blockedRequests))
                {
                    if (blockedRequests is JsonElement blockedElement && blockedElement.ValueKind == JsonValueKind.Number)
                    {
                        status.BlockedRequestsToday = blockedElement.GetInt32();
                    }
                }

                if (response.Data.TryGetValue("ActiveUsers", out var activeUsers))
                {
                    if (activeUsers is JsonElement activeElement && activeElement.ValueKind == JsonValueKind.Number)
                    {
                        status.ActiveUsers = activeElement.GetInt32();
                    }
                }

                if (response.Data.TryGetValue("ServiceVersion", out var version))
                {
                    if (version is JsonElement versionElement && versionElement.ValueKind == JsonValueKind.String)
                    {
                        status.ServiceVersion = versionElement.GetString() ?? "Unknown";
                    }
                }

                if (response.Data.TryGetValue("FilteringEnabled", out var filteringEnabled))
                {
                    if (filteringEnabled is JsonElement enabledElement && enabledElement.ValueKind == JsonValueKind.True)
                    {
                        status.IsFilteringActive = true;
                    }
                }

                if (response.Data.TryGetValue("DatabaseConnected", out var dbConnected))
                {
                    if (dbConnected is JsonElement dbElement && dbElement.ValueKind == JsonValueKind.True)
                    {
                        status.IsDatabaseConnected = true;
                    }
                }

                // Parse user statuses if available
                if (response.Data.TryGetValue("UserStatuses", out var userStatuses))
                {
                    if (userStatuses is JsonElement userElement && userElement.ValueKind == JsonValueKind.Array)
                    {
                        status.UserStatuses = ParseUserStatuses(userElement);
                    }
                }
            }

            return status;
        }

        /// <summary>
        /// Parse user status information from JSON
        /// </summary>
        private List<UserStatusInfo> ParseUserStatuses(JsonElement userArray)
        {
            var userStatuses = new List<UserStatusInfo>();

            foreach (var userElement in userArray.EnumerateArray())
            {
                if (userElement.ValueKind == JsonValueKind.Object)
                {
                    var userStatus = new UserStatusInfo();
                    
                    if (userElement.TryGetProperty("UserId", out var userIdElement))
                        userStatus.UserId = userIdElement.GetInt32();
                    
                    if (userElement.TryGetProperty("Name", out var nameElement))
                        userStatus.Name = nameElement.GetString() ?? "";
                    
                    if (userElement.TryGetProperty("Age", out var ageElement))
                        userStatus.Age = ageElement.GetInt32();
                    
                    if (userElement.TryGetProperty("UserType", out var typeElement))
                    {
                        if (Enum.TryParse<UserType>(typeElement.GetString(), out var userType))
                            userStatus.UserType = userType;
                    }
                    
                    if (userElement.TryGetProperty("IsOnline", out var onlineElement))
                        userStatus.IsOnline = onlineElement.GetBoolean();
                    
                    if (userElement.TryGetProperty("MinutesLeftToday", out var minutesElement))
                        userStatus.MinutesLeftToday = minutesElement.GetInt32();
                    
                    if (userElement.TryGetProperty("Status", out var statusElement))
                        userStatus.Status = statusElement.GetString() ?? "";

                    userStatuses.Add(userStatus);
                }
            }

            return userStatuses;
        }
    }

    /// <summary>
    /// Command to send to the service
    /// </summary>
    public class ServiceCommand
    {
        public string Action { get; set; } = "";
        public Dictionary<string, object>? Parameters { get; set; }
    }

    /// <summary>
    /// Response from the service
    /// </summary>
    public class ServiceResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, object>? Data { get; set; }
    }

    /// <summary>
    /// Current filtering status
    /// </summary>
    public class FilteringStatus
    {
        public bool IsEnabled { get; set; }
        public bool IsFilteringActive { get; set; }
        public bool IsDatabaseConnected { get; set; }
        public int TotalRequestsToday { get; set; }
        public int BlockedRequestsToday { get; set; }
        public int ActiveUsers { get; set; }
        public string ServiceVersion { get; set; } = "Unknown";
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        public List<UserStatusInfo> UserStatuses { get; set; } = new List<UserStatusInfo>();
        
        public int AllowedRequestsToday => TotalRequestsToday - BlockedRequestsToday;
        public double BlockPercentage => TotalRequestsToday > 0 ? (double)BlockedRequestsToday / TotalRequestsToday * 100 : 0;
    }

    /// <summary>
    /// User status information from service
    /// </summary>
    public class UserStatusInfo
    {
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public int Age { get; set; }
        public UserType UserType { get; set; }
        public bool IsOnline { get; set; }
        public int MinutesLeftToday { get; set; }
        public string Status { get; set; } = "";
    }
}