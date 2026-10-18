-- URL Filter System Database Schema
-- Comprehensive schema for cross-platform URL filtering with user management

-- Device Users Management
CREATE TABLE IF NOT EXISTS device_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT UNIQUE NOT NULL, -- Windows username or Android user ID
    display_name TEXT NOT NULL,
    user_type TEXT CHECK(user_type IN ('adult', 'teen', 'child', 'guest')) DEFAULT 'adult',
    age INTEGER,
    profile_created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT 1,
    parent_user_id INTEGER REFERENCES device_users(id), -- For child accounts
    avatar_path TEXT,
    last_login TIMESTAMP,
    created_by INTEGER REFERENCES admin_users(id)
);

-- User-Specific URL Permissions
CREATE TABLE IF NOT EXISTS user_url_permissions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    url_pattern TEXT NOT NULL,
    permission_type TEXT CHECK(permission_type IN ('allow', 'block', 'inherit')) DEFAULT 'inherit',
    category TEXT,
    granted_by INTEGER REFERENCES admin_users(id),
    granted_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    expires_date TIMESTAMP, -- Optional expiration
    notes TEXT,
    is_active BOOLEAN DEFAULT 1
);

-- Time-Based Access Control
CREATE TABLE IF NOT EXISTS user_time_restrictions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    day_of_week INTEGER CHECK(day_of_week BETWEEN 0 AND 6), -- 0=Sunday, 6=Saturday
    allowed_start_time TIME, -- e.g., '08:00:00'
    allowed_end_time TIME,   -- e.g., '20:00:00'
    daily_limit_minutes INTEGER, -- Total minutes allowed per day
    is_active BOOLEAN DEFAULT 1,
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Daily Usage Tracking
CREATE TABLE IF NOT EXISTS daily_usage_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    usage_date DATE NOT NULL,
    minutes_used INTEGER DEFAULT 0,
    last_activity_time TIMESTAMP,
    sessions_count INTEGER DEFAULT 0,
    youtube_minutes INTEGER DEFAULT 0,
    educational_minutes INTEGER DEFAULT 0,
    social_minutes INTEGER DEFAULT 0,
    gaming_minutes INTEGER DEFAULT 0,
    UNIQUE(user_id, usage_date)
);

-- Internet Sessions
CREATE TABLE IF NOT EXISTS internet_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    session_start TIMESTAMP NOT NULL,
    session_end TIMESTAMP,
    duration_minutes INTEGER,
    total_requests INTEGER DEFAULT 0,
    blocked_requests INTEGER DEFAULT 0,
    allowed_requests INTEGER DEFAULT 0,
    top_domains TEXT, -- JSON array of most visited domains
    device_info TEXT, -- Device/browser information
    ip_address TEXT
);

-- URL Lists Table (Enhanced)
CREATE TABLE IF NOT EXISTS url_lists (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    url_pattern TEXT NOT NULL,
    list_type TEXT CHECK(list_type IN ('whitelist', 'blacklist')) NOT NULL,
    category TEXT,
    age_appropriate TEXT CHECK(age_appropriate IN ('all_ages', 'teen_plus', 'adult_only')) DEFAULT 'all_ages',
    added_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    added_by INTEGER REFERENCES admin_users(id),
    is_active BOOLEAN DEFAULT 1,
    description TEXT,
    match_type TEXT CHECK(match_type IN ('exact', 'wildcard', 'regex', 'domain')) DEFAULT 'domain'
);

-- YouTube Channel Management (Enhanced)
CREATE TABLE IF NOT EXISTS youtube_channels (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    channel_id TEXT UNIQUE NOT NULL,
    channel_name TEXT NOT NULL,
    channel_url TEXT NOT NULL,
    channel_handle TEXT, -- @channel_handle format
    access_type TEXT CHECK(access_type IN ('allowed', 'blocked')) DEFAULT 'blocked',
    category TEXT,
    age_rating TEXT CHECK(age_rating IN ('kids', 'family', 'teen', 'adult')) DEFAULT 'adult',
    educational_content BOOLEAN DEFAULT 0,
    subscriber_count INTEGER,
    description TEXT,
    thumbnail_url TEXT,
    added_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    added_by INTEGER REFERENCES admin_users(id),
    last_updated TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT 1
);

