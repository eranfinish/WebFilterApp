using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AdminControlPanel.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AdminControlPanel.Views
{
    public partial class SiteManagementDialog : Window
    {
        private readonly UserDatabaseService? _databaseService;
        private List<UrlListItem> _blacklist = new();
        private List<UrlListItem> _whitelist = new();

        public SiteManagementDialog()
        {
            InitializeComponent();
            
            // Get database service
            try
            {
                if (Application.Current is App app && app.ServiceProvider != null)
                {
                    _databaseService = app.ServiceProvider.GetService<UserDatabaseService>();
                }
            }
            catch
            {
                // Service not available
            }
            
            Loaded += SiteManagementDialog_Loaded;
        }
        
        private async void SiteManagementDialog_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadSiteListsAsync();
        }

        private async System.Threading.Tasks.Task LoadSiteListsAsync()
        {
            try
            {
                if (_databaseService != null)
                {
                    _blacklist = await _databaseService.GetUrlListAsync("Blacklist");
                    _whitelist = await _databaseService.GetUrlListAsync("Whitelist");
                    
                    BlacklistView.ItemsSource = _blacklist;
                    WhitelistView.ItemsSource = _whitelist;
                    
                    StatusTextBlock.Text = $"Loaded {_blacklist.Count} blocked sites and {_whitelist.Count} allowed sites";
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Blue;
                }
                else
                {
                    StatusTextBlock.Text = "Database service not available";
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error loading site lists: {ex.Message}";
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private async void AddSite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var url = UrlTextBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(url))
                {
                    StatusTextBlock.Text = "Please enter a website URL";
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                    return;
                }

                // Clean up URL - remove protocol and www
                url = CleanUrl(url);

                var action = ActionComboBox.SelectedIndex == 0 ? "Blacklist" : "Whitelist";
                var category = ((ComboBoxItem)CategoryComboBox.SelectedItem).Content.ToString();

                if (_databaseService != null)
                {
                    // Check if URL already exists
                    var existing = await _databaseService.FindUrlInListsAsync(url);
                    if (existing != null)
                    {
                        StatusTextBlock.Text = $"Website {url} already exists in {existing.ListType}";
                        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Orange;
                        return;
                    }

                    // Add to database
                    var success = await _databaseService.AddUrlToListAsync(url, action, category, $"Added via admin panel");
                    
                    if (success)
                    {
                        StatusTextBlock.Text = $"✅ Added {url} to {action.ToLower()}";
                        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
                        
                        UrlTextBox.Text = "Enter website (e.g., facebook.com)";
                        
                        // Refresh the lists
                        await LoadSiteListsAsync();
                    }
                    else
                    {
                        StatusTextBlock.Text = $"Failed to add {url}";
                        StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                    }
                }
                else
                {
                    StatusTextBlock.Text = "Database service not available";
                    StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private async void RemoveSite_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is UrlListItem urlItem)
                {
                    var result = MessageBox.Show(
                        $"Are you sure you want to remove {urlItem.UrlPattern} from the {urlItem.ListType.ToLower()}?",
                        "Confirm Removal",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Question);

                    if (result == MessageBoxResult.Yes && _databaseService != null)
                    {
                        var success = await _databaseService.RemoveUrlFromListAsync(urlItem.UrlPattern, urlItem.ListType);
                        
                        if (success)
                        {
                            StatusTextBlock.Text = $"✅ Removed {urlItem.UrlPattern} from {urlItem.ListType.ToLower()}";
                            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Green;
                            
                            // Refresh the lists
                            await LoadSiteListsAsync();
                        }
                        else
                        {
                            StatusTextBlock.Text = $"Failed to remove {urlItem.UrlPattern}";
                            StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"Error: {ex.Message}";
                StatusTextBlock.Foreground = System.Windows.Media.Brushes.Red;
            }
        }

        private async void RefreshLists_Click(object sender, RoutedEventArgs e)
        {
            await LoadSiteListsAsync();
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

        private void UrlTextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (UrlTextBox.Text == "Enter website (e.g., facebook.com)")
            {
                UrlTextBox.Text = "";
                UrlTextBox.Foreground = System.Windows.Media.Brushes.Black;
            }
        }

        private void UrlTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(UrlTextBox.Text))
            {
                UrlTextBox.Text = "Enter website (e.g., facebook.com)";
                UrlTextBox.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }
    }
}