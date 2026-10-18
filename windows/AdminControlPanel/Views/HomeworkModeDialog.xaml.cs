using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SharedModels = UrlFilter.Shared.Models;

namespace AdminControlPanel.Views
{
    /// <summary>
    /// Dialog for configuring homework mode for selected users
    /// </summary>
    public partial class HomeworkModeDialog : Window
    {
        public List<SharedModels.DeviceUser> SelectedUsers { get; set; } = new List<SharedModels.DeviceUser>();
        public int SelectedDurationMinutes { get; private set; }
        
        private int _selectedOptionMinutes = 0;
        
        // Common homework duration options (in minutes)
        private readonly int[] _durationOptions = { 30, 60, 90, 120, 150, 180, 240, 300 };
        private readonly string[] _durationDescriptions = { 
            "Quick assignment", 
            "Standard homework", 
            "Extended study", 
            "Long assignment", 
            "Research project", 
            "Major homework", 
            "Big project", 
            "Final exam prep" 
        };

        public HomeworkModeDialog()
        {
            InitializeComponent();
            InitializeDialog();
        }

        public HomeworkModeDialog(List<SharedModels.DeviceUser> users)
        {
            InitializeComponent();
            SelectedUsers = users ?? new List<SharedModels.DeviceUser>();
            InitializeDialog();
        }

        private void InitializeDialog()
        {
            UpdateSelectedUsersDisplay();
            PopulateDurationOptions();
            UpdateCustomDurationDisplay();
        }

        private void UpdateSelectedUsersDisplay()
        {
            if (SelectedUsers.Any())
            {
                var userNames = SelectedUsers.Select(u => $"{GetUserIcon(u.UserType)} {u.DisplayName ?? u.Username}");
                SelectedUsersText.Text = string.Join(", ", userNames);
                SelectedUsersText.Foreground = Brushes.Black;
            }
            else
            {
                SelectedUsersText.Text = "No users selected";
                SelectedUsersText.Foreground = Brushes.Gray;
            }
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

        private void PopulateDurationOptions()
        {
            for (int i = 0; i < _durationOptions.Length; i++)
            {
                var minutes = _durationOptions[i];
                var description = _durationDescriptions[i];
                var durationOption = CreateDurationOptionCard(minutes, description);
                DurationOptionsPanel.Children.Add(durationOption);
            }
        }

        private Border CreateDurationOptionCard(int minutes, string description)
        {
            var border = new Border
            {
                Style = (Style)FindResource("DurationOptionStyle"),
                Tag = minutes
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Duration icon
            var icon = new TextBlock
            {
                Text = "⏰",
                FontSize = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            Grid.SetColumn(icon, 0);

            // Duration info
            var durationInfo = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            var durationText = new TextBlock
            {
                Text = FormatMinutes(minutes),
                FontSize = 16,
                FontWeight = FontWeights.Bold
            };

            var descText = new TextBlock
            {
                Text = description,
                FontSize = 12,
                Foreground = Brushes.Gray,
                Margin = new Thickness(0, 2, 0, 0)
            };

            durationInfo.Children.Add(durationText);
            durationInfo.Children.Add(descText);
            Grid.SetColumn(durationInfo, 1);

            // Recommendation indicator for common homework durations
            var recommendText = new TextBlock
            {
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic
            };

            if (minutes <= 120) // 2 hours or less
            {
                recommendText.Text = "Recommended";
                recommendText.Foreground = Brushes.Green;
            }
            else if (minutes <= 240) // 4 hours or less
            {
                recommendText.Text = "Long session";
                recommendText.Foreground = Brushes.Orange;
            }
            else
            {
                recommendText.Text = "Very long";
                recommendText.Foreground = Brushes.Red;
            }

            Grid.SetColumn(recommendText, 2);

            grid.Children.Add(icon);
            grid.Children.Add(durationInfo);
            grid.Children.Add(recommendText);

            border.Child = grid;
            border.MouseLeftButtonUp += DurationOption_Click;

            return border;
        }

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

        private void DurationOption_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is int minutes)
            {
                _selectedOptionMinutes = minutes;
                SelectedDurationMinutes = minutes;
                
                // Reset all borders to default style
                foreach (Border child in DurationOptionsPanel.Children.OfType<Border>())
                {
                    child.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224));
                    child.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                }
                
                // Highlight selected option
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                border.Background = new SolidColorBrush(Color.FromRgb(232, 245, 232));
                
                // Clear custom input
                CustomDurationTextBox.Text = "";
                
                // Enable button
                EnableButton.IsEnabled = true;
            }
        }

        private void EnableHomeworkMode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if custom duration is entered
                if (!string.IsNullOrWhiteSpace(CustomDurationTextBox.Text))
                {
                    if (int.TryParse(CustomDurationTextBox.Text, out int customMinutes) && customMinutes > 0)
                    {
                        SelectedDurationMinutes = customMinutes;
                    }
                    else
                    {
                        MessageBox.Show("Please enter a valid number of minutes (greater than 0).", 
                                      "Invalid Input", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                else if (_selectedOptionMinutes == 0)
                {
                    MessageBox.Show("Please select a duration option or enter custom duration.", 
                                  "No Duration Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Validate reasonable duration limits
                if (SelectedDurationMinutes > 480) // More than 8 hours
                {
                    var result = MessageBox.Show($"You're about to enable homework mode for {FormatMinutes(SelectedDurationMinutes)}. " +
                                               "This is a very long duration. Are you sure?", 
                                               "Long Homework Session", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                // Confirm the action
                var userNames = string.Join(", ", SelectedUsers.Select(u => u.DisplayName));
                var confirmResult = MessageBox.Show(
                    $"Enable Homework Mode for {FormatMinutes(SelectedDurationMinutes)} for:\n{userNames}\n\n" +
                    "During this time, only educational websites and resources will be accessible.\n\n" +
                    "Continue?",
                    "Confirm Homework Mode", 
                    MessageBoxButton.YesNo, 
                    MessageBoxImage.Question);

                if (confirmResult == MessageBoxResult.Yes)
                {
                    DialogResult = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error configuring homework mode: {ex.Message}", 
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CustomDurationTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Only allow numeric input
            var regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void CustomDurationTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Clear selected option when custom input is used
            if (!string.IsNullOrWhiteSpace(CustomDurationTextBox.Text))
            {
                _selectedOptionMinutes = 0;
                
                // Reset all option highlights
                foreach (Border child in DurationOptionsPanel.Children.OfType<Border>())
                {
                    child.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224));
                    child.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                }
                
                // Enable button if valid input
                if (int.TryParse(CustomDurationTextBox.Text, out int minutes) && minutes > 0)
                {
                    EnableButton.IsEnabled = true;
                }
            }
            
            UpdateCustomDurationDisplay();
        }

        private void UpdateCustomDurationDisplay()
        {
            if (int.TryParse(CustomDurationTextBox.Text, out int minutes) && minutes > 0)
            {
                CustomDurationHoursText.Text = FormatMinutes(minutes);
            }
            else
            {
                CustomDurationHoursText.Text = "0 minutes";
            }
        }
    }
}