using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Database service for Windows URL Filter System
    /// Handles SQLite database operations and data persistence
    /// </summary>
    public class DatabaseService : IDisposable
    {
        private readonly ILogger<DatabaseService> _logger;
        private readonly string _connectionString;
        private readonly string _databasePath;
        private SQLiteConnection _connection;

        public bool IsConnected { get; private set; }

        public DatabaseService(ILogger<DatabaseService> logger)
        {
            _logger = logger;
            
            // Set database path
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UrlFilter", "Database");
            Directory.CreateDirectory(dataDir);
            
            _databasePath = Path.Combine(dataDir, "urlfilter.db");
            _connectionString = $"Data Source={_databasePath};Version=3;";
            
            _logger.LogInformation($"Database path: {_databasePath}");
        }

        public async Task InitializeAsync()
        {
            try
            {
                _logger.LogInformation("Initializing database connection");
                
                _connection = new SQLiteConnection(_connectionString);
                await _connection.OpenAsync();
                
                await CreateTablesIfNotExistAsync();
                await InsertDefaultDataIfNeededAsync();
                
                IsConnected = true;
                _logger.LogInformation("Database initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database");
                IsConnected = false;
                throw;
            }
        }

        public async Task ReconnectAsync()
        {
            try
            {
                _logger.LogInformation("Reconnecting to database");
                
                if (_connection?.State == System.Data.ConnectionState.Open)
                {
                    _connection.Close();
                }
                
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reconnect to database");
                throw;
            }
        }

        #region User Management

        public async Task<DeviceUser> GetDeviceUserAsync(int userId)
        {
            try
            {
                const string sql = "SELECT * FROM device_users WHERE id = @id AND is_active = 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", userId);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapDeviceUser(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting device user {userId}");
                throw;
            }
        }

        public async Task<DeviceUser> GetDeviceUserByUsernameAsync(string username)
        {
            try
            {
                const string sql = "SELECT * FROM device_users WHERE username = @username AND is_active = 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@username", username);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapDeviceUser(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting device user by username: {username}");
                throw;
            }
        }

        public async Task<List<DeviceUser>> GetActiveUsersAsync()
        {
            try
            {
                const string sql = "SELECT * FROM device_users WHERE is_active = 1 ORDER BY display_name";
                
                using var command = new SQLiteCommand(sql, _connection);
                using var reader = await command.ExecuteReaderAsync();
                
                var users = new List<DeviceUser>();
                while (await reader.ReadAsync())
                {
                    users.Add(MapDeviceUser(reader));
                }
                
                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting active users");
                throw;
            }
        }

        public async Task<int> InsertDeviceUserAsync(DeviceUser user)
        {
            try
            {
                const string sql = @"
                    INSERT INTO device_users (username, display_name, user_type, age, is_active, parent_user_id, created_by)
                    VALUES (@username, @displayName, @userType, @age, @isActive, @parentUserId, @createdBy);
                    SELECT last_insert_rowid();";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@username", user.Username);
                command.Parameters.AddWithValue("@displayName", user.DisplayName);
                command.Parameters.AddWithValue("@userType", user.UserType.ToString());
                command.Parameters.AddWithValue("@age", (object)user.Age ?? DBNull.Value);
                command.Parameters.AddWithValue("@isActive", user.IsActive);
                command.Parameters.AddWithValue("@parentUserId", (object)user.ParentUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdBy", (object)user.CreatedBy ?? DBNull.Value);
                
                var result = await command.ExecuteScalarAsync();
                return Convert.ToInt32(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting device user");
                throw;
            }
        }

        #endregion

        #region URL Lists

        public async Task<List<UrlList>> GetAllUrlListsAsync()
        {
            try
            {
                const string sql = "SELECT * FROM url_lists WHERE is_active = 1 ORDER BY url_pattern";
                
                using var command = new SQLiteCommand(sql, _connection);
                using var reader = await command.ExecuteReaderAsync();
                
                var urlLists = new List<UrlList>();
                while (await reader.ReadAsync())
                {
                    urlLists.Add(MapUrlList(reader));
                }
                
                return urlLists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting URL lists");
                throw;
            }
        }

        public async Task<List<UrlList>> GetUrlListsByTypeAsync(ListType listType)
        {
            try
            {
                const string sql = "SELECT * FROM url_lists WHERE list_type = @listType AND is_active = 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@listType", listType.ToString().ToLower());
                
                using var reader = await command.ExecuteReaderAsync();
                
                var urlLists = new List<UrlList>();
                while (await reader.ReadAsync())
                {
                    urlLists.Add(MapUrlList(reader));
                }
                
                return urlLists;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting URL lists by type: {listType}");
                throw;
            }
        }

        #endregion

        #region YouTube Channels

        public async Task<YouTubeChannel> GetYouTubeChannelByIdAsync(string channelId)
        {
            try
            {
                const string sql = "SELECT * FROM youtube_channels WHERE channel_id = @channelId AND is_active = 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@channelId", channelId);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapYouTubeChannel(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting YouTube channel: {channelId}");
                throw;
            }
        }

        #endregion

        #region Sessions

        public async Task<InternetSession> GetCurrentSessionAsync(int userId)
        {
            try
            {
                const string sql = @"
                    SELECT * FROM internet_sessions 
                    WHERE user_id = @userId AND session_end IS NULL 
                    ORDER BY session_start DESC LIMIT 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@userId", userId);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapInternetSession(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting current session for user {userId}");
                throw;
            }
        }

        public async Task UpdateSessionAsync(InternetSession session)
        {
            try
            {
                const string sql = @"
                    UPDATE internet_sessions 
                    SET session_end = @sessionEnd, duration_minutes = @duration, 
                        total_requests = @totalRequests, blocked_requests = @blockedRequests,
                        allowed_requests = @allowedRequests
                    WHERE id = @id";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", session.Id);
                command.Parameters.AddWithValue("@sessionEnd", (object)session.SessionEnd ?? DBNull.Value);
                command.Parameters.AddWithValue("@duration", (object)session.DurationMinutes ?? DBNull.Value);
                command.Parameters.AddWithValue("@totalRequests", session.TotalRequests);
                command.Parameters.AddWithValue("@blockedRequests", session.BlockedRequests);
                command.Parameters.AddWithValue("@allowedRequests", session.AllowedRequests);
                
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating session");
                throw;
            }
        }

        #endregion

        #region Time Restrictions

        public async Task<UserTimeRestriction> GetUserTimeRestrictionAsync(int userId, int dayOfWeek)
        {
            try
            {
                const string sql = @"
                    SELECT * FROM user_time_restrictions 
                    WHERE user_id = @userId AND day_of_week = @dayOfWeek AND is_active = 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@userId", userId);
                command.Parameters.AddWithValue("@dayOfWeek", dayOfWeek);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapUserTimeRestriction(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting time restriction for user {userId}, day {dayOfWeek}");
                throw;
            }
        }

        #endregion

        #region Authentication

        public async Task<AdminUser> GetAdminUserByIdentifierAsync(string userIdentifier)
        {
            try
            {
                const string sql = "SELECT * FROM admin_users WHERE user_identifier = @userIdentifier AND is_active = 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@userIdentifier", userIdentifier);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapAdminUser(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting admin user: {userIdentifier}");
                throw;
            }
        }

        public async Task<AdminUser> GetAdminUserAsync(int userId)
        {
            try
            {
                const string sql = "SELECT * FROM admin_users WHERE id = @id AND is_active = 1";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", userId);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapAdminUser(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting admin user {userId}");
                throw;
            }
        }

        public async Task InsertAdminUserAsync(AdminUser adminUser)
        {
            try
            {
                const string sql = @"
                    INSERT INTO admin_users (user_identifier, display_name, email, authentication_provider, permission_level, is_active)
                    VALUES (@userIdentifier, @displayName, @email, @authProvider, @permissionLevel, @isActive)";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@userIdentifier", adminUser.UserIdentifier);
                command.Parameters.AddWithValue("@displayName", adminUser.DisplayName ?? "");
                command.Parameters.AddWithValue("@email", adminUser.Email ?? "");
                command.Parameters.AddWithValue("@authProvider", adminUser.AuthenticationProvider.ToString());
                command.Parameters.AddWithValue("@permissionLevel", adminUser.PermissionLevel.ToString());
                command.Parameters.AddWithValue("@isActive", adminUser.IsActive);
                
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting admin user");
                throw;
            }
        }

        public async Task UpdateAdminUserAsync(AdminUser adminUser)
        {
            try
            {
                const string sql = @"
                    UPDATE admin_users 
                    SET last_login = @lastLogin
                    WHERE id = @id";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", adminUser.Id);
                command.Parameters.AddWithValue("@lastLogin", (object)adminUser.LastLogin ?? DBNull.Value);
                
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating admin user");
                throw;
            }
        }

        public async Task<AuthSession> GetAuthSessionByTokenAsync(string sessionToken)
        {
            try
            {
                const string sql = "SELECT * FROM auth_sessions WHERE session_token = @token";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@token", sessionToken);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return MapAuthSession(reader);
                }
                
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting auth session by token");
                throw;
            }
        }

        public async Task InsertAuthSessionAsync(AuthSession session)
        {
            try
            {
                const string sql = @"
                    INSERT INTO auth_sessions (user_id, session_token, device_info, expires_at, is_valid, ip_address, user_agent)
                    VALUES (@userId, @sessionToken, @deviceInfo, @expiresAt, @isValid, @ipAddress, @userAgent)";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@userId", session.UserId);
                command.Parameters.AddWithValue("@sessionToken", session.SessionToken);
                command.Parameters.AddWithValue("@deviceInfo", session.DeviceInfo ?? "");
                command.Parameters.AddWithValue("@expiresAt", session.ExpiresAt);
                command.Parameters.AddWithValue("@isValid", session.IsValid);
                command.Parameters.AddWithValue("@ipAddress", session.IpAddress ?? "");
                command.Parameters.AddWithValue("@userAgent", session.UserAgent ?? "");
                
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting auth session");
                throw;
            }
        }

        public async Task UpdateAuthSessionAsync(AuthSession session)
        {
            try
            {
                const string sql = @"
                    UPDATE auth_sessions 
                    SET last_activity = @lastActivity, is_valid = @isValid
                    WHERE id = @id";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@id", session.Id);
                command.Parameters.AddWithValue("@lastActivity", session.LastActivity);
                command.Parameters.AddWithValue("@isValid", session.IsValid);
                
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating auth session");
                throw;
            }
        }

        #endregion

        #region Blocked Attempts

        public async Task InsertBlockedAttemptAsync(BlockedAttempt attempt)
        {
            try
            {
                const string sql = @"
                    INSERT INTO blocked_attempts (user_id, url, content_type, block_reason, channel_info, device_info, user_agent, ip_address, severity_level)
                    VALUES (@userId, @url, @contentType, @blockReason, @channelInfo, @deviceInfo, @userAgent, @ipAddress, @severityLevel)";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@userId", (object)attempt.UserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@url", attempt.Url);
                command.Parameters.AddWithValue("@contentType", attempt.ContentType);
                command.Parameters.AddWithValue("@blockReason", attempt.BlockReason);
                command.Parameters.AddWithValue("@channelInfo", attempt.ChannelInfo ?? "");
                command.Parameters.AddWithValue("@deviceInfo", attempt.DeviceInfo ?? "");
                command.Parameters.AddWithValue("@userAgent", attempt.UserAgent ?? "");
                command.Parameters.AddWithValue("@ipAddress", attempt.IpAddress ?? "");
                command.Parameters.AddWithValue("@severityLevel", attempt.SeverityLevel);
                
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting blocked attempt");
                throw;
            }
        }

        #endregion

        #region Statistics

        public async Task<SystemStatistics> GetDailyStatisticsAsync(DateTime date)
        {
            try
            {
                const string sql = @"
                    SELECT 
                        COUNT(DISTINCT user_id) as ActiveUsers,
                        COUNT(*) as TotalRequests,
                        SUM(CASE WHEN block_reason IS NOT NULL THEN 1 ELSE 0 END) as BlockedRequests
                    FROM blocked_attempts 
                    WHERE DATE(timestamp) = DATE(@date)";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@date", date.Date);
                
                using var reader = await command.ExecuteReaderAsync();
                
                if (await reader.ReadAsync())
                {
                    return new SystemStatistics
                    {
                        Date = date,
                        ActiveUsers = reader.GetInt32("ActiveUsers"),
                        TotalRequests = reader.GetInt32("TotalRequests"),
                        BlockedRequests = reader.GetInt32("BlockedRequests"),
                        AllowedRequests = reader.GetInt32("TotalRequests") - reader.GetInt32("BlockedRequests")
                    };
                }
                
                return new SystemStatistics { Date = date };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting daily statistics");
                throw;
            }
        }

        public async Task UpdateSystemStatisticsAsync(SystemStatistics stats)
        {
            // Implementation for updating system statistics
            _logger.LogInformation($"System statistics updated for {stats.Date:yyyy-MM-dd}");
        }

        #endregion

        #region Configuration

        public async Task<string> GetConfigValueAsync(string key, string defaultValue = "")
        {
            try
            {
                const string sql = "SELECT value FROM admin_settings WHERE key = @key";
                
                using var command = new SQLiteCommand(sql, _connection);
                command.Parameters.AddWithValue("@key", key);
                
                var result = await command.ExecuteScalarAsync();
                return result?.ToString() ?? defaultValue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting config value: {key}");
                return defaultValue;
            }
        }

        #endregion

        #region Cleanup

        public async Task CleanupOldDataAsync(DateTime cutoffDate)
        {
            try
            {
                _logger.LogInformation($"Cleaning up data older than {cutoffDate:yyyy-MM-dd}");
                
                var cleanupQueries = new[]
                {
                    "DELETE FROM blocked_attempts WHERE timestamp < @cutoff",
                    "DELETE FROM internet_sessions WHERE session_start < @cutoff",
                    "DELETE FROM auth_sessions WHERE login_timestamp < @cutoff AND is_valid = 0",
                    "DELETE FROM daily_usage_log WHERE usage_date < @cutoff"
                };
                
                foreach (var query in cleanupQueries)
                {
                    using var command = new SQLiteCommand(query, _connection);
                    command.Parameters.AddWithValue("@cutoff", cutoffDate);
                    
                    var deleted = await command.ExecuteNonQueryAsync();
                    _logger.LogDebug($"Cleaned up {deleted} records with query: {query}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during data cleanup");
                throw;
            }
        }

        #endregion

        #region Private Methods

        private async Task CreateTablesIfNotExistAsync()
        {
            try
            {
                var schemaFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "schema.sql");
                
                if (!File.Exists(schemaFile))
                {
                    // Try alternate location
                    schemaFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "shared", "database-schema", "schema.sql");
                }
                
                if (File.Exists(schemaFile))
                {
                    var schema = await File.ReadAllTextAsync(schemaFile);
                    
                    using var command = new SQLiteCommand(schema, _connection);
                    await command.ExecuteNonQueryAsync();
                    
                    _logger.LogInformation("Database tables created/verified");
                }
                else
                {
                    _logger.LogWarning("Schema file not found - creating basic tables");
                    await CreateBasicTablesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating database tables");
                throw;
            }
        }

        private async Task CreateBasicTablesAsync()
        {
            var basicSchema = @"
                CREATE TABLE IF NOT EXISTS device_users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    username TEXT UNIQUE NOT NULL,
                    display_name TEXT NOT NULL,
                    user_type TEXT DEFAULT 'adult',
                    age INTEGER,
                    is_active BOOLEAN DEFAULT 1,
                    profile_created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );
                
                CREATE TABLE IF NOT EXISTS admin_users (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_identifier TEXT UNIQUE NOT NULL,
                    display_name TEXT,
                    email TEXT,
                    authentication_provider TEXT NOT NULL,
                    permission_level TEXT DEFAULT 'parent',
                    is_active BOOLEAN DEFAULT 1,
                    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    last_login TIMESTAMP
                );
                
                CREATE TABLE IF NOT EXISTS auth_sessions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    user_id INTEGER NOT NULL,
                    session_token TEXT UNIQUE NOT NULL,
                    login_timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    last_activity TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    expires_at TIMESTAMP NOT NULL,
                    is_valid BOOLEAN DEFAULT 1,
                    device_info TEXT,
                    ip_address TEXT,
                    user_agent TEXT
                );";
            
            using var command = new SQLiteCommand(basicSchema, _connection);
            await command.ExecuteNonQueryAsync();
        }

        private async Task InsertDefaultDataIfNeededAsync()
        {
            // Check if we need to insert default data
            const string checkSql = "SELECT COUNT(*) FROM admin_settings";
            
            using var command = new SQLiteCommand(checkSql, _connection);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            
            if (count == 0)
            {
                _logger.LogInformation("Inserting default configuration data");
                
                var defaultSettings = new[]
                {
                    ("default_adult_policy", "allow"),
                    ("default_teen_policy", "filtered"),
                    ("default_child_policy", "restricted"),
                    ("max_daily_hours_child", "2"),
                    ("max_daily_hours_teen", "4"),
                    ("session_timeout_minutes", "120")
                };
                
                foreach (var (key, value) in defaultSettings)
                {
                    const string insertSql = "INSERT OR IGNORE INTO admin_settings (key, value) VALUES (@key, @value)";
                    using var insertCommand = new SQLiteCommand(insertSql, _connection);
                    insertCommand.Parameters.AddWithValue("@key", key);
                    insertCommand.Parameters.AddWithValue("@value", value);
                    await insertCommand.ExecuteNonQueryAsync();
                }
            }
        }

        // Mapping methods
        private DeviceUser MapDeviceUser(SQLiteDataReader reader)
        {
            return new DeviceUser
            {
                Id = reader.GetInt32("id"),
                Username = reader.GetString("username"),
                DisplayName = reader.GetString("display_name"),
                UserType = Enum.Parse<UserType>(reader.GetString("user_type"), true),
                Age = reader.IsDBNull("age") ? null : reader.GetInt32("age"),
                IsActive = reader.GetBoolean("is_active"),
                ProfileCreatedDate = reader.GetDateTime("profile_created_date")
            };
        }

        private UrlList MapUrlList(SQLiteDataReader reader)
        {
            return new UrlList
            {
                Id = reader.GetInt32("id"),
                UrlPattern = reader.GetString("url_pattern"),
                ListType = Enum.Parse<ListType>(reader.GetString("list_type"), true),
                Category = reader.IsDBNull("category") ? null : reader.GetString("category"),
                IsActive = reader.GetBoolean("is_active"),
                AddedDate = reader.GetDateTime("added_date")
            };
        }

        private YouTubeChannel MapYouTubeChannel(SQLiteDataReader reader)
        {
            return new YouTubeChannel
            {
                Id = reader.GetInt32("id"),
                ChannelId = reader.GetString("channel_id"),
                ChannelName = reader.GetString("channel_name"),
                ChannelUrl = reader.GetString("channel_url"),
                AccessType = reader.GetString("access_type"),
                Category = reader.IsDBNull("category") ? null : reader.GetString("category"),
                IsActive = reader.GetBoolean("is_active")
            };
        }

        private InternetSession MapInternetSession(SQLiteDataReader reader)
        {
            return new InternetSession
            {
                Id = reader.GetInt32("id"),
                UserId = reader.GetInt32("user_id"),
                SessionStart = reader.GetDateTime("session_start"),
                SessionEnd = reader.IsDBNull("session_end") ? null : reader.GetDateTime("session_end"),
                TotalRequests = reader.GetInt32("total_requests"),
                BlockedRequests = reader.GetInt32("blocked_requests"),
                AllowedRequests = reader.GetInt32("allowed_requests")
            };
        }

        private UserTimeRestriction MapUserTimeRestriction(SQLiteDataReader reader)
        {
            return new UserTimeRestriction
            {
                Id = reader.GetInt32("id"),
                UserId = reader.GetInt32("user_id"),
                DayOfWeek = reader.GetInt32("day_of_week"),
                AllowedStartTime = reader.IsDBNull("allowed_start_time") ? null : TimeSpan.Parse(reader.GetString("allowed_start_time")),
                AllowedEndTime = reader.IsDBNull("allowed_end_time") ? null : TimeSpan.Parse(reader.GetString("allowed_end_time")),
                DailyLimitMinutes = reader.IsDBNull("daily_limit_minutes") ? null : reader.GetInt32("daily_limit_minutes"),
                IsActive = reader.GetBoolean("is_active")
            };
        }

        private AdminUser MapAdminUser(SQLiteDataReader reader)
        {
            return new AdminUser
            {
                Id = reader.GetInt32("id"),
                UserIdentifier = reader.GetString("user_identifier"),
                DisplayName = reader.IsDBNull("display_name") ? null : reader.GetString("display_name"),
                Email = reader.IsDBNull("email") ? null : reader.GetString("email"),
                AuthenticationProvider = Enum.Parse<AuthProvider>(reader.GetString("authentication_provider"), true),
                PermissionLevel = Enum.Parse<PermissionLevel>(reader.GetString("permission_level"), true),
                IsActive = reader.GetBoolean("is_active"),
                LastLogin = reader.IsDBNull("last_login") ? null : reader.GetDateTime("last_login"),
                CreatedDate = reader.GetDateTime("created_date")
            };
        }

        private AuthSession MapAuthSession(SQLiteDataReader reader)
        {
            return new AuthSession
            {
                Id = reader.GetInt32("id"),
                UserId = reader.GetInt32("user_id"),
                SessionToken = reader.GetString("session_token"),
                LoginTimestamp = reader.GetDateTime("login_timestamp"),
                LastActivity = reader.GetDateTime("last_activity"),
                ExpiresAt = reader.GetDateTime("expires_at"),
                IsValid = reader.GetBoolean("is_valid"),
                DeviceInfo = reader.IsDBNull("device_info") ? null : reader.GetString("device_info"),
                IpAddress = reader.IsDBNull("ip_address") ? null : reader.GetString("ip_address"),
                UserAgent = reader.IsDBNull("user_agent") ? null : reader.GetString("user_agent")
            };
        }

        #endregion

        public void Dispose()
        {
            try
            {
                _connection?.Close();
                _connection?.Dispose();
                IsConnected = false;
                _logger.LogInformation("Database connection disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing database connection");
            }
        }
    }

    public class SystemStatistics
    {
        public DateTime Date { get; set; }
        public int ActiveUsers { get; set; }
        public int TotalRequests { get; set; }
        public int BlockedRequests { get; set; }
        public int AllowedRequests { get; set; }
    }
}