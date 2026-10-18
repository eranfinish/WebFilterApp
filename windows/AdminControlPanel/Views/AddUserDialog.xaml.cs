using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AdminControlPanel.Services;
using Microsoft.Extensions.DependencyInjection;
using UrlFilter.Shared.Models;

namespace AdminControlPanel.Views
{
    public partial class AddUserDialog : Window
    {
        private readonly UserDatabaseService _databaseService;

        public string DisplayName { get; private set; }
        public UserType SelectedUserType { get; private set; }
        public int? Age { get; private set; }
        public int DailyTimeLimitMinutes { get; private set; }
        public string SelectedWindowsUsername { get; private set; }

        public AddUserDialog()
        {
            InitializeComponent();

            // Get database service
            if (Application.Current is App app && app.ServiceProvider != null)
            {
                _databaseService = app.ServiceProvider.GetService<UserDatabaseService>();
            }

            LoadWindowsUsers();
            UpdateTimeLimitText();
        }

        private void LoadWindowsUsers()
        {
            try
            {
                if (_databaseService != null)
                {
                    var windowsUsers = _databaseService.GetWindowsUsers();

                    // Add a "None" option
                    WindowsUserComboBox.Items.Add(new WindowsUserInfo
                    {
                        Username = "",
                        DisplayName = "(No Windows account - manual entry)",
                        FullUsername = "",
                        IsAdmin = false,
                        IsEnabled = true
                    });

                    foreach (var user in windowsUsers)
                    {
                        // Skip disabled accounts
                        if (!user.IsEnabled) continue;

                        WindowsUserComboBox.Items.Add(user);
                    }

                    WindowsUserComboBox.SelectedIndex = 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not load Windows users: {ex.Message}",
                    "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void WindowsUserComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (WindowsUserComboBox.SelectedItem is WindowsUserInfo selectedUser)
            {
                // Auto-fill display name if empty
                if (string.IsNullOrWhiteSpace(DisplayNameTextBox.Text) &&
                    !string.IsNullOrEmpty(selectedUser.Username))
                {
                    DisplayNameTextBox.Text = selectedUser.DisplayName;
                }

                // Auto-select user type based on admin status
                if (selectedUser.IsAdmin)
                {
                    // Select Adult for admins
                    for (int i = 0; i < UserTypeComboBox.Items.Count; i++)
                    {
                        if (UserTypeComboBox.Items[i] is ComboBoxItem item &&
                            item.Tag?.ToString() == "Adult")
                        {
                            UserTypeComboBox.SelectedIndex = i;
                            break;
                        }
                    }
                }
            }
        }

        private void TimeLimitSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateTimeLimitText();
        }

        private void UpdateTimeLimitText()
        {
            if (TimeLimitText == null) return;

            int minutes = (int)TimeLimitSlider.Value;
            if (minutes >= 480)
            {
                TimeLimitText.Text = "Unlimited";
            }
            else if (minutes >= 60)
            {
                int hours = minutes / 60;
                int remainingMinutes = minutes % 60;
                if (remainingMinutes == 0)
                    TimeLimitText.Text = $"{hours} hour{(hours > 1 ? "s" : "")}";
                else
                    TimeLimitText.Text = $"{hours}h {remainingMinutes}m";
            }
            else
            {
                TimeLimitText.Text = $"{minutes} minutes";
            }
        }

        private void AgeTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsTextNumeric(e.Text);
        }

        private static bool IsTextNumeric(string text)
        {
            return Regex.IsMatch(text, "^[0-9]+$");
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            // Validate Windows user selection
            WindowsUserInfo selectedWindowsUser = null;
            if (WindowsUserComboBox.SelectedItem is WindowsUserInfo user)
            {
                selectedWindowsUser = user;
            }

            // Validate display name
            if (string.IsNullOrWhiteSpace(DisplayNameTextBox.Text))
            {
                MessageBox.Show("Please enter a display name.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                DisplayNameTextBox.Focus();
                return;
            }

            // Get values
            DisplayName = DisplayNameTextBox.Text.Trim();

            // Get Windows username (can be empty)
            SelectedWindowsUsername = selectedWindowsUser?.FullUsername;
            if (string.IsNullOrEmpty(SelectedWindowsUsername))
            {
                SelectedWindowsUsername = null;
            }

            // Get user type from selected item
            if (UserTypeComboBox.SelectedItem is ComboBoxItem selectedItem)
            {
                var tag = selectedItem.Tag?.ToString() ?? "Child";
                SelectedUserType = tag switch
                {
                    "Child" => UserType.Child,
                    "Teen" => UserType.Teen,
                    "Adult" => UserType.Adult,
                    "Guest" => UserType.Guest,
                    _ => UserType.Child
                };
            }

            // Get age if provided
            if (!string.IsNullOrWhiteSpace(AgeTextBox.Text))
            {
                if (int.TryParse(AgeTextBox.Text, out int age) && age > 0 && age < 120)
                {
                    Age = age;
                }
                else
                {
                    MessageBox.Show("Please enter a valid age (1-119).", "Validation Error",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    AgeTextBox.Focus();
                    return;
                }
            }

            // Get time limit
            DailyTimeLimitMinutes = (int)TimeLimitSlider.Value;
            if (DailyTimeLimitMinutes >= 480)
            {
                DailyTimeLimitMinutes = 0; // 0 means unlimited
            }

            DialogResult = true;
            Close();
        }
    }
}
