package com.urlfilter.services;

import android.accounts.Account;
import android.accounts.AccountManager;
import android.app.ActivityManager;
import android.content.Context;
import android.content.pm.PackageManager;
import android.content.pm.UserInfo;
import android.os.Build;
import android.os.Process;
import android.os.UserHandle;
import android.os.UserManager;
import android.util.Log;

import com.urlfilter.database.DatabaseHelper;
import com.urlfilter.models.DeviceUser;
import com.urlfilter.models.UserType;
import com.urlfilter.utils.AndroidLogger;

import java.util.ArrayList;
import java.util.Date;
import java.util.List;

/**
 * Android User Detection Service for URL Filter System
 * Detects and identifies the current active user for per-user filtering
 */
public class UserDetectionService {
    private static final String TAG = "UserDetectionService";
    
    private final Context context;
    private final DatabaseHelper databaseHelper;
    private final AndroidLogger logger;
    private final UserManager userManager;
    private final ActivityManager activityManager;
    
    // Cache for user detection
    private DeviceUser cachedCurrentUser;
    private long lastUserCheck;
    private static final long USER_CACHE_EXPIRY = 60 * 1000; // 1 minute
    
    public UserDetectionService(Context context, DatabaseHelper databaseHelper) {
        this.context = context;
        this.databaseHelper = databaseHelper;
        this.logger = AndroidLogger.getInstance();
        this.userManager = (UserManager) context.getSystemService(Context.USER_SERVICE);
        this.activityManager = (ActivityManager) context.getSystemService(Context.ACTIVITY_SERVICE);
    }
    
