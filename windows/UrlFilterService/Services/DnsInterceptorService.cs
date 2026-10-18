using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace UrlFilterService.Services
{
    /// <summary>
    /// DNS interceptor that acts as a local DNS proxy.
    /// Listens on 127.0.0.1:53, checks domains against the filter database,
    /// blocks blacklisted domains (returns 0.0.0.0), and forwards allowed queries to upstream DNS.
    /// </summary>
    public class DnsInterceptorService
    {
        private readonly ILogger<DnsInterceptorService> _logger;
        private readonly DatabaseService _databaseService;
        private UdpClient? _listener;
        private CancellationTokenSource? _cts;
        private bool _isRunning;
        private bool _filteringEnabled = true;

        // Upstream DNS server to forward allowed queries to
        private IPEndPoint _upstreamDns = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53);
        private IPEndPoint _upstreamDnsBackup = new IPEndPoint(IPAddress.Parse("1.1.1.1"), 53);

        // Cached block/allow lists for fast lookup
        private readonly ConcurrentDictionary<string, bool> _blockCache = new();
        private HashSet<string> _blacklistedDomains = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _whitelistedDomains = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _blacklistPatterns = new();
        private DateTime _lastCacheRefresh = DateTime.MinValue;
        private readonly TimeSpan _cacheRefreshInterval = TimeSpan.FromMinutes(5);
        private readonly object _listLock = new();

        // Original DNS settings per adapter (adapter index -> DNS servers)
        private readonly Dictionary<uint, string[]> _originalDnsSettings = new();

        // Statistics
        private long _totalQueries;
        private long _blockedQueries;
        private long _forwardedQueries;

        public bool IsRunning => _isRunning;
        public bool FilteringEnabled => _filteringEnabled;
        public long TotalQueries => Interlocked.Read(ref _totalQueries);
        public long BlockedQueries => Interlocked.Read(ref _blockedQueries);
        public long ForwardedQueries => Interlocked.Read(ref _forwardedQueries);

        public DnsInterceptorService(ILogger<DnsInterceptorService> logger, DatabaseService databaseService)
        {
            _logger = logger;
            _databaseService = databaseService;
        }

        /// <summary>
        /// Start the DNS interceptor: load rules, bind to port 53, change system DNS
        /// </summary>
        public async Task StartAsync()
        {
            if (_isRunning)
            {
                _logger.LogWarning("DNS interceptor is already running");
                return;
            }

            try
            {
                _logger.LogInformation("Starting DNS Interceptor Service...");

                // Load filter rules from database
                await RefreshBlockListAsync();

                // Save original DNS settings before changing them
                SaveOriginalDnsSettings();

                // Start UDP listener on 127.0.0.1:53
                _listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 53));
                _cts = new CancellationTokenSource();
                _isRunning = true;

                // Change system DNS to point to our proxy
                SetSystemDns("127.0.0.1");

                // Start the listening loop in the background
                _ = Task.Run(() => ListenLoopAsync(_cts.Token));

                _logger.LogInformation("DNS Interceptor Service started. Listening on 127.0.0.1:53");
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                _logger.LogError("Port 53 is already in use. Cannot start DNS interceptor.");
                throw new InvalidOperationException("DNS port 53 is already in use. Stop any other DNS service first.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start DNS Interceptor Service");
                // Attempt to restore DNS if we partially started
                RestoreOriginalDnsSettings();
                _isRunning = false;
                throw;
            }
        }

        /// <summary>
        /// Stop the DNS interceptor: restore system DNS, stop listening
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;

            _logger.LogInformation("Stopping DNS Interceptor Service...");

            _isRunning = false;
            _cts?.Cancel();

            // Restore original DNS settings FIRST (before killing the listener)
            RestoreOriginalDnsSettings();

            // Then close the listener
            try
            {
                _listener?.Close();
                _listener?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing DNS listener");
            }

            _listener = null;
            _cts?.Dispose();
            _cts = null;

            _logger.LogInformation("DNS Interceptor Service stopped. Original DNS restored.");
        }

        /// <summary>
        /// Enable filtering (blocked domains return 0.0.0.0)
        /// </summary>
        public void EnableFiltering()
        {
            _filteringEnabled = true;
            _blockCache.Clear();
            _logger.LogInformation("DNS filtering ENABLED");
        }

        /// <summary>
        /// Disable filtering (all domains forwarded to upstream)
        /// </summary>
        public void DisableFiltering()
        {
            _filteringEnabled = false;
            _blockCache.Clear();
            _logger.LogInformation("DNS filtering DISABLED - all traffic passes through");
        }

        /// <summary>
        /// Reload filter rules from the database
        /// </summary>
        public async Task RefreshBlockListAsync()
        {
            try
            {
                var urlLists = await _databaseService.GetAllUrlListsAsync();

                var newBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var newWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var newPatterns = new List<string>();

                foreach (var entry in urlLists)
                {
                    if (!entry.IsActive) continue;

                    string domain = ExtractDomainFromPattern(entry.UrlPattern);

                    if (entry.ListType == UrlFilter.Shared.Models.ListType.Blacklist)
                    {
                        if (entry.UrlPattern.Contains("*") || entry.UrlPattern.Contains("?"))
                        {
                            newPatterns.Add(entry.UrlPattern.ToLowerInvariant());
                        }
                        else
                        {
                            newBlacklist.Add(domain);
                        }
                    }
                    else if (entry.ListType == UrlFilter.Shared.Models.ListType.Whitelist)
                    {
                        newWhitelist.Add(domain);
                    }
                }

                lock (_listLock)
                {
                    _blacklistedDomains = newBlacklist;
                    _whitelistedDomains = newWhitelist;
                    _blacklistPatterns = newPatterns;
                }

                _blockCache.Clear();
                _lastCacheRefresh = DateTime.UtcNow;

                _logger.LogInformation("DNS filter rules reloaded: {BlacklistCount} blocked domains, {WhitelistCount} allowed domains, {PatternCount} patterns",
                    newBlacklist.Count, newWhitelist.Count, newPatterns.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh DNS block list from database");
            }
        }

        #region DNS Listening & Processing

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            _logger.LogInformation("DNS listen loop started");

            while (!ct.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var result = await _listener!.ReceiveAsync();
                    // Process each query in its own task for concurrency
                    _ = Task.Run(() => ProcessQueryAsync(result.Buffer, result.RemoteEndPoint), ct);
                }
                catch (ObjectDisposedException)
                {
                    // Listener was closed, exit gracefully
                    break;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
                {
                    // Cancelled, exit gracefully
                    break;
                }
                catch (Exception ex)
                {
                    if (_isRunning)
                    {
                        _logger.LogError(ex, "Error in DNS listen loop");
                        await Task.Delay(100); // Brief delay before retrying
                    }
                }
            }

            _logger.LogInformation("DNS listen loop ended");
        }

        private async Task ProcessQueryAsync(byte[] queryData, IPEndPoint clientEndPoint)
        {
            Interlocked.Increment(ref _totalQueries);

            try
            {
                // Refresh cache periodically
                if (DateTime.UtcNow - _lastCacheRefresh > _cacheRefreshInterval)
                {
                    await RefreshBlockListAsync();
                }

                string domain = ExtractDomainFromDnsQuery(queryData);
                if (string.IsNullOrEmpty(domain))
                {
                    // Can't parse domain, forward as-is
                    await ForwardAndReplyAsync(queryData, clientEndPoint);
                    return;
                }

                // If filtering is disabled, forward everything
                if (!_filteringEnabled)
                {
                    await ForwardAndReplyAsync(queryData, clientEndPoint);
                    return;
                }

                // Check if domain should be blocked
                if (IsDomainBlocked(domain))
                {
                    Interlocked.Increment(ref _blockedQueries);
                    _logger.LogInformation("BLOCKED DNS query for: {Domain}", domain);

                    // Return 0.0.0.0 response
                    byte[] blockedResponse = BuildBlockedResponse(queryData);
                    await _listener!.SendAsync(blockedResponse, blockedResponse.Length, clientEndPoint);

                    // Log to database
                    _ = LogBlockedAttemptAsync(domain);
                }
                else
                {
                    Interlocked.Increment(ref _forwardedQueries);
                    await ForwardAndReplyAsync(queryData, clientEndPoint);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing DNS query");
            }
        }

        /// <summary>
        /// Forward DNS query to upstream and relay the response back to the client
        /// </summary>
        private async Task ForwardAndReplyAsync(byte[] queryData, IPEndPoint clientEndPoint)
        {
            try
            {
                using var forwarder = new UdpClient();
                forwarder.Client.ReceiveTimeout = 3000; // 3 second timeout

                await forwarder.SendAsync(queryData, queryData.Length, _upstreamDns);

                var cts = new CancellationTokenSource(3000);
                try
                {
                    var response = await forwarder.ReceiveAsync();
                    await _listener!.SendAsync(response.Buffer, response.Buffer.Length, clientEndPoint);
                }
                catch
                {
                    // Try backup DNS
                    await forwarder.SendAsync(queryData, queryData.Length, _upstreamDnsBackup);
                    var response = await forwarder.ReceiveAsync();
                    await _listener!.SendAsync(response.Buffer, response.Buffer.Length, clientEndPoint);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to forward DNS query to upstream");
            }
        }

        #endregion

        #region Domain Blocking Logic

        /// <summary>
        /// Check if a domain is blocked based on blacklist/whitelist rules
        /// </summary>
        private bool IsDomainBlocked(string domain)
        {
            domain = domain.ToLowerInvariant().TrimEnd('.');

            // Check cache first
            if (_blockCache.TryGetValue(domain, out bool cachedResult))
            {
                return cachedResult;
            }

            bool blocked;
            lock (_listLock)
            {
                // Whitelist takes priority - if explicitly allowed, never block
                if (_whitelistedDomains.Contains(domain))
                {
                    blocked = false;
                }
                // Check exact domain match in blacklist
                else if (_blacklistedDomains.Contains(domain))
                {
                    blocked = true;
                }
                // Check parent domains (e.g., block *.facebook.com blocks sub.facebook.com)
                else if (IsSubdomainBlocked(domain))
                {
                    blocked = true;
                }
                // Check wildcard patterns
                else if (MatchesAnyPattern(domain))
                {
                    blocked = true;
                }
                else
                {
                    blocked = false;
                }
            }

            // Cache the result
            _blockCache.TryAdd(domain, blocked);
            return blocked;
        }

        private bool IsSubdomainBlocked(string domain)
        {
            // Check if any parent domain is in the blacklist
            // e.g., if "facebook.com" is blocked, "www.facebook.com" and "m.facebook.com" are also blocked
            var parts = domain.Split('.');
            for (int i = 1; i < parts.Length - 1; i++)
            {
                string parentDomain = string.Join(".", parts.Skip(i));
                if (_blacklistedDomains.Contains(parentDomain))
                {
                    return true;
                }
            }
            return false;
        }

        private bool MatchesAnyPattern(string domain)
        {
            foreach (var pattern in _blacklistPatterns)
            {
                if (WildcardMatch(domain, pattern))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool WildcardMatch(string input, string pattern)
        {
            // Convert wildcard pattern to simple matching
            // * matches any number of characters, ? matches single character
            string regexPattern = "^" +
                System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(input, regexPattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        #endregion

        #region DNS Packet Parsing & Building

        /// <summary>
        /// Extract domain name from a DNS query packet
        /// </summary>
        private string ExtractDomainFromDnsQuery(byte[] data)
        {
            try
            {
                if (data.Length < 12) return ""; // Too short for DNS header

                // Skip 12-byte header, read QNAME from question section
                int offset = 12;
                var labels = new List<string>();

                while (offset < data.Length)
                {
                    int labelLen = data[offset];
                    if (labelLen == 0) break; // End of domain name

                    // Safety: check for compression pointers or invalid length
                    if ((labelLen & 0xC0) == 0xC0) break; // Compression pointer
                    if (labelLen > 63) break; // Invalid label length

                    offset++;
                    if (offset + labelLen > data.Length) break;

                    labels.Add(Encoding.ASCII.GetString(data, offset, labelLen));
                    offset += labelLen;
                }

                return labels.Count > 0 ? string.Join(".", labels) : "";
            }
            catch
            {
                return "";
            }
        }

        /// <summary>
        /// Build a DNS response that returns 0.0.0.0 for a blocked domain
        /// </summary>
        private byte[] BuildBlockedResponse(byte[] query)
        {
            // Find the end of the question section
            int offset = 12; // Skip header
            while (offset < query.Length && query[offset] != 0)
            {
                int labelLen = query[offset];
                if ((labelLen & 0xC0) == 0xC0)
                {
                    offset += 2; // Compression pointer is 2 bytes
                    break;
                }
                offset += labelLen + 1;
            }
            if (offset < query.Length && query[offset] == 0) offset++; // Skip null terminator
            offset += 4; // Skip QTYPE (2) + QCLASS (2)

            int questionEnd = offset;

            // Build response: header + question + one A record answer
            byte[] response = new byte[questionEnd + 16];

            // Copy original header + question
            Array.Copy(query, 0, response, 0, Math.Min(questionEnd, query.Length));

            // Modify header flags
            response[2] = 0x81; // QR=1 (response), Opcode=0, AA=0, TC=0, RD=1
            response[3] = 0x80; // RA=1, Z=0, RCODE=0 (no error)
            // QDCOUNT stays as-is (copied from query)
            response[6] = 0; response[7] = 1; // ANCOUNT = 1
            response[8] = 0; response[9] = 0; // NSCOUNT = 0
            response[10] = 0; response[11] = 0; // ARCOUNT = 0

            // Answer section: A record pointing to 0.0.0.0
            int ans = questionEnd;
            response[ans] = 0xC0;     // Name pointer to offset 12 (QNAME in question)
            response[ans + 1] = 0x0C;
            response[ans + 2] = 0x00; response[ans + 3] = 0x01; // TYPE = A (1)
            response[ans + 4] = 0x00; response[ans + 5] = 0x01; // CLASS = IN (1)
            response[ans + 6] = 0x00; response[ans + 7] = 0x00;
            response[ans + 8] = 0x00; response[ans + 9] = 0x3C; // TTL = 60 seconds
            response[ans + 10] = 0x00; response[ans + 11] = 0x04; // RDLENGTH = 4
            response[ans + 12] = 0x00; response[ans + 13] = 0x00;
            response[ans + 14] = 0x00; response[ans + 15] = 0x00; // RDATA = 0.0.0.0

            return response;
        }

        #endregion

        #region System DNS Configuration

        /// <summary>
        /// Save original DNS settings for all active network adapters
        /// </summary>
        private void SaveOriginalDnsSettings()
        {
            try
            {
                _originalDnsSettings.Clear();

                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");

                foreach (ManagementObject adapter in searcher.Get())
                {
                    try
                    {
                        uint index = (uint)adapter["Index"];
                        var dnsServers = adapter["DNSServerSearchOrder"] as string[];

                        // Filter out our own loopback DNS (from a leftover previous run).
                        // If the only "saved" server is 127.0.0.1, treat as DHCP (empty array → restore to DHCP).
                        var cleaned = dnsServers?
                            .Where(s => !string.IsNullOrWhiteSpace(s)
                                        && s != "127.0.0.1"
                                        && !s.StartsWith("127."))
                            .ToArray() ?? Array.Empty<string>();

                        _originalDnsSettings[index] = cleaned;

                        string adapterName = adapter["Description"]?.ToString() ?? "Unknown";
                        string currentDns = cleaned.Length > 0 ? string.Join(", ", cleaned) : "DHCP";
                        _logger.LogInformation("Saved DNS for adapter '{Adapter}' (idx {Index}): {Dns}",
                            adapterName, index, currentDns);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to save DNS for adapter");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save original DNS settings");
            }
        }

        /// <summary>
        /// Set system DNS to our local proxy (127.0.0.1)
        /// </summary>
        private void SetSystemDns(string dnsServer)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");

                foreach (ManagementObject adapter in searcher.Get())
                {
                    try
                    {
                        string adapterName = adapter["Description"]?.ToString() ?? "Unknown";

                        var newDns = new ManagementBaseObject[] { };
                        var setDnsParams = adapter.GetMethodParameters("SetDNSServerSearchOrder");
                        setDnsParams["DNSServerSearchOrder"] = new string[] { dnsServer };

                        var result = adapter.InvokeMethod("SetDNSServerSearchOrder", setDnsParams, null);
                        uint returnValue = (uint)result["ReturnValue"];

                        if (returnValue == 0)
                        {
                            _logger.LogInformation("Set DNS to {Dns} for adapter '{Adapter}'", dnsServer, adapterName);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to set DNS for adapter '{Adapter}'. Return code: {Code}",
                                adapterName, returnValue);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to set DNS for adapter");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to set system DNS");
            }
        }

        /// <summary>
        /// Restore original DNS settings for all network adapters
        /// </summary>
        private void RestoreOriginalDnsSettings()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = True");

                foreach (ManagementObject adapter in searcher.Get())
                {
                    try
                    {
                        uint index = (uint)adapter["Index"];
                        string adapterName = adapter["Description"]?.ToString() ?? "Unknown";

                        var setDnsParams = adapter.GetMethodParameters("SetDNSServerSearchOrder");

                        if (_originalDnsSettings.TryGetValue(index, out string[]? originalDns)
                            && originalDns != null && originalDns.Length > 0)
                        {
                            // Restore original static DNS
                            setDnsParams["DNSServerSearchOrder"] = originalDns;
                            _logger.LogInformation("Restoring DNS for '{Adapter}' to: {Dns}",
                                adapterName, string.Join(", ", originalDns));
                        }
                        else
                        {
                            // Restore to DHCP (null = use DHCP DNS)
                            setDnsParams["DNSServerSearchOrder"] = null;
                            _logger.LogInformation("Restoring DNS for '{Adapter}' to DHCP", adapterName);
                        }

                        var result = adapter.InvokeMethod("SetDNSServerSearchOrder", setDnsParams, null);
                        uint returnValue = (uint)result["ReturnValue"];

                        if (returnValue != 0)
                        {
                            _logger.LogWarning("Failed to restore DNS for adapter '{Adapter}'. Return code: {Code}",
                                adapterName, returnValue);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to restore DNS for adapter");
                    }
                }

                _originalDnsSettings.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore original DNS settings");
            }
        }

        #endregion

        #region Helpers

        private string ExtractDomainFromPattern(string urlPattern)
        {
            // Remove protocol prefix if present
            string domain = urlPattern;
            if (domain.Contains("://"))
            {
                domain = domain.Substring(domain.IndexOf("://") + 3);
            }

            // Remove path
            int slashIndex = domain.IndexOf('/');
            if (slashIndex >= 0)
            {
                domain = domain.Substring(0, slashIndex);
            }

            // Remove port
            int colonIndex = domain.IndexOf(':');
            if (colonIndex >= 0)
            {
                domain = domain.Substring(0, colonIndex);
            }

            return domain.Trim().ToLowerInvariant();
        }

        private async Task LogBlockedAttemptAsync(string domain)
        {
            try
            {
                var blockedAttempt = new UrlFilter.Shared.Models.BlockedAttempt
                {
                    Url = domain,
                    ContentType = "dns_query",
                    BlockReason = "blacklist",
                    Timestamp = DateTime.UtcNow,
                    DeviceInfo = Environment.MachineName,
                    SeverityLevel = "low"
                };

                await _databaseService.InsertBlockedAttemptAsync(blockedAttempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to log blocked DNS attempt for {Domain}", domain);
            }
        }

        #endregion
    }
}
