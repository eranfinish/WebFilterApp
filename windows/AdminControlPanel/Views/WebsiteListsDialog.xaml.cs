using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using AdminControlPanel.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AdminControlPanel.Views
{
    public partial class WebsiteListsDialog : Window
    {
        private readonly UserDatabaseService? _databaseService;
        private readonly ILogger<WebsiteListsDialog>? _logger;
        private List<UrlListItem> _blacklist = new();
        private List<UrlListItem> _whitelist = new();

        public WebsiteListsDialog()
        {
            InitializeComponent();
            // Get services from DI
            try
            {
                if (Application.Current is App app && app.ServiceProvider != null)
                {
                    _databaseService = app.ServiceProvider.GetService<UserDatabaseService>();
                    _logger = app.ServiceProvider.GetService<ILogger<WebsiteListsDialog>>();
                }
                
                _logger?.LogInformation("WebsiteListsDialog initialized with database service: {HasService}", _databaseService != null);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize services in WebsiteListsDialog");
            }
            
            QuickAddTextBox.Text = "Enter website to block or allow (e.g., facebook.com)";
            QuickAddTextBox.GotFocus += QuickAddTextBox_GotFocus;
            QuickAddTextBox.LostFocus += QuickAddTextBox_LostFocus;
            QuickAddTextBox.Foreground = System.Windows.Media.Brushes.Gray;
            
            Loaded += WebsiteListsDialog_Loaded;
        }
        
        private async void WebsiteListsDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadAllListsAsync();
        }

        private async System.Threading.Tasks.Task LoadAllListsAsync()
        {
            _logger?.LogInformation("Starting LoadAllListsAsync");
            
            try
            {
                StatusText.Text = "Loading website lists...";
                
                if (_databaseService is not null)
                {
                    _logger?.LogInformation("Database service available, loading lists from database");
                    
                    _blacklist = await _databaseService.GetUrlListAsync("Blacklist");
                    _whitelist = await _databaseService.GetUrlListAsync("Whitelist");
                    
                    _logger?.LogInformation("Loaded {BlacklistCount} blacklist and {WhitelistCount} whitelist items", _blacklist.Count, _whitelist.Count);
                    
                    BlockedSitesList.ItemsSource = _blacklist;
                    AllowedSitesList.ItemsSource = _whitelist;
                    
                    CountsText.Text = $"Blocked: {_blacklist.Count} | Allowed: {_whitelist.Count}";
                    StatusText.Text = "Lists loaded successfully";
                }
                else
                {
                    _logger?.LogWarning("Database service not available, using demo data");
                    
                    StatusText.Text = "Database service not available - using demo data";
                    
                    // Create demo data
                    _blacklist = new List<UrlListItem>
                    {
                        new UrlListItem { UrlPattern = "facebook.com", Category = "Social Media", ListType = "Blacklist" },
                        new UrlListItem { UrlPattern = "instagram.com", Category = "Social Media", ListType = "Blacklist" },
                        new UrlListItem { UrlPattern = "tiktok.com", Category = "Social Media", ListType = "Blacklist" },
                        new UrlListItem { UrlPattern = "*.gambling.com", Category = "Gambling", ListType = "Blacklist" },
                        new UrlListItem { UrlPattern = "*.adult.*", Category = "Adult Content", ListType = "Blacklist" }
                    };
                    
                    _whitelist = new List<UrlListItem>
                    {
                        new UrlListItem { UrlPattern = "khanacademy.org", Category = "Education", ListType = "Whitelist" },
                        new UrlListItem { UrlPattern = "wikipedia.org", Category = "Education", ListType = "Whitelist" },
                        new UrlListItem { UrlPattern = "nationalgeographic.com", Category = "Education", ListType = "Whitelist" },
                        new UrlListItem { UrlPattern = "*.edu", Category = "Education", ListType = "Whitelist" }
                    };
                    
                    BlockedSitesList.ItemsSource = _blacklist;
                    AllowedSitesList.ItemsSource = _whitelist;
                    CountsText.Text = $"Blocked: {_blacklist.Count} | Allowed: {_whitelist.Count} (Demo)";
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error loading lists: {ex.Message}";
                _logger?.LogError(ex, "Exception in LoadAllListsAsync");
                
                StatusText.Text = errorMessage;
                CountsText.Text = "Error";
            }
        }

        private async void QuickBlock_Click(object sender, RoutedEventArgs e)
        {
            await QuickAddSiteAsync("Blacklist", "🚫 BLOCKED");
        }

        private async void QuickAllow_Click(object sender, RoutedEventArgs e)
        {
            await QuickAddSiteAsync("Whitelist", "✅ ALLOWED");
        }

        private async System.Threading.Tasks.Task QuickAddSiteAsync(string listType, string actionText)
        {
            var originalUrl = QuickAddTextBox.Text.Trim();
            _logger?.LogInformation("Starting QuickAddSiteAsync for {ListType} with URL: {Url}", listType, originalUrl);
            
            try
            {
                if (string.IsNullOrWhiteSpace(originalUrl) || originalUrl == "Enter website to block or allow (e.g., facebook.com)")
                {
                    var message = "Please enter a website URL";
                    _logger?.LogWarning("QuickAddSiteAsync validation failed: empty or placeholder URL");
                    StatusText.Text = message;
                    return;
                }

                // Clean up URL
                var cleanedUrl = CleanUrl(originalUrl);
                _logger?.LogInformation("URL cleaned from {Original} to {Cleaned}", originalUrl, cleanedUrl);

                if (_databaseService is not null)
                {
                    _logger?.LogInformation("Database service available, checking for existing URL");
                    
                    // Check if already exists
                    var existing = await _databaseService.FindUrlInListsAsync(cleanedUrl);
                    if (existing != null)
                    {
                        var message = $"Website {cleanedUrl} already exists in {existing.ListType}";
                        _logger?.LogWarning("URL already exists: {Url} in {ExistingListType}", cleanedUrl, existing.ListType);
                        StatusText.Text = message;
                        return;
                    }
                    
                    _logger?.LogInformation("URL not found in existing lists, attempting to add to {ListType}", listType);

                    // Add to database
                    var success = await _databaseService.AddUrlToListAsync(cleanedUrl, listType, "Custom", $"Added via quick action on {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    
                    if (success)
                    {
                        var message = $"{actionText}: {cleanedUrl}";
                        _logger?.LogInformation("Successfully added {Url} to {ListType}", cleanedUrl, listType);
                        
                        StatusText.Text = message;
                        QuickAddTextBox.Text = "Enter website to block or allow (e.g., facebook.com)";
                        QuickAddTextBox.Foreground = System.Windows.Media.Brushes.Gray;
                        
                        // Refresh the lists
                        await LoadAllListsAsync();
                        
                        // Show success message
                        MessageBox.Show($"✅ Successfully added {cleanedUrl} to {listType}!\n\nThe site will be {(listType == "Blacklist" ? "blocked" : "allowed")} once the filtering service applies the changes.", 
                                      "Site Added", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        var message = $"Failed to add {cleanedUrl}";
                        _logger?.LogError("Failed to add {Url} to {ListType} - database operation returned false", cleanedUrl, listType);
                        StatusText.Text = message;
                        
                        MessageBox.Show($"❌ Failed to add {cleanedUrl} to {listType}.\n\nThis could be due to:\n• Database connection issues\n• Invalid URL format\n• Permissions problems\n\nCheck the logs for more details.", 
                                      "Add Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                else
                {
                    var message = $"Demo mode: Would {actionText.ToLower()} {cleanedUrl}";
                    _logger?.LogWarning("Database service not available, running in demo mode for {Url}", cleanedUrl);
                    StatusText.Text = message;
                    
                    MessageBox.Show($"🔧 Demo Mode Active\n\nDatabase service is not available.\nWould {actionText.ToLower()}: {cleanedUrl}\n\nTo enable actual blocking/allowing, ensure the database service is running.", 
                                  "Demo Mode", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                var errorMessage = $"Error: {ex.Message}";
                _logger?.LogError(ex, "Exception in QuickAddSiteAsync for {ListType} with URL {Url}", listType, originalUrl);
                
                StatusText.Text = errorMessage;
                
                MessageBox.Show($"💥 Error occurred while adding site:\n\n{ex.Message}\n\nOperation: Add {originalUrl} to {listType}\nTime: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\nThis error has been logged for troubleshooting.", 
                              "Operation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void RemoveBlocked_Click(object sender, RoutedEventArgs e)
        {
            await RemoveSiteAsync(sender, "blacklist");
        }

        private async void RemoveAllowed_Click(object sender, RoutedEventArgs e)
        {
            await RemoveSiteAsync(sender, "whitelist");
        }

        private async System.Threading.Tasks.Task RemoveSiteAsync(object sender, string listTypeName)
        {
            try
            {
                if (sender is Button button && button.Tag is UrlListItem urlItem)
                {
                    var result = MessageBox.Show(
                        $"Remove {urlItem.UrlPattern} from {listTypeName}?",
                        "Confirm Removal",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes)
                    {
                        if (_databaseService is not null)
                        {
                            var success = await _databaseService.RemoveUrlFromListAsync(urlItem.UrlPattern, urlItem.ListType);
                            
                            if (success)
                            {
                                StatusText.Text = $"Removed {urlItem.UrlPattern} from {listTypeName}";
                                await LoadAllListsAsync();
                            }
                            else
                            {
                                StatusText.Text = $"Failed to remove {urlItem.UrlPattern}";
                            }
                        }
                        else
                        {
                            StatusText.Text = $"Demo mode: Would remove {urlItem.UrlPattern}";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error: {ex.Message}";
            }
        }

        private async void RefreshLists_Click(object sender, RoutedEventArgs e)
        {
            await LoadAllListsAsync();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private string CleanUrl(string url)
        {
            // Remove common prefixes
            url = url.Replace("https://", "").Replace("http://", "").Replace("www.", "");
            
            // Remove trailing slash
            if (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);
            
            // Take only the domain part if there's a path
            if (url.Contains("/"))
                url = url.Split('/')[0];
            
            return url.ToLowerInvariant();
        }

        private void QuickAddTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (QuickAddTextBox.Text == "Enter website to block or allow (e.g., facebook.com)")
            {
                QuickAddTextBox.Text = "";
                QuickAddTextBox.Foreground = System.Windows.Media.Brushes.Black;
            }
        }

        private void QuickAddTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(QuickAddTextBox.Text))
            {
                QuickAddTextBox.Text = "Enter website to block or allow (e.g., facebook.com)";
                QuickAddTextBox.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }
    }
}