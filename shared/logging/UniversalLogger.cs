using System;
using System.IO;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Diagnostics;

namespace UrlFilter.Shared.Logging
{
    /// <summary>
    /// Universal logger for URL Filter System
    /// Supports file logging, console output, and Windows Event Log
    /// Thread-safe and high-performance with async operations
    /// </summary>
    public class UniversalLogger : IDisposable
    {
        private static readonly Lazy<UniversalLogger> _instance = new Lazy<UniversalLogger>(() => new UniversalLogger());
        public static UniversalLogger Instance => _instance.Value;

        private readonly string _logDirectory;
        private readonly string _currentLogFile;
        private readonly ConcurrentQueue<LogEntry> _logQueue;
        private readonly Task _logWriterTask;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _disposed = false;

        // Configuration
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
        public bool EnableConsoleOutput { get; set; } = true;
        public bool EnableFileLogging { get; set; } = true;
        public bool EnableEventLogOutput { get; set; } = true;
        public int MaxLogFileSizeMB { get; set; } = 100;
        public int MaxLogFileCount { get; set; } = 10;

        private UniversalLogger()
        {
            _logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UrlFilter",
                "Logs"
            );

            Directory.CreateDirectory(_logDirectory);

            _currentLogFile = Path.Combine(_logDirectory, $"urlfilter_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            _logQueue = new ConcurrentQueue<LogEntry>();
            _cancellationTokenSource = new CancellationTokenSource();

            // Start background log writer
            _logWriterTask = Task.Run(ProcessLogQueue, _cancellationTokenSource.Token);

            // Log startup
            LogInfo("UniversalLogger initialized", "Logger");
            LogInfo($"Log directory: {_logDirectory}", "Logger");
            LogInfo($"Current log file: {_currentLogFile}", "Logger");
        }

        #region Public Logging Methods

