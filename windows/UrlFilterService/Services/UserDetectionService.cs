using System;
using System.Collections.Generic;
using System.DirectoryServices.AccountManagement;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Windows User Detection Service for URL Filter System
    /// Detects and identifies the current active user for per-user filtering
    /// </summary>
    public class UserDetectionService
    {
        private readonly ILogger<UserDetectionService> _logger;
        private readonly DatabaseService _databaseService;
        
        // Cache for user detection to avoid repeated WMI queries
        private DeviceUser _cachedCurrentUser;
        private DateTime _lastUserCheck;
        private readonly TimeSpan _userCacheExpiry = TimeSpan.FromMinutes(1);

        public UserDetectionService(ILogger<UserDetectionService> logger, DatabaseService databaseService)
        {
            _logger = logger;
            _databaseService = databaseService;
        }

        /// <summary>
        /// Get the current active user on the system
        /// </summary>
        public async Task<DeviceUser> GetCurrentUserAsync()
        {
            try
            {
                // Check cache first
                if (_cachedCurrentUser != null && DateTime.Now - _lastUserCheck < _userCacheExpiry)
                {
                    _logger.LogTrace($"Returning cached current user: {_cachedCurrentUser.Username}");
                    return _cachedCurrentUser;
                }

                _logger.LogDebug("Detecting current active user", "UserDetection");

                // Get current Windows user
                var windowsIdentity = WindowsIdentity.GetCurrent();
                var userPrincipal = UserPrincipal.Current;
                
                if (windowsIdentity == null)
                {
                    _logger.LogWarning("Could not detect current Windows user");
                    return null;
                }

                var username = windowsIdentity.Name;
                var displayName = userPrincipal?.DisplayName ?? ExtractUsernameFromIdentity(username);

                _logger.LogTrace($"Windows user detected: {username} ({displayName})");

                // Look up user in database
                var deviceUser = await _databaseService.GetDeviceUserByUsernameAsync(username);
                
                if (deviceUser == null)
                {
                    _logger.LogDebug($"User {username} not found in database, creating new user profile");
                    
                    // Create new user profile
                    deviceUser = new DeviceUser
                    {
                        Username = username,
                        DisplayName = displayName,
                        UserType = DetermineUserTypeFromWindowsUser(userPrincipal),
                        IsActive = true,
                        ProfileCreatedDate = DateTime.Now
                    };

                    deviceUser.Id = await _databaseService.InsertDeviceUserAsync(deviceUser);
                    _logger.LogInformation($"Created new user profile: {username} (ID: {deviceUser.Id})");
                }

                // Update cache
                _cachedCurrentUser = deviceUser;
                _lastUserCheck = DateTime.Now;

                _logger.LogDebug($"Current user identified: {deviceUser.Username} (Type: {deviceUser.UserType})");
                return deviceUser;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error detecting current user");
                return null;
            }
        }

        /// <summary>
        /// Get user from specific process ID
        /// </summary>
        public async Task<DeviceUser> GetUserFromProcessAsync(int processId)
        {
            try
            {
                _logger.LogTrace($"Getting user for process ID: {processId}");

                // Get process owner using WMI
                var processOwner = GetProcessOwner(processId);
                
                if (string.IsNullOrEmpty(processOwner))
                {
                    _logger.LogWarning($"Could not determine owner for process {processId}");
                    return await GetCurrentUserAsync(); // Fallback to current user
                }

                // Look up user in database
                var deviceUser = await _databaseService.GetDeviceUserByUsernameAsync(processOwner);
                
                if (deviceUser == null)
                {
                    _logger.LogDebug($"Process owner {processOwner} not found in database, using current user");
                    return await GetCurrentUserAsync(); // Fallback to current user
                }

                return deviceUser;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting user from process {processId}");
                return await GetCurrentUserAsync(); // Fallback to current user
            }
        }

        /// <summary>
        /// Get all active users on the system
        /// </summary>
        public async Task<List<DeviceUser>> GetActiveSystemUsersAsync()
        {
            try
            {
                _logger.LogDebug("Getting all active system users");

                var activeUsers = new List<DeviceUser>();

                // Get logged-in users from WMI
                var loggedInUsers = GetLoggedInUsers();
                
                foreach (var username in loggedInUsers)
                {
                    try
                    {
                        var deviceUser = await _databaseService.GetDeviceUserByUsernameAsync(username);
                        if (deviceUser != null && deviceUser.IsActive)
                        {
                            activeUsers.Add(deviceUser);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Error getting user data for {username}");
                    }
                }

                _logger.LogDebug($"Found {activeUsers.Count} active system users");
                return activeUsers;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active system users");
                return new List<DeviceUser>();
            }
        }

        /// <summary>
        /// Check if user has specific permission for a resource
        /// </summary>
        public async Task<bool> UserHasPermissionAsync(DeviceUser user, string permission, string resource = null)
        {
            try
            {
                if (user == null)
                {
                    return false;
                }

                // Check user type permissions
                switch (user.UserType)
                {
                    case UserType.Admin:
                        return true; // Admins have all permissions
                    
                    case UserType.Adult:
                        // Adults have most permissions except admin functions
                        return !permission.ToLower().Contains("admin");
                    
                    case UserType.Teen:
                        // Teens have limited permissions
                        var teenPermissions = new[] { "browse", "basic_access", "educational" };
                        return teenPermissions.Any(p => permission.ToLower().Contains(p));
                    
                    case UserType.Child:
                        // Children have very limited permissions
                        var childPermissions = new[] { "educational", "safe_browse" };
                        return childPermissions.Any(p => permission.ToLower().Contains(p));
                    
                    case UserType.Guest:
                        // Guests have minimal permissions
                        return permission.ToLower() == "basic_access";
                    
                    default:
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking permission {permission} for user {user?.Username}");
                return false; // Fail secure
            }
        }

        /// <summary>
        /// Start user detection service
        /// </summary>
        public void Start()
        {
            _logger.LogInformation("Starting User Detection Service");
            InvalidateUserCache();
        }

        /// <summary>
        /// Stop user detection service
        /// </summary>
        public void Stop()
        {
            _logger.LogInformation("Stopping User Detection Service");
            _cachedCurrentUser = null;
        }

        /// <summary>
        /// Invalidate user cache to force fresh detection
        /// </summary>
        public void InvalidateUserCache()
        {
            _logger.LogDebug("Invalidating user detection cache");
            _cachedCurrentUser = null;
            _lastUserCheck = DateTime.MinValue;
        }

        #region Private Methods

        private string ExtractUsernameFromIdentity(string identity)
        {
            try
            {
                // Extract username from domain\username format
                var parts = identity.Split('\\');
                return parts.Length > 1 ? parts[parts.Length - 1] : identity;
            }
            catch
            {
                return identity;
            }
        }

        private UserType DetermineUserTypeFromWindowsUser(UserPrincipal userPrincipal)
        {
            try
            {
                if (userPrincipal == null)
                {
                    return UserType.Adult; // Default
                }

                // Check if user is in Administrators group
                var adminGroup = GroupPrincipal.FindByIdentity(
                    new PrincipalContext(ContextType.Machine), 
                    IdentityType.Name, 
                    "Administrators"
                );

                if (adminGroup != null && userPrincipal.IsMemberOf(adminGroup))
                {
                    return UserType.Admin;
                }

                // For now, default to Adult
                // In a real implementation, you might check specific groups
                // or use other criteria to determine user type
                return UserType.Adult;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error determining user type from Windows user");
                return UserType.Adult; // Safe default
            }
        }

        private string GetProcessOwner(int processId)
        {
            try
            {
                var query = $"SELECT * FROM Win32_Process WHERE ProcessId = {processId}";
                using var searcher = new ManagementObjectSearcher(query);
                
                foreach (ManagementObject process in searcher.Get())
                {
                    var owner = new string[2];
                    var result = process.InvokeMethod("GetOwner", owner);
                    
                    if (owner[0] != null)
                    {
                        var domain = owner[1] ?? Environment.MachineName;
                        return $"{domain}\\{owner[0]}";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting process owner for PID {processId}");
            }

            return null;
        }

        private List<string> GetLoggedInUsers()
        {
            var users = new List<string>();
            
            try
            {
                // Get users from WMI Win32_LoggedOnUser
                var query = "SELECT * FROM Win32_LoggedOnUser";
                using var searcher = new ManagementObjectSearcher(query);
                
                foreach (ManagementObject user in searcher.Get())
                {
                    var antecedent = user["Antecedent"]?.ToString();
                    if (!string.IsNullOrEmpty(antecedent))
                    {
                        // Extract username from WMI path
                        var username = ExtractUsernameFromWMIPath(antecedent);
                        if (!string.IsNullOrEmpty(username) && !users.Contains(username))
                        {
                            users.Add(username);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting logged in users from WMI");
            }

            return users;
        }

        private string ExtractUsernameFromWMIPath(string wmiPath)
        {
            try
            {
                // WMI path format: \\MACHINE\root\cimv2:Win32_Account.Domain="DOMAIN",Name="USERNAME"
                var nameIndex = wmiPath.IndexOf("Name=\"");
                if (nameIndex >= 0)
                {
                    var start = nameIndex + 6; // Skip 'Name="'
                    var end = wmiPath.IndexOf("\"", start);
                    if (end > start)
                    {
                        return wmiPath.Substring(start, end - start);
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return null;
        }

        #endregion
    }

    /// <summary>
    /// Result of user detection operations
    /// </summary>
    public class UserDetectionResult
    {
        public bool Success { get; set; }
        public DeviceUser User { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime DetectionTime { get; set; }

        public static UserDetectionResult Successful(DeviceUser user)
        {
            return new UserDetectionResult
            {
                Success = true,
                User = user,
                DetectionTime = DateTime.Now
            };
        }

        public static UserDetectionResult Failed(string error)
        {
            return new UserDetectionResult
            {
                Success = false,
                ErrorMessage = error,
                DetectionTime = DateTime.Now
            };
        }
    }
}