-- User-Specific YouTube Access
CREATE TABLE IF NOT EXISTS user_youtube_permissions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    channel_id INTEGER NOT NULL REFERENCES youtube_channels(id) ON DELETE CASCADE,
    permission_type TEXT CHECK(permission_type IN ('allow', 'block', 'time_limited')) DEFAULT 'block',
    daily_limit_minutes INTEGER, -- YouTube time limit for this channel
    granted_by INTEGER REFERENCES admin_users(id),
    granted_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    expires_date TIMESTAMP,
    is_active BOOLEAN DEFAULT 1,
    UNIQUE(user_id, channel_id)
);

-- YouTube Video Patterns (for granular control)
CREATE TABLE IF NOT EXISTS youtube_patterns (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    pattern_type TEXT CHECK(pattern_type IN ('channel_id', 'channel_name', 'video_id', 'playlist_id', 'search_term')) NOT NULL,
    pattern_value TEXT NOT NULL,
    access_type TEXT CHECK(access_type IN ('allowed', 'blocked')) DEFAULT 'blocked',
    age_restriction TEXT CHECK(age_restriction IN ('all_ages', 'teen_plus', 'adult_only')) DEFAULT 'all_ages',
    description TEXT,
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT 1
);

-- Blocked Attempts Log (Enhanced)
CREATE TABLE IF NOT EXISTS blocked_attempts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER REFERENCES device_users(id),
    url TEXT NOT NULL,
    content_type TEXT CHECK(content_type IN ('youtube_channel', 'youtube_video', 'website', 'social_media', 'gaming', 'adult', 'other')) DEFAULT 'website',
    block_reason TEXT CHECK(block_reason IN ('time_limit', 'blacklist', 'age_restriction', 'schedule', 'user_block', 'youtube_time_limit', 'parental_control')) NOT NULL,
    channel_info TEXT, -- JSON with channel details if YouTube content
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    device_info TEXT,
    user_agent TEXT,
    ip_address TEXT,
    severity_level TEXT CHECK(severity_level IN ('low', 'medium', 'high', 'critical')) DEFAULT 'medium'
);

-- Admin Users and Permissions
CREATE TABLE IF NOT EXISTS admin_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_identifier TEXT UNIQUE NOT NULL, -- AD username, Azure AD object ID, or Android account email
    display_name TEXT,
    email TEXT,
    authentication_provider TEXT CHECK(authentication_provider IN ('windows_ad', 'azure_ad', 'google_account', 'android_work_profile', 'local')) NOT NULL,
    permission_level TEXT CHECK(permission_level IN ('super_admin', 'admin', 'moderator', 'parent')) DEFAULT 'parent',
    last_login TIMESTAMP,
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT 1,
    profile_picture_url TEXT,
    phone_number TEXT,
    emergency_contact_email TEXT
);

-- SSO Authentication Sessions
CREATE TABLE IF NOT EXISTS auth_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES admin_users(id) ON DELETE CASCADE,
    session_token TEXT UNIQUE NOT NULL,
    device_info TEXT,
    login_timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    last_activity TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    expires_at TIMESTAMP NOT NULL,
    is_valid BOOLEAN DEFAULT 1,
    ip_address TEXT,
    user_agent TEXT
);

-- Admin Settings
CREATE TABLE IF NOT EXISTS admin_settings (
    key TEXT PRIMARY KEY,
    value TEXT,
    value_type TEXT CHECK(value_type IN ('string', 'integer', 'boolean', 'json')) DEFAULT 'string',
    description TEXT,
    category TEXT,
    is_sensitive BOOLEAN DEFAULT 0, -- Marks settings that should be encrypted
    modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    modified_by INTEGER REFERENCES admin_users(id)
);