        public void LogTrace(string message, string category = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
        {
            Log(LogLevel.Trace, message, category, null, memberName, filePath, lineNumber);
        }

        public void LogDebug(string message, string category = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
        {
            Log(LogLevel.Debug, message, category, null, memberName, filePath, lineNumber);
        }

        public void LogInfo(string message, string category = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
        {
            Log(LogLevel.Information, message, category, null, memberName, filePath, lineNumber);
        }

        public void LogWarning(string message, string category = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
        {
            Log(LogLevel.Warning, message, category, null, memberName, filePath, lineNumber);
        }

        public void LogError(string message, string exception = null, string category = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
        {
            Log(LogLevel.Error, message, category, new Exception( exception), memberName, filePath, lineNumber);
        }

        public void LogCritical(string message, Exception exception = null, string category = null, [CallerMemberName] string memberName = "", [CallerFilePath] string filePath = "", [CallerLineNumber] int lineNumber = 0)
        {
            Log(LogLevel.Critical, message, category, exception, memberName, filePath, lineNumber);
        }

        public void LogAudit(string action, string details, string userId = null, string category = "AUDIT")
        {
            var auditMessage = $"AUDIT: {action} | User: {userId ?? "SYSTEM"} | Details: {details}";
            Log(LogLevel.Information, auditMessage, category, null, "LogAudit", "", 0);
        }

        public void LogSecurity(string event_, string details, string userId = null, string ipAddress = null)
        {
            var securityMessage = $"SECURITY: {event_} | User: {userId ?? "UNKNOWN"} | IP: {ipAddress ?? "UNKNOWN"} | Details: {details}";
            Log(LogLevel.Warning, securityMessage, "SECURITY", null, "LogSecurity", "", 0);
        }

        public void LogPerformance(string operation, TimeSpan duration, string details = null)
        {
            var perfMessage = $"PERFORMANCE: {operation} took {duration.TotalMilliseconds:F2}ms";
            if (!string.IsNullOrEmpty(details))
            {
                perfMessage += $" | {details}";
            }
            Log(LogLevel.Information, perfMessage, "PERFORMANCE", null, "LogPerformance", "", 0);
        }

        #endregion

        #region Core Logging Method

        private void Log(LogLevel level, string message, string category, Exception exception, string memberName, string filePath, int lineNumber)
        {
            if (level < MinimumLevel || _disposed)
                return;

            var fileName = Path.GetFileName(filePath);
            var logEntry = new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message,
                Category = category ?? "General",
                Exception = exception,
                MemberName = memberName,
                FileName = fileName,
                LineNumber = lineNumber,
                ThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId,
                ProcessId = Process.GetCurrentProcess().Id
            };

            _logQueue.Enqueue(logEntry);

            // For critical errors, also log immediately to console
            if (level >= LogLevel.Critical && EnableConsoleOutput)
            {
                Console.WriteLine($"[CRITICAL] {FormatLogEntry(logEntry)}");
            }
        }

        #endregion

        #region Background Log Processing

        private async Task ProcessLogQueue()
        {
            var buffer = new List<LogEntry>();
            
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Collect log entries
                    while (_logQueue.TryDequeue(out LogEntry entry) && buffer.Count < 100)
                    {
                        buffer.Add(entry);
                    }

                    if (buffer.Count > 0)
                    {
                        await WriteLogEntriesAsync(buffer);
                        buffer.Clear();
                    }

                    await Task.Delay(100, _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Emergency logging - write directly to console
                    Console.WriteLine($"[ERROR] Logger exception: {ex.Message}");
                }
            }

            // Process remaining entries
            while (_logQueue.TryDequeue(out LogEntry entry))
            {
                buffer.Add(entry);
            }

            if (buffer.Count > 0)
            {
                await WriteLogEntriesAsync(buffer);
            }
        }

        private async Task WriteLogEntriesAsync(List<LogEntry> entries)
        {
            var logLines = new StringBuilder();

            foreach (var entry in entries)
            {
                var formattedEntry = FormatLogEntry(entry);

                // Console output
                if (EnableConsoleOutput)
                {
                    WriteToConsole(entry.Level, formattedEntry);
                }

                // File output
                if (EnableFileLogging)
                {
                    logLines.AppendLine(formattedEntry);
                }

                // Windows Event Log
                if (EnableEventLogOutput && OperatingSystem.IsWindows() && entry.Level >= LogLevel.Warning)
                {
                    WriteToEventLog(entry);
                }
            }

            // Write to file
            if (EnableFileLogging && logLines.Length > 0)
            {
                await WriteToFileAsync(logLines.ToString());
            }
        }

        private string FormatLogEntry(LogEntry entry)
        {
            var sb = new StringBuilder();
            
            sb.Append($"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} ");
            sb.Append($"[{entry.Level.ToString().ToUpper()}] ");
            sb.Append($"[{entry.Category}] ");
            sb.Append($"[T{entry.ThreadId:D3}] ");
            
            if (!string.IsNullOrEmpty(entry.FileName))
            {
                sb.Append($"[{entry.FileName}:{entry.LineNumber}] ");
            }
            
            if (!string.IsNullOrEmpty(entry.MemberName))
            {
                sb.Append($"[{entry.MemberName}] ");
            }

            sb.Append(entry.Message);

            if (entry.Exception != null)
            {
                sb.AppendLine();
                sb.Append($"    Exception: {entry.Exception.GetType().Name}: {entry.Exception.Message}");
                if (!string.IsNullOrEmpty(entry.Exception.StackTrace))
                {
                    sb.AppendLine();
                    sb.Append($"    Stack Trace: {entry.Exception.StackTrace}");
                }
            }

            return sb.ToString();
        }

        #endregion

        #region Output Writers

        private void WriteToConsole(LogLevel level, string message)
        {
            var originalColor = Console.ForegroundColor;
            
            Console.ForegroundColor = level switch
            {
                LogLevel.Trace => ConsoleColor.Gray,
                LogLevel.Debug => ConsoleColor.White,
                LogLevel.Information => ConsoleColor.Green,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                LogLevel.Critical => ConsoleColor.Magenta,
                _ => ConsoleColor.White
            };

            Console.WriteLine(message);
            Console.ForegroundColor = originalColor;
        }

        private async Task WriteToFileAsync(string content)
        {
            try
            {
                // Check file size and rotate if needed
                if (File.Exists(_currentLogFile))
                {
                    var fileInfo = new FileInfo(_currentLogFile);
                    if (fileInfo.Length > MaxLogFileSizeMB * 1024 * 1024)
                    {
                        await RotateLogFileAsync();
                    }
                }

                await File.AppendAllTextAsync(_currentLogFile, content, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to write to log file: {ex.Message}");
            }
        }

        private void WriteToEventLog(LogEntry entry)
        {
            if (!OperatingSystem.IsWindows())
                return;

            try
            {
                var eventLogType = entry.Level switch
                {
                    LogLevel.Error or LogLevel.Critical => EventLogEntryType.Error,
                    LogLevel.Warning => EventLogEntryType.Warning,
                    _ => EventLogEntryType.Information
                };

                using var eventLog = new EventLog("Application")
                {
                    Source = "URL Filter Service"
                };

                var message = $"[{entry.Category}] {entry.Message}";
                if (entry.Exception != null)
                {
                    message += $"\n\nException: {entry.Exception}";
                }

                eventLog.WriteEntry(message, eventLogType, 1000 + (int)entry.Level);
            }
            catch
            {
                // Silently ignore event log errors
            }
        }

        #endregion

        #region File Management

        private async Task RotateLogFileAsync()
        {
            try
            {
                var directory = Path.GetDirectoryName(_currentLogFile);
                var baseFileName = "urlfilter";
                
                // Find existing log files and number them
                var existingFiles = Directory.GetFiles(directory, $"{baseFileName}_*.log")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .ToList();

                // Remove excess files
                for (int i = MaxLogFileCount - 1; i < existingFiles.Count; i++)
                {
                    try
                    {
                        File.Delete(existingFiles[i]);
                    }
                    catch
                    {
                        // Ignore delete errors
                    }
                }

                // Create new log file name
                var newLogFile = Path.Combine(directory, $"{baseFileName}_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                
                LogInfo($"Rotating log file to: {newLogFile}", "Logger");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Failed to rotate log file: {ex.Message}");
            }
        }

        public async Task<string[]> GetRecentLogFilesAsync(int count = 5)
        {
            try
            {
                var logFiles = Directory.GetFiles(_logDirectory, "urlfilter_*.log")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .Take(count)
                    .ToArray();

                return logFiles;
            }
            catch
            {
                return new string[0];
            }
        }

        public async Task<string> GetLogContentAsync(string logFilePath, int maxLines = 1000)
        {
            try
            {
                if (!File.Exists(logFilePath))
                    return "Log file not found";

                var lines = await File.ReadAllLinesAsync(logFilePath);
                var recentLines = lines.TakeLast(maxLines);
                
                return string.Join(Environment.NewLine, recentLines);
            }
            catch (Exception ex)
            {
                return $"Error reading log file: {ex.Message}";
            }
        }

        #endregion

        #region Utility Methods

        public void FlushLogs()
        {
            var timeout = TimeSpan.FromSeconds(5);
            var stopwatch = Stopwatch.StartNew();

            while (_logQueue.Count > 0 && stopwatch.Elapsed < timeout)
            {
                Task.Delay(100).Wait();
            }
        }

        public LogStatistics GetStatistics()
        {
            var logFiles = Directory.GetFiles(_logDirectory, "urlfilter_*.log");
            var totalSize = logFiles.Sum(f => new FileInfo(f).Length);

            return new LogStatistics
            {
                LogDirectory = _logDirectory,
                CurrentLogFile = _currentLogFile,
                LogFileCount = logFiles.Length,
                TotalLogSizeBytes = totalSize,
                QueuedEntries = _logQueue.Count,
                MinimumLevel = MinimumLevel,
                EnableConsoleOutput = EnableConsoleOutput,
                EnableFileLogging = EnableFileLogging,
                EnableEventLogOutput = EnableEventLogOutput
            };
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                LogInfo("UniversalLogger shutting down", "Logger");
                
                _disposed = true;
                _cancellationTokenSource?.Cancel();
                
                try
                {
                    _logWriterTask?.Wait(TimeSpan.FromSeconds(5));
                }
                catch
                {
                    // Ignore timeout
                }

                _cancellationTokenSource?.Dispose();
            }
        }

        #endregion
    }

    #region Supporting Types

    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Information = 2,
        Warning = 3,
        Error = 4,
        Critical = 5
    }

    internal class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Message { get; set; }
        public string Category { get; set; }
        public Exception Exception { get; set; }
        public string MemberName { get; set; }
        public string FileName { get; set; }
        public int LineNumber { get; set; }
        public int ThreadId { get; set; }
        public int ProcessId { get; set; }
    }

    public class LogStatistics
    {
        public string LogDirectory { get; set; }
        public string CurrentLogFile { get; set; }
        public int LogFileCount { get; set; }
        public long TotalLogSizeBytes { get; set; }
        public int QueuedEntries { get; set; }
        public LogLevel MinimumLevel { get; set; }
        public bool EnableConsoleOutput { get; set; }
        public bool EnableFileLogging { get; set; }
        public bool EnableEventLogOutput { get; set; }

        public string TotalLogSizeMB => $"{TotalLogSizeBytes / (1024.0 * 1024.0):F2} MB";
    }

    #endregion
}