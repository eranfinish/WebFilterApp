using System.Windows;
using System.Windows.Input;
using AdminControlPanel.Services;

namespace AdminControlPanel.Views
{
    public partial class LoginDialog : Window
    {
        private readonly AdminAuthenticationService _authService;

        public bool IsAuthenticated { get; private set; }

        public LoginDialog()
        {
            InitializeComponent();
            _authService = new AdminAuthenticationService();

            // Focus on password box when loaded
            Loaded += (s, e) => PasswordBox.Focus();
        }

        private void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            AttemptLogin();
        }

        private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                AttemptLogin();
            }
        }

        private void AttemptLogin()
        {
            var password = PasswordBox.Password;

            if (string.IsNullOrEmpty(password))
            {
                ShowError("Please enter your PIN or password.");
                return;
            }

            // Disable button during authentication
            LoginButton.IsEnabled = false;
            LoginButton.Content = "Verifying...";

            var result = _authService.Authenticate(password);

            if (result.Success)
            {
                IsAuthenticated = true;
                DialogResult = true;
                Close();
            }
            else
            {
                LoginButton.IsEnabled = true;
                LoginButton.Content = "Login";
                PasswordBox.Password = "";
                PasswordBox.Focus();

                if (result.IsLockedOut)
                {
                    ShowLockout(result.Message);
                    LoginButton.IsEnabled = false;
                }
                else
                {
                    ShowError(result.Message);
                }
            }
        }

        private void ForgotPassword_Click(object sender, RoutedEventArgs e)
        {
            var securityQuestion = _authService.GetSecurityQuestion();

            if (string.IsNullOrEmpty(securityQuestion))
            {
                MessageBox.Show(
                    "No security question was configured.\n\n" +
                    "Please contact another administrator or reinstall the application.",
                    "Password Recovery",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            // Show security question dialog
            var resetDialog = new PasswordResetDialog(_authService, securityQuestion);
            resetDialog.Owner = this;

            if (resetDialog.ShowDialog() == true)
            {
                MessageBox.Show(
                    "Password has been reset successfully.\n\nPlease login with your new password.",
                    "Password Reset",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                PasswordBox.Password = "";
                PasswordBox.Focus();

                // Reset lockout state visually
                ErrorBorder.Visibility = Visibility.Collapsed;
                LockoutBorder.Visibility = Visibility.Collapsed;
                LoginButton.IsEnabled = true;
            }
        }

        private void ShowError(string message)
        {
            ErrorMessage.Text = message;
            ErrorBorder.Visibility = Visibility.Visible;
            LockoutBorder.Visibility = Visibility.Collapsed;
        }

        private void ShowLockout(string message)
        {
            LockoutMessage.Text = message;
            LockoutBorder.Visibility = Visibility.Visible;
            ErrorBorder.Visibility = Visibility.Collapsed;
        }
    }
}
