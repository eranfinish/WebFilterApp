using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Core filtering engine for URL Filter System
    /// Handles URL pattern matching, user-specific rules, and filtering decisions
    /// </summary>
    public class FilterEngine
    {
        private readonly ILogger<FilterEngine> _logger;
        private readonly DatabaseService _databaseService;
        private readonly TimeControlManager _timeManager;
        
        // Cached filter rules for performance
        private List<UrlList> _cachedUrlLists;
        private Dictionary<string, List<UserUrlPermission>> _cachedUserPermissions;
        private DateTime _lastCacheRefresh;
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

        public FilterEngine(ILogger<FilterEngine> logger, DatabaseService databaseService)
        {
            _logger = logger;
            _databaseService = databaseService;
            _timeManager = new TimeControlManager(_logger, databaseService);
            
            _cachedUrlLists = new List<UrlList>();
            _cachedUserPermissions = new Dictionary<string, List<UserUrlPermission>>();
            _lastCacheRefresh = DateTime.MinValue;
        }

        /// <summary>
        /// Check if a URL should be allowed or blocked for a specific user
        /// </summary>
        public async Task<FilterResult> CheckUrlAccessAsync(string url, DeviceUser user)
        {
            try
            {
                _logger.LogTrace($"Checking URL access: {url} for user {user?.Username ?? "unknown"}", "FilterEngine");

                // Refresh cache if needed
                await RefreshCacheIfNeededAsync();

                // Check time-based restrictions first
                if (user != null)
                {
                    var timeResult = await _timeManager.CheckInternetAccessAsync(user.Id);
                    if (!timeResult.IsAllowed)
                    {
                        _logger.LogInformation($"Time restriction blocked access to {url} for user {user.Username}: {timeResult.Reason}", "FilterEngine");
                        return FilterResult.Blocked(timeResult.Reason, "time_restriction");
                    }
                }

                // Check user-specific permissions first (highest priority)
                if (user != null)
                {
                    var userPermissionResult = await CheckUserSpecificPermissionsAsync(url, user);
                    if (userPermissionResult != null)
                    {
                        _logger.LogDebug($"User-specific permission applied for {url}: {userPermissionResult.Reason}", "FilterEngine");
                        return userPermissionResult;
                    }
                }

                // Check global URL lists (whitelist/blacklist)
                var globalResult = await CheckGlobalUrlListsAsync(url, user);
                if (globalResult != null)
                {
                    _logger.LogDebug($"Global filter applied for {url}: {globalResult.Reason}", "FilterEngine");
                    return globalResult;
                }

                // Check age-appropriate content
                if (user != null && user.UserType == UserType.Child)
                {
                    var ageResult = CheckAgeAppropriateContent(url, user);
                    if (!ageResult.IsAllowed)
                    {
                        _logger.LogInformation($"Age restriction blocked {url} for child user {user.Username}", "FilterEngine");
                        return ageResult;
                    }
                }

                // Apply default policy based on user type
                var defaultResult = ApplyDefaultPolicy(url, user);
                _logger.LogTrace($"Applied default policy for {url}: {defaultResult.Reason}", "FilterEngine");
                
                return defaultResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking URL access for {url}");
                
                // Fail-safe: Allow access on error to prevent complete internet blockage
                return FilterResult.Allowed("Error in filtering - fail-safe allow");
            }
        }

        /// <summary>
        /// Check packet access based on traffic analysis
        /// </summary>
        public FilterResult CheckPacketAccess(object analysis, DeviceUser user)
        {
            try
            {
                // This is a simplified implementation
                // In practice, you would extract URL from the traffic analysis
                
                var url = ExtractUrlFromAnalysis(analysis);
                if (string.IsNullOrEmpty(url))
                {
                    return FilterResult.Allowed("No URL detected in packet");
                }

                // Use async version synchronously for now (could be improved)
                var result = CheckUrlAccessAsync(url, user).GetAwaiter().GetResult();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking packet access");
                return FilterResult.Allowed("Error in packet filtering - fail-safe allow");
            }
        }

        /// <summary>
        /// Check time-based access for a user
        /// </summary>
        public AccessResult CheckTimeAccess(DeviceUser user, UserTimeRestriction timeRestriction, DateTime currentTime)
        {
            try
            {
                return _timeManager.CheckTimeAccess(user, timeRestriction, currentTime);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking time access");
                return AccessResult.Allowed(); // Fail-safe
            }
        }

        /// <summary>
        /// Check YouTube-specific access
        /// </summary>
        public async Task<FilterResult> CheckYouTubeAccessAsync(string channelId, string videoUrl, DeviceUser user)
        {
            try
            {
                _logger.LogTrace($"Checking YouTube access: Channel {channelId}, Video {videoUrl} for user {user?.Username}", "FilterEngine");

                // Check time restrictions first
                if (user != null)
                {
                    var timeResult = await _timeManager.CheckInternetAccessAsync(user.Id);
                    if (!timeResult.IsAllowed)
                    {
                        return FilterResult.Blocked(timeResult.Reason, "time_restriction");
                    }
                }

                // Get channel information
                var channel = await _databaseService.GetYouTubeChannelByIdAsync(channelId);
                
                if (channel != null)
                {
                    // Check if channel is explicitly blocked
                    if (channel.AccessType == "blocked")
                    {
                        return FilterResult.Blocked($"YouTube channel '{channel.ChannelName}' is blocked", "youtube_channel_blocked");
                    }

                    // Check age appropriateness
                    if (user?.UserType == UserType.Child && channel.AgeRating == "adult")
                    {
                        return FilterResult.Blocked($"YouTube channel '{channel.ChannelName}' is not appropriate for children", "age_restriction");
                    }

                    // Check if channel is explicitly allowed
                    if (channel.AccessType == "allowed")
                    {
                        return FilterResult.Allowed($"YouTube channel '{channel.ChannelName}' is explicitly allowed");
                    }
                }

                // Apply default YouTube policy
                var defaultYouTubePolicy = await GetDefaultYouTubePolicyAsync(user);
                
                switch (defaultYouTubePolicy.ToLower())
                {
                    case "allow":
                        return FilterResult.Allowed("Default YouTube policy allows access");
                    case "block":
                        return FilterResult.Blocked("Default YouTube policy blocks access", "youtube_default_block");
                    case "educational_only":
                        if (channel?.EducationalContent == true)
                        {
                            return FilterResult.Allowed("Educational YouTube content allowed");
                        }
                        else
                        {
                            return FilterResult.Blocked("Only educational YouTube content allowed", "youtube_educational_only");
                        }
                    default:
                        return FilterResult.Allowed("Unknown YouTube policy - defaulting to allow");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking YouTube access for channel {channelId}");
                return FilterResult.Allowed("Error in YouTube filtering - fail-safe allow");
            }
        }

        #region Private Methods

        private async Task RefreshCacheIfNeededAsync()
        {
            if (DateTime.Now - _lastCacheRefresh > _cacheExpiry)
            {
                _logger.LogDebug("Refreshing filter cache", "FilterEngine");
                
                try
                {
                    _cachedUrlLists = await _databaseService.GetAllUrlListsAsync();
                    _lastCacheRefresh = DateTime.Now;
                    
                    _logger.LogDebug($"Filter cache refreshed: {_cachedUrlLists.Count} URL rules loaded", "FilterEngine");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error refreshing filter cache");
                    // Continue with old cache
                }
            }
        }

        private async Task<FilterResult> CheckUserSpecificPermissionsAsync(string url, DeviceUser user)
        {
            try
            {
                // This would check user_url_permissions table
                // For now, simplified implementation
                
                var domain = ExtractDomainFromUrl(url);
                
                // Check if user has specific permission for this domain
                // Implementation would query database for user-specific rules
                
                return null; // No user-specific rule found
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking user-specific permissions");
                return null;
            }
        }

        private async Task<FilterResult> CheckGlobalUrlListsAsync(string url, DeviceUser user)
        {
            try
            {
                var domain = ExtractDomainFromUrl(url);
                
                foreach (var urlList in _cachedUrlLists)
                {
                    if (IsUrlMatch(url, domain, urlList.UrlPattern, urlList.MatchType))
                    {
                        if (urlList.ListType == ListType.Blacklist)
                        {
                            return FilterResult.Blocked($"URL blocked by rule: {urlList.UrlPattern}", "blacklist");
                        }
                        else if (urlList.ListType == ListType.Whitelist)
                        {
                            return FilterResult.Allowed($"URL allowed by whitelist rule: {urlList.UrlPattern}");
                        }
                    }
                }

                return null; // No matching rule found
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking global URL lists");
                return null;
            }
        }

        private FilterResult CheckAgeAppropriateContent(string url, DeviceUser user)
        {
            try
            {
                // Simplified age-appropriate content checking
                var domain = ExtractDomainFromUrl(url);
                
                // List of domains typically not appropriate for children
                var adultDomains = new[]
                {
                    "reddit.com", "twitter.com", "facebook.com", "instagram.com",
                    "tiktok.com", "snapchat.com", "discord.com"
                };

                if (user.UserType == UserType.Child)
                {
                    if (adultDomains.Any(d => domain.Contains(d)))
                    {
                        return FilterResult.Blocked($"Site {domain} not appropriate for children", "age_restriction");
                    }
                }

                return FilterResult.Allowed("Age-appropriate content");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking age-appropriate content");
                return FilterResult.Allowed("Error in age checking - fail-safe allow");
            }
        }

        private FilterResult ApplyDefaultPolicy(string url, DeviceUser user)
        {
            try
            {
                var userType = user?.UserType ?? UserType.Adult;
                
                switch (userType)
                {
                    case UserType.Adult:
                        return FilterResult.Allowed("Default adult policy allows access");
                        
                    case UserType.Teen:
                        // Teens have filtered access - block social media during school hours
                        var currentHour = DateTime.Now.Hour;
                        if (currentHour >= 8 && currentHour < 15) // School hours
                        {
                            var domain = ExtractDomainFromUrl(url);
                            var socialMediaDomains = new[] { "facebook.com", "instagram.com", "tiktok.com", "snapchat.com" };
                            
                            if (socialMediaDomains.Any(d => domain.Contains(d)))
                            {
                                return FilterResult.Blocked("Social media blocked during school hours", "teen_school_hours");
                            }
                        }
                        return FilterResult.Allowed("Default teen policy allows access");
                        
                    case UserType.Child:
                        // Children have restricted access - only allow educational and safe sites
                        var childDomain = ExtractDomainFromUrl(url);
                        var educationalDomains = new[] 
                        { 
                            "khan", "educational", "school", "library", "wikipedia", 
                            "nationalgeographic", "nasa", "smithsonian", "pbs"
                        };
                        
                        if (educationalDomains.Any(d => childDomain.Contains(d)))
                        {
                            return FilterResult.Allowed("Educational content allowed for children");
                        }
                        
                        return FilterResult.Blocked("Default child policy restricts access", "child_restricted");
                        
                    case UserType.Guest:
                        return FilterResult.Blocked("Guest users have limited access", "guest_restricted");
                        
                    default:
                        return FilterResult.Allowed("Unknown user type - defaulting to allow");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying default policy");
                return FilterResult.Allowed("Error in default policy - fail-safe allow");
            }
        }

        private bool IsUrlMatch(string url, string domain, string pattern, string matchType)
        {
            try
            {
                switch (matchType?.ToLower())
                {
                    case "exact":
                        return string.Equals(url, pattern, StringComparison.OrdinalIgnoreCase);
                        
                    case "domain":
                        return domain.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                        
                    case "wildcard":
                        return IsWildcardMatch(url, pattern) || IsWildcardMatch(domain, pattern);
                        
                    case "regex":
                        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                        return regex.IsMatch(url) || regex.IsMatch(domain);
                        
                    default:
                        // Default to domain matching
                        return domain.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error matching URL pattern: {pattern}");
                return false;
            }
        }

        private bool IsWildcardMatch(string text, string pattern)
        {
            try
            {
                // Convert wildcard pattern to regex
                var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var regex = new Regex(regexPattern, RegexOptions.IgnoreCase);
                return regex.IsMatch(text);
            }
            catch
            {
                return false;
            }
        }

        private string ExtractDomainFromUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                {
                    return uri.Host.ToLowerInvariant();
                }
                
                // Handle URLs without protocol
                if (!url.Contains("://"))
                {
                    url = "http://" + url;
                    if (Uri.TryCreate(url, UriKind.Absolute, out uri))
                    {
                        return uri.Host.ToLowerInvariant();
                    }
                }

                // Fallback - extract domain manually
                var parts = url.Split('/');
                if (parts.Length > 0)
                {
                    var domainPart = parts[0].Contains("://") ? parts[2] : parts[0];
                    return domainPart.Split(':')[0].ToLowerInvariant();
                }

                return url.ToLowerInvariant();
            }
            catch
            {
                return url.ToLowerInvariant();
            }
        }

        private string ExtractUrlFromAnalysis(object analysis)
        {
            try
            {
                // This would extract URL from traffic analysis object
                // For now, return empty string as placeholder
                return "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<string> GetDefaultYouTubePolicyAsync(DeviceUser user)
        {
            try
            {
                var userType = user?.UserType ?? UserType.Adult;
                
                switch (userType)
                {
                    case UserType.Adult:
                        return await _databaseService.GetConfigValueAsync("default_youtube_policy_adult", "allow");
                    case UserType.Teen:
                        return await _databaseService.GetConfigValueAsync("default_youtube_policy_teen", "educational_only");
                    case UserType.Child:
                        return await _databaseService.GetConfigValueAsync("default_youtube_policy_child", "block");
                    default:
                        return "block";
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting default YouTube policy");
                return "allow"; // Fail-safe
            }
        }

        #endregion
    }

    /// <summary>
    /// Time-based access control manager
    /// </summary>
    public class TimeControlManager
    {
        private readonly ILogger _logger;
        private readonly DatabaseService _databaseService;

        public TimeControlManager(ILogger logger, DatabaseService databaseService)
        {
            _logger = logger;
            _databaseService = databaseService;
        }

        public async Task<AccessResult> CheckInternetAccessAsync(int userId)
        {
            try
            {
                var now = DateTime.Now;
                var dayOfWeek = (int)now.DayOfWeek;
                
                // Get time restriction for current day
                var timeRestriction = await _databaseService.GetUserTimeRestrictionAsync(userId, dayOfWeek);
                
                if (timeRestriction != null && timeRestriction.IsActive)
                {
                    // Check if current time is within allowed hours
                    if (timeRestriction.AllowedStartTime.HasValue && timeRestriction.AllowedEndTime.HasValue)
                    {
                        var currentTime = now.TimeOfDay;
                        var startTime = timeRestriction.AllowedStartTime.Value;
                        var endTime = timeRestriction.AllowedEndTime.Value;
                        
                        if (currentTime < startTime || currentTime > endTime)
                        {
                            return AccessResult.Blocked($"Outside allowed hours: {startTime:hh\\:mm} - {endTime:hh\\:mm}");
                        }
                    }

                    // Check daily time limit
                    if (timeRestriction.DailyLimitMinutes.HasValue)
                    {
                        var todayUsage = await GetDailyUsageMinutesAsync(userId, now.Date);
                        var remainingMinutes = timeRestriction.DailyLimitMinutes.Value - todayUsage;
                        
                        if (remainingMinutes <= 0)
                        {
                            return AccessResult.Blocked($"Daily time limit reached: {timeRestriction.DailyLimitMinutes.Value} minutes");
                        }
                        
                        return AccessResult.Allowed(remainingMinutes);
                    }
                }

                return AccessResult.Allowed();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking internet access for user {userId}");
                return AccessResult.Allowed(); // Fail-safe
            }
        }

        public AccessResult CheckTimeAccess(DeviceUser user, UserTimeRestriction timeRestriction, DateTime currentTime)
        {
            try
            {
                if (timeRestriction == null || !timeRestriction.IsActive)
                {
                    return AccessResult.Allowed();
                }

                // Check time window
                if (timeRestriction.AllowedStartTime.HasValue && timeRestriction.AllowedEndTime.HasValue)
                {
                    var time = currentTime.TimeOfDay;
                    var start = timeRestriction.AllowedStartTime.Value;
                    var end = timeRestriction.AllowedEndTime.Value;
                    
                    if (time < start || time > end)
                    {
                        return AccessResult.Blocked($"Outside allowed hours: {start:hh\\:mm} - {end:hh\\:mm}");
                    }
                }

                return AccessResult.Allowed();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking time access");
                return AccessResult.Allowed(); // Fail-safe
            }
        }

        private async Task<int> GetDailyUsageMinutesAsync(int userId, DateTime date)
        {
            try
            {
                // This would query the daily_usage_log table
                // For now, return 0 as placeholder
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting daily usage for user {userId}");
                return 0;
            }
        }
    }
}