using System.Windows;
using System.Windows.Controls;
using AdminControlPanel.Services;

namespace AdminControlPanel.Views
{
    public partial class AdminSetupDialog : Window
    {
        private readonly AdminAuthenticationService _authService;

        public bool SetupCompleted { get; private set; }

        public AdminSetupDialog()
        {
            InitializeComponent();
            _authService = new AdminAuthenticationService();

            // Select first security question by default
            SecurityQuestionCombo.SelectedIndex = 0;

            // Focus on password box when loaded
            Loaded += (s, e) => PasswordBox.Focus();
        }

        private void CompleteSetup_Click(object sender, RoutedEventArgs e)
        {
            // Validate inputs
            var password = PasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;

            if (string.IsNullOrWhiteSpace(password))
            {
                ShowError("Please enter a PIN or password.");
                PasswordBox.Focus();
                return;
            }

            if (password.Length < 4)
            {
                ShowError("PIN/password must be at least 4 characters.");
                PasswordBox.Focus();
                return;
            }

            if (password != confirmPassword)
            {
                ShowError("Passwords do not match. Please try again.");
                ConfirmPasswordBox.Password = "";
                ConfirmPasswordBox.Focus();
                return;
            }

            // Get security question and answer
            string securityQuestion = null;
            string securityAnswer = null;

            if (SecurityQuestionCombo.SelectedItem is ComboBoxItem selectedItem)
            {
                securityQuestion = selectedItem.Content.ToString();
            }

            if (!string.IsNullOrWhiteSpace(SecurityAnswerBox.Text))
            {
                securityAnswer = SecurityAnswerBox.Text;
            }

            // Setup credentials
            var success = _authService.SetupAdminCredentials(password, securityQuestion, securityAnswer);

            if (success)
            {
                SetupCompleted = true;

                MessageBox.Show(
                    "Admin protection has been set up successfully!\n\n" +
                    "You will need to enter this PIN/password each time you open the control panel.\n\n" +
                    "Keep it safe and don't share it with children!",
                    "Setup Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                DialogResult = true;
                Close();
            }
            else
            {
                ShowError("Failed to save credentials. Please try again.");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to cancel setup?\n\n" +
                "Without admin protection, anyone can access and modify the filter settings.",
                "Cancel Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                DialogResult = false;
                Close();
            }
        }

        private void ShowError(string message)
        {
            ErrorMessage.Text = message;
            ErrorBorder.Visibility = Visibility.Visible;
        }
    }
}
