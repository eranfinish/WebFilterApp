package com.urlfilter.models;

import java.util.Date;
import java.util.List;
import java.util.ArrayList;

/**
 * Shared Data Models for URL Filter System - Android Implementation
 * These models mirror the C# models for cross-platform consistency
 */

public class DataModels {
    
    public enum UserType {
        ADULT, TEEN, CHILD, GUEST
    }

    public enum PermissionType {
        ALLOW, BLOCK, INHERIT
    }

    public enum ListType {
        WHITELIST, BLACKLIST
    }

    public enum AgeRating {
        ALL_AGES, TEEN_PLUS, ADULT_ONLY
    }

    public enum AuthProvider {
        WINDOWS_AD, AZURE_AD, GOOGLE_ACCOUNT, ANDROID_WORK_PROFILE, LOCAL
    }

    public enum PermissionLevel {
        SUPER_ADMIN, ADMIN, MODERATOR, PARENT
    }

    public static class DeviceUser {
        public int id;
        public String username;
        public String displayName;
        public UserType userType = UserType.ADULT;
        public Integer age;
        public Date profileCreatedDate = new Date();
        public boolean isActive = true;
        public Integer parentUserId;
        public String avatarPath;
        public Date lastLogin;
        public Integer createdBy;

        // Navigation properties (for convenience, not persisted)
        public DeviceUser parentUser;
        public List<DeviceUser> childUsers = new ArrayList<>();
        public List<UserUrlPermission> urlPermissions = new ArrayList<>();
        public List<UserTimeRestriction> timeRestrictions = new ArrayList<>();
        public List<DailyUsageLog> usageLogs = new ArrayList<>();

        public DeviceUser() {}

        public DeviceUser(String username, String displayName, UserType userType) {
            this.username = username;
            this.displayName = displayName;
            this.userType = userType;
        }
    }

    public static class UserUrlPermission {
        public int id;
        public int userId;
        public String urlPattern;
        public PermissionType permissionType = PermissionType.INHERIT;
        public String category;
        public Integer grantedBy;
        public Date grantedDate = new Date();
        public Date expiresDate;
        public String notes;
        public boolean isActive = true;

        public UserUrlPermission() {}

        public UserUrlPermission(int userId, String urlPattern, PermissionType permissionType) {
            this.userId = userId;
            this.urlPattern = urlPattern;
            this.permissionType = permissionType;
        }
    }

    public static class UserTimeRestriction {
        public int id;
        public int userId;
        public int dayOfWeek; // 0=Sunday, 6=Saturday
        public String allowedStartTime; // "HH:MM:SS"
        public String allowedEndTime;   // "HH:MM:SS"
        public Integer dailyLimitMinutes;
        public boolean isActive = true;
        public Date createdDate = new Date();
        public Date modifiedDate = new Date();

        public UserTimeRestriction() {}

        public UserTimeRestriction(int userId, int dayOfWeek, String startTime, String endTime, int dailyLimit) {
            this.userId = userId;
            this.dayOfWeek = dayOfWeek;
            this.allowedStartTime = startTime;
            this.allowedEndTime = endTime;
            this.dailyLimitMinutes = dailyLimit;
        }
    }

    public static class DailyUsageLog {
        public int id;
        public int userId;
        public Date usageDate;
        public int minutesUsed = 0;
        public Date lastActivityTime;
        public int sessionsCount = 0;
        public int youtubeMinutes = 0;
        public int educationalMinutes = 0;
        public int socialMinutes = 0;
        public int gamingMinutes = 0;

        public DailyUsageLog() {}

        public DailyUsageLog(int userId, Date usageDate) {
            this.userId = userId;
            this.usageDate = usageDate;
        }
    }

    public static class InternetSession {
        public int id;
        public int userId;
        public Date sessionStart;
        public Date sessionEnd;
        public Integer durationMinutes;
        public int totalRequests = 0;
        public int blockedRequests = 0;
        public int allowedRequests = 0;
        public String topDomains; // JSON array
        public String deviceInfo;
        public String ipAddress;

        public InternetSession() {}

        public InternetSession(int userId) {
            this.userId = userId;
            this.sessionStart = new Date();
        }
    }

