package com.urlfilter.auth;

import android.accounts.Account;
import android.accounts.AccountManager;
import android.app.admin.DevicePolicyManager;
import android.content.ComponentName;
import android.content.Context;
import android.util.Log;

import com.google.android.gms.auth.api.signin.GoogleSignIn;
import com.google.android.gms.auth.api.signin.GoogleSignInAccount;
import com.google.android.gms.auth.api.signin.GoogleSignInClient;
import com.google.android.gms.auth.api.signin.GoogleSignInOptions;
import com.google.android.gms.common.api.ApiException;
import com.google.android.gms.tasks.Task;
import com.urlfilter.database.DatabaseHelper;
import com.urlfilter.models.DataModels;
import com.urlfilter.admin.AdminDeviceAdminReceiver;

import java.util.Date;
import java.util.UUID;
import java.util.concurrent.CompletableFuture;

/**
 * Android Authentication Service supporting Google Sign-In, Work Profiles, and MDM
 * Provides SSO capabilities for admin authentication on Android devices
 */
public class AndroidAuthenticationService {
    
    private static final String TAG = "AndroidAuthService";
    
    private final Context context;
    private final DatabaseHelper databaseHelper;
    private final AccountManager accountManager;
    private final DevicePolicyManager devicePolicyManager;
    private GoogleSignInClient googleSignInClient;
    
    public AndroidAuthenticationService(Context context) {
        this.context = context;
        this.databaseHelper = new DatabaseHelper(context);
        this.accountManager = AccountManager.get(context);
        this.devicePolicyManager = (DevicePolicyManager) context.getSystemService(Context.DEVICE_POLICY_SERVICE);
        initializeGoogleSignIn();
    }
    
    private void initializeGoogleSignIn() {
        try {
            GoogleSignInOptions gso = new GoogleSignInOptions.Builder(GoogleSignInOptions.DEFAULT_SIGN_IN)
                    .requestEmail()
                    .requestProfile()
                    .requestIdToken("your-google-client-id") // Replace with actual client ID
                    .build();
            
            googleSignInClient = GoogleSignIn.getClient(context, gso);
            Log.i(TAG, "Google Sign-In client initialized");
        } catch (Exception e) {
            Log.e(TAG, "Failed to initialize Google Sign-In client", e);
        }
    }
    
    /**
     * Authenticates user using device work profile
     */
    public CompletableFuture<AuthenticationResult> authenticateWithWorkProfile() {
        return CompletableFuture.supplyAsync(() -> {
            try {
                Log.i(TAG, "Attempting work profile authentication");
                
                ComponentName adminComponent = new ComponentName(context, AdminDeviceAdminReceiver.class);
                
                // Check if app is device owner or profile owner
                boolean isDeviceOwner = devicePolicyManager.isDeviceOwnerApp(context.getPackageName());
                boolean isProfileOwner = devicePolicyManager.isProfileOwnerApp(context.getPackageName());
                
                if (!isDeviceOwner && !isProfileOwner) {
                    return AuthenticationResult.failed("App is not configured as device/profile owner");
                }
                
                // Get work profile user information
                WorkProfileInfo workProfileInfo = getWorkProfileInfo();
                if (workProfileInfo == null) {
                    return AuthenticationResult.failed("Unable to get work profile information");
                }
                
                // Create or get admin user
                DataModels.AdminUser adminUser = getOrCreateAdminUser(
                        workProfileInfo.getEmail(),
                        DataModels.AuthProvider.ANDROID_WORK_PROFILE,
                        workProfileInfo.getDisplayName()
                );
                
                // Create authentication session
                DataModels.AuthSession session = createAuthSession(adminUser);
                
                Log.i(TAG, "Work profile authentication successful for: " + workProfileInfo.getEmail());
                return AuthenticationResult.success(adminUser, session.sessionToken);
                
            } catch (Exception e) {
                Log.e(TAG, "Work profile authentication failed", e);
                return AuthenticationResult.failed("Work profile authentication error: " + e.getMessage());
            }
        });
    }
    
