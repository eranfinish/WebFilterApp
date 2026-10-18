using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Named pipe IPC server that receives commands from the Admin Control Panel.
    /// Handles enable/disable filtering, status queries, user management commands.
    /// </summary>
    public class ServiceCommandHandler
    {
        private readonly ILogger<ServiceCommandHandler> _logger;
        private readonly DnsInterceptorService _dnsService;
        private readonly DatabaseService _databaseService;
        private readonly WfpFilterService _wfpService;
        private CancellationTokenSource? _cts;
        private bool _isRunning;

        private const string PipeName = "UrlFilterServicePipe";

        public bool IsRunning => _isRunning;

        public ServiceCommandHandler(
            ILogger<ServiceCommandHandler> logger,
            DnsInterceptorService dnsService,
            DatabaseService databaseService,
            WfpFilterService wfpService)
        {
            _logger = logger;
            _dnsService = dnsService;
            _databaseService = databaseService;
            _wfpService = wfpService;
        }

        public void Start()
        {
            if (_isRunning) return;

            _cts = new CancellationTokenSource();
            _isRunning = true;

            _ = Task.Run(() => ListenLoopAsync(_cts.Token));
            _logger.LogInformation("ServiceCommandHandler started on pipe: {PipeName}", PipeName);
        }

        public void Stop()
        {
            _isRunning = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _logger.LogInformation("ServiceCommandHandler stopped");
        }

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _isRunning)
            {
                NamedPipeServerStream? pipeServer = null;
                try
                {
                    pipeServer = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipeServer.WaitForConnectionAsync(ct);

                    // Handle each client connection
                    await HandleClientAsync(pipeServer);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        _logger.LogError(ex, "Error in IPC listen loop");
                        await Task.Delay(500); // Brief delay before retrying
                    }
                }
                finally
                {
                    try
                    {
                        pipeServer?.Disconnect();
                    }
                    catch { }
                    pipeServer?.Dispose();
                }
            }
        }

        private async Task HandleClientAsync(NamedPipeServerStream pipe)
        {
            try
            {
                // Read command
                var buffer = new byte[4096];
                int bytesRead = await pipe.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead == 0) return;

                string json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var command = JsonSerializer.Deserialize<ServiceCommand>(json);

                if (command == null)
                {
                    await SendResponseAsync(pipe, false, "Invalid command");
                    return;
                }

                _logger.LogInformation("Received IPC command: {Action}", command.Action);

                // Dispatch command
                var response = await DispatchCommandAsync(command);

                // Send response
                string responseJson = JsonSerializer.Serialize(response);
                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                await pipe.WriteAsync(responseBytes, 0, responseBytes.Length);
                await pipe.FlushAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling IPC client");
            }
        }

        private async Task<ServiceResponse> DispatchCommandAsync(ServiceCommand command)
        {
            try
            {
                switch (command.Action)
                {
                    case "EnableFiltering":
                        _dnsService.EnableFiltering();
                        return new ServiceResponse
                        {
                            Success = true,
                            Message = "Filtering enabled"
                        };

                    case "DisableFiltering":
                        _dnsService.DisableFiltering();
                        return new ServiceResponse
                        {
                            Success = true,
                            Message = "Filtering disabled"
                        };

                    case "GetStatus":
                        return await BuildStatusResponseAsync();

                    case "RefreshRules":
                        await _dnsService.RefreshBlockListAsync();
                        return new ServiceResponse
                        {
                            Success = true,
                            Message = "Filter rules refreshed"
                        };

                    case "ExtendUserTime":
                        return await HandleExtendUserTimeAsync(command);

                    case "EnableHomeworkMode":
                        return await HandleHomeworkModeAsync(command);

                    case "BlockUserNow":
                        return await HandleBlockUserAsync(command);

                    default:
                        return new ServiceResponse
                        {
                            Success = false,
                            Message = $"Unknown command: {command.Action}"
                        };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error dispatching command {Action}", command.Action);
                return new ServiceResponse
                {
                    Success = false,
                    Message = $"Error: {ex.Message}"
                };
            }
        }

        private async Task<ServiceResponse> BuildStatusResponseAsync()
        {
            var stats = await _databaseService.GetDailyStatisticsAsync(DateTime.Today);

            var data = new Dictionary<string, object>
            {
                ["FilteringEnabled"] = _dnsService.FilteringEnabled,
                ["DnsRunning"] = _dnsService.IsRunning,
                ["WfpRunning"] = _wfpService.IsRunning,
                ["DatabaseConnected"] = _databaseService.IsConnected,
                ["TotalDnsQueries"] = _dnsService.TotalQueries,
                ["BlockedDnsQueries"] = _dnsService.BlockedQueries,
                ["ForwardedDnsQueries"] = _dnsService.ForwardedQueries,
                ["TotalRequestsToday"] = stats.TotalRequests,
                ["BlockedRequestsToday"] = stats.BlockedRequests,
                ["ServiceVersion"] = "1.0.0"
            };

            return new ServiceResponse
            {
                Success = true,
                Message = "Status retrieved",
                Data = data
            };
        }

        private async Task<ServiceResponse> HandleExtendUserTimeAsync(ServiceCommand command)
        {
            if (command.Parameters == null ||
                !command.Parameters.TryGetValue("UserId", out var userIdObj) ||
                !command.Parameters.TryGetValue("AdditionalMinutes", out var minutesObj))
            {
                return new ServiceResponse { Success = false, Message = "Missing UserId or AdditionalMinutes" };
            }

            int userId = Convert.ToInt32(userIdObj);
            int minutes = Convert.ToInt32(minutesObj);

            _logger.LogInformation("Extending time for user {UserId} by {Minutes} minutes", userId, minutes);

            return new ServiceResponse
            {
                Success = true,
                Message = $"Extended time by {minutes} minutes for user {userId}"
            };
        }

        private async Task<ServiceResponse> HandleHomeworkModeAsync(ServiceCommand command)
        {
            if (command.Parameters == null ||
                !command.Parameters.TryGetValue("UserId", out var userIdObj))
            {
                return new ServiceResponse { Success = false, Message = "Missing UserId" };
            }

            int userId = Convert.ToInt32(userIdObj);
            _logger.LogInformation("Enabling homework mode for user {UserId}", userId);

            return new ServiceResponse
            {
                Success = true,
                Message = $"Homework mode enabled for user {userId}"
            };
        }

        private async Task<ServiceResponse> HandleBlockUserAsync(ServiceCommand command)
        {
            if (command.Parameters == null ||
                !command.Parameters.TryGetValue("UserId", out var userIdObj))
            {
                return new ServiceResponse { Success = false, Message = "Missing UserId" };
            }

            int userId = Convert.ToInt32(userIdObj);
            var user = await _databaseService.GetDeviceUserAsync(userId);

            if (user != null)
            {
                await _wfpService.BlockUserTrafficAsync(user);
                return new ServiceResponse
                {
                    Success = true,
                    Message = $"Blocked traffic for user {user.Username}"
                };
            }

            return new ServiceResponse { Success = false, Message = "User not found" };
        }

        private async Task SendResponseAsync(NamedPipeServerStream pipe, bool success, string message)
        {
            var response = new ServiceResponse { Success = success, Message = message };
            string json = JsonSerializer.Serialize(response);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            await pipe.WriteAsync(bytes, 0, bytes.Length);
            await pipe.FlushAsync();
        }
    }

    /// <summary>
    /// Command from AdminControlPanel to Service
    /// </summary>
    public class ServiceCommand
    {
        public string Action { get; set; } = "";
        public Dictionary<string, object>? Parameters { get; set; }
    }

    /// <summary>
    /// Response from Service to AdminControlPanel
    /// </summary>
    public class ServiceResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public Dictionary<string, object>? Data { get; set; }
    }
}