    public static class UrlList {
        public int id;
        public String urlPattern;
        public ListType listType;
        public String category;
        public AgeRating ageAppropriate = AgeRating.ALL_AGES;
        public Date addedDate = new Date();
        public Integer addedBy;
        public boolean isActive = true;
        public String description;
        public String matchType = "domain"; // exact, wildcard, regex, domain

        public UrlList() {}

        public UrlList(String urlPattern, ListType listType) {
            this.urlPattern = urlPattern;
            this.listType = listType;
        }
    }

    public static class YouTubeChannel {
        public int id;
        public String channelId;
        public String channelName;
        public String channelUrl;
        public String channelHandle; // @channel_handle format
        public String accessType = "blocked"; // allowed, blocked
        public String category;
        public String ageRating = "adult"; // kids, family, teen, adult
        public boolean educationalContent = false;
        public Integer subscriberCount;
        public String description;
        public String thumbnailUrl;
        public Date addedDate = new Date();
        public Integer addedBy;
        public Date lastUpdated = new Date();
        public boolean isActive = true;

        public YouTubeChannel() {}

        public YouTubeChannel(String channelId, String channelName, String channelUrl) {
            this.channelId = channelId;
            this.channelName = channelName;
            this.channelUrl = channelUrl;
        }
    }

    public static class UserYouTubePermission {
        public int id;
        public int userId;
        public int channelId;
        public String permissionType = "block"; // allow, block, time_limited
        public Integer dailyLimitMinutes;
        public Integer grantedBy;
        public Date grantedDate = new Date();
        public Date expiresDate;
        public boolean isActive = true;

        public UserYouTubePermission() {}

        public UserYouTubePermission(int userId, int channelId, String permissionType) {
            this.userId = userId;
            this.channelId = channelId;
            this.permissionType = permissionType;
        }
    }

    public static class BlockedAttempt {
        public int id;
        public Integer userId;
        public String url;
        public String contentType = "website"; // youtube_channel, youtube_video, website, social_media, gaming, adult, other
        public String blockReason; // time_limit, blacklist, age_restriction, schedule, user_block, youtube_time_limit, parental_control
        public String channelInfo; // JSON with channel details if YouTube content
        public Date timestamp = new Date();
        public String deviceInfo;
        public String userAgent;
        public String ipAddress;
        public String severityLevel = "medium"; // low, medium, high, critical

        public BlockedAttempt() {}

        public BlockedAttempt(String url, String blockReason) {
            this.url = url;
            this.blockReason = blockReason;
        }

        public BlockedAttempt(Integer userId, String url, String blockReason) {
            this.userId = userId;
            this.url = url;
            this.blockReason = blockReason;
        }
    }

    public static class AdminUser {
        public int id;
        public String userIdentifier; // AD username, Azure AD object ID, or Android account email
        public String displayName;
        public String email;
        public AuthProvider authenticationProvider;
        public PermissionLevel permissionLevel = PermissionLevel.PARENT;
        public Date lastLogin;
        public Date createdDate = new Date();
        public boolean isActive = true;
        public String profilePictureUrl;
        public String phoneNumber;
        public String emergencyContactEmail;

        public AdminUser() {}

        public AdminUser(String userIdentifier, AuthProvider authProvider) {
            this.userIdentifier = userIdentifier;
            this.authenticationProvider = authProvider;
        }
    }

    public static class AuthSession {
        public int id;
        public int userId;
        public String sessionToken;
        public String deviceInfo;
        public Date loginTimestamp = new Date();
        public Date lastActivity = new Date();
        public Date expiresAt;
        public boolean isValid = true;
        public String ipAddress;
        public String userAgent;

        public AuthSession() {}

        public AuthSession(int userId, String sessionToken, Date expiresAt) {
            this.userId = userId;
            this.sessionToken = sessionToken;
            this.expiresAt = expiresAt;
        }
    }

    public static class AdminSetting {
        public String key;
        public String value;
        public String valueType = "string"; // string, integer, boolean, json
        public String description;
        public String category;
        public boolean isSensitive = false;
        public Date modifiedDate = new Date();
        public Integer modifiedBy;

        public AdminSetting() {}

        public AdminSetting(String key, String value) {
            this.key = key;
            this.value = value;
        }
    }

