using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using UrlFilter.Shared.Logging;

namespace AdminControlPanel.Views
{
    public partial class LogViewerDialog : Window
    {
        private readonly UniversalLogger _universalLogger;
        private readonly DispatcherTimer _refreshTimer;
        private string _currentFilter = "";
        private string _currentLogLevel = "All Levels";
        private List<LogEntry> _allLogs = new();
        
        public LogViewerDialog()
        {
            InitializeComponent();
            _universalLogger = UniversalLogger.Instance;
            
            // Setup auto-refresh timer
            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            
            Loaded += LogViewerDialog_Loaded;
        }
        
        private async void LogViewerDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadLogsAsync();
            if (AutoRefreshCheckBox.IsChecked == true)
            {
                _refreshTimer.Start();
            }
        }
        
        private async System.Threading.Tasks.Task LoadLogsAsync()
        {
            try
            {
                StatusText.Text = "Loading logs...";
                
                // Get recent logs from UniversalLogger
                var logs = new List<LogEntry>();
                
                // Try to read from log files
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UrlFilter", "Logs");
                
                if (Directory.Exists(logDir))
                {
                    var logFiles = Directory.GetFiles(logDir, "*.log").OrderByDescending(f => File.GetLastWriteTime(f)).Take(3);
                    
                    foreach (var logFile in logFiles)
                    {
                        try
                        {
                            var lines = await File.ReadAllLinesAsync(logFile);
                            foreach (var line in lines.TakeLast(500)) // Last 500 lines per file
                            {
                                if (TryParseLogLine(line, out var logEntry))
                                {
                                    logs.Add(logEntry);
                                }
                            }
                        }
                        catch
                        {
                            // Skip problematic files
                        }
                    }
                }
                
                // Add some demo entries if no logs found
                if (!logs.Any())
                {
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.Now.AddMinutes(-5),
                        Level = "Info",
                        Category = "System",
                        Message = "No log files found. This dialog will show system logs when operations occur.",
                        Method = "LoadLogsAsync"
                    });
                    
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.Now.AddMinutes(-3),
                        Level = "Warning",
                        Category = "WebsiteListsDialog",
                        Message = "Database service not available - running in demo mode",
                        Method = "LoadAllListsAsync"
                    });
                    
                    logs.Add(new LogEntry
                    {
                        Timestamp = DateTime.Now.AddMinutes(-1),
                        Level = "Info",
                        Category = "LogViewerDialog", 
                        Message = "Log viewer opened by user",
                        Method = "LoadLogsAsync"
                    });
                }
                
                _allLogs = logs.OrderByDescending(l => l.Timestamp).ToList();
                ApplyFiltersAndDisplay();
                
                StatusText.Text = $"Loaded {_allLogs.Count} log entries";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading logs: {ex.Message}";
                LogTextBlock.Text = $"Error loading logs:\n{ex.Message}\n\nThis could be due to:\n• Log files not found\n• Permission issues\n• Log directory not created\n\nTry running some operations to generate logs.";
            }
        }
        
        private bool TryParseLogLine(string line, out LogEntry logEntry)
        {
            logEntry = null;
            
            try
            {
                // Simple log parsing - adjust based on your log format
                if (line.Length > 30 && line.Contains(" | "))
                {
                    var parts = line.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        logEntry = new LogEntry
                        {
                            Timestamp = DateTime.TryParse(parts[0], out var ts) ? ts : DateTime.Now,
                            Level = parts[1],
                            Category = parts[2],
                            Message = parts.Length > 3 ? string.Join(" | ", parts.Skip(3)) : parts[3],
                            Method = "Unknown"
                        };
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }
            
            return false;
        }
        
        private void ApplyFiltersAndDisplay()
        {
            var filteredLogs = _allLogs.AsEnumerable();
            
            // Apply level filter
            if (_currentLogLevel != "All Levels")
            {
                filteredLogs = filteredLogs.Where(l => l.Level.Equals(_currentLogLevel, StringComparison.OrdinalIgnoreCase));
            }
            
            // Apply text filter
            if (!string.IsNullOrWhiteSpace(_currentFilter) && _currentFilter != "Filter logs...")
            {
                filteredLogs = filteredLogs.Where(l => 
                    l.Message.Contains(_currentFilter, StringComparison.OrdinalIgnoreCase) ||
                    l.Category.Contains(_currentFilter, StringComparison.OrdinalIgnoreCase));
            }
            
            var displayLogs = filteredLogs.ToList();
            
            // Format for display
            var logText = new StringBuilder();
            foreach (var log in displayLogs)
            {
                var levelIcon = log.Level.ToUpper() switch
                {
                    "ERROR" => "❌",
                    "WARNING" => "⚠️",
                    "INFO" => "ℹ️",
                    "DEBUG" => "🔍",
                    _ => "📋"
                };
                
                logText.AppendLine($"{log.Timestamp:yyyy-MM-dd HH:mm:ss} | {levelIcon} {log.Level} | {log.Category} | {log.Message}");
                if (!string.IsNullOrWhiteSpace(log.Method))
                {
                    logText.AppendLine($"    Method: {log.Method}");
                }
                logText.AppendLine();
            }
            
            LogTextBlock.Text = logText.Length > 0 ? logText.ToString() : "No logs match the current filters.";
            LogCountText.Text = $"({displayLogs.Count} entries)";
            
            // Auto-scroll to bottom for new entries
            LogScrollViewer.ScrollToBottom();
        }
        
        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadLogsAsync();
        }
        
        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Clear all displayed logs?\n\nThis will not delete log files, only clear the current display.", 
                                       "Clear Logs", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _allLogs.Clear();
                LogTextBlock.Text = "Logs cleared. Click Refresh to reload from files.";
                LogCountText.Text = "(0 entries)";
                StatusText.Text = "Logs cleared";
            }
        }
        
        private void LogLevelFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LogLevelFilter.SelectedItem is ComboBoxItem selected)
            {
                _currentLogLevel = selected.Content.ToString();
                ApplyFiltersAndDisplay();
            }
        }
        
        private void FilterTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (FilterTextBox.Text == "Filter logs...")
            {
                FilterTextBox.Text = "";
                FilterTextBox.Foreground = Brushes.Black;
            }
        }
        
        private void FilterTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(FilterTextBox.Text))
            {
                FilterTextBox.Text = "Filter logs...";
                FilterTextBox.Foreground = Brushes.Gray;
            }
        }
        
        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentFilter = FilterTextBox.Text;
            if (_currentFilter != "Filter logs...")
            {
                ApplyFiltersAndDisplay();
            }
        }
        
        private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
        {
            if (AutoRefreshCheckBox.IsChecked == true)
            {
                _refreshTimer.Start();
                StatusText.Text = "Auto-refresh enabled";
            }
            else
            {
                _refreshTimer.Stop();
                StatusText.Text = "Auto-refresh disabled";
            }
        }
        
        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            await LoadLogsAsync();
        }
        
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _refreshTimer.Stop();
            DialogResult = true;
            Close();
        }
        
        protected override void OnClosed(EventArgs e)
        {
            _refreshTimer.Stop();
            base.OnClosed(e);
        }
        
        private class LogEntry
        {
            public DateTime Timestamp { get; set; }
            public string Level { get; set; } = "";
            public string Category { get; set; } = "";
            public string Message { get; set; } = "";
            public string Method { get; set; } = "";
        }
    }
}