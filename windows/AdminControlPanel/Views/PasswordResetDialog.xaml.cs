using System.Windows;
using AdminControlPanel.Services;

namespace AdminControlPanel.Views
{
    public partial class PasswordResetDialog : Window
    {
        private readonly AdminAuthenticationService _authService;

        public PasswordResetDialog(AdminAuthenticationService authService, string securityQuestion)
        {
            InitializeComponent();
            _authService = authService;

            SecurityQuestionText.Text = securityQuestion;

            // Focus on answer box when loaded
            Loaded += (s, e) => SecurityAnswerBox.Focus();
        }

        private void ResetPassword_Click(object sender, RoutedEventArgs e)
        {
            var answer = SecurityAnswerBox.Text;
            var newPassword = NewPasswordBox.Password;
            var confirmPassword = ConfirmPasswordBox.Password;

            // Validate answer
            if (string.IsNullOrWhiteSpace(answer))
            {
                ShowError("Please enter your security answer.");
                SecurityAnswerBox.Focus();
                return;
            }

            // Verify security answer
            if (!_authService.VerifySecurityAnswer(answer))
            {
                ShowError("Incorrect security answer. Please try again.");
                SecurityAnswerBox.SelectAll();
                SecurityAnswerBox.Focus();
                return;
            }

            // Validate new password
            if (string.IsNullOrWhiteSpace(newPassword))
            {
                ShowError("Please enter a new PIN/password.");
                NewPasswordBox.Focus();
                return;
            }

            if (newPassword.Length < 4)
            {
                ShowError("PIN/password must be at least 4 characters.");
                NewPasswordBox.Focus();
                return;
            }

            if (newPassword != confirmPassword)
            {
                ShowError("Passwords do not match. Please try again.");
                ConfirmPasswordBox.Password = "";
                ConfirmPasswordBox.Focus();
                return;
            }

            // Reset password
            if (_authService.ResetPassword(newPassword))
            {
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError("Failed to reset password. Please try again.");
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowError(string message)
        {
            ErrorMessage.Text = message;
            ErrorBorder.Visibility = Visibility.Visible;
        }
    }
}