    public static class ContentCategory {
        public int id;
        public String categoryName;
        public String description;
        public AgeRating ageAppropriate = AgeRating.ALL_AGES;
        public String defaultAction = "block"; // allow, block
        public String colorCode; // For UI color coding
        public String iconName; // For UI icons
        public boolean isSystem = false; // System categories cannot be deleted
        public Date createdDate = new Date();

        public ContentCategory() {}

        public ContentCategory(String categoryName, String description) {
            this.categoryName = categoryName;
            this.description = description;
        }
    }

    // Filter result classes
    public static class FilterResult {
        public boolean isAllowed;
        public String reason;
        public String category;
        public Integer remainingMinutes;

        public FilterResult() {}

        public FilterResult(boolean isAllowed, String reason) {
            this.isAllowed = isAllowed;
            this.reason = reason;
        }

        public static FilterResult allowed(String reason) {
            return allowed(reason, null);
        }

        public static FilterResult allowed(String reason, Integer remainingMinutes) {
            FilterResult result = new FilterResult(true, reason);
            result.remainingMinutes = remainingMinutes;
            return result;
        }

        public static FilterResult blocked(String reason) {
            return blocked(reason, null);
        }

        public static FilterResult blocked(String reason, String category) {
            FilterResult result = new FilterResult(false, reason);
            result.category = category;
            return result;
        }
    }

    public static class AccessResult {
        public boolean isAllowed;
        public String reason;
        public Integer remainingMinutes;

        public AccessResult() {}

        public AccessResult(boolean isAllowed, String reason) {
            this.isAllowed = isAllowed;
            this.reason = reason;
        }

        public static AccessResult allowed() {
            return allowed(null);
        }

        public static AccessResult allowed(Integer remainingMinutes) {
            AccessResult result = new AccessResult(true, "Access granted");
            result.remainingMinutes = remainingMinutes;
            return result;
        }

        public static AccessResult blocked(String reason) {
            return new AccessResult(false, reason);
        }
    }

    // Configuration classes
    public static class FilterConfiguration {
        public boolean enableYouTubeFiltering = true;
        public boolean enableTimeBasedFiltering = true;
        public boolean enableAgeBasedFiltering = true;
        public boolean enableAuditLogging = true;
        public String defaultAdultPolicy = "allow";
        public String defaultTeenPolicy = "filtered";
        public String defaultChildPolicy = "restricted";
        public int maxDailyHoursChild = 2;
        public int maxDailyHoursTeen = 4;
        public int sessionTimeoutMinutes = 120;
        public boolean blockVpnAccess = true;
        public String youTubeApiKey;

        public FilterConfiguration() {}
    }

    // Dashboard data classes
    public static class FamilyDashboardData {
        public List<UserStatusInfo> userStatuses = new ArrayList<>();
        public int totalHoursUsedToday;
        public int totalHoursAllowedToday;
        public List<RecentAlert> recentAlerts = new ArrayList<>();
        public SystemStatus systemStatus;

        public FamilyDashboardData() {}
    }

    public static class UserStatusInfo {
        public int userId;
        public String name;
        public int age;
        public UserType userType;
        public boolean isOnline;
        public int minutesLeftToday;
        public String status; // Online, Time Up, Blocked, Offline
        public String statusColor; // For UI

        public UserStatusInfo() {}

        public UserStatusInfo(int userId, String name, int age, UserType userType) {
            this.userId = userId;
            this.name = name;
            this.age = age;
            this.userType = userType;
        }
    }

    public static class RecentAlert {
        public int userId;
        public String userName;
        public String alertMessage;
        public Date timestamp;
        public String severity;
        public String url;

        public RecentAlert() {}

        public RecentAlert(int userId, String userName, String alertMessage, String severity) {
            this.userId = userId;
            this.userName = userName;
            this.alertMessage = alertMessage;
            this.severity = severity;
            this.timestamp = new Date();
        }
    }

    public static class SystemStatus {
        public boolean isRunning;
        public String version;
        public Date lastUpdate;
        public int activeUsers;
        public int totalBlocksToday;

        public SystemStatus() {}

        public SystemStatus(boolean isRunning, String version) {
            this.isRunning = isRunning;
            this.version = version;
            this.lastUpdate = new Date();
        }
    }
}