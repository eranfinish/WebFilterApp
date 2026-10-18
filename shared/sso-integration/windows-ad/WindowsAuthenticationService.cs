using System;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Client;
using UrlFilter.Shared.Models;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Windows Authentication Service supporting both Active Directory and Azure AD
    /// Provides SSO capabilities for admin authentication
    /// </summary>
    public class AuthenticationService
    {
        private readonly ILogger<AuthenticationService> _logger;
        private readonly DatabaseService _databaseService;
        private IPublicClientApplication _msalApp;
        private readonly string[] _scopes = { "User.Read" };

        public AuthenticationService(ILogger<AuthenticationService> logger, DatabaseService databaseService)
        {
            _logger = logger;
            _databaseService = databaseService;
            InitializeMsalApp();
        }

        private void InitializeMsalApp()
        {
            try
            {
                // Initialize MSAL for Azure AD authentication
                _msalApp = PublicClientApplicationBuilder
                    .Create("your-azure-ad-client-id") // Replace with actual client ID
                    .WithAuthority("https://login.microsoftonline.com/your-tenant-id") // Replace with tenant ID
                    .WithRedirectUri("http://localhost")
                    .Build();

                _logger.LogInformation("MSAL application initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MSAL application");
            }
        }

        /// <summary>
        /// Authenticates user using Windows Integrated Authentication
        /// </summary>
        [SupportedOSPlatform("windows")]
        public async Task<AuthenticationResult> AuthenticateWindowsUserAsync()
        {
            try
            {
                // Get current Windows user
                var windowsIdentity = WindowsIdentity.GetCurrent();
                if (windowsIdentity == null || !windowsIdentity.IsAuthenticated)
                {
                    return AuthenticationResult.Failed("Windows user not authenticated");
                }

                string username = windowsIdentity.Name;
                _logger.LogInformation($"Authenticating Windows user: {username}");

                // Check if user is in admin groups
                bool isAdmin = await IsUserInAdminGroupAsync(windowsIdentity);
                if (!isAdmin)
                {
                    return AuthenticationResult.Failed("User is not authorized as administrator");
                }

                // Get or create admin user record
                var adminUser = await GetOrCreateAdminUserAsync(username, AuthProvider.WindowsAD, windowsIdentity.User?.Value);

                // Create authentication session
                var session = await CreateAuthSessionAsync(adminUser);

                return AuthenticationResult.Success(adminUser, session.SessionToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Windows authentication");
                return AuthenticationResult.Failed($"Authentication error: {ex.Message}");
            }
        }

        /// <summary>
        /// Authenticates user using Azure Active Directory
        /// </summary>
        public async Task<AuthenticationResult> AuthenticateAzureAdUserAsync()
        {
            try
            {
                if (_msalApp == null)
                {
                    return AuthenticationResult.Failed("Azure AD authentication not configured");
                }

                var accounts = await _msalApp.GetAccountsAsync();
                Microsoft.Identity.Client.AuthenticationResult result = null;

                try
                {
                    // Try silent authentication first
                    result = await _msalApp.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                                          .ExecuteAsync();
                }
                catch (MsalUiRequiredException)
                {
                    try
                    {
                        // Interactive authentication required
                        result = await _msalApp.AcquireTokenInteractive(_scopes)
                                               .WithAccount(accounts.FirstOrDefault())
                                               .WithPrompt(Prompt.SelectAccount)
                                               .ExecuteAsync();
                    }
                    catch (MsalException msalEx)
                    {
                        _logger.LogError(msalEx, "Azure AD interactive authentication failed");
                        return AuthenticationResult.Failed($"Azure AD authentication failed: {msalEx.Message}");
                    }
                }

                if (result?.Account == null)
                {
                    return AuthenticationResult.Failed("Azure AD authentication failed");
                }

                _logger.LogInformation($"Azure AD user authenticated: {result.Account.Username}");

                // Check admin permissions (this could be role-based or group-based)
                bool isAdmin = await IsAzureAdUserAdminAsync(result);
                if (!isAdmin)
                {
                    return AuthenticationResult.Failed("User is not authorized as administrator");
                }

                // Get or create admin user record
                var adminUser = await GetOrCreateAdminUserAsync(
                    result.Account.Username, 
                    AuthProvider.AzureAD, 
                    result.Account.HomeAccountId.ObjectId);

                // Create authentication session
                var session = await CreateAuthSessionAsync(adminUser);

                return AuthenticationResult.Success(adminUser, session.SessionToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Azure AD authentication");
                return AuthenticationResult.Failed($"Azure AD authentication error: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates an existing authentication session
        /// </summary>
        public async Task<bool> ValidateSessionAsync(string sessionToken)
        {
            try
            {
                if (string.IsNullOrEmpty(sessionToken))
                {
                    return false;
                }

                var session = await _databaseService.GetAuthSessionByTokenAsync(sessionToken);
                
                if (session == null || !session.IsValid || session.ExpiresAt < DateTime.UtcNow)
                {
                    if (session != null)
                    {
                        // Invalidate expired session
                        session.IsValid = false;
                        await _databaseService.UpdateAuthSessionAsync(session);
                    }
                    return false;
                }

                // Update last activity
                session.LastActivity = DateTime.UtcNow;
                await _databaseService.UpdateAuthSessionAsync(session);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating session");
                return false;
            }
        }

        /// <summary>
        /// Gets admin user from session token
        /// </summary>
        public async Task<AdminUser> GetAdminUserFromSessionAsync(string sessionToken)
        {
            try
            {
                var session = await _databaseService.GetAuthSessionByTokenAsync(sessionToken);
                if (session?.IsValid == true && session.ExpiresAt > DateTime.UtcNow)
                {
                    return await _databaseService.GetAdminUserAsync(session.UserId);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting admin user from session");
                return null;
            }
        }

        /// <summary>
        /// Invalidates user session (logout)
        /// </summary>
        public async Task<bool> InvalidateSessionAsync(string sessionToken)
        {
            try
            {
                var session = await _databaseService.GetAuthSessionByTokenAsync(sessionToken);
                if (session != null)
                {
                    session.IsValid = false;
                    await _databaseService.UpdateAuthSessionAsync(session);
                    
                    _logger.LogInformation($"Session invalidated for user {session.UserId}");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error invalidating session");
                return false;
            }
        }

        [SupportedOSPlatform("windows")]
        private async Task<bool> IsUserInAdminGroupAsync(WindowsIdentity identity)
        {
            try
            {
                // Check if user is in local administrators group
                var principal = new WindowsPrincipal(identity);
                if (principal.IsInRole(WindowsBuiltInRole.Administrator))
                {
                    return true;
                }

                // Check domain groups using PrincipalContext
                if (identity.Name.Contains("\\"))
                {
                    string[] parts = identity.Name.Split('\\');
                    string domain = parts[0];
                    string username = parts[1];

                    using (var context = new PrincipalContext(ContextType.Domain, domain))
                    {
                        var user = UserPrincipal.FindByIdentity(context, username);
                        
                        if (user != null)
                        {
                            // Check specific admin groups
                            var adminGroups = new[] { "Domain Admins", "IT Administrators", "URL Filter Admins" };
                            
                            foreach (var groupName in adminGroups)
                            {
                                var group = GroupPrincipal.FindByIdentity(context, groupName);
                                if (group != null && user.IsMemberOf(group))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking user admin status");
                return false;
            }
        }

        private async Task<bool> IsAzureAdUserAdminAsync(Microsoft.Identity.Client.AuthenticationResult authResult)
        {
            try
            {
                // In a real implementation, you would:
                // 1. Query Microsoft Graph API to get user roles/groups
                // 2. Check if user has admin roles assigned
                // 3. Validate against your application's admin group/role

                // For now, simplified check based on configuration
                var adminEmails = await _databaseService.GetConfigValueAsync("azure_ad_admin_emails", "");
                var adminList = adminEmails.Split(',', StringSplitOptions.RemoveEmptyEntries);
                
                return adminList.Any(email => 
                    string.Equals(email.Trim(), authResult.Account.Username, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking Azure AD user admin status");
                return false;
            }
        }

        private async Task<AdminUser> GetOrCreateAdminUserAsync(string userIdentifier, AuthProvider provider, string providerId = null)
        {
            try
            {
                // Try to find existing admin user
                var adminUser = await _databaseService.GetAdminUserByIdentifierAsync(userIdentifier);
                
                if (adminUser == null)
                {
                    // Create new admin user
                    adminUser = new AdminUser
                    {
                        UserIdentifier = userIdentifier,
                        DisplayName = ExtractDisplayName(userIdentifier),
                        Email = ExtractEmail(userIdentifier),
                        AuthenticationProvider = provider,
                        PermissionLevel = PermissionLevel.Admin, // Default level
                        CreatedDate = DateTime.UtcNow,
                        IsActive = true
                    };

                    await _databaseService.InsertAdminUserAsync(adminUser);
                    _logger.LogInformation($"Created new admin user: {userIdentifier}");
                }
                else
                {
                    // Update last login
                    adminUser.LastLogin = DateTime.UtcNow;
                    await _databaseService.UpdateAdminUserAsync(adminUser);
                }

                return adminUser;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting/creating admin user: {userIdentifier}");
                throw;
            }
        }

        private async Task<AuthSession> CreateAuthSessionAsync(AdminUser adminUser)
        {
            try
            {
                var session = new AuthSession
                {
                    UserId = adminUser.Id,
                    SessionToken = GenerateSessionToken(),
                    LoginTimestamp = DateTime.UtcNow,
                    LastActivity = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddHours(8), // 8-hour session
                    IsValid = true,
                    DeviceInfo = Environment.MachineName,
                    IpAddress = GetLocalIpAddress()
                };

                await _databaseService.InsertAuthSessionAsync(session);
                _logger.LogInformation($"Created auth session for user {adminUser.UserIdentifier}");

                return session;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating auth session");
                throw;
            }
        }

        private string GenerateSessionToken()
        {
            return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        }

        private string ExtractDisplayName(string userIdentifier)
        {
            if (userIdentifier.Contains("\\"))
            {
                return userIdentifier.Split('\\')[1];
            }
            
            if (userIdentifier.Contains("@"))
            {
                return userIdentifier.Split('@')[0];
            }

            return userIdentifier;
        }

        private string ExtractEmail(string userIdentifier)
        {
            if (userIdentifier.Contains("@"))
            {
                return userIdentifier;
            }

            // For domain users, try to construct email
            if (userIdentifier.Contains("\\"))
            {
                var parts = userIdentifier.Split('\\');
                return $"{parts[1]}@{parts[0]}.local";
            }

            return null;
        }

        private string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                var localIp = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return localIp?.ToString() ?? "127.0.0.1";
            }
            catch
            {
                return "127.0.0.1";
            }
        }
    }

    /// <summary>
    /// Authentication result class
    /// </summary>
    public class AuthenticationResult
    {
        public bool IsSuccess { get; private set; }
        public string ErrorMessage { get; private set; }
        public AdminUser AdminUser { get; private set; }
        public string SessionToken { get; private set; }

        private AuthenticationResult(bool success, string errorMessage, AdminUser adminUser, string sessionToken)
        {
            IsSuccess = success;
            ErrorMessage = errorMessage;
            AdminUser = adminUser;
            SessionToken = sessionToken;
        }

        public static AuthenticationResult Success(AdminUser adminUser, string sessionToken)
        {
            return new AuthenticationResult(true, null, adminUser, sessionToken);
        }

        public static AuthenticationResult Failed(string errorMessage)
        {
            return new AuthenticationResult(false, errorMessage, null, null);
        }
    }
}