    /**
     * Authenticates user using Google account
     */
    public CompletableFuture<AuthenticationResult> authenticateWithGoogleAccount() {
        return CompletableFuture.supplyAsync(() -> {
            try {
                Log.i(TAG, "Attempting Google account authentication");
                
                // Try silent sign-in first
                Task<GoogleSignInAccount> silentSignInTask = googleSignInClient.silentSignIn();
                GoogleSignInAccount account = null;
                
                try {
                    // Wait for silent sign-in to complete
                    account = silentSignInTask.getResult(ApiException.class);
                } catch (ApiException e) {
                    Log.d(TAG, "Silent sign-in failed, interactive sign-in required: " + e.getStatusCode());
                    // Interactive sign-in will be handled by the calling activity
                    return AuthenticationResult.failed("Interactive sign-in required");
                }
                
                if (account == null) {
                    return AuthenticationResult.failed("Google account authentication failed");
                }
                
                // Verify user is authorized admin
                if (!isAuthorizedAdmin(account.getEmail())) {
                    return AuthenticationResult.failed("User not authorized as administrator");
                }
                
                // Create or get admin user
                DataModels.AdminUser adminUser = getOrCreateAdminUser(
                        account.getEmail(),
                        DataModels.AuthProvider.GOOGLE_ACCOUNT,
                        account.getDisplayName()
                );
                
                // Create authentication session
                DataModels.AuthSession session = createAuthSession(adminUser);
                
                Log.i(TAG, "Google authentication successful for: " + account.getEmail());
                return AuthenticationResult.success(adminUser, session.sessionToken);
                
            } catch (Exception e) {
                Log.e(TAG, "Google account authentication failed", e);
                return AuthenticationResult.failed("Google authentication error: " + e.getMessage());
            }
        });
    }
    
    /**
     * Authenticates using device accounts (Google, Microsoft, etc.)
     */
    public CompletableFuture<AuthenticationResult> authenticateWithDeviceAccounts() {
        return CompletableFuture.supplyAsync(() -> {
            try {
                Log.i(TAG, "Attempting device account authentication");
                
                // Get available accounts
                Account[] googleAccounts = accountManager.getAccountsByType("com.google");
                Account[] microsoftAccounts = accountManager.getAccountsByType("com.microsoft.workaccount");
                
                Account selectedAccount = null;
                DataModels.AuthProvider authProvider = DataModels.AuthProvider.GOOGLE_ACCOUNT;
                
                // Prefer work accounts
                if (microsoftAccounts.length > 0) {
                    selectedAccount = microsoftAccounts[0];
                    authProvider = DataModels.AuthProvider.ANDROID_WORK_PROFILE;
                } else if (googleAccounts.length > 0) {
                    selectedAccount = googleAccounts[0];
                    authProvider = DataModels.AuthProvider.GOOGLE_ACCOUNT;
                } else {
                    return AuthenticationResult.failed("No suitable device accounts found");
                }
                
                // Verify user is authorized admin
                if (!isAuthorizedAdmin(selectedAccount.name)) {
                    return AuthenticationResult.failed("User not authorized as administrator");
                }
                
                // Create or get admin user
                DataModels.AdminUser adminUser = getOrCreateAdminUser(
                        selectedAccount.name,
                        authProvider,
                        selectedAccount.name
                );
                
                // Create authentication session
                DataModels.AuthSession session = createAuthSession(adminUser);
                
                Log.i(TAG, "Device account authentication successful for: " + selectedAccount.name);
                return AuthenticationResult.success(adminUser, session.sessionToken);
                
            } catch (Exception e) {
                Log.e(TAG, "Device account authentication failed", e);
                return AuthenticationResult.failed("Device account authentication error: " + e.getMessage());
            }
        });
    }
    
    /**
     * Validates an existing authentication session
     */
    public CompletableFuture<Boolean> validateSession(String sessionToken) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                if (sessionToken == null || sessionToken.isEmpty()) {
                    return false;
                }
                
