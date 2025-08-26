using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Common.Configuration;

namespace Common.Services
{
    /// <summary>
    /// Configuration service for managing application settings
    /// </summary>
    public class ConfigurationService
    {
        private readonly ILogger<ConfigurationService> _logger;
        private readonly Dictionary<string, object> _runtimeConfig;

        public ConfigurationService(ILogger<ConfigurationService> logger)
        {
            _logger = logger;
            _runtimeConfig = new Dictionary<string, object>();
        }

        /// <summary>
        /// Get configuration value
        /// </summary>
        public T GetValue<T>(string key, T defaultValue = default)
        {
            try
            {
                if (_runtimeConfig.ContainsKey(key))
                {
                    var value = _runtimeConfig[key];
                    if (value is T directValue)
                        return directValue;
                    
                    // Try to convert
                    return (T)Convert.ChangeType(value, typeof(T));
                }

                return defaultValue;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error getting config value for key: {key}");
                return defaultValue;
            }
        }

        /// <summary>
        /// Set configuration value
        /// </summary>
        public void SetValue<T>(string key, T value)
        {
            try
            {
                _runtimeConfig[key] = value;
                _logger.LogDebug($"Configuration value set: {key} = {value}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting config value for key: {key}");
            }
        }

        /// <summary>
        /// Load configuration from file
        /// </summary>
        public async Task<bool> LoadConfigurationAsync(string configPath)
        {
            try
            {
                if (!File.Exists(configPath))
                {
                    _logger.LogWarning($"Configuration file not found: {configPath}");
                    return false;
                }

                var json = await File.ReadAllTextAsync(configPath);
                var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(json);

                if (config != null)
                {
                    foreach (var kvp in config)
                    {
                        _runtimeConfig[kvp.Key] = kvp.Value;
                    }
                }

                _logger.LogInformation($"Configuration loaded from: {configPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading configuration from: {configPath}");
                return false;
            }
        }

        /// <summary>
        /// Save configuration to file
        /// </summary>
        public async Task<bool> SaveConfigurationAsync(string configPath)
        {
            try
            {
                var directory = Path.GetDirectoryName(configPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var json = JsonConvert.SerializeObject(_runtimeConfig, Formatting.Indented);
                await File.WriteAllTextAsync(configPath, json);

                _logger.LogInformation($"Configuration saved to: {configPath}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error saving configuration to: {configPath}");
                return false;
            }
        }

        /// <summary>
        /// Check if test mode is enabled
        /// </summary>
        public bool IsTestModeEnabled()
        {
            return GetValue("test_mode_enabled", false);
        }

        /// <summary>
        /// Get test mode type
        /// </summary>
        public string GetTestMode()
        {
            return GetValue("test_mode", "normal");
        }

        /// <summary>
        /// Enable safe test mode
        /// </summary>
        public void EnableSafeTestMode(string testType = "monitor_only")
        {
            SetValue("test_mode_enabled", true);
            SetValue("test_mode", testType);
            SetValue("safe_mode", true);
            _logger.LogInformation($"Safe test mode enabled: {testType}");
        }

        /// <summary>
        /// Disable test mode
        /// </summary>
        public void DisableTestMode()
        {
            SetValue("test_mode_enabled", false);
            SetValue("test_mode", "normal");
            SetValue("safe_mode", false);
            _logger.LogInformation("Test mode disabled");
        }

        /// <summary>
        /// Get all configuration keys
        /// </summary>
        public IEnumerable<string> GetAllKeys()
        {
            return _runtimeConfig.Keys;
        }

        /// <summary>
        /// Clear all configuration
        /// </summary>
        public void Clear()
        {
            _runtimeConfig.Clear();
            _logger.LogInformation("Configuration cleared");
        }
    }
}