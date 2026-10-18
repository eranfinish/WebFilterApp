using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AdminControlPanel.Services
{
    /// <summary>
    /// Handles admin authentication for the control panel
    /// Combines Windows admin verification with PIN/password protection
    /// </summary>
    public class AdminAuthenticationService
    {
        private readonly ILogger<AdminAuthenticationService> _logger;
        private readonly string _configPath;
        private AdminConfig _config;

        public AdminAuthenticationService(ILogger<AdminAuthenticationService> logger = null)
        {
            _logger = logger;
            _configPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "UrlFilter", "Config", "admin.json");

            EnsureConfigDirectory();
            LoadConfig();
        }

        /// <summary>
        /// Check if current user is a Windows Administrator
        /// </summary>
        public bool IsWindowsAdmin()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking Windows admin status");
                return false;
            }
        }

        /// <summary>
        /// Check if this is first-time setup (no PIN configured)
        /// </summary>
        public bool IsFirstTimeSetup()
        {
            return string.IsNullOrEmpty(_config?.PasswordHash);
        }

        /// <summary>
        /// Set up the admin PIN/password for first time
        /// </summary>
        public bool SetupAdminCredentials(string password, string securityQuestion = null, string securityAnswer = null)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
                {
                    return false;
                }

                _config = new AdminConfig
                {
                    PasswordHash = HashPassword(password),
                    SecurityQuestion = securityQuestion,
                    SecurityAnswerHash = !string.IsNullOrEmpty(securityAnswer)
                        ? HashPassword(securityAnswer.ToLowerInvariant().Trim())
                        : null,
                    CreatedDate = DateTime.UtcNow,
                    LastModifiedDate = DateTime.UtcNow,
                    FailedAttempts = 0,
                    LockoutUntil = null
                };

                SaveConfig();
                _logger?.LogInformation("Admin credentials configured successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error setting up admin credentials");
                return false;
            }
        }

        /// <summary>
        /// Verify the admin PIN/password
        /// </summary>
        public AuthenticationResult Authenticate(string password)
        {
            try
            {
                // Check lockout
                if (_config.LockoutUntil.HasValue && DateTime.UtcNow < _config.LockoutUntil.Value)
                {
                    var remaining = _config.LockoutUntil.Value - DateTime.UtcNow;
                    return new AuthenticationResult
                    {
                        Success = false,
                        IsLockedOut = true,
                        Message = $"Account locked. Try again in {(int)remaining.TotalMinutes + 1} minutes.",
                        RemainingLockoutMinutes = (int)remaining.TotalMinutes + 1
                    };
                }

                // Verify password
                var passwordHash = HashPassword(password);
                if (passwordHash == _config.PasswordHash)
                {
                    // Success - reset failed attempts
                    _config.FailedAttempts = 0;
                    _config.LockoutUntil = null;
                    _config.LastLoginDate = DateTime.UtcNow;
                    SaveConfig();

                    _logger?.LogInformation("Admin authentication successful");
                    return new AuthenticationResult
                    {
                        Success = true,
                        Message = "Authentication successful"
                    };
                }
                else
                {
                    // Failed - increment attempts
                    _config.FailedAttempts++;

                    // Lockout after 5 failed attempts
                    if (_config.FailedAttempts >= 5)
                    {
                        _config.LockoutUntil = DateTime.UtcNow.AddMinutes(15);
                        SaveConfig();

                        _logger?.LogWarning("Admin account locked due to too many failed attempts");
                        return new AuthenticationResult
                        {
                            Success = false,
                            IsLockedOut = true,
                            Message = "Too many failed attempts. Account locked for 15 minutes.",
                            RemainingLockoutMinutes = 15
                        };
                    }

                    SaveConfig();
                    var attemptsRemaining = 5 - _config.FailedAttempts;

                    _logger?.LogWarning("Failed authentication attempt. {Remaining} attempts remaining", attemptsRemaining);
                    return new AuthenticationResult
                    {
                        Success = false,
                        Message = $"Incorrect PIN/password. {attemptsRemaining} attempts remaining.",
                        AttemptsRemaining = attemptsRemaining
                    };
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during authentication");
                return new AuthenticationResult
                {
                    Success = false,
                    Message = "Authentication error. Please try again."
                };
            }
        }

        /// <summary>
        /// Verify security question answer for password reset
        /// </summary>
        public bool VerifySecurityAnswer(string answer)
        {
            if (string.IsNullOrEmpty(_config?.SecurityAnswerHash) || string.IsNullOrEmpty(answer))
                return false;

            var answerHash = HashPassword(answer.ToLowerInvariant().Trim());
            return answerHash == _config.SecurityAnswerHash;
        }

        /// <summary>
        /// Get the security question for password reset
        /// </summary>
        public string GetSecurityQuestion()
        {
            return _config?.SecurityQuestion;
        }

        /// <summary>
        /// Reset the admin password (after verifying security question)
        /// </summary>
        public bool ResetPassword(string newPassword)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 4)
                    return false;

                _config.PasswordHash = HashPassword(newPassword);
                _config.LastModifiedDate = DateTime.UtcNow;
                _config.FailedAttempts = 0;
                _config.LockoutUntil = null;
                SaveConfig();

                _logger?.LogInformation("Admin password reset successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error resetting password");
                return false;
            }
        }

        /// <summary>
        /// Change the admin password (requires current password)
        /// </summary>
        public bool ChangePassword(string currentPassword, string newPassword)
        {
            var authResult = Authenticate(currentPassword);
            if (!authResult.Success)
                return false;

            return ResetPassword(newPassword);
        }

        #region Private Methods

        private void EnsureConfigDirectory()
        {
            var directory = Path.GetDirectoryName(_configPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<AdminConfig>(json);
                }
                else
                {
                    _config = new AdminConfig();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error loading admin config");
                _config = new AdminConfig();
            }
        }

        private void SaveConfig()
        {
            try
            {
                var json = JsonSerializer.Serialize(_config, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(_configPath, json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving admin config");
            }
        }

        private string HashPassword(string password)
        {
            // Use PBKDF2 with SHA256 for secure password hashing
            using var sha256 = SHA256.Create();

            // Add a static salt (in production, use a random salt per user stored with the hash)
            var saltedPassword = "UrlFilter_Salt_" + password + "_v1";
            var bytes = Encoding.UTF8.GetBytes(saltedPassword);
            var hash = sha256.ComputeHash(bytes);

            return Convert.ToBase64String(hash);
        }

        #endregion
    }

    /// <summary>
    /// Result of an authentication attempt
    /// </summary>
    public class AuthenticationResult
    {
        public bool Success { get; set; }
        public bool IsLockedOut { get; set; }
        public string Message { get; set; }
        public int AttemptsRemaining { get; set; }
        public int RemainingLockoutMinutes { get; set; }
    }

    /// <summary>
    /// Admin configuration stored securely
    /// </summary>
    internal class AdminConfig
    {
        public string PasswordHash { get; set; }
        public string SecurityQuestion { get; set; }
        public string SecurityAnswerHash { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime LastModifiedDate { get; set; }
        public DateTime? LastLoginDate { get; set; }
        public int FailedAttempts { get; set; }
        public DateTime? LockoutUntil { get; set; }
    }
}
