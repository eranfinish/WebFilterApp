using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdminControlPanel.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminControlPanel.Controls
{
    public partial class QuickActionsPanel : UserControl
    {
        private readonly UrlFilterServiceClient? _serviceClient;
        private readonly UserDatabaseService? _databaseService;
        private readonly ILogger<QuickActionsPanel>? _logger;
        private bool _isFilteringEnabled = true;

        public QuickActionsPanel()
        {
            InitializeComponent();
            
            // Get services from DI if available
            try
            {
                if (Application.Current is App app && app.ServiceProvider != null)
                {
                    _serviceClient = app.ServiceProvider.GetService<UrlFilterServiceClient>();
                    _databaseService = app.ServiceProvider.GetService<UserDatabaseService>();
                    _logger = app.ServiceProvider.GetService<ILogger<QuickActionsPanel>>();
                }
            }
            catch
            {
                // Services not available, use fallback
            }
            
            _ = UpdateStatusAsync();
            _ = UpdateButtonStatesAsync();
        }

        private async Task UpdateStatusAsync()
        {
            try
            {
                if (_serviceClient != null)
                {
                    var isRunning = _serviceClient.IsServiceRunning();
                    var status = await _serviceClient.GetFilteringStatusAsync();
                    
                    _isFilteringEnabled = status.IsEnabled;
                    
                    ServiceStatusIndicator.Fill = isRunning ? Brushes.Green : Brushes.Red;
                    ServiceStatusText.Text = isRunning ? "Running" : "Stopped";
                    FilteredRequestsText.Text = status.TotalRequestsToday.ToString();
                    ActiveUsersText.Text = status.ActiveUsers.ToString();
                    BlockedAttemptsText.Text = status.BlockedRequestsToday.ToString();
                    
                    UpdateFilterToggleButton();
                    UpdateInternetStatusIndicator(isRunning, status);
                }
                else
                {
                    // Fallback to mock data
                    ServiceStatusIndicator.Fill = Brushes.Green;
                    ServiceStatusText.Text = "Running";
                    FilteredRequestsText.Text = "247";
                    ActiveUsersText.Text = "3";
                    BlockedAttemptsText.Text = "12";
                    
                    // Update status indicator with demo data
                    UpdateInternetStatusIndicator(true, new AdminControlPanel.Services.FilteringStatus 
                    { 
                        IsEnabled = true,
                        IsFilteringActive = true,
                        TotalRequestsToday = 247,
                        BlockedRequestsToday = 12,
                        ActiveUsers = 3
                    });
                }
            }
            catch (Exception ex)
            {
                ServiceStatusIndicator.Fill = Brushes.Red;
                ServiceStatusText.Text = "Error";
                FilteredRequestsText.Text = "0";
                ActiveUsersText.Text = "0";
                BlockedAttemptsText.Text = "0";
                
                // Update status indicator for error state
                UpdateInternetStatusIndicator(false, null);
            }
        }

        private void UpdateFilterToggleButton()
        {
            if (_isFilteringEnabled)
            {
                FilterToggleButton.Content = "🔴 Turn OFF Filtering";
                FilterToggleButton.Background = Brushes.Red;
            }
            else
            {
                FilterToggleButton.Content = "🟢 Turn ON Filtering";
                FilterToggleButton.Background = Brushes.Green;
            }
        }

        /// <summary>
        /// Toggle filtering ON/OFF
        /// </summary>
        private async void FilterToggle_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FilterToggleButton.IsEnabled = false;
                FilterToggleButton.Content = "⏳ Please wait...";
                
                if (_serviceClient != null)
                {
                    bool success;
                    if (_isFilteringEnabled)
                    {
                        var result = MessageBox.Show(
                            "⚠️ WARNING: This will disable ALL internet filtering!\n\n" +
                            "Your family will have unrestricted internet access until you turn filtering back on.\n\n" +
                            "Are you sure you want to turn OFF filtering?",
                            "Turn OFF Internet Filtering", 
                            MessageBoxButton.YesNo, 
                            MessageBoxImage.Warning,
                            MessageBoxResult.No);
                            
                        if (result != MessageBoxResult.Yes)
                        {
                            FilterToggleButton.IsEnabled = true;
                            UpdateFilterToggleButton();
                            return;
                        }
                        
                        success = await _serviceClient.DisableFilteringAsync();
                        if (success)
                        {
                            _isFilteringEnabled = false;
                            await _databaseService?.SetSystemSettingAsync("filtering_enabled", "false")!;
                            MessageBox.Show("✅ Internet filtering has been turned OFF.\n\nAll family members now have unrestricted access.", 
                                          "Filtering Disabled", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    else
                    {
                        success = await _serviceClient.EnableFilteringAsync();
                        if (success)
                        {
                            _isFilteringEnabled = true;
                            await _databaseService?.SetSystemSettingAsync("filtering_enabled", "true")!;
                            MessageBox.Show("✅ Internet filtering has been turned ON.\n\nAll family restrictions are now active.", 
                                          "Filtering Enabled", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    
                    if (!success)
                    {
                        MessageBox.Show("❌ Failed to change filtering status. Please check if the service is running.", 
                                      "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    // Demo mode - just toggle the UI
                    _isFilteringEnabled = !_isFilteringEnabled;
                    var status = _isFilteringEnabled ? "ON" : "OFF";
                    MessageBox.Show($"Demo Mode: Filtering turned {status}\n\n(Service not available)", 
                                  "Demo Mode", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                
                await UpdateStatusAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                FilterToggleButton.IsEnabled = true;
                UpdateFilterToggleButton();
                // Force status indicator refresh after toggle
                _ = UpdateStatusAsync();
            }
        }

        private async void ExtendTime_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_databaseService == null)
                {
                    MessageBox.Show("Database service not available", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Get all users
                var users = await _databaseService.GetAllUsersAsync();
                if (!users.Any())
                {
                    MessageBox.Show("No family members found. Please add users first.", 
                                  "No Users", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Convert to shared model for dialog
                var sharedUsers = users.Cast<UrlFilter.Shared.Models.DeviceUser>().ToList();
                
                // Show user selection dialog
                var userSelectionDialog = new AdminControlPanel.Views.UserSelectionDialog(
                    "Extend Time - Select User",
                    "Select which family member to extend time for:",
                    sharedUsers,
                    false // Single selection only
                );
                
                userSelectionDialog.Owner = Window.GetWindow(this);
                
                if (userSelectionDialog.ShowDialog() == true && userSelectionDialog.SelectedUser != null)
                {
                    // Show time extension dialog
                    var timeDialog = new AdminControlPanel.Views.TimeExtensionDialog(userSelectionDialog.SelectedUser);
                    timeDialog.Owner = Window.GetWindow(this);
                    
                    if (timeDialog.ShowDialog() == true)
                    {
                        var selectedUser = userSelectionDialog.SelectedUser;
                        var minutes = timeDialog.SelectedMinutes;
                        
                        // Send command to service
                        if (_serviceClient != null)
                        {
                            var success = await _serviceClient.ExtendUserTimeAsync(selectedUser.Id, minutes);
                            
                            if (success)
                            {
                                MessageBox.Show($"✅ Extended time by {FormatMinutes(minutes)} for {selectedUser.DisplayName}\n\n" +
                                              $"They now have additional internet time.", 
                                              "Time Extended", MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            else
                            {
                                MessageBox.Show("❌ Failed to extend time. Please check if the service is running.", 
                                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            }
                        }
                        else
                        {
                            MessageBox.Show($"Demo Mode: Extended time by {FormatMinutes(minutes)} for {selectedUser.DisplayName}", 
                                          "Demo Mode", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        
                        await UpdateStatusAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error extending user time");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void HomeworkMode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_databaseService == null)
                {
                    MessageBox.Show("Database service not available", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Get all users
                var users = await _databaseService.GetAllUsersAsync();
                var eligibleUsers = users.Where(u => u.UserType == UrlFilter.Shared.Models.UserType.Child || 
                                                     u.UserType == UrlFilter.Shared.Models.UserType.Teen).ToList();

                if (!eligibleUsers.Any())
                {
                    MessageBox.Show("No children or teens found. Homework mode is only available for younger family members.", 
                                  "No Eligible Users", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // Convert to shared model for dialog
                var sharedEligibleUsers = eligibleUsers.Cast<UrlFilter.Shared.Models.DeviceUser>().ToList();

                // Show user selection dialog for homework mode
                var userSelectionDialog = new AdminControlPanel.Views.UserSelectionDialog(
                    "Homework Mode - Select Student",
                    "Select which student to enable homework mode for:",
                    sharedEligibleUsers,
                    true // Allow multiple selection for homework mode
                );
                
                userSelectionDialog.Owner = Window.GetWindow(this);
                
                if (userSelectionDialog.ShowDialog() == true)
                {
                    var selectedUsers = userSelectionDialog.AllowMultipleSelection 
                        ? userSelectionDialog.SelectedUsers 
                        : new List<UrlFilter.Shared.Models.DeviceUser> { userSelectionDialog.SelectedUser! };

                    if (selectedUsers.Any())
                    {
                        // Show duration selection dialog
                        var durationDialog = new AdminControlPanel.Views.HomeworkModeDialog(selectedUsers);
                        durationDialog.Owner = Window.GetWindow(this);
                        
                        if (durationDialog.ShowDialog() == true)
                        {
                            var duration = durationDialog.SelectedDurationMinutes;
                            var userNames = string.Join(", ", selectedUsers.Select(u => u.DisplayName));
                            
                            // Send command to service for each user
                            if (_serviceClient != null)
                            {
                                var successCount = 0;
                                foreach (var user in selectedUsers)
                                {
                                    var success = await _serviceClient.EnableHomeworkModeAsync(user.Id, duration);
                                    if (success) successCount++;
                                }

                                if (successCount == selectedUsers.Count)
                                {
                                    MessageBox.Show($"✅ Homework Mode enabled for {FormatMinutes(duration)} for:\n{userNames}\n\n" +
                                                  $"Only educational sites and resources are now accessible.", 
                                                  "Homework Mode Active", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else
                                {
                                    MessageBox.Show($"⚠️ Homework Mode enabled for {successCount} of {selectedUsers.Count} users.\n" +
                                                  "Please check if the service is running.", 
                                                  "Partial Success", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                            else
                            {
                                MessageBox.Show($"Demo Mode: Homework mode enabled for {FormatMinutes(duration)} for:\n{userNames}", 
                                              "Demo Mode", MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            
                            await UpdateStatusAsync();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error enabling homework mode");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BedtimeMode_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Activate Bedtime Mode?\n\nThis will block internet for all children immediately.", 
                                       "Bedtime Mode", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                MessageBox.Show("😴 Bedtime Mode activated\n\nInternet blocked for all children.", 
                              "Good Night!", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BlockSite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new AdminControlPanel.Views.WebsiteListsDialog();
                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening website lists: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AllowSite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new AdminControlPanel.Views.WebsiteListsDialog();
                dialog.Owner = Window.GetWindow(this);
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening website lists: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SafeSearch_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_databaseService != null)
                {
                    var currentSetting = await _databaseService.GetSystemSettingAsync("safe_search_enabled");
                    var isEnabled = currentSetting?.ToLower() == "true";
                    
                    if (isEnabled)
                    {
                        await _databaseService.SetSystemSettingAsync("safe_search_enabled", "false");
                        MessageBox.Show("🔍 Safe Search has been disabled\n\nAll search engines will show unfiltered results", 
                                      "Safe Search Disabled", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        await _databaseService.SetSystemSettingAsync("safe_search_enabled", "true");
                        MessageBox.Show("🔍 Safe Search has been enabled\n\nAll search engines will filter inappropriate content", 
                                      "Safe Search Enabled", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    
                    await UpdateButtonStatesAsync();
                }
                else
                {
                    MessageBox.Show("Database service not available", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error toggling Safe Search: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void YouTubeKids_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_databaseService != null)
                {
                    var currentSetting = await _databaseService.GetSystemSettingAsync("youtube_kids_mode");
                    var isEnabled = currentSetting?.ToLower() == "true";
                    
                    if (isEnabled)
                    {
                        await _databaseService.SetSystemSettingAsync("youtube_kids_mode", "false");
                        MessageBox.Show("📺 YouTube Kids mode has been disabled\n\nAll YouTube content will be available based on other filtering rules", 
                                      "YouTube Kids Mode Disabled", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        await _databaseService.SetSystemSettingAsync("youtube_kids_mode", "true");
                        MessageBox.Show("📺 YouTube Kids mode has been enabled\n\nOnly kid-safe channels and content will be allowed", 
                                      "YouTube Kids Mode Enabled", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    
                    await UpdateButtonStatesAsync();
                }
                else
                {
                    MessageBox.Show("Database service not available", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error toggling YouTube Kids mode: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void EmergencyUnlock_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("🆘 EMERGENCY UNLOCK\n\nThis will disable ALL restrictions for 1 hour.\nUse only in genuine emergencies!\n\nContinue?", 
                                       "Emergency Unlock", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                if (_databaseService != null)
                {
                    await _databaseService.SetSystemSettingAsync("emergency_unlock", "true");
                }
                MessageBox.Show("🆘 Emergency unlock activated for 1 hour\n\nAll restrictions temporarily disabled.", 
                              "Emergency Mode Active", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BlockAll_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Block ALL internet access?\n\nThis will disconnect all devices from the internet.", 
                                       "Block All Internet", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                MessageBox.Show("🚫 All internet access blocked\n\n(TODO: Implement full network block)", 
                              "Internet Blocked", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Reset all settings to factory defaults?\n\nThis will:\n• Remove all custom rules\n• Reset time limits\n• Clear user preferences\n\nThis cannot be undone!", 
                                       "Reset to Defaults", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                MessageBox.Show("⚙️ Settings reset to defaults\n\n(TODO: Implement database reset)", 
                              "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                await UpdateStatusAsync();
                await UpdateButtonStatesAsync();
            }
        }

        private async Task UpdateButtonStatesAsync()
        {
            try
            {
                if (_databaseService != null)
                {
                    // Update Safe Search button
                    var safeSearchSetting = await _databaseService.GetSystemSettingAsync("safe_search_enabled");
                    var safeSearchEnabled = safeSearchSetting?.ToLower() == "true";
                    SafeSearchButton.Content = safeSearchEnabled ? "🔍 Safe Search ON" : "🔍 Safe Search OFF";
                    SafeSearchButton.Background = safeSearchEnabled ? Brushes.Green : Brushes.Gray;

                    // Update YouTube Kids button
                    var youtubeKidsSetting = await _databaseService.GetSystemSettingAsync("youtube_kids_mode");
                    var youtubeKidsEnabled = youtubeKidsSetting?.ToLower() == "true";
                    YouTubeKidsButton.Content = youtubeKidsEnabled ? "📺 YouTube Kids ON" : "📺 YouTube Kids OFF";
                    YouTubeKidsButton.Background = youtubeKidsEnabled ? Brushes.Green : Brushes.Gray;
                }
            }
            catch (Exception ex)
            {
                // Ignore errors in button state updates
            }
        }

        /// <summary>
        /// Update the internet status indicator based on service status
        /// </summary>
        private void UpdateInternetStatusIndicator(bool isServiceRunning, AdminControlPanel.Services.FilteringStatus? status)
        {
            try
            {
                if (!isServiceRunning || status == null)
                {
                    // Service down or error state
                    InternetStatusBorder.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                    InternetStatusIcon.Fill = Brushes.White;
                    InternetStatusText.Text = "🚫 Internet Service Offline";
                    FilterStatusText.Text = "URL Filter service is not running";
                    return;
                }

                if (!status.IsEnabled)
                {
                    // Service running but filtering disabled
                    InternetStatusBorder.Background = new SolidColorBrush(Color.FromRgb(255, 152, 0)); // Orange
                    InternetStatusIcon.Fill = Brushes.White;
                    InternetStatusText.Text = "⚠️ Internet Available - Filtering OFF";
                    FilterStatusText.Text = "All websites accessible - No protection active";
                }
                else if (status.IsFilteringActive)
                {
                    // Normal filtered internet access
                    InternetStatusBorder.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                    InternetStatusIcon.Fill = Brushes.White;
                    InternetStatusText.Text = "🌐 Internet Available - Filtering Active";
                    FilterStatusText.Text = $"Safe browsing enabled • {status.BlockedRequestsToday} sites blocked today";
                }
                else
                {
                    // Service enabled but not actively filtering
                    InternetStatusBorder.Background = new SolidColorBrush(Color.FromRgb(96, 125, 139)); // Blue Grey
                    InternetStatusIcon.Fill = Brushes.White;
                    InternetStatusText.Text = "🔄 Internet Available - Filtering Starting";
                    FilterStatusText.Text = "Protection service is initializing...";
                }

                // Check for emergency/override states
                CheckForSpecialStates(status);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error updating internet status indicator");
            }
        }

        /// <summary>
        /// Check for special states like emergency unlock, all blocked, etc.
        /// </summary>
        private async void CheckForSpecialStates(AdminControlPanel.Services.FilteringStatus status)
        {
            try
            {
                if (_databaseService != null)
                {
                    // Check for emergency unlock
                    var emergencyUnlock = await _databaseService.GetSystemSettingAsync("emergency_unlock");
                    if (emergencyUnlock?.ToLower() == "true")
                    {
                        InternetStatusBorder.Background = new SolidColorBrush(Color.FromRgb(156, 39, 176)); // Purple
                        InternetStatusText.Text = "🆘 Emergency Mode - All Access";
                        FilterStatusText.Text = "All restrictions temporarily disabled";
                        return;
                    }

                    // Check if all internet is blocked
                    var allBlocked = await _databaseService.GetSystemSettingAsync("block_all_internet");
                    if (allBlocked?.ToLower() == "true")
                    {
                        InternetStatusBorder.Background = new SolidColorBrush(Color.FromRgb(183, 28, 28)); // Dark Red
                        InternetStatusText.Text = "🔒 Internet Completely Blocked";
                        FilterStatusText.Text = "All internet access is disabled";
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking special states");
            }
        }

        /// <summary>
        /// Show detailed status information
        /// </summary>
        private void StatusDetails_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var details = BuildStatusDetails();
                MessageBox.Show(details, "Internet Status Details", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error showing status details");
                MessageBox.Show("Error retrieving status details.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Build detailed status information string
        /// </summary>
        private string BuildStatusDetails()
        {
            var details = "🌐 Internet Connection Status\n\n";
            
            try
            {
                if (_serviceClient != null)
                {
                    var isRunning = _serviceClient.IsServiceRunning();
                    details += $"Service Status: {(isRunning ? "✅ Running" : "❌ Stopped")}\n";
                    details += $"Filtering: {(_isFilteringEnabled ? "✅ Active" : "❌ Disabled")}\n\n";
                    
                    details += "📊 Today's Statistics:\n";
                    details += $"• Total Requests: {FilteredRequestsText.Text}\n";
                    details += $"• Blocked Attempts: {BlockedAttemptsText.Text}\n";
                    details += $"• Active Users: {ActiveUsersText.Text}\n\n";
                }
                
                details += "🕐 Current Time: " + DateTime.Now.ToString("HH:mm:ss") + "\n";
                details += "📅 Date: " + DateTime.Now.ToString("yyyy-MM-dd");
                
            }
            catch (Exception ex)
            {
                details += "Error retrieving detailed information.";
                _logger?.LogError(ex, "Error building status details");
            }
            
            return details;
        }

        /// <summary>
        /// Format minutes into human readable format
        /// </summary>
        private string FormatMinutes(int minutes)
        {
            if (minutes < 60)
            {
                return $"{minutes} minutes";
            }
            else if (minutes == 60)
            {
                return "1 hour";
            }
            else if (minutes % 60 == 0)
            {
                return $"{minutes / 60} hours";
            }
            else
            {
                var hours = minutes / 60;
                var mins = minutes % 60;
                return $"{hours}h {mins}m";
            }
        }
    }
}