// Shared Data Models for URL Filter System
// These models can be used across Windows and Android implementations

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace UrlFilter.Shared.Models
{
    public enum UserType
    {
        Adult,
        Teen,
        Child,
        Guest,
        Admin
    }

    public enum PermissionType
    {
        Allow,
        Block,
        Inherit
    }

    public enum ListType
    {
        Whitelist,
        Blacklist
    }

    public enum AgeRating
    {
        AllAges,
        TeenPlus,
        AdultOnly
    }

    public enum AuthProvider
    {
        WindowsAD,
        AzureAD,
        GoogleAccount,
        AndroidWorkProfile,
        Local
    }

    public enum PermissionLevel
    {
        SuperAdmin,
        Admin,
        Moderator,
        Parent
    }

    public class DeviceUser
    {
        public int Id { get; set; }
        
        [Required]
        public string Username { get; set; } = string.Empty;
        
        [Required]
        public string? DisplayName { get; set; } = string.Empty;
        
        public UserType UserType { get; set; } = UserType.Adult;
        
        public int? Age { get; set; }
        
        public DateTime ProfileCreatedDate { get; set; } = DateTime.UtcNow;
        
        public bool IsActive { get; set; } = true;
        
        public int? ParentUserId { get; set; }
        
        public string? AvatarPath { get; set; }
        
        public DateTime? LastLogin { get; set; }
        
        public int? CreatedBy { get; set; }

        // Navigation properties
        public DeviceUser? ParentUser { get; set; }
        public List<DeviceUser> ChildUsers { get; set; } = new List<DeviceUser>();
        public List<UserUrlPermission> UrlPermissions { get; set; } = new List<UserUrlPermission>();
        public List<UserTimeRestriction> TimeRestrictions { get; set; } = new List<UserTimeRestriction>();
        public List<DailyUsageLog> UsageLogs { get; set; } = new List<DailyUsageLog>();
    }

    public class UserUrlPermission
    {
        public int Id { get; set; }
        
        public int UserId { get; set; }
        
        [Required]
        public string UrlPattern { get; set; } = string.Empty;
        
        public PermissionType PermissionType { get; set; } = PermissionType.Inherit;
        
        public string? Category { get; set; }
        
        public int? GrantedBy { get; set; }
        
        public DateTime GrantedDate { get; set; } = DateTime.UtcNow;
        
        public DateTime? ExpiresDate { get; set; }
        
        public string? Notes { get; set; }
        
        public bool IsActive { get; set; } = true;

        // Navigation properties
        public DeviceUser? User { get; set; }
        public AdminUser? GrantedByAdmin { get; set; }
    }

    public class UserTimeRestriction
    {
        public int Id { get; set; }
        
        public int UserId { get; set; }
        
        public int DayOfWeek { get; set; } // 0=Sunday, 6=Saturday
        
        public TimeSpan? AllowedStartTime { get; set; }
        
        public TimeSpan? AllowedEndTime { get; set; }
        
        public int? DailyLimitMinutes { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

        // Navigation properties
        public DeviceUser? User { get; set; }
    }

    public class DailyUsageLog
    {
        public int Id { get; set; }
        
        public int UserId { get; set; }
        
        public DateTime UsageDate { get; set; }
        
        public int MinutesUsed { get; set; } = 0;
        
        public DateTime? LastActivityTime { get; set; }
        
        public int SessionsCount { get; set; } = 0;
        
        public int YouTubeMinutes { get; set; } = 0;
        
        public int EducationalMinutes { get; set; } = 0;
        
        public int SocialMinutes { get; set; } = 0;
        
        public int GamingMinutes { get; set; } = 0;

        // Navigation properties
        public DeviceUser? User { get; set; }
    }

    public class InternetSession
    {
        public int Id { get; set; }
        
        public int UserId { get; set; }
        
        public DateTime SessionStart { get; set; }
        
        public DateTime? SessionEnd { get; set; }
        
        public int? DurationMinutes { get; set; }
        
        public int TotalRequests { get; set; } = 0;
        
        public int BlockedRequests { get; set; } = 0;
        
        public int AllowedRequests { get; set; } = 0;
        
        public string? TopDomains { get; set; } // JSON array
        
        public string? DeviceInfo { get; set; }
        
        public string? IpAddress { get; set; }

        public DateTime? LastActivityTime { get; set; }

        // Navigation properties
        public DeviceUser? User { get; set; }
    }

    public class UrlList
    {
        public int Id { get; set; }
        
        [Required]
        public string UrlPattern { get; set; } = string.Empty;
        
        public ListType ListType { get; set; }
        
        public string? Category { get; set; }
        
        public AgeRating AgeAppropriate { get; set; } = AgeRating.AllAges;
        
        public DateTime AddedDate { get; set; } = DateTime.UtcNow;
        
        public int? AddedBy { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        public string? Description { get; set; }
        
        public string MatchType { get; set; } = "domain"; // exact, wildcard, regex, domain

        // Navigation properties
        public AdminUser? AddedByAdmin { get; set; }
    }

    public class YouTubeChannel
    {
        public int Id { get; set; }
        
        [Required]
        public string ChannelId { get; set; } = string.Empty;
        
        [Required]
        public string ChannelName { get; set; } = string.Empty;
        
        [Required]
        public string ChannelUrl { get; set; } = string.Empty;
        
        public string? ChannelHandle { get; set; } // @channel_handle format
        
        public string AccessType { get; set; } = "blocked"; // allowed, blocked
        
        public string? Category { get; set; }
        
        public string AgeRating { get; set; } = "adult"; // kids, family, teen, adult
        
        public bool EducationalContent { get; set; } = false;
        
        public int? SubscriberCount { get; set; }
        
        public string? Description { get; set; }
        
        public string? ThumbnailUrl { get; set; }
        
        public DateTime AddedDate { get; set; } = DateTime.UtcNow;
        
        public int? AddedBy { get; set; }
        
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        
        public bool IsActive { get; set; } = true;

        // Navigation properties
        public AdminUser? AddedByAdmin { get; set; }
        public List<UserYouTubePermission> UserPermissions { get; set; } = new List<UserYouTubePermission>();
    }

    public class UserYouTubePermission
    {
        public int Id { get; set; }
        
        public int UserId { get; set; }
        
        public int ChannelId { get; set; }
        
        public string PermissionType { get; set; } = "block"; // allow, block, time_limited
        
        public int? DailyLimitMinutes { get; set; }
        
        public int? GrantedBy { get; set; }
        
        public DateTime GrantedDate { get; set; } = DateTime.UtcNow;
        
        public DateTime? ExpiresDate { get; set; }
        
        public bool IsActive { get; set; } = true;

        // Navigation properties
        public DeviceUser? User { get; set; }
        public YouTubeChannel? Channel { get; set; }
        public AdminUser? GrantedByAdmin { get; set; }
    }

    public class BlockedAttempt
    {
        public int Id { get; set; }
        
        public int? UserId { get; set; }
        
        [Required]
        public string Url { get; set; } = string.Empty;
        
        public string ContentType { get; set; } = "website"; // youtube_channel, youtube_video, website, social_media, gaming, adult, other
        
        [Required]
        public string BlockReason { get; set; } = string.Empty; // time_limit, blacklist, age_restriction, schedule, user_block, youtube_time_limit, parental_control
        
        public string? ChannelInfo { get; set; } // JSON with channel details if YouTube content
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        public string? DeviceInfo { get; set; }
        
        public string? UserAgent { get; set; }
        
        public string? IpAddress { get; set; }
        
        public string SeverityLevel { get; set; } = "medium"; // low, medium, high, critical

        // Navigation properties
        public DeviceUser? User { get; set; }
    }

    public class AdminUser
    {
        public int Id { get; set; }
        
        [Required]
        public string UserIdentifier { get; set; } = string.Empty; // AD username, Azure AD object ID, or Android account email
        
        public string? DisplayName { get; set; }
        
        public string? Email { get; set; }
        
        public AuthProvider AuthenticationProvider { get; set; }
        
        public PermissionLevel PermissionLevel { get; set; } = PermissionLevel.Parent;
        
        public DateTime? LastLogin { get; set; }
        
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        
        public bool IsActive { get; set; } = true;
        
        public string? ProfilePictureUrl { get; set; }
        
        public string? PhoneNumber { get; set; }
        
        public string? EmergencyContactEmail { get; set; }

        // Navigation properties
        public List<AuthSession> AuthSessions { get; set; } = new List<AuthSession>();
        public List<DeviceUser> CreatedUsers { get; set; } = new List<DeviceUser>();
        public List<UserUrlPermission> GrantedPermissions { get; set; } = new List<UserUrlPermission>();
    }

    public class AuthSession
    {
        public int Id { get; set; }
        
        public int UserId { get; set; }
        
        [Required]
        public string SessionToken { get; set; } = string.Empty;
        
        public string? DeviceInfo { get; set; }
        
        public DateTime LoginTimestamp { get; set; } = DateTime.UtcNow;
        
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        
        public DateTime ExpiresAt { get; set; }
        
        public bool IsValid { get; set; } = true;
        
        public string? IpAddress { get; set; }
        
        public string? UserAgent { get; set; }

        // Navigation properties
        public AdminUser? User { get; set; }
    }

    public class AdminSetting
    {
        [Key]
        public string Key { get; set; } = string.Empty;
        
        public string? Value { get; set; }
        
        public string ValueType { get; set; } = "string"; // string, integer, boolean, json
        
        public string? Description { get; set; }
        
        public string? Category { get; set; }
        
        public bool IsSensitive { get; set; } = false;
        
        public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
        
        public int? ModifiedBy { get; set; }

        // Navigation properties
        public AdminUser? ModifiedByAdmin { get; set; }
    }

    public class ContentCategory
    {
        public int Id { get; set; }
        
        [Required]
        public string CategoryName { get; set; } = string.Empty;
        
        public string? Description { get; set; }
        
        public AgeRating AgeAppropriate { get; set; } = AgeRating.AllAges;
        
        public string DefaultAction { get; set; } = "block"; // allow, block
        
        public string? ColorCode { get; set; } // For UI color coding
        
        public string? IconName { get; set; } // For UI icons
        
        public bool IsSystem { get; set; } = false; // System categories cannot be deleted
        
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    }

    // Filter result classes
    public class FilterResult
    {
        public bool IsAllowed { get; set; }
        public string? Reason { get; set; }
        public string? Category { get; set; }
        public int? RemainingMinutes { get; set; }

        public static FilterResult Allowed(string reason = "Access granted", int? remainingMinutes = null)
        {
            return new FilterResult { IsAllowed = true, Reason = reason, RemainingMinutes = remainingMinutes };
        }

        public static FilterResult Blocked(string reason, string category = null)
        {
            return new FilterResult { IsAllowed = false, Reason = reason, Category = category };
        }
    }

    public class AccessResult
    {
        public bool IsAllowed { get; set; }
        public string? Reason { get; set; }
        public int? RemainingMinutes { get; set; }

        public static AccessResult Allowed(int? remainingMinutes = null)
        {
            return new AccessResult { IsAllowed = true, RemainingMinutes = remainingMinutes };
        }

        public static AccessResult Blocked(string reason)
        {
            return new AccessResult { IsAllowed = false, Reason = reason };
        }
    }

    // Configuration classes
    public class FilterConfiguration
    {
        public bool EnableYouTubeFiltering { get; set; } = true;
        public bool EnableTimeBasedFiltering { get; set; } = true;
        public bool EnableAgeBasedFiltering { get; set; } = true;
        public bool EnableAuditLogging { get; set; } = true;
        public string DefaultAdultPolicy { get; set; } = "allow";
        public string DefaultTeenPolicy { get; set; } = "filtered";
        public string DefaultChildPolicy { get; set; } = "restricted";
        public int MaxDailyHoursChild { get; set; } = 2;
        public int MaxDailyHoursTeen { get; set; } = 4;
        public int SessionTimeoutMinutes { get; set; } = 120;
        public bool BlockVpnAccess { get; set; } = true;
        public string? YouTubeApiKey { get; set; }
    }

    // Dashboard data classes
    public class FamilyDashboardData
    {
        public List<UserStatusInfo> UserStatuses { get; set; } = new List<UserStatusInfo>();
        public int TotalHoursUsedToday { get; set; }
        public int TotalHoursAllowedToday { get; set; }
        public List<RecentAlert> RecentAlerts { get; set; } = new List<RecentAlert>();
        public SystemStatus SystemStatus { get; set; }
    }

    public class UserStatusInfo
    {
        public int UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Age { get; set; }
        public UserType UserType { get; set; }
        public bool IsOnline { get; set; }
        public int MinutesLeftToday { get; set; }
        public string Status { get; set; } = string.Empty; // Online, Time Up, Blocked, Offline
        public string StatusColor { get; set; } = string.Empty; // For UI
    }

    public class RecentAlert
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public string AlertMessage { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Severity { get; set; } = string.Empty;
        public string Url { get; set; }
    }

    public class SystemStatus
    {
        public bool IsRunning { get; set; }
        public string Version { get; set; } = string.Empty;
        public DateTime LastUpdate { get; set; }
        public int ActiveUsers { get; set; }
        public int TotalBlocksToday { get; set; }
    }

    public class AuditLog
    {
        public int Id { get; set; }
        
        [Required]
        public string EventType { get; set; } = string.Empty;
        
        [Required]
        public string Description { get; set; } = string.Empty;
        
        public int? UserId { get; set; }
        
        public string Username { get; set; } = string.Empty;
        
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        public string? IpAddress { get; set; }
        
        public string? DeviceInfo { get; set; }
        
        public string Severity { get; set; } = "Information"; // Information, Warning, Error, Critical
        
        public string? AdditionalData { get; set; } // JSON for extra context

        // Navigation properties
        public DeviceUser? User { get; set; }
    }
}