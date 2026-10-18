using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Enforces system-wide policies that are hard to bypass:
    ///  - Disables DNS-over-HTTPS in Chrome/Edge/Firefox via Group Policy registry keys
    ///  - Blocks known DoH server IPs via WFP
    ///  - Seeds browser-download domains into the blacklist
    ///  - Kills unauthorized browser processes
    ///  - Re-applies policies periodically so they can't be undone
    /// </summary>
    public class PolicyEnforcementService
    {
        private readonly ILogger<PolicyEnforcementService> _logger;
        private readonly WfpFilterService _wfpService;
        private readonly DatabaseService _databaseService;
        private readonly DnsInterceptorService _dnsService;

        private Timer? _enforcementTimer;
        private bool _isRunning;

        // Well-known DoH server IPs to block at the network layer.
        // These are the most common public DoH resolvers children might try to use.
        private static readonly string[] DoHServerIps = new[]
        {
            "1.1.1.1", "1.0.0.1",           // Cloudflare
            "8.8.8.8", "8.8.4.4",           // Google (NOTE: also our upstream — we'll exclude)
            "9.9.9.9", "149.112.112.112",   // Quad9
            "94.140.14.14", "94.140.15.15", // AdGuard
            "208.67.222.222", "208.67.220.220", // OpenDNS
            "76.76.2.0", "76.76.10.0",      // ControlD
            "45.90.28.0", "45.90.30.0",     // NextDNS
        };

        // Browser download domains — added to blacklist on startup
        private static readonly string[] BrowserDownloadDomains = new[]
        {
            // Chrome / Chromium
            "chrome.google.com",
            "www.google.com/chrome",
            "dl.google.com",
            // Firefox / Mozilla
            "mozilla.org",
            "www.mozilla.org",
            "firefox.com",
            "download.mozilla.org",
            // Opera
            "opera.com",
            "www.opera.com",
            "download.opera.com",
            // Brave
            "brave.com",
            "laptop-updates.brave.com",
            // Vivaldi
            "vivaldi.com",
            "downloads.vivaldi.com",
            // Tor
            "torproject.org",
            "www.torproject.org",
            "dist.torproject.org",
            // Epic / other privacy browsers
            "epicbrowser.com",
            // Generic software repositories that host browsers
            "filehippo.com",
            "softonic.com",
            "download.cnet.com",
            "sourceforge.net",
            // VPN download pages (common bypass attempt)
            "nordvpn.com",
            "expressvpn.com",
            "protonvpn.com",
            "surfshark.com",
            "hola.org",
        };

        // Allowed browser process names (won't be killed). Edge/IE come with Windows.
        private static readonly HashSet<string> AllowedBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "msedge",          // Microsoft Edge (bundled with Windows)
            "iexplore",        // Internet Explorer (legacy)
        };

        // Browsers to terminate on sight
        private static readonly HashSet<string> UnauthorizedBrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome",
            "firefox",
            "opera",
            "brave",
            "vivaldi",
            "tor",
            "torbrowser",
            "epic",
            "waterfox",
            "palemoon",
            "yandex",
            "ucbrowser",
        };

        public PolicyEnforcementService(
            ILogger<PolicyEnforcementService> logger,
            WfpFilterService wfpService,
            DatabaseService databaseService,
            DnsInterceptorService dnsService)
        {
            _logger = logger;
            _wfpService = wfpService;
            _databaseService = databaseService;
            _dnsService = dnsService;
        }

        public void Start()
        {
            _isRunning = true;
            _logger.LogInformation("Starting Policy Enforcement Service");

            // Initial enforcement
            ApplyAll();

            // Re-apply every 60 seconds — if a smart child deletes a registry key,
            // we put it back within a minute.
            _enforcementTimer = new Timer(_ => ApplyAll(), null,
                TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
        }

        public void Stop()
        {
            _isRunning = false;
            _enforcementTimer?.Dispose();
            _enforcementTimer = null;
            _logger.LogInformation("Policy Enforcement Service stopped");

            // Note: we do NOT remove registry policies on shutdown.
            // Leaving them in place keeps DoH disabled even if the service is killed.
        }

        private void ApplyAll()
        {
            if (!_isRunning) return;
            try
            {
                ApplyDoHBlockingPolicies();
                BlockDoHServerIps();
                SeedBrowserDownloadBlacklist();
                KillUnauthorizedBrowsers();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during policy enforcement pass");
            }
        }

        #region DoH Registry Policies

        /// <summary>
        /// Write registry policies that force-disable DNS-over-HTTPS in all major browsers.
        /// These are Group Policy keys — the user can't override them from browser settings.
        /// </summary>
        private void ApplyDoHBlockingPolicies()
        {
            try
            {
                // Chrome
                SetPolicyKey(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off");
                SetPolicyKey(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsTemplates", "");
                SetPolicyKey(@"SOFTWARE\Policies\Google\Chrome", "BuiltInDnsClientEnabled", 0, RegistryValueKind.DWord);

                // Edge
                SetPolicyKey(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off");
                SetPolicyKey(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsTemplates", "");
                SetPolicyKey(@"SOFTWARE\Policies\Microsoft\Edge", "BuiltInDnsClientEnabled", 0, RegistryValueKind.DWord);

                // Firefox (via Mozilla policies) — Mode 5 = off
                SetPolicyKey(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled", 0, RegistryValueKind.DWord);
                SetPolicyKey(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Locked", 1, RegistryValueKind.DWord);

                // Windows 11 built-in DoH — disable
                SetPolicyKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters",
                    "EnableAutoDoh", 0, RegistryValueKind.DWord);

                _logger.LogDebug("DoH-blocking registry policies applied");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply DoH registry policies (admin rights required)");
            }
        }

        private static void SetPolicyKey(string subKey, string valueName, object value,
            RegistryValueKind kind = RegistryValueKind.String)
        {
            using var key = Registry.LocalMachine.CreateSubKey(subKey, true);
            key?.SetValue(valueName, value, kind);
        }

        #endregion

        #region WFP DoH IP Blocking

        /// <summary>
        /// Block outbound traffic to well-known DoH servers at the network layer.
        /// Even if a browser somehow uses DoH, it can't reach the server.
        /// </summary>
        private void BlockDoHServerIps()
        {
            try
            {
                foreach (var ip in DoHServerIps)
                {
                    // Skip 8.8.8.8 if we're using it as upstream — otherwise we block ourselves
                    if (ip == "8.8.8.8" || ip == "8.8.4.4") continue;
                    if (ip == "1.1.1.1" || ip == "1.0.0.1") continue; // backup upstream

                    if (IPAddress.TryParse(ip, out var addr))
                    {
                        _wfpService.BlockIpAddress(addr, $"DoH-{ip}");
                    }
                }
                _logger.LogDebug("DoH server IPs blocked at WFP layer");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to block DoH server IPs");
            }
        }

        #endregion

        #region Browser Download Blacklist

        /// <summary>
        /// Ensure all known browser download domains are present in the blacklist.
        /// Runs on every pass (idempotent — skips rows that already exist).
        /// </summary>
        private void SeedBrowserDownloadBlacklist()
        {
            try
            {
                using var conn = _databaseService.OpenConnection();
                using var cmd = conn.CreateCommand();

                int added = 0;
                foreach (var domain in BrowserDownloadDomains)
                {
                    cmd.Parameters.Clear();
                    cmd.CommandText = "SELECT COUNT(*) FROM url_lists WHERE url_pattern = @d AND list_type = 'blacklist'";
                    cmd.Parameters.AddWithValue("@d", domain);
                    long exists = Convert.ToInt64(cmd.ExecuteScalar() ?? 0);
                    if (exists > 0) continue;

                    cmd.Parameters.Clear();
                    cmd.CommandText = @"INSERT INTO url_lists (url_pattern, list_type, is_active, match_type, description)
                                        VALUES (@d, 'blacklist', 1, 'domain', 'Policy: browser/VPN download')";
                    cmd.Parameters.AddWithValue("@d", domain);
                    cmd.ExecuteNonQuery();
                    added++;
                }

                if (added > 0)
                {
                    _logger.LogInformation("Seeded {Count} browser-download domains into blacklist", added);
                    // Ask DNS service to reload its rules
                    _ = _dnsService.RefreshBlockListAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to seed browser-download blacklist");
            }
        }

        #endregion

        #region Unauthorized Process Termination

        /// <summary>
        /// Scan running processes and kill any browser that isn't on the allow-list.
        /// </summary>
        private void KillUnauthorizedBrowsers()
        {
            try
            {
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        string name = proc.ProcessName;
                        if (AllowedBrowserProcesses.Contains(name)) continue;
                        if (!UnauthorizedBrowserProcesses.Contains(name)) continue;

                        _logger.LogWarning("Terminating unauthorized browser: {Name} (PID {Pid})",
                            name, proc.Id);
                        proc.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // Ignore per-process errors (access denied, already exited)
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error scanning processes");
            }
        }

        #endregion
    }
}