-- Content Categories for easy management
CREATE TABLE IF NOT EXISTS content_categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    category_name TEXT UNIQUE NOT NULL,
    description TEXT,
    age_appropriate TEXT CHECK(age_appropriate IN ('all_ages', 'teen_plus', 'adult_only')) DEFAULT 'all_ages',
    default_action TEXT CHECK(default_action IN ('allow', 'block')) DEFAULT 'block',
    color_code TEXT, -- For UI color coding
    icon_name TEXT, -- For UI icons
    is_system BOOLEAN DEFAULT 0, -- System categories cannot be deleted
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Device Information and Management
CREATE TABLE IF NOT EXISTS managed_devices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id TEXT UNIQUE NOT NULL, -- Hardware ID or UUID
    device_name TEXT NOT NULL,
    device_type TEXT CHECK(device_type IN ('windows', 'android', 'ios')) NOT NULL,
    os_version TEXT,
    app_version TEXT,
    last_sync TIMESTAMP,
    status TEXT CHECK(status IN ('active', 'inactive', 'suspended', 'offline')) DEFAULT 'active',
    registered_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    registered_by INTEGER REFERENCES admin_users(id),
    device_info TEXT -- JSON with detailed device information
);

-- Emergency Override Codes
CREATE TABLE IF NOT EXISTS emergency_codes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code TEXT UNIQUE NOT NULL,
    created_by INTEGER NOT NULL REFERENCES admin_users(id),
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    expires_date TIMESTAMP NOT NULL,
    max_uses INTEGER DEFAULT 1,
    used_count INTEGER DEFAULT 0,
    is_active BOOLEAN DEFAULT 1,
    purpose TEXT,
    device_id INTEGER REFERENCES managed_devices(id) -- Optional: device-specific codes
);

-- Emergency Usage Log
CREATE TABLE IF NOT EXISTS emergency_usage_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    code_id INTEGER NOT NULL REFERENCES emergency_codes(id),
    user_id INTEGER REFERENCES device_users(id),
    used_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    device_info TEXT,
    ip_address TEXT,
    duration_minutes INTEGER,
    justification TEXT
);

-- System Audit Log
CREATE TABLE IF NOT EXISTS audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    table_name TEXT NOT NULL,
    record_id INTEGER,
    action_type TEXT CHECK(action_type IN ('INSERT', 'UPDATE', 'DELETE')) NOT NULL,
    old_values TEXT, -- JSON
    new_values TEXT, -- JSON
    changed_by INTEGER REFERENCES admin_users(id),
    change_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    ip_address TEXT,
    user_agent TEXT,
    change_reason TEXT
);

-- Notification Templates
CREATE TABLE IF NOT EXISTS notification_templates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    template_name TEXT UNIQUE NOT NULL,
    template_type TEXT CHECK(template_type IN ('email', 'sms', 'push', 'in_app')) NOT NULL,
    subject TEXT,
    body_template TEXT NOT NULL,
    is_active BOOLEAN DEFAULT 1,
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- User Notifications
CREATE TABLE IF NOT EXISTS user_notifications (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    template_id INTEGER REFERENCES notification_templates(id),
    title TEXT NOT NULL,
    message TEXT NOT NULL,
    notification_type TEXT CHECK(notification_type IN ('info', 'warning', 'error', 'success')) DEFAULT 'info',
    is_read BOOLEAN DEFAULT 0,
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    expires_date TIMESTAMP,
    action_url TEXT,
    priority TEXT CHECK(priority IN ('low', 'normal', 'high', 'urgent')) DEFAULT 'normal'
);

