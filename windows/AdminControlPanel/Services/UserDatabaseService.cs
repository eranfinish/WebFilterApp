using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.DirectoryServices.AccountManagement;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace AdminControlPanel.Services
{
    /// <summary>
    /// Service for managing user data in SQLite database
    /// </summary>
    public class UserDatabaseService
    {
        private readonly ILogger<UserDatabaseService> _logger;
        private readonly string _connectionString;
        private const string DatabaseFileName = "urlfilter.db";

        public UserDatabaseService(ILogger<UserDatabaseService> logger)
        {
            _logger = logger;
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), 
                                     "UrlFilter", DatabaseFileName);
            _connectionString = $"Data Source={dbPath};Version=3;";
            
            EnsureDatabaseExists();
        }

        /// <summary>
        /// Ensure database and tables exist
        /// </summary>
        private void EnsureDatabaseExists()
        {
            try
            {
                var dbDir = Path.GetDirectoryName(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UrlFilter"));
                if (!Directory.Exists(dbDir))
                {
                    Directory.CreateDirectory(dbDir!);
                }

                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();
                
                CreateTables(connection);
                CreateSampleData(connection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database");
            }
        }

        /// <summary>
        /// Create database tables
        /// </summary>
        private void CreateTables(SQLiteConnection connection)
        {
            var createTablesScript = @"
                CREATE TABLE IF NOT EXISTS device_users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT UNIQUE NOT NULL,
                    windows_username TEXT,
                    display_name TEXT NOT NULL,
                    user_type TEXT CHECK(user_type IN ('Adult', 'Teen', 'Child', 'Guest', 'Admin')) DEFAULT 'Adult',
                    age INTEGER,
                    is_active BOOLEAN DEFAULT 1,
                    profile_created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    avatar_path TEXT
                );

                CREATE TABLE IF NOT EXISTS user_time_restrictions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER REFERENCES device_users(id),
                    day_of_week INTEGER CHECK(day_of_week BETWEEN 0 AND 6),
                    allowed_start_time TIME,
                    allowed_end_time TIME,
                    daily_limit_minutes INTEGER,
                    is_active BOOLEAN DEFAULT 1
                );

                CREATE TABLE IF NOT EXISTS daily_usage_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER REFERENCES device_users(id),
                    usage_date DATE,
                    minutes_used INTEGER DEFAULT 0,
                    last_activity_time TIMESTAMP,
                    sessions_count INTEGER DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS system_settings (
                    key TEXT PRIMARY KEY,
                    value TEXT,
                    modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );

                CREATE TABLE IF NOT EXISTS url_lists (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    url_pattern TEXT NOT NULL,
                    list_type TEXT CHECK(list_type IN ('Whitelist', 'Blacklist')) NOT NULL,
                    category TEXT,
                    age_appropriate TEXT CHECK(age_appropriate IN ('AllAges', 'TeenPlus', 'AdultOnly')) DEFAULT 'AllAges',
                    added_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    added_by INTEGER,
                    is_active BOOLEAN DEFAULT 1,
                    description TEXT,
                    match_type TEXT CHECK(match_type IN ('exact', 'wildcard', 'regex', 'domain')) DEFAULT 'domain'
                );

                CREATE TABLE IF NOT EXISTS user_url_permissions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER REFERENCES device_users(id),
                    url_pattern TEXT NOT NULL,
                    permission_type TEXT CHECK(permission_type IN ('Allow', 'Block', 'Inherit')) DEFAULT 'Inherit',
                    category TEXT,
                    granted_by INTEGER,
                    granted_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    expires_date TIMESTAMP,
                    notes TEXT,
                    is_active BOOLEAN DEFAULT 1
                );
            ";

            using var command = new SQLiteCommand(createTablesScript, connection);
            command.ExecuteNonQuery();
        }

        /// <summary>
        /// Create sample family data
        /// </summary>
        private void CreateSampleData(SQLiteConnection connection)
        {
            // Check if we already have users
            using var checkCmd = new SQLiteCommand("SELECT COUNT(*) FROM device_users", connection);
            var userCount = Convert.ToInt32(checkCmd.ExecuteScalar());
            
            if (userCount == 0)
            {
                var sampleDataScript = @"
                    INSERT INTO device_users (username, display_name, user_type, age, is_active) VALUES
                    ('emma_johnson', 'Emma', 'Child', 8, 1),
                    ('jake_johnson', 'Jake', 'Teen', 15, 1),
                    ('sarah_johnson', 'Sarah', 'Adult', 42, 1);

                    INSERT INTO user_time_restrictions (user_id, day_of_week, allowed_start_time, allowed_end_time, daily_limit_minutes, is_active) VALUES
                    (1, 1, '15:00', '19:00', 120, 1), -- Emma Monday
                    (1, 2, '15:00', '19:00', 120, 1), -- Emma Tuesday
                    (1, 6, '09:00', '20:00', 180, 1), -- Emma Saturday
                    (2, 1, '16:00', '21:00', 180, 1), -- Jake Monday
                    (2, 2, '16:00', '21:00', 180, 1), -- Jake Tuesday
                    (2, 6, '10:00', '22:00', 300, 1); -- Jake Saturday

                    INSERT INTO daily_usage_log (user_id, usage_date, minutes_used, sessions_count) VALUES
                    (1, date('now'), 75, 3),
                    (2, date('now'), 0, 0),
                    (3, date('now'), 45, 2);

                    INSERT INTO system_settings (key, value) VALUES
                    ('filtering_enabled', 'true'),
                    ('emergency_unlock', 'false'),
                    ('homework_mode_active', 'false');

                    INSERT INTO url_lists (url_pattern, list_type, category, description, match_type) VALUES
                    ('facebook.com', 'Blacklist', 'Social Media', 'Facebook social network', 'domain'),
                    ('instagram.com', 'Blacklist', 'Social Media', 'Instagram photo sharing', 'domain'),
                    ('tiktok.com', 'Blacklist', 'Social Media', 'TikTok video platform', 'domain'),
                    ('*.gambling.com', 'Blacklist', 'Gambling', 'All gambling sites', 'wildcard'),
                    ('*.adult.*', 'Blacklist', 'Adult Content', 'Adult content sites', 'wildcard'),
                    ('khanacademy.org', 'Whitelist', 'Education', 'Khan Academy educational platform', 'domain'),
                    ('wikipedia.org', 'Whitelist', 'Education', 'Wikipedia encyclopedia', 'domain'),
                    ('nationalgeographic.com', 'Whitelist', 'Education', 'National Geographic educational content', 'domain'),
                    ('pbs.org', 'Whitelist', 'Education', 'PBS educational content', 'domain'),
                    ('*.edu', 'Whitelist', 'Education', 'Educational institutions', 'wildcard');
                ";

                using var command = new SQLiteCommand(sampleDataScript, connection);
                command.ExecuteNonQuery();
                
                _logger.LogInformation("Sample family data created");
            }
        }

        /// <summary>
        /// Get all family members
        /// </summary>
        public async Task<List<DeviceUser>> GetAllUsersAsync()
        {
            var users = new List<DeviceUser>();
            
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                var sql = @"SELECT u.id, u.username, u.windows_username, u.display_name, u.user_type,
                                  u.age, u.is_active, u.profile_created_date, u.avatar_path,
                                  COALESCE(d.minutes_used, 0) as today_minutes,
                                  r.daily_limit_minutes
                           FROM device_users u
                           LEFT JOIN daily_usage_log d ON u.id = d.user_id AND d.usage_date = date('now')
                           LEFT JOIN user_time_restrictions r ON u.id = r.user_id AND r.day_of_week = strftime('%w', 'now')
                           WHERE u.is_active = 1
                           ORDER BY u.user_type, u.age";
                
                using var command = new SQLiteCommand(sql, connection);
                using var reader = await command.ExecuteReaderAsync();
                
                while (await reader.ReadAsync())
                {
                    users.Add(new DeviceUser
                    {
                        Id = Convert.ToInt32(reader["id"]),
                        Username = reader["username"].ToString()!,
                        WindowsUsername = reader["windows_username"] == DBNull.Value ? null : reader["windows_username"].ToString(),
                        DisplayName = reader["display_name"].ToString()!,
                        UserType = Enum.Parse<UserType>(reader["user_type"].ToString()!),
                        Age = reader["age"] == DBNull.Value ? null : Convert.ToInt32(reader["age"]),
                        IsActive = Convert.ToBoolean(reader["is_active"]),
                        ProfileCreatedDate = Convert.ToDateTime(reader["profile_created_date"]),
                        TodayMinutesUsed = reader["today_minutes"] == DBNull.Value ? 0 : Convert.ToInt32(reader["today_minutes"]),
                        DailyLimitMinutes = reader["daily_limit_minutes"] == DBNull.Value ? null : Convert.ToInt32(reader["daily_limit_minutes"])
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get users");
            }
            
            return users;
        }

        /// <summary>
        /// Get all Windows user accounts on this computer
        /// </summary>
        public List<WindowsUserInfo> GetWindowsUsers()
        {
            var users = new List<WindowsUserInfo>();

            try
            {
                using var context = new PrincipalContext(ContextType.Machine);
                using var searcher = new PrincipalSearcher(new UserPrincipal(context));

                foreach (var result in searcher.FindAll())
                {
                    if (result is UserPrincipal user)
                    {
                        // Skip built-in accounts
                        if (user.Sid.Value.StartsWith("S-1-5-21-") == false)
                            continue;

                        // Check if admin
                        bool isAdmin = false;
                        try
                        {
                            using var adminGroup = GroupPrincipal.FindByIdentity(context, "Administrators");
                            if (adminGroup != null)
                            {
                                isAdmin = user.IsMemberOf(adminGroup);
                            }
                        }
                        catch { }

                        users.Add(new WindowsUserInfo
                        {
                            Username = user.SamAccountName,
                            DisplayName = user.DisplayName ?? user.SamAccountName,
                            FullUsername = $"{Environment.MachineName}\\{user.SamAccountName}",
                            IsAdmin = isAdmin,
                            IsEnabled = user.Enabled ?? true
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to enumerate Windows users");
            }

            return users.OrderBy(u => u.Username).ToList();
        }

        /// <summary>
        /// Add new family member with Windows username
        /// </summary>
        public async Task<DeviceUser?> AddUserAsync(string displayName, UserType userType, int? age = null, string windowsUsername = null)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var username = displayName.ToLower().Replace(" ", "_") + "_" + DateTime.Now.Ticks;
                var sql = @"INSERT INTO device_users (username, windows_username, display_name, user_type, age, is_active)
                           VALUES (@username, @windowsUsername, @displayName, @userType, @age, 1);
                           SELECT last_insert_rowid();";

                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@username", username);
                command.Parameters.AddWithValue("@windowsUsername", windowsUsername ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@displayName", displayName);
                command.Parameters.AddWithValue("@userType", userType.ToString());
                command.Parameters.AddWithValue("@age", age.HasValue ? (object)age.Value : DBNull.Value);

                var userId = Convert.ToInt32(await command.ExecuteScalarAsync());

                _logger.LogInformation("Added new user: {DisplayName} (Windows: {WindowsUser}, ID: {UserId})",
                    displayName, windowsUsername ?? "none", userId);

                return new DeviceUser
                {
                    Id = userId,
                    Username = username,
                    WindowsUsername = windowsUsername,
                    DisplayName = displayName,
                    UserType = userType,
                    Age = age,
                    IsActive = true,
                    ProfileCreatedDate = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add user: {DisplayName}", displayName);
                return null;
            }
        }

        /// <summary>
        /// Update user time limit
        /// </summary>
        public async Task<bool> UpdateUserTimeLimitAsync(int userId, int dailyLimitMinutes)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                var sql = @"INSERT OR REPLACE INTO user_time_restrictions 
                           (user_id, day_of_week, daily_limit_minutes, is_active) 
                           VALUES (@userId, strftime('%w', 'now'), @dailyLimit, 1)";
                
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@userId", userId);
                command.Parameters.AddWithValue("@dailyLimit", dailyLimitMinutes);
                
                await command.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update time limit for user {UserId}", userId);
                return false;
            }
        }

        /// <summary>
        /// Get system setting
        /// </summary>
        public async Task<string?> GetSystemSettingAsync(string key)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                using var command = new SQLiteCommand("SELECT value FROM system_settings WHERE key = @key", connection);
                command.Parameters.AddWithValue("@key", key);
                
                return await command.ExecuteScalarAsync() as string;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get system setting: {Key}", key);
                return null;
            }
        }

        /// <summary>
        /// Set system setting
        /// </summary>
        public async Task<bool> SetSystemSettingAsync(string key, string value)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                var sql = @"INSERT OR REPLACE INTO system_settings (key, value, modified_date) 
                           VALUES (@key, @value, CURRENT_TIMESTAMP)";
                
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", value);
                
                await command.ExecuteNonQueryAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set system setting: {Key}", key);
                return false;
            }
        }

        /// <summary>
        /// Add URL to blacklist or whitelist
        /// </summary>
        public async Task<bool> AddUrlToListAsync(string urlPattern, string listType, string category = null, string description = null, string matchType = "domain")
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                var sql = @"INSERT INTO url_lists (url_pattern, list_type, category, description, match_type, is_active) 
                           VALUES (@urlPattern, @listType, @category, @description, @matchType, 1)";
                
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@urlPattern", urlPattern);
                command.Parameters.AddWithValue("@listType", listType);
                command.Parameters.AddWithValue("@category", category ?? "Custom");
                command.Parameters.AddWithValue("@description", description ?? $"{listType} rule for {urlPattern}");
                command.Parameters.AddWithValue("@matchType", matchType);
                
                await command.ExecuteNonQueryAsync();
                _logger.LogInformation("Added URL to {ListType}: {UrlPattern}", listType, urlPattern);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add URL to {ListType}: {UrlPattern}", listType, urlPattern);
                return false;
            }
        }

        /// <summary>
        /// Remove URL from lists
        /// </summary>
        public async Task<bool> RemoveUrlFromListAsync(string urlPattern, string listType)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                var sql = @"UPDATE url_lists SET is_active = 0 
                           WHERE url_pattern = @urlPattern AND list_type = @listType";
                
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@urlPattern", urlPattern);
                command.Parameters.AddWithValue("@listType", listType);
                
                var rowsAffected = await command.ExecuteNonQueryAsync();
                if (rowsAffected > 0)
                {
                    _logger.LogInformation("Removed URL from {ListType}: {UrlPattern}", listType, urlPattern);
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to remove URL from {ListType}: {UrlPattern}", listType, urlPattern);
                return false;
            }
        }

        /// <summary>
        /// Get all URLs in whitelist or blacklist
        /// </summary>
        public async Task<List<UrlListItem>> GetUrlListAsync(string listType = null)
        {
            var urlList = new List<UrlListItem>();
            
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                var sql = listType != null 
                    ? @"SELECT * FROM url_lists WHERE list_type = @listType AND is_active = 1 ORDER BY category, url_pattern"
                    : @"SELECT * FROM url_lists WHERE is_active = 1 ORDER BY list_type, category, url_pattern";
                
                using var command = new SQLiteCommand(sql, connection);
                if (listType != null)
                {
                    command.Parameters.AddWithValue("@listType", listType);
                }
                
                using var reader = await command.ExecuteReaderAsync();
                
                while (await reader.ReadAsync())
                {
                    urlList.Add(new UrlListItem
                    {
                        Id = Convert.ToInt32(reader["id"]),
                        UrlPattern = reader["url_pattern"].ToString()!,
                        ListType = reader["list_type"].ToString()!,
                        Category = reader["category"]?.ToString() ?? "Custom",
                        Description = reader["description"]?.ToString() ?? "",
                        MatchType = reader["match_type"]?.ToString() ?? "domain",
                        AddedDate = Convert.ToDateTime(reader["added_date"])
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get URL list for type: {ListType}", listType ?? "all");
            }
            
            return urlList;
        }

        /// <summary>
        /// Check if URL exists in any list
        /// </summary>
        public async Task<UrlListItem?> FindUrlInListsAsync(string urlPattern)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();
                
                var sql = @"SELECT * FROM url_lists 
                           WHERE url_pattern = @urlPattern AND is_active = 1 
                           ORDER BY list_type DESC"; // Whitelist first
                
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@urlPattern", urlPattern);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return new UrlListItem
                    {
                        Id = Convert.ToInt32(reader["id"]),
                        UrlPattern = reader["url_pattern"].ToString()!,
                        ListType = reader["list_type"].ToString()!,
                        Category = reader["category"]?.ToString() ?? "Custom",
                        Description = reader["description"]?.ToString() ?? "",
                        MatchType = reader["match_type"]?.ToString() ?? "domain",
                        AddedDate = Convert.ToDateTime(reader["added_date"])
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to find URL in lists: {UrlPattern}", urlPattern);
            }
            
            return null;
        }

        /// <summary>
        /// Delete a user by ID
        /// </summary>
        public async Task<bool> DeleteUserAsync(int userId)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                // First check if user exists
                var checkSql = "SELECT COUNT(1) FROM device_users WHERE id = @userId";
                using var checkCommand = new SQLiteCommand(checkSql, connection);
                checkCommand.Parameters.AddWithValue("@userId", userId);
                var userExists = Convert.ToInt32(await checkCommand.ExecuteScalarAsync()) > 0;

                if (!userExists)
                {
                    _logger.LogWarning("Attempted to delete non-existent user: {UserId}", userId);
                    return false;
                }

                // Soft delete: set is_active to 0 instead of actually deleting
                var sql = "UPDATE device_users SET is_active = 0 WHERE id = @userId";
                using var command = new SQLiteCommand(sql, connection);
                command.Parameters.AddWithValue("@userId", userId);
                
                var result = await command.ExecuteNonQueryAsync();
                
                if (result > 0)
                {
                    _logger.LogInformation("User {UserId} has been deactivated", userId);
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete user: {UserId}", userId);
                return false;
            }
        }
    }

    /// <summary>
    /// URL List Item for whitelist/blacklist management
    /// </summary>
    public class UrlListItem
    {
        public int Id { get; set; }
        public string UrlPattern { get; set; }
        public string ListType { get; set; } // "Whitelist" or "Blacklist"
        public string Category { get; set; }
        public string Description { get; set; }
        public string MatchType { get; set; } // "exact", "domain", "wildcard", "regex"
        public DateTime AddedDate { get; set; }
    }

    /// <summary>
    /// Extended DeviceUser with runtime data
    /// </summary>
    public class DeviceUser : UrlFilter.Shared.Models.DeviceUser
    {
        public string WindowsUsername { get; set; }
        public int TodayMinutesUsed { get; set; }
        public int? DailyLimitMinutes { get; set; }

        public int RemainingMinutes => DailyLimitMinutes.HasValue ?
            Math.Max(0, DailyLimitMinutes.Value - TodayMinutesUsed) : int.MaxValue;

        public bool IsTimeUp => DailyLimitMinutes.HasValue && TodayMinutesUsed >= DailyLimitMinutes.Value;

        public string StatusText => IsTimeUp ? "Time Up" : IsActive ? "Online" : "Offline";

        public string StatusColor => IsTimeUp ? "Red" : IsActive ? "Green" : "Gray";

        public string TimeRemainingText => DailyLimitMinutes.HasValue ?
            $"{RemainingMinutes}min left" : "Unlimited";

        public string AgeText => Age.HasValue ? $"Age {Age}" : "";

        public string WindowsUserDisplay => !string.IsNullOrEmpty(WindowsUsername)
            ? WindowsUsername : "(Not linked)";
    }

    /// <summary>
    /// Windows user account information
    /// </summary>
    public class WindowsUserInfo
    {
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string FullUsername { get; set; }  // COMPUTERNAME\Username
        public bool IsAdmin { get; set; }
        public bool IsEnabled { get; set; }

        public string DisplayText => IsAdmin
            ? $"{DisplayName} ({Username}) - Administrator"
            : $"{DisplayName} ({Username})";

        public override string ToString() => DisplayText;
    }
}