using System;
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
    /// Dialog for extending internet time for a specific user
    /// </summary>
    public partial class TimeExtensionDialog : Window
    {
        public SharedModels.DeviceUser? TargetUser { get; set; }
        public int SelectedMinutes { get; private set; }
        
        private int _selectedOptionMinutes = 0;
        
        // Common time extension options
        private readonly int[] _timeOptions = { 15, 30, 60, 90, 120, 180, 240 };
        private readonly string[] _timeDescriptions = { 
            "Quick break", 
            "Short extension", 
            "Standard hour", 
            "Homework time", 
            "Movie time", 
            "Long project", 
            "Special occasion" 
        };

        public TimeExtensionDialog()
        {
            InitializeComponent();
            InitializeDialog();
        }

        public TimeExtensionDialog(SharedModels.DeviceUser user)
        {
            InitializeComponent();
            TargetUser = user;
            InitializeDialog();
        }

        private void InitializeDialog()
        {
            if (TargetUser != null)
            {
                UpdateUserInfo();
            }
            
            PopulateTimeOptions();
        }

        private void UpdateUserInfo()
        {
            if (TargetUser == null) return;

            UserNameText.Text = TargetUser.DisplayName ?? TargetUser.Username;
            UserIcon.Text = GetUserIcon(TargetUser.UserType);
            
            // Show current user status (this would come from service in real implementation)
            var currentTime = DateTime.Now.ToString("HH:mm");
            UserStatusText.Text = $"{TargetUser.UserType} • Current time: {currentTime}";
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

        private void PopulateTimeOptions()
        {
            for (int i = 0; i < _timeOptions.Length; i++)
            {
                var minutes = _timeOptions[i];
                var description = _timeDescriptions[i];
                var timeOption = CreateTimeOptionCard(minutes, description);
                TimeOptionsPanel.Children.Add(timeOption);
            }
        }

        private Border CreateTimeOptionCard(int minutes, string description)
        {
            var border = new Border
            {
                Style = (Style)FindResource("TimeOptionStyle"),
                Tag = minutes
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Time icon
            var icon = new TextBlock
            {
                Text = "⏱️",
                FontSize = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 15, 0)
            };
            Grid.SetColumn(icon, 0);

            // Time info
            var timeInfo = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            var timeText = new TextBlock
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

            timeInfo.Children.Add(timeText);
            timeInfo.Children.Add(descText);
            Grid.SetColumn(timeInfo, 1);

            // Quick select indicator
            var selectText = new TextBlock
            {
                Text = "Click to select",
                FontSize = 11,
                Foreground = Brushes.Blue,
                VerticalAlignment = VerticalAlignment.Center,
                FontStyle = FontStyles.Italic
            };
            Grid.SetColumn(selectText, 2);

            grid.Children.Add(icon);
            grid.Children.Add(timeInfo);
            grid.Children.Add(selectText);

            border.Child = grid;
            border.MouseLeftButtonUp += TimeOption_Click;

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

        private void TimeOption_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is int minutes)
            {
                _selectedOptionMinutes = minutes;
                SelectedMinutes = minutes;
                
                // Reset all borders to default style
                foreach (Border child in TimeOptionsPanel.Children.OfType<Border>())
                {
                    child.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224));
                    child.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                }
                
                // Highlight selected option
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(76, 175, 80));
                border.Background = new SolidColorBrush(Color.FromRgb(232, 245, 232));
                
                // Clear custom input
                CustomMinutesTextBox.Text = "";
                
                // Enable extend button
                ExtendButton.IsEnabled = true;
            }
        }

        private void ExtendTime_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Check if custom time is entered
                if (!string.IsNullOrWhiteSpace(CustomMinutesTextBox.Text))
                {
                    if (int.TryParse(CustomMinutesTextBox.Text, out int customMinutes) && customMinutes > 0)
                    {
                        SelectedMinutes = customMinutes;
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
                    MessageBox.Show("Please select a time option or enter custom minutes.", 
                                  "No Time Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Validate reasonable time limits
                if (SelectedMinutes > 720) // More than 12 hours
                {
                    var result = MessageBox.Show($"You're about to extend time by {FormatMinutes(SelectedMinutes)}. " +
                                               "This is a very long extension. Are you sure?", 
                                               "Long Time Extension", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result != MessageBoxResult.Yes)
                    {
                        return;
                    }
                }

                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error processing time extension: {ex.Message}", 
                              "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CustomMinutesTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Only allow numeric input
            var regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        private void CustomMinutesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Clear selected option when custom input is used
            if (!string.IsNullOrWhiteSpace(CustomMinutesTextBox.Text))
            {
                _selectedOptionMinutes = 0;
                
                // Reset all option highlights
                foreach (Border child in TimeOptionsPanel.Children.OfType<Border>())
                {
                    child.BorderBrush = new SolidColorBrush(Color.FromRgb(224, 224, 224));
                    child.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                }
                
                // Enable extend button if valid input
                if (int.TryParse(CustomMinutesTextBox.Text, out int minutes) && minutes > 0)
                {
                    ExtendButton.IsEnabled = true;
                }
            }
        }
    }
}