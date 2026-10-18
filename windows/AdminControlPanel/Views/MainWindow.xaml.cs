using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdminControlPanel.Services;
using AdminControlPanel.ViewModels;
using AdminControlPanel.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using UrlFilter.Shared.Models;

namespace AdminControlPanel.Views
{
    public partial class MainWindow : Window
    {
        private readonly UserDatabaseService? _databaseService;
        private readonly UrlFilterServiceClient? _serviceClient;
        private bool _isFilteringEnabled = true;

        public MainWindow(MainWindowViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            // Get services
            try
            {
                if (Application.Current is App app && app.ServiceProvider != null)
                {
                    _databaseService = app.ServiceProvider.GetService<UserDatabaseService>();
                    _serviceClient = app.ServiceProvider.GetService<UrlFilterServiceClient>();
                }
            }
            catch
            {
                // Service not available
            }

            Loaded += MainWindow_Loaded;
        }
        
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await RefreshFilteringStatusAsync();
                await LoadFamilyDataAsync();
                await LoadWebsiteListsAsync();
                await LoadUsersAsync();
                await LoadSettingsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading main window: {ex.Message}\n\n{ex.StackTrace}",
                    "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #region Filtering Toggle

        private async void ToggleFiltering_Click(object sender, RoutedEventArgs e)
        {
            if (_serviceClient == null)
            {
                MessageBox.Show("Cannot communicate with the filtering service.\nMake sure the service is running.",
                    "Service Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                bool success;
                if (_isFilteringEnabled)
                {
                    var result = MessageBox.Show(
                        "Are you sure you want to DISABLE filtering?\n\nAll internet traffic will be unfiltered.",
                        "Disable Filtering", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                    if (result != MessageBoxResult.Yes) return;

                    success = await _serviceClient.DisableFilteringAsync();
                }
                else
                {
                    success = await _serviceClient.EnableFilteringAsync();
                }

                if (success)
                {
                    _isFilteringEnabled = !_isFilteringEnabled;
                    UpdateFilteringStatusUI();
                }
                else
                {
                    MessageBox.Show("Failed to change filtering state. Check service logs.",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error toggling filtering: {ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task RefreshFilteringStatusAsync()
        {
            try
            {
                if (_serviceClient != null)
                {
                    var status = await _serviceClient.GetFilteringStatusAsync();
                    _isFilteringEnabled = status.IsFilteringActive || status.IsEnabled;
                    UpdateFilteringStatusUI();
                }
            }
            catch
            {
                // Default to enabled state if can't connect
                _isFilteringEnabled = true;
                UpdateFilteringStatusUI();
            }
        }

        private void UpdateFilteringStatusUI()
        {
            if (_isFilteringEnabled)
            {
                FilterStatusBorder.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
                FilterStatusText.Text = "Filtering: ON";
                ToggleFilteringButton.Content = "Disable Filtering";
                ToggleFilteringButton.Background = new SolidColorBrush(Color.FromRgb(255, 87, 34)); // Orange-red
            }
            else
            {
                FilterStatusBorder.Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)); // Red
                FilterStatusText.Text = "Filtering: OFF";
                ToggleFilteringButton.Content = "Enable Filtering";
                ToggleFilteringButton.Background = new SolidColorBrush(Color.FromRgb(76, 175, 80)); // Green
            }
        }

        #endregion
        
        private async Task LoadFamilyDataAsync()
        {
            try
            {
                if (_databaseService != null)
                {
                    var users = await _databaseService.GetAllUsersAsync();
                    
                    // Clear existing user cards
                    UserCardsPanel.Children.Clear();
                    
                    // Add user cards for each family member
                    foreach (var user in users)
                    {
                        var userCard = new UserProfileCard(user);
                        userCard.Width = 200;
                        userCard.Margin = new Thickness(10);
                        UserCardsPanel.Children.Add(userCard);
                    }
                    
                    // Update family status
                    var activeUsers = users.Count(u => u.IsActive);
                    var timeUpUsers = users.Count(u => u.IsTimeUp);
                    
                    var statusText = timeUpUsers > 0 
                        ? $"👨‍👩‍👧‍👦 Family Status: {timeUpUsers} time limit(s) reached ⚠️" 
                        : "👨‍👩‍👧‍👦 Family Status: All Good ✅";
                        
                    // Update the family status text in the XAML
                    UpdateFamilyStatusText(statusText);
                }
                else
                {
                    // Fallback - create sample cards
                    CreateSampleUserCards();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading family data: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                CreateSampleUserCards();
            }
        }
        
        private void CreateSampleUserCards()
        {
            UserCardsPanel.Children.Clear();
            
            var sampleUsers = new[]
            {
                new { Name = "Emma", Age = 8, Status = "Online", TimeLeft = "45min left", Color = "Green" },
                new { Name = "Jake", Age = 15, Status = "Time Up", TimeLeft = "Blocked", Color = "Red" },
                new { Name = "Sarah", Age = 42, Status = "Online", TimeLeft = "Unlimited", Color = "Green" }
            };
            
            foreach (var user in sampleUsers)
            {
                var userCard = new UserProfileCard();
                userCard.Width = 200;
                userCard.Margin = new Thickness(10);
                
                // Set sample data (this would normally be done through data binding)
                SetUserCardData(userCard, user.Name, user.Age, user.Status, user.TimeLeft, user.Color);
                
                UserCardsPanel.Children.Add(userCard);
            }
        }
        
        private void SetUserCardData(UserProfileCard card, string name, int age, string status, string timeLeft, string color)
        {
            // This is a simplified way to set data - normally we'd use proper data binding
            if (card.FindName("UserName") is TextBlock nameBlock)
                nameBlock.Text = name;
            if (card.FindName("UserAge") is TextBlock ageBlock)
                ageBlock.Text = $"Age {age}";
            if (card.FindName("StatusText") is TextBlock statusBlock)
                statusBlock.Text = status;
            if (card.FindName("TimeRemaining") is TextBlock timeBlock)
                timeBlock.Text = timeLeft;
            if (card.FindName("StatusIndicator") is System.Windows.Shapes.Ellipse indicator)
            {
                indicator.Fill = color switch
                {
                    "Green" => Brushes.Green,
                    "Red" => Brushes.Red,
                    "Gray" => Brushes.Gray,
                    _ => Brushes.Gray
                };
            }
        }
        
        private void UpdateFamilyStatusText(string statusText)
        {
            // Find the family status TextBlock in the XAML by traversing the visual tree
            try
            {
                var border = FindVisualChild<Border>(this);
                if (border != null)
                {
                    var statusTextBlock = FindElementWithText(border, "👨‍👩‍👧‍👦 Family Status:");
                    if (statusTextBlock != null)
                    {
                        statusTextBlock.Text = statusText;
                    }
                }
            }
            catch
            {
                // If we can't find the element, just ignore - it's not critical
            }
        }
        
        private TextBlock? FindElementWithText(DependencyObject parent, string textStart)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock textBlock && textBlock.Text.StartsWith(textStart))
                {
                    return textBlock;
                }
                
                var result = FindElementWithText(child, textStart);
                if (result != null)
                    return result;
            }
            return null;
        }
        
        private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T found)
                {
                    return found;
                }
                
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }

        private async Task LoadWebsiteListsAsync()
        {
            try
            {
                if (_databaseService != null)
                {
                    var blacklist = await _databaseService.GetUrlListAsync("Blacklist");
                    var whitelist = await _databaseService.GetUrlListAsync("Whitelist");
                    
                    WebsiteListsPanel.Children.Clear();
                    
                    // Blocked Sites Summary
                    var blockedBorder = new Border
                    {
                        Background = Brushes.White,
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(20),
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    
                    var blockedStack = new StackPanel();
                    blockedStack.Children.Add(new TextBlock 
                    { 
                        Text = $"🚫 Blocked Websites ({blacklist.Count})", 
                        FontWeight = FontWeights.Bold, 
                        FontSize = 16, 
                        Margin = new Thickness(0, 0, 0, 10) 
                    });
                    
                    if (blacklist.Any())
                    {
                        foreach (var site in blacklist.Take(5))
                        {
                            blockedStack.Children.Add(new TextBlock 
                            { 
                                Text = $"• {site.UrlPattern} ({site.Category})", 
                                Margin = new Thickness(10, 2, 0, 2) 
                            });
                        }
                        
                        if (blacklist.Count > 5)
                        {
                            blockedStack.Children.Add(new TextBlock 
                            { 
                                Text = $"... and {blacklist.Count - 5} more", 
                                FontStyle = FontStyles.Italic,
                                Margin = new Thickness(10, 2, 0, 2) 
                            });
                        }
                    }
                    else
                    {
                        blockedStack.Children.Add(new TextBlock 
                        { 
                            Text = "No blocked websites configured", 
                            FontStyle = FontStyles.Italic,
                            Margin = new Thickness(10, 2, 0, 2) 
                        });
                    }
                    
                    blockedBorder.Child = blockedStack;
                    WebsiteListsPanel.Children.Add(blockedBorder);
                    
                    // Allowed Sites Summary
                    var allowedBorder = new Border
                    {
                        Background = Brushes.White,
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(20),
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    
                    var allowedStack = new StackPanel();
                    allowedStack.Children.Add(new TextBlock 
                    { 
                        Text = $"✅ Allowed Websites ({whitelist.Count})", 
                        FontWeight = FontWeights.Bold, 
                        FontSize = 16, 
                        Margin = new Thickness(0, 0, 0, 10) 
                    });
                    
                    if (whitelist.Any())
                    {
                        foreach (var site in whitelist.Take(5))
                        {
                            allowedStack.Children.Add(new TextBlock 
                            { 
                                Text = $"• {site.UrlPattern} ({site.Category})", 
                                Margin = new Thickness(10, 2, 0, 2) 
                            });
                        }
                        
                        if (whitelist.Count > 5)
                        {
                            allowedStack.Children.Add(new TextBlock 
                            { 
                                Text = $"... and {whitelist.Count - 5} more", 
                                FontStyle = FontStyles.Italic,
                                Margin = new Thickness(10, 2, 0, 2) 
                            });
                        }
                    }
                    else
                    {
                        allowedStack.Children.Add(new TextBlock 
                        { 
                            Text = "No explicitly allowed websites configured", 
                            FontStyle = FontStyles.Italic,
                            Margin = new Thickness(10, 2, 0, 2) 
                        });
                    }
                    
                    allowedBorder.Child = allowedStack;
                    WebsiteListsPanel.Children.Add(allowedBorder);
                }
                else
                {
                    WebsiteListsPanel.Children.Clear();
                    WebsiteListsPanel.Children.Add(new TextBlock 
                    { 
                        Text = "Database service not available", 
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(20) 
                    });
                }
            }
            catch (Exception ex)
            {
                WebsiteListsPanel.Children.Clear();
                WebsiteListsPanel.Children.Add(new TextBlock 
                { 
                    Text = $"Error loading website lists: {ex.Message}", 
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brushes.Red,
                    Margin = new Thickness(20) 
                });
            }
        }
        
        private void ManageWebsites_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Views.WebsiteListsDialog();
                dialog.Owner = this;
                dialog.ShowDialog();
                
                // Refresh the lists after dialog closes
                _ = LoadWebsiteListsAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening website management: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        
        private void ViewStatistics_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Website filtering statistics will be available in a future update.", 
                          "Statistics", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        
        private async void RefreshLists_Click(object sender, RoutedEventArgs e)
        {
            await LoadWebsiteListsAsync();
        }

        #region User Management

        private async Task LoadUsersAsync()
        {
            try
            {
                if (_databaseService == null)
                {
                    CreateSampleUserList();
                    return;
                }

                var users = await _databaseService.GetAllUsersAsync();
                UserListPanel.Children.Clear();

                if (!users.Any())
                {
                    var emptyMessage = new TextBlock
                    {
                        Text = "No family members added yet. Click 'Add Family Member' to get started.",
                        FontStyle = FontStyles.Italic,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(20),
                        Foreground = Brushes.Gray
                    };
                    UserListPanel.Children.Add(emptyMessage);
                    return;
                }

                foreach (var user in users)
                {
                    var userCard = CreateUserCard(user);
                    UserListPanel.Children.Add(userCard);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading users: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
                CreateSampleUserList();
            }
        }

        private void CreateSampleUserList()
        {
            UserListPanel.Children.Clear();
            
            var sampleUsers = new[]
            {
                new { Name = "Emma Johnson", Age = 8, Type = "Child", Status = "Online", TimeLeft = "45 min left" },
                new { Name = "Jake Johnson", Age = 15, Type = "Teen", Status = "Time Up", TimeLeft = "Blocked until tomorrow" },
                new { Name = "Sarah Johnson", Age = 42, Type = "Adult", Status = "Online", TimeLeft = "Unlimited access" }
            };

            foreach (var user in sampleUsers)
            {
                var card = CreateSampleUserCard(user.Name, user.Age, user.Type, user.Status, user.TimeLeft);
                UserListPanel.Children.Add(card);
            }
        }

        private Border CreateUserCard(AdminControlPanel.Services.DeviceUser user)
        {
            var border = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.LightGray
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // User avatar
            var avatar = new TextBlock
            {
                Text = GetUserTypeIcon(user.UserType),
                FontSize = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            Grid.SetColumn(avatar, 0);

            // User info
            var userInfo = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameText = new TextBlock
            {
                Text = user.DisplayName ?? user.Username,
                FontSize = 16,
                FontWeight = FontWeights.Bold
            };

            var detailsText = new TextBlock
            {
                Text = $"{user.UserType}" + (user.Age.HasValue ? $" • {user.Age} years old" : ""),
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var statusText = new TextBlock
            {
                Text = user.IsActive ? "Active" : "Inactive",
                FontSize = 12,
                Foreground = user.IsActive ? Brushes.Green : Brushes.Red,
                Margin = new Thickness(0, 2, 0, 0)
            };

            userInfo.Children.Add(nameText);
            userInfo.Children.Add(detailsText);
            userInfo.Children.Add(statusText);
            Grid.SetColumn(userInfo, 1);

            // Edit button
            var editButton = new Button
            {
                Content = "✏️ Edit",
                Width = 70,
                Height = 30,
                Margin = new Thickness(5, 0, 5, 0),
                Background = new SolidColorBrush(Color.FromRgb(33, 150, 243)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.Bold,
                Tag = user
            };
            editButton.Click += EditUser_Click;
            Grid.SetColumn(editButton, 2);

            // Delete button
            var deleteButton = new Button
            {
                Content = "🗑️",
                Width = 35,
                Height = 30,
                Background = new SolidColorBrush(Color.FromRgb(244, 67, 54)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.Bold,
                Tag = user
            };
            deleteButton.Click += DeleteUser_Click;
            Grid.SetColumn(deleteButton, 3);

            grid.Children.Add(avatar);
            grid.Children.Add(userInfo);
            grid.Children.Add(editButton);
            grid.Children.Add(deleteButton);

            border.Child = grid;
            return border;
        }

        private Border CreateSampleUserCard(string name, int age, string type, string status, string timeLeft)
        {
            var border = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 0, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.LightGray
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // User avatar
            var avatar = new TextBlock
            {
                Text = type switch
                {
                    "Child" => "👶",
                    "Teen" => "🧑‍🎓", 
                    "Adult" => "👨‍💼",
                    _ => "👤"
                },
                FontSize = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            Grid.SetColumn(avatar, 0);

            // User info
            var userInfo = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center
            };

            userInfo.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 16,
                FontWeight = FontWeights.Bold
            });

            userInfo.Children.Add(new TextBlock
            {
                Text = $"{type} • {age} years old",
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 0)
            });

            userInfo.Children.Add(new TextBlock
            {
                Text = $"Status: {status} • {timeLeft}",
                FontSize = 12,
                Foreground = status == "Online" ? Brushes.Green : Brushes.Red,
                Margin = new Thickness(0, 2, 0, 0)
            });

            Grid.SetColumn(userInfo, 1);

            // Demo label
            var demoLabel = new TextBlock
            {
                Text = "Demo Data",
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(demoLabel, 2);

            grid.Children.Add(avatar);
            grid.Children.Add(userInfo);
            grid.Children.Add(demoLabel);

            border.Child = grid;
            return border;
        }

        private string GetUserTypeIcon(UrlFilter.Shared.Models.UserType userType)
        {
            return userType switch
            {
                UrlFilter.Shared.Models.UserType.Child => "👶",
                UrlFilter.Shared.Models.UserType.Teen => "🧑‍🎓",
                UrlFilter.Shared.Models.UserType.Adult => "👨‍💼",
                UrlFilter.Shared.Models.UserType.Guest => "👤",
                UrlFilter.Shared.Models.UserType.Admin => "👑",
                _ => "👤"
            };
        }

        // User Management Event Handlers
        private async void AddUser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new Views.AddUserDialog();
                dialog.Owner = this;
                if (dialog.ShowDialog() == true)
                {
                    // Add the user to the database
                    if (_databaseService != null)
                    {
                        var newUser = await _databaseService.AddUserAsync(
                            dialog.DisplayName,
                            dialog.SelectedUserType,
                            dialog.Age,
                            dialog.SelectedWindowsUsername);  // Pass Windows username

                        if (newUser != null)
                        {
                            // If time limit was specified, update it
                            if (dialog.DailyTimeLimitMinutes > 0)
                            {
                                await _databaseService.UpdateUserTimeLimitAsync(newUser.Id, dialog.DailyTimeLimitMinutes);
                            }

                            var windowsInfo = !string.IsNullOrEmpty(dialog.SelectedWindowsUsername)
                                ? $"\nLinked to Windows user: {dialog.SelectedWindowsUsername}"
                                : "\n(Not linked to a Windows account)";

                            MessageBox.Show($"User '{dialog.DisplayName}' has been added successfully!{windowsInfo}",
                                          "User Added", MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }

                    await LoadUsersAsync();
                    await LoadFamilyDataAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error adding user: {ex.Message}", "Error",
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void EditUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AdminControlPanel.Services.DeviceUser user)
            {
                MessageBox.Show($"Edit user functionality for {user.DisplayName} will be implemented soon.", 
                              "Coming Soon", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private async void DeleteUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is AdminControlPanel.Services.DeviceUser user)
            {
                var result = MessageBox.Show($"Are you sure you want to delete {user.DisplayName}?\n\nThis action cannot be undone.", 
                                           "Delete User", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                
                if (result == MessageBoxResult.Yes)
                {
                    try
                    {
                        if (_databaseService != null)
                        {
                            await _databaseService.DeleteUserAsync(user.Id);
                            await LoadUsersAsync();
                            MessageBox.Show($"User {user.DisplayName} has been deleted.", "User Deleted", 
                                          MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Error deleting user: {ex.Message}", "Error", 
                                      MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        private async void RefreshUsers_Click(object sender, RoutedEventArgs e)
        {
            await LoadUsersAsync();
        }

        private void ExportUsers_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|CSV files (*.csv)|*.csv",
                    Title = "Export Users",
                    FileName = $"UrlFilter_Users_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    MessageBox.Show($"Users will be exported to:\n{saveDialog.FileName}\n\n(Export functionality coming soon)", 
                                  "Export Users", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting users: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportUsers_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Filter = "JSON files (*.json)|*.json|CSV files (*.csv)|*.csv",
                    Title = "Import Users"
                };

                if (openDialog.ShowDialog() == true)
                {
                    MessageBox.Show($"Users will be imported from:\n{openDialog.FileName}\n\n(Import functionality coming soon)", 
                                  "Import Users", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error importing users: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Settings Management

        private async Task LoadSettingsAsync()
        {
            try
            {
                if (_databaseService != null)
                {
                    // Load settings from database
                    var autoStart = await _databaseService.GetSystemSettingAsync("auto_start_with_windows");
                    AutoStartCheckBox.IsChecked = autoStart?.ToLower() == "true";

                    var enableLogging = await _databaseService.GetSystemSettingAsync("enable_logging");
                    EnableLoggingCheckBox.IsChecked = enableLogging?.ToLower() != "false"; // Default true

                    var updateFreq = await _databaseService.GetSystemSettingAsync("update_frequency");
                    UpdateFrequencyComboBox.SelectedIndex = updateFreq switch
                    {
                        "daily" => 0,
                        "weekly" => 1,
                        "monthly" => 2,
                        "manual" => 3,
                        _ => 1 // Default weekly
                    };

                    var notificationLevel = await _databaseService.GetSystemSettingAsync("notification_level");
                    NotificationLevelComboBox.SelectedIndex = notificationLevel switch
                    {
                        "all" => 0,
                        "important" => 1,
                        "minimal" => 2,
                        "none" => 3,
                        _ => 1 // Default important
                    };

                    var childPolicy = await _databaseService.GetSystemSettingAsync("default_child_policy");
                    DefaultChildPolicyComboBox.SelectedIndex = childPolicy switch
                    {
                        "allow_all" => 0,
                        "filtered" => 1,
                        "restricted" => 2,
                        "block_all" => 3,
                        _ => 2 // Default restricted
                    };

                    var teenPolicy = await _databaseService.GetSystemSettingAsync("default_teen_policy");
                    DefaultTeenPolicyComboBox.SelectedIndex = teenPolicy switch
                    {
                        "allow_all" => 0,
                        "filtered" => 1,
                        "restricted" => 2,
                        "block_all" => 3,
                        _ => 1 // Default filtered
                    };

                    var blockVpn = await _databaseService.GetSystemSettingAsync("block_vpn_proxy");
                    BlockVpnCheckBox.IsChecked = blockVpn?.ToLower() != "false"; // Default true

                    var safeSearch = await _databaseService.GetSystemSettingAsync("safe_search_enabled");
                    SafeSearchCheckBox.IsChecked = safeSearch?.ToLower() != "false"; // Default true

                    var maxChildHours = await _databaseService.GetSystemSettingAsync("max_daily_hours_child");
                    MaxChildHoursTextBox.Text = maxChildHours ?? "2";

                    var maxTeenHours = await _databaseService.GetSystemSettingAsync("max_daily_hours_teen");
                    MaxTeenHoursTextBox.Text = maxTeenHours ?? "4";

                    var sessionTimeout = await _databaseService.GetSystemSettingAsync("session_timeout_minutes");
                    SessionTimeoutTextBox.Text = sessionTimeout ?? "120";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading settings: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void SaveSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_databaseService == null)
                {
                    MessageBox.Show("Database service not available", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Validate numeric inputs
                if (!int.TryParse(MaxChildHoursTextBox.Text, out int childHours) || childHours < 0 || childHours > 24)
                {
                    MessageBox.Show("Please enter a valid number of hours for children (0-24).", 
                                  "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(MaxTeenHoursTextBox.Text, out int teenHours) || teenHours < 0 || teenHours > 24)
                {
                    MessageBox.Show("Please enter a valid number of hours for teens (0-24).", 
                                  "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(SessionTimeoutTextBox.Text, out int timeout) || timeout < 1 || timeout > 720)
                {
                    MessageBox.Show("Please enter a valid session timeout (1-720 minutes).", 
                                  "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Save all settings
                await _databaseService.SetSystemSettingAsync("auto_start_with_windows", AutoStartCheckBox.IsChecked == true ? "true" : "false");
                await _databaseService.SetSystemSettingAsync("enable_logging", EnableLoggingCheckBox.IsChecked == true ? "true" : "false");
                
                var updateFreq = UpdateFrequencyComboBox.SelectedIndex switch
                {
                    0 => "daily",
                    1 => "weekly", 
                    2 => "monthly",
                    3 => "manual",
                    _ => "weekly"
                };
                await _databaseService.SetSystemSettingAsync("update_frequency", updateFreq);

                var notificationLevel = NotificationLevelComboBox.SelectedIndex switch
                {
                    0 => "all",
                    1 => "important",
                    2 => "minimal", 
                    3 => "none",
                    _ => "important"
                };
                await _databaseService.SetSystemSettingAsync("notification_level", notificationLevel);

                var childPolicy = DefaultChildPolicyComboBox.SelectedIndex switch
                {
                    0 => "allow_all",
                    1 => "filtered",
                    2 => "restricted",
                    3 => "block_all",
                    _ => "restricted"
                };
                await _databaseService.SetSystemSettingAsync("default_child_policy", childPolicy);

                var teenPolicy = DefaultTeenPolicyComboBox.SelectedIndex switch
                {
                    0 => "allow_all",
                    1 => "filtered", 
                    2 => "restricted",
                    3 => "block_all",
                    _ => "filtered"
                };
                await _databaseService.SetSystemSettingAsync("default_teen_policy", teenPolicy);

                await _databaseService.SetSystemSettingAsync("block_vpn_proxy", BlockVpnCheckBox.IsChecked == true ? "true" : "false");
                await _databaseService.SetSystemSettingAsync("safe_search_enabled", SafeSearchCheckBox.IsChecked == true ? "true" : "false");
                await _databaseService.SetSystemSettingAsync("max_daily_hours_child", MaxChildHoursTextBox.Text);
                await _databaseService.SetSystemSettingAsync("max_daily_hours_teen", MaxTeenHoursTextBox.Text);
                await _databaseService.SetSystemSettingAsync("session_timeout_minutes", SessionTimeoutTextBox.Text);

                MessageBox.Show("Settings saved successfully!", "Settings Saved", 
                              MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error saving settings: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ResetSettings_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("Reset all settings to factory defaults?\n\nThis action cannot be undone.", 
                                       "Reset Settings", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    AutoStartCheckBox.IsChecked = true;
                    EnableLoggingCheckBox.IsChecked = true;
                    UpdateFrequencyComboBox.SelectedIndex = 1; // Weekly
                    NotificationLevelComboBox.SelectedIndex = 1; // Important
                    DefaultChildPolicyComboBox.SelectedIndex = 2; // Restricted
                    DefaultTeenPolicyComboBox.SelectedIndex = 1; // Filtered
                    BlockVpnCheckBox.IsChecked = true;
                    SafeSearchCheckBox.IsChecked = true;
                    MaxChildHoursTextBox.Text = "2";
                    MaxTeenHoursTextBox.Text = "4";
                    SessionTimeoutTextBox.Text = "120";

                    MessageBox.Show("Settings have been reset to defaults. Click 'Save Settings' to apply the changes.", 
                                  "Settings Reset", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error resetting settings: {ex.Message}", "Error", 
                                  MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ExportSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var saveDialog = new SaveFileDialog
                {
                    Filter = "JSON files (*.json)|*.json",
                    Title = "Export Settings",
                    FileName = $"UrlFilter_Settings_{DateTime.Now:yyyyMMdd_HHmmss}.json"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    MessageBox.Show($"Settings will be exported to:\n{saveDialog.FileName}\n\n(Export functionality coming soon)", 
                                  "Export Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error exporting settings: {ex.Message}", "Error", 
                              MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}