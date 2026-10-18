using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SharedModels = UrlFilter.Shared.Models;

namespace AdminControlPanel.Views
{
    /// <summary>
    /// Dialog for selecting which user(s) to apply an action to
    /// </summary>
    public partial class UserSelectionDialog : Window
    {
        public List<SharedModels.DeviceUser> AvailableUsers { get; set; } = new List<SharedModels.DeviceUser>();
        public SharedModels.DeviceUser? SelectedUser { get; private set; }
        public List<SharedModels.DeviceUser> SelectedUsers { get; private set; } = new List<SharedModels.DeviceUser>();
        public bool AllowMultipleSelection { get; set; } = false;
        public string ActionTitle { get; set; } = "Select User";
        public string ActionDescription { get; set; } = "Select which family member to apply the action to:";

        public UserSelectionDialog()
        {
            InitializeComponent();
            DataContext = this;
        }

        public UserSelectionDialog(string title, string description, List<SharedModels.DeviceUser> users, bool allowMultiple = false)
        {
            InitializeComponent();
            
            ActionTitle = title;
            ActionDescription = description;
            AvailableUsers = users ?? new List<SharedModels.DeviceUser>();
            AllowMultipleSelection = allowMultiple;
            
            InitializeDialog();
        }

        private void InitializeDialog()
        {
            TitleTextBlock.Text = ActionTitle;
            DescriptionTextBlock.Text = ActionDescription;
            
            if (!AllowMultipleSelection)
            {
                SelectAllButton.Visibility = Visibility.Collapsed;
            }
            
            PopulateUserList();
        }

        private void PopulateUserList()
        {
            UserListPanel.Children.Clear();
            
            if (!AvailableUsers.Any())
            {
                // Show empty state
                var emptyBorder = new Border
                {
                    Style = (Style)FindResource("UserCardStyle"),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                
                var emptyStack = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "👤",
                    FontSize = 48,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 10)
                });
                
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "No users found",
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                
                emptyStack.Children.Add(new TextBlock
                {
                    Text = "Please add family members first",
                    FontSize = 14,
                    Foreground = Brushes.Gray,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 5, 0, 0)
                });
                
                emptyBorder.Child = emptyStack;
                UserListPanel.Children.Add(emptyBorder);
                return;
            }
            
            foreach (var user in AvailableUsers)
            {
                var userCard = CreateUserCard(user);
                UserListPanel.Children.Add(userCard);
            }
        }

        private Border CreateUserCard(SharedModels.DeviceUser user)
        {
            var border = new Border
            {
                Style = (Style)FindResource("UserCardStyle"),
                Tag = user
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // User avatar/icon
            var avatar = new TextBlock
            {
                Text = GetUserIcon(user.UserType),
                FontSize = 32,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            Grid.SetColumn(avatar, 0);

            // User info
            var userInfo = new StackPanel
            {
                Orientation = Orientation.Vertical,
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

            userInfo.Children.Add(nameText);
            userInfo.Children.Add(detailsText);
            Grid.SetColumn(userInfo, 1);

            // Status indicator
            var statusText = new TextBlock
            {
                Text = GetUserStatus(user),
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = GetUserStatusColor(user),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(statusText, 2);

            grid.Children.Add(avatar);
            grid.Children.Add(userInfo);
            grid.Children.Add(statusText);

            border.Child = grid;
            border.MouseLeftButtonUp += UserCard_Click;

            return border;
        }

        private string GetUserIcon(SharedModels.UserType userType)
        {
            return userType switch
            {
                SharedModels.UserType.Child => "👶",
                SharedModels.UserType.Teen => "🧑‍🎓",
                SharedModels.UserType.Adult => "👨‍💼",
                SharedModels.UserType.Guest => "👤",
                SharedModels.UserType.Admin => "👑",
                _ => "👤"
            };
        }

        private string GetUserStatus(SharedModels.DeviceUser user)
        {
            // This would typically come from the service showing actual status
            var random = new Random(user.Id); // Deterministic based on user ID
            var statuses = new[] { "Online", "Offline", "Time Left: 45min", "Blocked" };
            return statuses[random.Next(statuses.Length)];
        }

        private Brush GetUserStatusColor(SharedModels.DeviceUser user)
        {
            var status = GetUserStatus(user);
            return status switch
            {
                "Online" => Brushes.Green,
                "Offline" => Brushes.Gray,
                "Blocked" => Brushes.Red,
                _ when status.Contains("Time Left") => Brushes.Orange,
                _ => Brushes.Gray
            };
        }

        private void UserCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is SharedModels.DeviceUser user)
            {
                if (AllowMultipleSelection)
                {
                    if (SelectedUsers.Contains(user))
                    {
                        SelectedUsers.Remove(user);
                        border.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245)); // Default background
                    }
                    else
                    {
                        SelectedUsers.Add(user);
                        border.Background = new SolidColorBrush(Color.FromRgb(200, 255, 200)); // Selected background
                    }
                }
                else
                {
                    SelectedUser = user;
                    DialogResult = true;
                    Close();
                }
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (AllowMultipleSelection)
            {
                if (SelectedUsers.Count == AvailableUsers.Count)
                {
                    // Deselect all
                    SelectedUsers.Clear();
                    SelectAllButton.Content = "Select All";
                    
                    // Reset visual state
                    foreach (Border border in UserListPanel.Children.OfType<Border>())
                    {
                        border.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                    }
                }
                else
                {
                    // Select all
                    SelectedUsers.Clear();
                    SelectedUsers.AddRange(AvailableUsers);
                    SelectAllButton.Content = "Deselect All";
                    
                    // Update visual state
                    foreach (Border border in UserListPanel.Children.OfType<Border>())
                    {
                        border.Background = new SolidColorBrush(Color.FromRgb(200, 255, 200));
                    }
                }
                
                if (SelectedUsers.Any())
                {
                    DialogResult = true;
                    Close();
                }
            }
        }
    }
}