                DataModels.AuthSession session = databaseHelper.getAuthSessionByToken(sessionToken);
                
                if (session == null || !session.isValid || session.expiresAt.before(new Date())) {
                    if (session != null) {
                        // Invalidate expired session
                        session.isValid = false;
                        databaseHelper.updateAuthSession(session);
                    }
                    return false;
                }
                
                // Update last activity
                session.lastActivity = new Date();
                databaseHelper.updateAuthSession(session);
                
                return true;
                
            } catch (Exception e) {
                Log.e(TAG, "Error validating session", e);
                return false;
            }
        });
    }
    
    /**
     * Gets admin user from session token
     */
    public CompletableFuture<DataModels.AdminUser> getAdminUserFromSession(String sessionToken) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                DataModels.AuthSession session = databaseHelper.getAuthSessionByToken(sessionToken);
                if (session != null && session.isValid && session.expiresAt.after(new Date())) {
                    return databaseHelper.getAdminUser(session.userId);
                }
                return null;
            } catch (Exception e) {
                Log.e(TAG, "Error getting admin user from session", e);
                return null;
            }
        });
    }
    
    /**
     * Invalidates user session (logout)
     */
    public CompletableFuture<Boolean> invalidateSession(String sessionToken) {
        return CompletableFuture.supplyAsync(() -> {
            try {
                DataModels.AuthSession session = databaseHelper.getAuthSessionByToken(sessionToken);
                if (session != null) {
                    session.isValid = false;
                    databaseHelper.updateAuthSession(session);
                    
                    Log.i(TAG, "Session invalidated for user " + session.userId);
                    return true;
                }
                return false;
            } catch (Exception e) {
                Log.e(TAG, "Error invalidating session", e);
                return false;
            }
        });
    }
    
    /**
     * Signs out from Google account
     */
    public CompletableFuture<Void> signOutGoogle() {
        return CompletableFuture.supplyAsync(() -> {
            try {
                if (googleSignInClient != null) {
                    googleSignInClient.signOut();
                    Log.i(TAG, "Google sign-out successful");
                }
                return null;
            } catch (Exception e) {
                Log.e(TAG, "Error signing out from Google", e);
                return null;
            }
        });
    }
    
    private WorkProfileInfo getWorkProfileInfo() {
        try {
            // Get work profile account information
            Account[] workAccounts = accountManager.getAccountsByType("com.microsoft.workaccount");
            if (workAccounts.length > 0) {
                Account workAccount = workAccounts[0];
                return new WorkProfileInfo(workAccount.name, workAccount.name);
            }
            
            // Fallback to Google work account
            Account[] googleAccounts = accountManager.getAccountsByType("com.google");
            for (Account account : googleAccounts) {
                // Check if this is a work account (simplified check)
                if (account.name.contains("@") && !account.name.contains("gmail.com")) {
                    return new WorkProfileInfo(account.name, account.name);
                }
            }
            
            return null;
        } catch (Exception e) {
            Log.e(TAG, "Error getting work profile info", e);
            return null;
        }
    }
    
    private boolean isAuthorizedAdmin(String email) {
        try {
            // Check against configured admin list
            String adminEmails = databaseHelper.getConfigValue("authorized_admin_emails", "");
            String[] adminList = adminEmails.split(",");
            
            for (String adminEmail : adminList) {
                if (adminEmail.trim().equalsIgnoreCase(email)) {
                    return true;
                }
            }
            
            // Check if email domain is in allowed admin domains
            String adminDomains = databaseHelper.getConfigValue("authorized_admin_domains", "");
            if (!adminDomains.isEmpty() && email.contains("@")) {
                String domain = email.split("@")[1];
                String[] allowedDomains = adminDomains.split(",");
                
                for (String allowedDomain : allowedDomains) {
                    if (domain.equalsIgnoreCase(allowedDomain.trim())) {
                        return true;
                    }
                }
            }
            
            return false;
        } catch (Exception e) {
            Log.e(TAG, "Error checking admin authorization", e);
            return false;
        }
    }
    
    private DataModels.AdminUser getOrCreateAdminUser(String userIdentifier, DataModels.AuthProvider provider, String displayName) {
        try {
            // Try to find existing admin user
            DataModels.AdminUser adminUser = databaseHelper.getAdminUserByIdentifier(userIdentifier);
            
            if (adminUser == null) {
                // Create new admin user
                adminUser = new DataModels.AdminUser();
                adminUser.userIdentifier = userIdentifier;
                adminUser.displayName = displayName;
                adminUser.email = userIdentifier;
                adminUser.authenticationProvider = provider;
                adminUser.permissionLevel = DataModels.PermissionLevel.PARENT; // Default level
                adminUser.createdDate = new Date();
                adminUser.isActive = true;
                
                long userId = databaseHelper.insertAdminUser(adminUser);
                adminUser.id = (int) userId;
                
                Log.i(TAG, "Created new admin user: " + userIdentifier);
            } else {
                // Update last login
                adminUser.lastLogin = new Date();
                databaseHelper.updateAdminUser(adminUser);
            }
            
            return adminUser;
        } catch (Exception e) {
            Log.e(TAG, "Error getting/creating admin user: " + userIdentifier, e);
            throw new RuntimeException(e);
        }
    }
    
    private DataModels.AuthSession createAuthSession(DataModels.AdminUser adminUser) {
        try {
            DataModels.AuthSession session = new DataModels.AuthSession();
            session.userId = adminUser.id;
            session.sessionToken = generateSessionToken();
            session.loginTimestamp = new Date();
            session.lastActivity = new Date();
            session.expiresAt = new Date(System.currentTimeMillis() + (8 * 60 * 60 * 1000)); // 8 hours
            session.isValid = true;
            session.deviceInfo = android.os.Build.MODEL;
            session.ipAddress = getLocalIpAddress();
            
            long sessionId = databaseHelper.insertAuthSession(session);
            session.id = (int) sessionId;
            
            Log.i(TAG, "Created auth session for user " + adminUser.userIdentifier);
            return session;
        } catch (Exception e) {
            Log.e(TAG, "Error creating auth session", e);
            throw new RuntimeException(e);
        }
    }
    
    private String generateSessionToken() {
        return UUID.randomUUID().toString().replace("-", "") + 
               UUID.randomUUID().toString().replace("-", "");
    }
    
    private String getLocalIpAddress() {
        try {
            java.net.InetAddress inetAddress = java.net.InetAddress.getLocalHost();
            return inetAddress.getHostAddress();
        } catch (Exception e) {
            return "127.0.0.1";
        }
    }
    
    /**
     * Work profile information holder
     */
    private static class WorkProfileInfo {
        private final String email;
        private final String displayName;
        
        public WorkProfileInfo(String email, String displayName) {
            this.email = email;
            this.displayName = displayName;
        }
        
        public String getEmail() {
            return email;
        }
        
        public String getDisplayName() {
            return displayName;
        }
    }
    
    /**
     * Authentication result class
     */
    public static class AuthenticationResult {
        private final boolean success;
        private final String errorMessage;
        private final DataModels.AdminUser adminUser;
        private final String sessionToken;
        
        private AuthenticationResult(boolean success, String errorMessage, DataModels.AdminUser adminUser, String sessionToken) {
            this.success = success;
            this.errorMessage = errorMessage;
            this.adminUser = adminUser;
            this.sessionToken = sessionToken;
        }
        
        public static AuthenticationResult success(DataModels.AdminUser adminUser, String sessionToken) {
            return new AuthenticationResult(true, null, adminUser, sessionToken);
        }
        
        public static AuthenticationResult failed(String errorMessage) {
            return new AuthenticationResult(false, errorMessage, null, null);
        }
        
        public boolean isSuccess() {
            return success;
        }
        
        public String getErrorMessage() {
            return errorMessage;
        }
        
        public DataModels.AdminUser getAdminUser() {
            return adminUser;
        }
        
        public String getSessionToken() {
            return sessionToken;
        }
    }
}