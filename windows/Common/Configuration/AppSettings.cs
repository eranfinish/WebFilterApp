using System;
using System.IO;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;

namespace Common.Configuration
{
    /// <summary>
    /// Application settings management for URL Filter system
    /// </summary>
    public class AppSettings
    {
        private static AppSettings _instance;
        private static readonly object _lock = new object();
        
        public string DatabasePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UrlFilter", "Database", "urlfilter.db");
            
        public string LogPath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "UrlFilter", "Logs");
            
        public int ProxyPort { get; set; } = 8080;
        public bool EnableHttpsInspection { get; set; } = true;
        public int CacheExpiryMinutes { get; set; } = 5;
        public int SessionTimeoutMinutes { get; set; } = 120;
        public string DefaultUserPolicy { get; set; } = "adult";
        public bool SafeTestMode { get; set; } = false;
        
        public static AppSettings Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = LoadSettings();
                        }
                    }
                }
                return _instance;
            }
        }
        
        private static AppSettings LoadSettings()
        {
            try
            {
                var configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "UrlFilter", "config.json");
                
                if (File.Exists(configPath))
                {
                    var json = File.ReadAllText(configPath);
                    return JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch (Exception)
            {
                // If loading fails, return default settings
            }
            
            return new AppSettings();
        }
        
        public void Save()
        {
            try
            {
                var configDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "UrlFilter");
                
                Directory.CreateDirectory(configDir);
                
                var configPath = Path.Combine(configDir, "config.json");
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(configPath, json);
            }
            catch (Exception)
            {
                // Ignore save errors for now
            }
        }
    }
}