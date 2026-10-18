-- URL Filter System - Client Database Initialization Script
-- Version: 1.0.0
-- Generated: 2025-12-09
-- This script creates and populates the SQLite database for client deployment

-- ============================================================================
-- SCHEMA CREATION
-- ============================================================================

-- Device Users Management
CREATE TABLE IF NOT EXISTS device_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    username TEXT UNIQUE NOT NULL,
    display_name TEXT NOT NULL,
    user_type TEXT CHECK(user_type IN ('adult', 'teen', 'child', 'guest')) DEFAULT 'adult',
    age INTEGER,
    profile_created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    is_active BOOLEAN DEFAULT 1,
    parent_user_id INTEGER REFERENCES device_users(id),
    avatar_path TEXT,
    last_login TIMESTAMP,
    created_by INTEGER REFERENCES admin_users(id)
);

-- Admin Users and Permissions
CREATE TABLE IF NOT EXISTS admin_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_identifier TEXT UNIQUE NOT NULL,
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

-- User-Specific URL Permissions
CREATE TABLE IF NOT EXISTS user_url_permissions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    url_pattern TEXT NOT NULL,
    permission_type TEXT CHECK(permission_type IN ('allow', 'block', 'inherit')) DEFAULT 'inherit',
    category TEXT,
    granted_by INTEGER REFERENCES admin_users(id),
    granted_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    expires_date TIMESTAMP,
    notes TEXT,
    is_active BOOLEAN DEFAULT 1
);

-- Time-Based Access Control
CREATE TABLE IF NOT EXISTS user_time_restrictions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES device_users(id) ON DELETE CASCADE,
    day_of_week INTEGER CHECK(day_of_week BETWEEN 0 AND 6),
    allowed_start_time TIME,
    allowed_end_time TIME,
    daily_limit_minutes INTEGER,
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
    top_domains TEXT,
    device_info TEXT,
    ip_address TEXT
);

-- URL Lists Table (Whitelist/Blacklist)
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

-- YouTube Channel Management
CREATE TABLE IF NOT EXISTS youtube_channels (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    channel_id TEXT UNIQUE NOT NULL,
    channel_name TEXT NOT NULL,
    channel_url TEXT NOT NULL,
    channel_handle TEXT,
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
    daily_limit_minutes INTEGER,
    granted_by INTEGER REFERENCES admin_users(id),
    granted_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    expires_date TIMESTAMP,
    is_active BOOLEAN DEFAULT 1,
    UNIQUE(user_id, channel_id)
);

-- YouTube Video Patterns
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

-- Blocked Attempts Log
CREATE TABLE IF NOT EXISTS blocked_attempts (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER REFERENCES device_users(id),
    url TEXT NOT NULL,
    content_type TEXT CHECK(content_type IN ('youtube_channel', 'youtube_video', 'website', 'social_media', 'gaming', 'adult', 'other')) DEFAULT 'website',
    block_reason TEXT CHECK(block_reason IN ('time_limit', 'blacklist', 'age_restriction', 'schedule', 'user_block', 'youtube_time_limit', 'parental_control')) NOT NULL,
    channel_info TEXT,
    timestamp TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    device_info TEXT,
    user_agent TEXT,
    ip_address TEXT,
    severity_level TEXT CHECK(severity_level IN ('low', 'medium', 'high', 'critical')) DEFAULT 'medium'
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
    is_sensitive BOOLEAN DEFAULT 0,
    modified_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    modified_by INTEGER REFERENCES admin_users(id)
);