-- Create Indexes for Performance
CREATE INDEX IF NOT EXISTS idx_device_users_username ON device_users(username);
CREATE INDEX IF NOT EXISTS idx_device_users_type ON device_users(user_type);
CREATE INDEX IF NOT EXISTS idx_user_url_permissions_user_id ON user_url_permissions(user_id);
CREATE INDEX IF NOT EXISTS idx_user_time_restrictions_user_id ON user_time_restrictions(user_id);
CREATE INDEX IF NOT EXISTS idx_daily_usage_log_user_date ON daily_usage_log(user_id, usage_date);
CREATE INDEX IF NOT EXISTS idx_internet_sessions_user_start ON internet_sessions(user_id, session_start);
CREATE INDEX IF NOT EXISTS idx_url_lists_pattern ON url_lists(url_pattern);
CREATE INDEX IF NOT EXISTS idx_url_lists_type ON url_lists(list_type);
CREATE INDEX IF NOT EXISTS idx_youtube_channels_channel_id ON youtube_channels(channel_id);
CREATE INDEX IF NOT EXISTS idx_user_youtube_permissions_user_channel ON user_youtube_permissions(user_id, channel_id);
CREATE INDEX IF NOT EXISTS idx_blocked_attempts_user_timestamp ON blocked_attempts(user_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_blocked_attempts_timestamp ON blocked_attempts(timestamp);
CREATE INDEX IF NOT EXISTS idx_auth_sessions_token ON auth_sessions(session_token);
CREATE INDEX IF NOT EXISTS idx_audit_log_table_record ON audit_log(table_name, record_id);
CREATE INDEX IF NOT EXISTS idx_audit_log_date ON audit_log(change_date);

-- Insert Default Data

-- Default admin settings
INSERT OR IGNORE INTO admin_settings (key, value, value_type, description, category) VALUES
('default_adult_policy', 'allow', 'string', 'Default internet access policy for adults', 'security'),
('default_teen_policy', 'filtered', 'string', 'Default internet access policy for teenagers', 'security'),
('default_child_policy', 'restricted', 'string', 'Default internet access policy for children', 'security'),
('max_daily_hours_child', '2', 'integer', 'Maximum daily internet hours for children', 'time_limits'),
('max_daily_hours_teen', '4', 'integer', 'Maximum daily internet hours for teenagers', 'time_limits'),
('youtube_api_key', '', 'string', 'YouTube Data API key', 'api'),
('emergency_code_expiry_hours', '24', 'integer', 'Emergency override code expiration time in hours', 'emergency'),
('session_timeout_minutes', '120', 'integer', 'Admin session timeout in minutes', 'security'),
('enable_audit_logging', '1', 'boolean', 'Enable comprehensive audit logging', 'logging'),
('block_vpn_access', '1', 'boolean', 'Block access to VPN services', 'security');

-- Default content categories
INSERT OR IGNORE INTO content_categories (category_name, description, age_appropriate, default_action, color_code, icon_name, is_system) VALUES
('Educational', 'Educational websites and content', 'all_ages', 'allow', '#4CAF50', 'school', 1),
('Social Media', 'Social networking platforms', 'teen_plus', 'block', '#2196F3', 'people', 1),
('Gaming', 'Online games and gaming platforms', 'teen_plus', 'block', '#FF9800', 'games', 1),
('Adult Content', 'Adult and mature content', 'adult_only', 'block', '#F44336', 'warning', 1),
('News', 'News and current events', 'teen_plus', 'allow', '#607D8B', 'newspaper', 1),
('Shopping', 'E-commerce and shopping sites', 'teen_plus', 'allow', '#9C27B0', 'shopping_cart', 1),
('Streaming', 'Video streaming services', 'all_ages', 'allow', '#E91E63', 'play_circle', 1),
('Security Risk', 'Potentially malicious or unsafe sites', 'all_ages', 'block', '#D32F2F', 'security', 1);

-- Default notification templates
INSERT OR IGNORE INTO notification_templates (template_name, template_type, subject, body_template) VALUES
('time_limit_warning', 'in_app', 'Time Limit Warning', 'You have {{remaining_minutes}} minutes of internet time remaining today.'),
('time_limit_exceeded', 'in_app', 'Internet Time Used Up', 'Your daily internet time limit has been reached. Try again tomorrow!'),
('blocked_attempt', 'in_app', 'Website Blocked', 'Access to {{url}} was blocked due to {{reason}}.'),
('emergency_code_used', 'email', 'Emergency Code Used', 'An emergency override code was used on {{device_name}} at {{timestamp}}.');