    /**
     * Get the current active user on the Android device
     */
    public DeviceUser getCurrentUser() {
        try {
            // Check cache first
            long now = System.currentTimeMillis();
            if (cachedCurrentUser != null && (now - lastUserCheck) < USER_CACHE_EXPIRY) {
                logger.logTrace("Returning cached current user: " + cachedCurrentUser.getUsername());
                return cachedCurrentUser;
            }
            
            logger.logDebug("Detecting current active Android user");
            
            DeviceUser deviceUser = null;
            
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR1) {
                deviceUser = detectMultiUserProfile();
            }
            
            if (deviceUser == null) {
                deviceUser = detectSingleUserProfile();
            }
            
            if (deviceUser == null) {
                logger.logWarning("Could not detect Android user, creating default");
                deviceUser = createDefaultUser();
            }
            
            // Update cache
            cachedCurrentUser = deviceUser;
            lastUserCheck = now;
            
            logger.logDebug("Current user identified: " + deviceUser.getUsername() + 
                          " (Type: " + deviceUser.getUserType() + ")");
            return deviceUser;
            
        } catch (Exception ex) {
            logger.logError("Error detecting current user", ex);
            return createDefaultUser();
        }
    }
    
    /**
     * Get user from app package name or UID
     */
    public DeviceUser getUserFromApp(String packageName, int uid) {
        try {
            logger.logTrace("Getting user for package: " + packageName + ", UID: " + uid);
            
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR1) {
                // Extract user ID from UID
                int userId = UserHandle.getUserId(uid);
                UserInfo userInfo = getUserInfoById(userId);
                
                if (userInfo != null) {
                    String username = getUsernameFromUserInfo(userInfo);
                    DeviceUser deviceUser = databaseHelper.getDeviceUserByUsername(username);
                    
                    if (deviceUser != null) {
                        return deviceUser;
                    }
                }
            }
            
            // Fallback to current user
            return getCurrentUser();
            
        } catch (Exception ex) {
            logger.logError("Error getting user from app " + packageName, ex);
            return getCurrentUser();
        }
    }
    
    /**
     * Get all active users on the Android device
     */
    public List<DeviceUser> getActiveSystemUsers() {
        List<DeviceUser> activeUsers = new ArrayList<>();
        
        try {
            logger.logDebug("Getting all active system users");
            
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR1) {
                List<UserInfo> users = userManager.getUsers();
                
                for (UserInfo userInfo : users) {
                    if (!userInfo.isEnabled() || userInfo.isGuest()) {
                        continue;
                    }
                    
                    try {
                        String username = getUsernameFromUserInfo(userInfo);
                        DeviceUser deviceUser = databaseHelper.getDeviceUserByUsername(username);
                        
                        if (deviceUser != null && deviceUser.isActive()) {
                            activeUsers.add(deviceUser);
                        }
                    } catch (Exception ex) {
                        logger.logWarning("Error getting user data for user " + userInfo.id, ex);
                    }
                }
            } else {
                // Single user system
                DeviceUser currentUser = getCurrentUser();
                if (currentUser != null) {
                    activeUsers.add(currentUser);
                }
            }
            
            logger.logDebug("Found " + activeUsers.size() + " active system users");
            return activeUsers;
            
        } catch (Exception ex) {
            logger.logError("Error getting active system users", ex);
            return activeUsers;
        }
    }
    
    /**
     * Check if user has specific permission for a resource
     */
    public boolean userHasPermission(DeviceUser user, String permission, String resource) {
        try {
            if (user == null) {
                return false;
            }
            
            // Check user type permissions
            switch (user.getUserType()) {
                case ADMIN:
                    return true; // Admins have all permissions
                
                case ADULT:
                    // Adults have most permissions except admin functions
                    return !permission.toLowerCase().contains("admin");
                
                case TEEN:
                    // Teens have limited permissions
                    String[] teenPermissions = {"browse", "basic_access", "educational"};
                    for (String perm : teenPermissions) {
                        if (permission.toLowerCase().contains(perm)) {
                            return true;
                        }
                    }
                    return false;
                
                case CHILD:
                    // Children have very limited permissions
                    String[] childPermissions = {"educational", "safe_browse"};
                    for (String perm : childPermissions) {
                        if (permission.toLowerCase().contains(perm)) {
                            return true;
                        }
                    }
                    return false;
                
                case GUEST:
                    // Guests have minimal permissions
                    return "basic_access".equals(permission.toLowerCase());
                
                default:
                    return false;
            }
            
        } catch (Exception ex) {
            logger.logError("Error checking permission " + permission + 
                          " for user " + (user != null ? user.getUsername() : "null"), ex);
            return false; // Fail secure
        }
    }
    
    /**
     * Invalidate user cache to force fresh detection
     */
    public void invalidateUserCache() {
        logger.logDebug("Invalidating user detection cache");
        cachedCurrentUser = null;
        lastUserCheck = 0;
    }
    
    // Private helper methods
    
    private DeviceUser detectMultiUserProfile() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.JELLY_BEAN_MR1) {
            return null;
        }
        
        try {
            // Get current user info
            int currentUserId = getCurrentUserId();
            UserInfo currentUserInfo = getUserInfoById(currentUserId);
            
            if (currentUserInfo != null) {
                String username = getUsernameFromUserInfo(currentUserInfo);
                
                // Look up in database
                DeviceUser deviceUser = databaseHelper.getDeviceUserByUsername(username);
                
                if (deviceUser == null) {
                    // Create new user profile
                    deviceUser = createUserFromUserInfo(currentUserInfo);
                    int userId = databaseHelper.insertDeviceUser(deviceUser);
                    deviceUser.setId(userId);
                    logger.logInfo("Created new user profile: " + username + " (ID: " + userId + ")");
                }
                
                return deviceUser;
            }
            
        } catch (Exception ex) {
            logger.logError("Error detecting multi-user profile", ex);
        }
        
        return null;
    }
    
    private DeviceUser detectSingleUserProfile() {
        try {
            // For single user systems, use device owner or primary account
            String username = getPrimaryAccountName();
            
            if (username == null || username.isEmpty()) {
                username = "android_user";
            }
            
            // Look up in database
            DeviceUser deviceUser = databaseHelper.getDeviceUserByUsername(username);
            
            if (deviceUser == null) {
                // Create new user profile
                deviceUser = new DeviceUser();
                deviceUser.setUsername(username);
                deviceUser.setDisplayName(getDisplayNameForUser(username));
                deviceUser.setUserType(determineUserTypeFromContext());
                deviceUser.setActive(true);
                deviceUser.setProfileCreatedDate(new Date());
                
                int userId = databaseHelper.insertDeviceUser(deviceUser);
                deviceUser.setId(userId);
                logger.logInfo("Created new single user profile: " + username + " (ID: " + userId + ")");
            }
            
            return deviceUser;
            
        } catch (Exception ex) {
            logger.logError("Error detecting single user profile", ex);
            return null;
        }
    }
    
    private DeviceUser createDefaultUser() {
        try {
            DeviceUser deviceUser = new DeviceUser();
            deviceUser.setUsername("default_android_user");
            deviceUser.setDisplayName("Android User");
            deviceUser.setUserType(UserType.ADULT);
            deviceUser.setActive(true);
            deviceUser.setProfileCreatedDate(new Date());
            
            // Check if default user already exists
            DeviceUser existing = databaseHelper.getDeviceUserByUsername(deviceUser.getUsername());
            if (existing != null) {
                return existing;
            }
            
            int userId = databaseHelper.insertDeviceUser(deviceUser);
            deviceUser.setId(userId);
            
            logger.logInfo("Created default user profile (ID: " + userId + ")");
            return deviceUser;
            
        } catch (Exception ex) {
            logger.logError("Error creating default user", ex);
            // Return a minimal user object
            DeviceUser deviceUser = new DeviceUser();
            deviceUser.setId(-1);
            deviceUser.setUsername("emergency_user");
            deviceUser.setDisplayName("Emergency User");
            deviceUser.setUserType(UserType.ADULT);
            deviceUser.setActive(true);
            return deviceUser;
        }
    }
    
    private DeviceUser createUserFromUserInfo(UserInfo userInfo) {
        DeviceUser deviceUser = new DeviceUser();
        deviceUser.setUsername(getUsernameFromUserInfo(userInfo));
        deviceUser.setDisplayName(userInfo.name != null ? userInfo.name : deviceUser.getUsername());
        deviceUser.setUserType(determineUserTypeFromUserInfo(userInfo));
        deviceUser.setActive(true);
        deviceUser.setProfileCreatedDate(new Date());
        return deviceUser;
    }
    
    private String getUsernameFromUserInfo(UserInfo userInfo) {
        if (userInfo.name != null && !userInfo.name.isEmpty()) {
            return userInfo.name.replaceAll("\\s+", "_").toLowerCase();
        }
        return "android_user_" + userInfo.id;
    }
    
    private UserType determineUserTypeFromUserInfo(UserInfo userInfo) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR2) {
            if (userInfo.isAdmin()) {
                return UserType.ADMIN;
            }
            if (userInfo.isRestricted()) {
                return UserType.CHILD;
            }
        }
        
        // Default to adult for regular users
        return UserType.ADULT;
    }
    
    private UserType determineUserTypeFromContext() {
        try {
            // Check if device has device admin features enabled
            if (context.getPackageManager().hasSystemFeature(PackageManager.FEATURE_DEVICE_ADMIN)) {
                // Could potentially detect managed profile or work profile
                // For now, default to adult
            }
            
            return UserType.ADULT;
        } catch (Exception ex) {
            logger.logError("Error determining user type from context", ex);
            return UserType.ADULT;
        }
    }
    
    private String getPrimaryAccountName() {
        try {
            AccountManager accountManager = AccountManager.get(context);
            Account[] accounts = accountManager.getAccounts();
            
            // Look for Google account first
            for (Account account : accounts) {
                if ("com.google".equals(account.type)) {
                    return account.name;
                }
            }
            
            // Return first account if no Google account
            if (accounts.length > 0) {
                return accounts[0].name;
            }
            
        } catch (Exception ex) {
            logger.logError("Error getting primary account name", ex);
        }
        
        return null;
    }
    
    private String getDisplayNameForUser(String username) {
        // Convert email to display name or clean up username
        if (username.contains("@")) {
            return username.substring(0, username.indexOf("@"));
        }
        return username.replace("_", " ");
    }
    
    private int getCurrentUserId() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR1) {
            return UserHandle.myUserId();
        }
        return 0; // Single user system
    }
    
    private UserInfo getUserInfoById(int userId) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.JELLY_BEAN_MR1) {
            try {
                return userManager.getUserInfo(userId);
            } catch (Exception ex) {
                logger.logError("Error getting user info for ID " + userId, ex);
            }
        }
        return null;
    }
}

/**
 * Result of user detection operations
 */
class UserDetectionResult {
    private boolean success;
    private DeviceUser user;
    private String errorMessage;
    private long detectionTime;
    
    public static UserDetectionResult successful(DeviceUser user) {
        UserDetectionResult result = new UserDetectionResult();
        result.success = true;
        result.user = user;
        result.detectionTime = System.currentTimeMillis();
        return result;
    }
    
    public static UserDetectionResult failed(String error) {
        UserDetectionResult result = new UserDetectionResult();
        result.success = false;
        result.errorMessage = error;
        result.detectionTime = System.currentTimeMillis();
        return result;
    }
    
    // Getters
    public boolean isSuccess() { return success; }
    public DeviceUser getUser() { return user; }
    public String getErrorMessage() { return errorMessage; }
    public long getDetectionTime() { return detectionTime; }
}