-- Content Categories
CREATE TABLE IF NOT EXISTS content_categories (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    category_name TEXT UNIQUE NOT NULL,
    description TEXT,
    age_appropriate TEXT CHECK(age_appropriate IN ('all_ages', 'teen_plus', 'adult_only')) DEFAULT 'all_ages',
    default_action TEXT CHECK(default_action IN ('allow', 'block')) DEFAULT 'block',
    color_code TEXT,
    icon_name TEXT,
    is_system BOOLEAN DEFAULT 0,
    created_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Managed Devices
CREATE TABLE IF NOT EXISTS managed_devices (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    device_id TEXT UNIQUE NOT NULL,
    device_name TEXT NOT NULL,
    device_type TEXT CHECK(device_type IN ('windows', 'android', 'ios')) NOT NULL,
    os_version TEXT,
    app_version TEXT,
    last_sync TIMESTAMP,
    status TEXT CHECK(status IN ('active', 'inactive', 'suspended', 'offline')) DEFAULT 'active',
    registered_date TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    registered_by INTEGER REFERENCES admin_users(id),
    device_info TEXT
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
    device_id INTEGER REFERENCES managed_devices(id)
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
    old_values TEXT,
    new_values TEXT,
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

-- ============================================================================
-- INDEXES FOR PERFORMANCE
-- ============================================================================

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

-- ============================================================================
-- DEFAULT ADMIN SETTINGS
-- ============================================================================

INSERT OR IGNORE INTO admin_settings (key, value, value_type, description, category) VALUES
-- Security policies
('default_adult_policy', 'allow', 'string', 'Default internet access policy for adults', 'security'),
('default_teen_policy', 'filtered', 'string', 'Default internet access policy for teenagers', 'security'),
('default_child_policy', 'restricted', 'string', 'Default internet access policy for children', 'security'),
('block_vpn_access', '1', 'boolean', 'Block access to VPN services', 'security'),
('block_proxy_access', '1', 'boolean', 'Block access to web proxy services', 'security'),
('safe_search_enabled', '1', 'boolean', 'Force safe search on search engines', 'security'),
('https_only_mode', '0', 'boolean', 'Only allow HTTPS connections', 'security'),

-- Time limits
('max_daily_hours_child', '2', 'integer', 'Maximum daily internet hours for children', 'time_limits'),
('max_daily_hours_teen', '4', 'integer', 'Maximum daily internet hours for teenagers', 'time_limits'),
('max_daily_hours_guest', '1', 'integer', 'Maximum daily internet hours for guests', 'time_limits'),
('youtube_daily_limit_child', '60', 'integer', 'Daily YouTube minutes for children', 'time_limits'),
('youtube_daily_limit_teen', '120', 'integer', 'Daily YouTube minutes for teenagers', 'time_limits'),
('bedtime_enabled', '1', 'boolean', 'Enable automatic bedtime blocking', 'time_limits'),
('bedtime_start', '21:00', 'string', 'Bedtime start (internet blocked)', 'time_limits'),
('bedtime_end', '07:00', 'string', 'Bedtime end (internet allowed)', 'time_limits'),

-- API keys (empty by default - admin should fill)
('youtube_api_key', '', 'string', 'YouTube Data API key for channel info', 'api'),

-- Emergency settings
('emergency_code_expiry_hours', '24', 'integer', 'Emergency override code expiration time in hours', 'emergency'),
('emergency_code_length', '6', 'integer', 'Length of generated emergency codes', 'emergency'),

-- Session settings
('session_timeout_minutes', '120', 'integer', 'Admin session timeout in minutes', 'session'),
('max_concurrent_sessions', '3', 'integer', 'Max concurrent admin sessions per user', 'session'),

-- Logging settings
('enable_audit_logging', '1', 'boolean', 'Enable comprehensive audit logging', 'logging'),
('log_blocked_attempts', '1', 'boolean', 'Log all blocked access attempts', 'logging'),
('log_retention_days', '90', 'integer', 'Number of days to retain logs', 'logging'),

-- Notifications
('notify_on_bypass_attempt', '1', 'boolean', 'Notify admin on bypass attempts', 'notifications'),
('notify_on_time_limit', '1', 'boolean', 'Notify users before time limit reached', 'notifications'),
('time_warning_minutes', '10', 'integer', 'Minutes before limit to show warning', 'notifications'),

-- UI settings
('theme', 'light', 'string', 'Admin panel theme (light/dark)', 'ui'),
('language', 'en', 'string', 'Interface language', 'ui'),
('date_format', 'MM/dd/yyyy', 'string', 'Date display format', 'ui'),
('time_format', '12h', 'string', 'Time display format (12h/24h)', 'ui');

-- ============================================================================
-- CONTENT CATEGORIES
-- ============================================================================

INSERT OR IGNORE INTO content_categories (category_name, description, age_appropriate, default_action, color_code, icon_name, is_system) VALUES
('Educational', 'Educational websites, online courses, learning platforms', 'all_ages', 'allow', '#4CAF50', 'school', 1),
('Social Media', 'Social networking platforms (Facebook, Instagram, TikTok, etc.)', 'teen_plus', 'block', '#2196F3', 'people', 1),
('Gaming', 'Online games, gaming platforms, game streaming', 'teen_plus', 'block', '#FF9800', 'games', 1),
('Adult Content', 'Adult, mature, or explicit content', 'adult_only', 'block', '#F44336', 'warning', 1),
('News', 'News websites and current events', 'teen_plus', 'allow', '#607D8B', 'newspaper', 1),
('Shopping', 'E-commerce and online shopping', 'teen_plus', 'allow', '#9C27B0', 'shopping_cart', 1),
('Streaming', 'Video streaming services (Netflix, Disney+, etc.)', 'all_ages', 'allow', '#E91E63', 'play_circle', 1),
('Security Risk', 'Malware, phishing, and unsafe websites', 'all_ages', 'block', '#D32F2F', 'security', 1),
('Search Engines', 'Web search engines', 'all_ages', 'allow', '#3F51B5', 'search', 1),
('Email', 'Email services and webmail', 'teen_plus', 'allow', '#009688', 'email', 1),
('Forums', 'Discussion forums and message boards', 'teen_plus', 'block', '#795548', 'forum', 1),
('Dating', 'Dating websites and apps', 'adult_only', 'block', '#E91E63', 'favorite', 1),
('Gambling', 'Online gambling and betting sites', 'adult_only', 'block', '#FF5722', 'casino', 1),
('Drugs', 'Drug-related content', 'adult_only', 'block', '#9E9E9E', 'block', 1),
('Violence', 'Violent or graphic content', 'adult_only', 'block', '#B71C1C', 'dangerous', 1),
('VPN/Proxy', 'VPN services and web proxies', 'adult_only', 'block', '#455A64', 'vpn_key', 1),
('File Sharing', 'File sharing and torrent sites', 'adult_only', 'block', '#546E7A', 'cloud_download', 1),
('Reference', 'Dictionaries, encyclopedias, reference sites', 'all_ages', 'allow', '#00BCD4', 'book', 1),
('Kids', 'Child-safe websites and games', 'all_ages', 'allow', '#8BC34A', 'child_care', 1),
('Productivity', 'Productivity tools and office applications', 'all_ages', 'allow', '#03A9F4', 'work', 1);

-- ============================================================================
-- DEFAULT URL BLACKLIST (Blocked Sites)
-- ============================================================================

INSERT OR IGNORE INTO url_lists (url_pattern, list_type, category, age_appropriate, description, match_type) VALUES
-- Adult Content
('*.pornhub.com', 'blacklist', 'Adult Content', 'adult_only', 'Adult video site', 'wildcard'),
('*.xvideos.com', 'blacklist', 'Adult Content', 'adult_only', 'Adult video site', 'wildcard'),
('*.xnxx.com', 'blacklist', 'Adult Content', 'adult_only', 'Adult video site', 'wildcard'),
('*.redtube.com', 'blacklist', 'Adult Content', 'adult_only', 'Adult video site', 'wildcard'),
('*.youporn.com', 'blacklist', 'Adult Content', 'adult_only', 'Adult video site', 'wildcard'),
('*.brazzers.com', 'blacklist', 'Adult Content', 'adult_only', 'Adult video site', 'wildcard'),
('*.onlyfans.com', 'blacklist', 'Adult Content', 'adult_only', 'Adult content platform', 'wildcard'),

-- Social Media (blocked for children by default)
('*.tiktok.com', 'blacklist', 'Social Media', 'teen_plus', 'Short video social media', 'wildcard'),
('*.snapchat.com', 'blacklist', 'Social Media', 'teen_plus', 'Social media messaging', 'wildcard'),
('*.instagram.com', 'blacklist', 'Social Media', 'teen_plus', 'Photo sharing social media', 'wildcard'),
('*.twitter.com', 'blacklist', 'Social Media', 'teen_plus', 'Social media platform', 'wildcard'),
('*.x.com', 'blacklist', 'Social Media', 'teen_plus', 'Social media platform (Twitter)', 'wildcard'),
('*.facebook.com', 'blacklist', 'Social Media', 'teen_plus', 'Social network', 'wildcard'),
('*.reddit.com', 'blacklist', 'Forums', 'teen_plus', 'Discussion forum', 'wildcard'),
('*.tumblr.com', 'blacklist', 'Social Media', 'teen_plus', 'Microblogging platform', 'wildcard'),
('*.discord.com', 'blacklist', 'Social Media', 'teen_plus', 'Chat platform', 'wildcard'),
('*.discordapp.com', 'blacklist', 'Social Media', 'teen_plus', 'Chat platform', 'wildcard'),

-- Gaming (blocked for children by default)
('*.twitch.tv', 'blacklist', 'Gaming', 'teen_plus', 'Game streaming platform', 'wildcard'),
('*.steampowered.com', 'blacklist', 'Gaming', 'teen_plus', 'Gaming platform', 'wildcard'),
('*.epicgames.com', 'blacklist', 'Gaming', 'teen_plus', 'Gaming platform', 'wildcard'),
('*.roblox.com', 'blacklist', 'Gaming', 'teen_plus', 'Online game platform', 'wildcard'),
('*.minecraft.net', 'blacklist', 'Gaming', 'teen_plus', 'Gaming', 'wildcard'),
('*.fortnite.com', 'blacklist', 'Gaming', 'teen_plus', 'Online game', 'wildcard'),
('*.origin.com', 'blacklist', 'Gaming', 'teen_plus', 'Gaming platform', 'wildcard'),
('*.battlenet.com', 'blacklist', 'Gaming', 'teen_plus', 'Gaming platform', 'wildcard'),
('*.ea.com', 'blacklist', 'Gaming', 'teen_plus', 'Gaming company', 'wildcard'),
('*.coolmathgames.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games (often used to waste time)', 'wildcard'),
('*.poki.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games', 'wildcard'),
('*.y8.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games', 'wildcard'),
('*.addictinggames.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games', 'wildcard'),
('*.miniclip.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games', 'wildcard'),
('*.kongregate.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games', 'wildcard'),
('*.crazygames.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games', 'wildcard'),
('*.friv.com', 'blacklist', 'Gaming', 'all_ages', 'Browser games', 'wildcard'),

-- Gambling
('*.bet365.com', 'blacklist', 'Gambling', 'adult_only', 'Gambling site', 'wildcard'),
('*.draftkings.com', 'blacklist', 'Gambling', 'adult_only', 'Sports betting', 'wildcard'),
('*.fanduel.com', 'blacklist', 'Gambling', 'adult_only', 'Sports betting', 'wildcard'),
('*.pokerstars.com', 'blacklist', 'Gambling', 'adult_only', 'Online poker', 'wildcard'),
('*.888casino.com', 'blacklist', 'Gambling', 'adult_only', 'Online casino', 'wildcard'),
('*.williamhill.com', 'blacklist', 'Gambling', 'adult_only', 'Gambling site', 'wildcard'),

-- VPN/Proxy (bypass tools)
('*.nordvpn.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN service', 'wildcard'),
('*.expressvpn.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN service', 'wildcard'),
('*.surfshark.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN service', 'wildcard'),
('*.protonvpn.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN service', 'wildcard'),
('*.hidemyass.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN/Proxy service', 'wildcard'),
('*.privateinternetaccess.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN service', 'wildcard'),
('*.kproxy.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'Web proxy', 'wildcard'),
('*.proxysite.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'Web proxy', 'wildcard'),
('*.hide.me', 'blacklist', 'VPN/Proxy', 'adult_only', 'Web proxy', 'wildcard'),
('*.whoer.net', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN/Proxy service', 'wildcard'),
('*.tunnelbear.com', 'blacklist', 'VPN/Proxy', 'adult_only', 'VPN service', 'wildcard'),

-- File Sharing / Torrents
('*.thepiratebay.org', 'blacklist', 'File Sharing', 'adult_only', 'Torrent site', 'wildcard'),
('*.1337x.to', 'blacklist', 'File Sharing', 'adult_only', 'Torrent site', 'wildcard'),
('*.rarbg.to', 'blacklist', 'File Sharing', 'adult_only', 'Torrent site', 'wildcard'),
('*.yts.mx', 'blacklist', 'File Sharing', 'adult_only', 'Torrent site', 'wildcard'),
('*.nyaa.si', 'blacklist', 'File Sharing', 'adult_only', 'Torrent site', 'wildcard'),
('*.torrentz2.eu', 'blacklist', 'File Sharing', 'adult_only', 'Torrent search', 'wildcard'),

-- Dating
('*.tinder.com', 'blacklist', 'Dating', 'adult_only', 'Dating app', 'wildcard'),
('*.bumble.com', 'blacklist', 'Dating', 'adult_only', 'Dating app', 'wildcard'),
('*.match.com', 'blacklist', 'Dating', 'adult_only', 'Dating site', 'wildcard'),
('*.okcupid.com', 'blacklist', 'Dating', 'adult_only', 'Dating site', 'wildcard'),
('*.plentyoffish.com', 'blacklist', 'Dating', 'adult_only', 'Dating site', 'wildcard'),
('*.hinge.co', 'blacklist', 'Dating', 'adult_only', 'Dating app', 'wildcard'),

-- Violence/Shock
('*.liveleak.com', 'blacklist', 'Violence', 'adult_only', 'Shock content', 'wildcard'),
('*.bestgore.com', 'blacklist', 'Violence', 'adult_only', 'Gore content', 'wildcard'),
('*.theync.com', 'blacklist', 'Violence', 'adult_only', 'Shock content', 'wildcard'),

-- Dark Web / Hacking
('*.torproject.org', 'blacklist', 'Security Risk', 'adult_only', 'Tor browser (bypass tool)', 'wildcard');

-- ============================================================================
-- DEFAULT URL WHITELIST (Always Allowed)
-- ============================================================================

INSERT OR IGNORE INTO url_lists (url_pattern, list_type, category, age_appropriate, description, match_type) VALUES
-- Search Engines (with safe search forced)
('*.google.com', 'whitelist', 'Search Engines', 'all_ages', 'Google search and services', 'wildcard'),
('*.bing.com', 'whitelist', 'Search Engines', 'all_ages', 'Bing search', 'wildcard'),
('*.duckduckgo.com', 'whitelist', 'Search Engines', 'all_ages', 'DuckDuckGo search', 'wildcard'),

-- Educational Sites
('*.khanacademy.org', 'whitelist', 'Educational', 'all_ages', 'Khan Academy learning platform', 'wildcard'),
('*.wikipedia.org', 'whitelist', 'Reference', 'all_ages', 'Wikipedia encyclopedia', 'wildcard'),
('*.britannica.com', 'whitelist', 'Reference', 'all_ages', 'Encyclopedia Britannica', 'wildcard'),
('*.coursera.org', 'whitelist', 'Educational', 'all_ages', 'Online courses', 'wildcard'),
('*.edx.org', 'whitelist', 'Educational', 'all_ages', 'Online courses', 'wildcard'),
('*.udemy.com', 'whitelist', 'Educational', 'all_ages', 'Online courses', 'wildcard'),
('*.codecademy.com', 'whitelist', 'Educational', 'all_ages', 'Learn to code', 'wildcard'),
('*.duolingo.com', 'whitelist', 'Educational', 'all_ages', 'Language learning', 'wildcard'),
('*.quizlet.com', 'whitelist', 'Educational', 'all_ages', 'Study flashcards', 'wildcard'),
('*.brainpop.com', 'whitelist', 'Educational', 'all_ages', 'Educational videos for kids', 'wildcard'),
('*.pbslearningmedia.org', 'whitelist', 'Educational', 'all_ages', 'PBS educational content', 'wildcard'),
('*.education.com', 'whitelist', 'Educational', 'all_ages', 'Educational worksheets', 'wildcard'),
('*.ixl.com', 'whitelist', 'Educational', 'all_ages', 'Math and English learning', 'wildcard'),
('*.mathway.com', 'whitelist', 'Educational', 'all_ages', 'Math problem solver', 'wildcard'),
('*.wolframalpha.com', 'whitelist', 'Educational', 'all_ages', 'Computational knowledge', 'wildcard'),
('*.scratch.mit.edu', 'whitelist', 'Educational', 'all_ages', 'Kids coding platform', 'wildcard'),
('*.code.org', 'whitelist', 'Educational', 'all_ages', 'Learn to code', 'wildcard'),
('*.typing.com', 'whitelist', 'Educational', 'all_ages', 'Typing practice', 'wildcard'),
('*.readworks.org', 'whitelist', 'Educational', 'all_ages', 'Reading comprehension', 'wildcard'),
('*.newsela.com', 'whitelist', 'Educational', 'all_ages', 'News for students', 'wildcard'),
('*.nationalgeographic.com', 'whitelist', 'Educational', 'all_ages', 'Science and nature', 'wildcard'),
('*.nasa.gov', 'whitelist', 'Educational', 'all_ages', 'NASA space education', 'wildcard'),
('*.smithsonianmag.com', 'whitelist', 'Educational', 'all_ages', 'Smithsonian content', 'wildcard'),

-- Kids Sites
('*.pbskids.org', 'whitelist', 'Kids', 'all_ages', 'PBS Kids games and videos', 'wildcard'),
('*.nickjr.com', 'whitelist', 'Kids', 'all_ages', 'Nick Jr. kids content', 'wildcard'),
('*.sesamestreet.org', 'whitelist', 'Kids', 'all_ages', 'Sesame Street', 'wildcard'),
('*.starfall.com', 'whitelist', 'Kids', 'all_ages', 'Kids learning games', 'wildcard'),
('*.abcya.com', 'whitelist', 'Kids', 'all_ages', 'Educational games for kids', 'wildcard'),
('*.funbrain.com', 'whitelist', 'Kids', 'all_ages', 'Educational games', 'wildcard'),
('*.storylineonline.net', 'whitelist', 'Kids', 'all_ages', 'Read-aloud stories', 'wildcard'),
('*.safeyoutube.net', 'whitelist', 'Kids', 'all_ages', 'Safe YouTube viewer', 'wildcard'),

-- Productivity / Office
('*.office.com', 'whitelist', 'Productivity', 'all_ages', 'Microsoft Office online', 'wildcard'),
('*.docs.google.com', 'whitelist', 'Productivity', 'all_ages', 'Google Docs', 'wildcard'),
('*.drive.google.com', 'whitelist', 'Productivity', 'all_ages', 'Google Drive', 'wildcard'),
('*.classroom.google.com', 'whitelist', 'Educational', 'all_ages', 'Google Classroom', 'wildcard'),
('*.zoom.us', 'whitelist', 'Productivity', 'all_ages', 'Video conferencing', 'wildcard'),
('*.teams.microsoft.com', 'whitelist', 'Productivity', 'all_ages', 'Microsoft Teams', 'wildcard'),
('*.notion.so', 'whitelist', 'Productivity', 'all_ages', 'Note taking app', 'wildcard'),
('*.canva.com', 'whitelist', 'Productivity', 'all_ages', 'Design tool', 'wildcard'),

-- Libraries / Research
('*.archive.org', 'whitelist', 'Reference', 'all_ages', 'Internet Archive', 'wildcard'),
('*.scholar.google.com', 'whitelist', 'Educational', 'all_ages', 'Google Scholar', 'wildcard'),
('*.jstor.org', 'whitelist', 'Educational', 'all_ages', 'Academic journals', 'wildcard'),
('*.gutenberg.org', 'whitelist', 'Reference', 'all_ages', 'Free ebooks', 'wildcard'),

-- Weather / Utilities
('*.weather.com', 'whitelist', 'Reference', 'all_ages', 'Weather information', 'wildcard'),
('*.timeanddate.com', 'whitelist', 'Reference', 'all_ages', 'Time and date utilities', 'wildcard');

-- ============================================================================
-- EDUCATIONAL YOUTUBE CHANNELS (Pre-approved for Kids)
-- ============================================================================

INSERT OR IGNORE INTO youtube_channels (channel_id, channel_name, channel_url, channel_handle, access_type, category, age_rating, educational_content, description) VALUES
-- Kids Educational
('UC_x5XG1OV2P6uZZ5FSM9Ttw', 'Khan Academy', 'https://youtube.com/khanacademy', '@khanacademy', 'allowed', 'Educational', 'kids', 1, 'Free online education - math, science, and more'),
('UCXuqSBlHAE6Xw-yeJA0Tunw', 'Crash Course', 'https://youtube.com/crashcourse', '@crashcourse', 'allowed', 'Educational', 'family', 1, 'Educational series on various subjects'),
('UC6nSFpj9HTCZ5t-N3Rm3-HA', 'Vsauce', 'https://youtube.com/vsauce', '@Vsauce', 'allowed', 'Educational', 'family', 1, 'Science and education videos'),
('UCsooa4yRKGN_zEE8iknghZA', 'TED-Ed', 'https://youtube.com/teded', '@TEDEd', 'allowed', 'Educational', 'family', 1, 'Lessons worth sharing'),
('UCV0qA-eDDICsRR9rPcnG7tw', 'Kurzgesagt', 'https://youtube.com/kurzgesagt', '@kurzgesagt', 'allowed', 'Educational', 'family', 1, 'Science and philosophy explained'),
('UCsXVk37bltHxD1rDPwtNM8Q', 'Kurzgesagt In a Nutshell', 'https://youtube.com/inanutshell', '@kurzgesagt', 'allowed', 'Educational', 'family', 1, 'Science animations'),
('UC7_gcs09iThXybpVgjHZ_7g', 'PBS Space Time', 'https://youtube.com/pbsspacetime', '@pbsspacetime', 'allowed', 'Educational', 'teen', 1, 'Space and physics education'),
('UCZYTClx2T1of7BRZ86-8fow', 'SciShow', 'https://youtube.com/scishow', '@SciShow', 'allowed', 'Educational', 'family', 1, 'Science exploration'),
('UCvjgEDvShRsALpsSsq6LcjA', 'National Geographic', 'https://youtube.com/natgeo', '@NatGeo', 'allowed', 'Educational', 'family', 1, 'Nature and science'),
('UCOEYIhlCwR5dkKKgwLd2X8g', 'SmarterEveryDay', 'https://youtube.com/smartereveryday', '@smartereveryday', 'allowed', 'Educational', 'family', 1, 'Science experiments'),
('UCHnyfMqiRRG1u-2MsSQLbXA', 'Veritasium', 'https://youtube.com/veritasium', '@veritasium', 'allowed', 'Educational', 'family', 1, 'Science and engineering'),
('UCs4aHmggTfFrpkPcWSaBN9g', 'PBS Eons', 'https://youtube.com/eons', '@eons', 'allowed', 'Educational', 'family', 1, 'Natural history'),
('UCbmNph6atAoGfqLoCL_duAg', 'Numberphile', 'https://youtube.com/numberphile', '@numberphile', 'allowed', 'Educational', 'family', 1, 'Math videos'),

-- Kids Entertainment (Safe)
('UCkQO3QsgTpNTsOw6ujimT5Q', 'Sesame Street', 'https://youtube.com/sesamestreet', '@SesameStreet', 'allowed', 'Kids', 'kids', 1, 'Educational kids content'),
('UC-Uk07ds_zfLh_tI_5v1G6A', 'National Geographic Kids', 'https://youtube.com/natgeokids', '@NatGeoKids', 'allowed', 'Educational', 'kids', 1, 'Science for kids'),
('UCyUvPAmDWX2yvNV4N6cHwwA', 'Art for Kids Hub', 'https://youtube.com/artforkidshub', '@ArtforKidsHub', 'allowed', 'Educational', 'kids', 1, 'Art lessons for children'),
('UC295-Dw_tDNtZXFeAPAQKEw', 'CoCoMelon', 'https://youtube.com/cocomelon', '@CoComelon', 'allowed', 'Kids', 'kids', 1, 'Nursery rhymes and kids songs'),
('UCrNnk0wFBnCS1awGjq_ijGQ', 'Blippi', 'https://youtube.com/blippi', '@Blippi', 'allowed', 'Kids', 'kids', 1, 'Educational videos for toddlers'),
('UC4KiBA4Nqxb8YqjKCkHLVYQ', 'Super Simple Songs', 'https://youtube.com/supersimplesongs', '@SuperSimpleSongs', 'allowed', 'Kids', 'kids', 1, 'Songs for children'),

-- Coding / Technology
('UCW5YeuERMmlnqo4oq8vwUpg', 'The Net Ninja', 'https://youtube.com/thenetninja', '@NetNinja', 'allowed', 'Educational', 'teen', 1, 'Web development tutorials'),
('UCvjgJvBlUQFCqVBKce1O-UQ', 'freeCodeCamp', 'https://youtube.com/freecodecamp', '@freecodecamp', 'allowed', 'Educational', 'teen', 1, 'Learn to code free'),
('UCsBjURrPoezykLs9EqgamOA', 'Fireship', 'https://youtube.com/fireship', '@Fireship', 'allowed', 'Educational', 'teen', 1, 'Quick tech tutorials'),
('UCtxCXg-UvSnTKPOzLH4wJaQ', 'Coding Train', 'https://youtube.com/codingtrain', '@TheCodingTrain', 'allowed', 'Educational', 'family', 1, 'Creative coding tutorials'),
('UC8butISFwT-Wl7EV0hUK0BQ', 'CS50', 'https://youtube.com/cs50', '@cs50', 'allowed', 'Educational', 'teen', 1, 'Harvard CS course'),

-- History
('UCHd62-u_v4DvJ8TCFtpi4GA', 'Extra History', 'https://youtube.com/extrahistory', '@ExtraHistory', 'allowed', 'Educational', 'family', 1, 'Animated history lessons'),
('UCJkMlOu7faDgqh4PfzbpLdg', 'OverSimplified', 'https://youtube.com/oversimplified', '@OverSimplified', 'allowed', 'Educational', 'family', 1, 'History explained simply'),
('UCggHoXaj8BQHIiPmOxezeWA', 'History Matters', 'https://youtube.com/historymatters', '@HistoryMatters', 'allowed', 'Educational', 'family', 1, 'Short history lessons');

-- ============================================================================
-- NOTIFICATION TEMPLATES
-- ============================================================================

INSERT OR IGNORE INTO notification_templates (template_name, template_type, subject, body_template) VALUES
('time_limit_warning', 'in_app', 'Time Limit Warning', 'You have {{remaining_minutes}} minutes of internet time remaining today.'),
('time_limit_exceeded', 'in_app', 'Internet Time Used Up', 'Your daily internet time limit has been reached. Try again tomorrow!'),
('time_limit_extended', 'in_app', 'Extra Time Granted', 'A parent has given you {{extra_minutes}} extra minutes of internet time.'),
('blocked_attempt', 'in_app', 'Website Blocked', 'Access to {{url}} was blocked. Reason: {{reason}}.'),
('bedtime_warning', 'in_app', 'Bedtime Soon', 'Internet access will be turned off in {{remaining_minutes}} minutes for bedtime.'),
('bedtime_active', 'in_app', 'Bedtime Mode Active', 'Internet is turned off during bedtime hours. Goodnight!'),
('homework_mode_on', 'in_app', 'Homework Mode', 'Homework mode is active. Only educational sites are accessible.'),
('homework_mode_off', 'in_app', 'Homework Mode Ended', 'Homework mode has ended. Regular access restored.'),
('emergency_code_used', 'email', 'Emergency Override Code Used', 'An emergency override code was used on {{device_name}} at {{timestamp}}. User: {{user_name}}.'),
('bypass_attempt', 'email', 'Bypass Attempt Detected', '{{user_name}} attempted to bypass internet restrictions on {{device_name}} at {{timestamp}}. URL: {{url}}.'),
('weekly_report', 'email', 'Weekly Internet Usage Report', 'Here is the weekly internet usage summary for your family:\n\n{{report_content}}'),
('new_device_registered', 'email', 'New Device Registered', 'A new device ({{device_name}}) has been registered to your URL Filter account.');

-- ============================================================================
-- SAMPLE FAMILY DATA (for demo/testing - can be removed in production)
-- ============================================================================

-- Sample Admin User (local admin for initial setup)
INSERT OR IGNORE INTO admin_users (user_identifier, display_name, email, authentication_provider, permission_level) VALUES
('local_admin', 'System Administrator', 'admin@localhost', 'local', 'super_admin');

-- Sample Family Members
INSERT OR IGNORE INTO device_users (username, display_name, user_type, age, parent_user_id) VALUES
('DESKTOP\Parent', 'Mom', 'adult', 42, NULL),
('DESKTOP\Dad', 'Dad', 'adult', 45, NULL);

-- Get parent IDs for child accounts (using subquery)
INSERT OR IGNORE INTO device_users (username, display_name, user_type, age, parent_user_id)
SELECT 'DESKTOP\Emma', 'Emma', 'child', 8, id FROM device_users WHERE username = 'DESKTOP\Parent';

INSERT OR IGNORE INTO device_users (username, display_name, user_type, age, parent_user_id)
SELECT 'DESKTOP\Jake', 'Jake', 'teen', 15, id FROM device_users WHERE username = 'DESKTOP\Parent';

-- Sample Time Restrictions for Emma (child) - All days
INSERT OR IGNORE INTO user_time_restrictions (user_id, day_of_week, allowed_start_time, allowed_end_time, daily_limit_minutes)
SELECT id, 0, '09:00:00', '20:00:00', 180 FROM device_users WHERE username = 'DESKTOP\Emma'
UNION ALL
SELECT id, 1, '15:00:00', '19:00:00', 120 FROM device_users WHERE username = 'DESKTOP\Emma'
UNION ALL
SELECT id, 2, '15:00:00', '19:00:00', 120 FROM device_users WHERE username = 'DESKTOP\Emma'
UNION ALL
SELECT id, 3, '15:00:00', '19:00:00', 120 FROM device_users WHERE username = 'DESKTOP\Emma'
UNION ALL
SELECT id, 4, '15:00:00', '19:00:00', 120 FROM device_users WHERE username = 'DESKTOP\Emma'
UNION ALL
SELECT id, 5, '15:00:00', '20:00:00', 150 FROM device_users WHERE username = 'DESKTOP\Emma'
UNION ALL
SELECT id, 6, '09:00:00', '20:00:00', 180 FROM device_users WHERE username = 'DESKTOP\Emma';

-- Sample Time Restrictions for Jake (teen) - All days
INSERT OR IGNORE INTO user_time_restrictions (user_id, day_of_week, allowed_start_time, allowed_end_time, daily_limit_minutes)
SELECT id, 0, '08:00:00', '22:00:00', 240 FROM device_users WHERE username = 'DESKTOP\Jake'
UNION ALL
SELECT id, 1, '15:00:00', '21:00:00', 180 FROM device_users WHERE username = 'DESKTOP\Jake'
UNION ALL
SELECT id, 2, '15:00:00', '21:00:00', 180 FROM device_users WHERE username = 'DESKTOP\Jake'
UNION ALL
SELECT id, 3, '15:00:00', '21:00:00', 180 FROM device_users WHERE username = 'DESKTOP\Jake'
UNION ALL
SELECT id, 4, '15:00:00', '21:00:00', 180 FROM device_users WHERE username = 'DESKTOP\Jake'
UNION ALL
SELECT id, 5, '15:00:00', '22:00:00', 240 FROM device_users WHERE username = 'DESKTOP\Jake'
UNION ALL
SELECT id, 6, '08:00:00', '22:00:00', 240 FROM device_users WHERE username = 'DESKTOP\Jake';

-- ============================================================================
-- DATABASE VERSION TRACKING
-- ============================================================================

INSERT OR IGNORE INTO admin_settings (key, value, value_type, description, category) VALUES
('db_version', '1.0.0', 'string', 'Database schema version', 'system'),
('db_created_date', datetime('now'), 'string', 'Database creation date', 'system'),
('db_last_migration', '1.0.0', 'string', 'Last applied migration version', 'system');

-- ============================================================================
-- END OF INITIALIZATION SCRIPT
-- ============================================================================
