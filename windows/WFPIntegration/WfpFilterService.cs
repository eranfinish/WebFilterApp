using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;
//using UrlFilterService.Services;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Windows Filtering Platform (WFP) integration service
    /// Provides system-level network traffic filtering capabilities
    /// </summary>
    public class WfpFilterService
    {
        private readonly ILogger<WfpFilterService> _logger;
        //private readonly DatabaseService _databaseService;
        private IntPtr _engineHandle;
        private readonly List<WfpFilter> _activeFilters;
        private bool _isRunning = false;

        // WFP API Constants
        private const uint FWP_E_ALREADY_EXISTS = 0x80320009;
        private const uint ERROR_SUCCESS = 0;
        private const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;

        public WfpFilterService(ILogger<WfpFilterService> logger/*, DatabaseService databaseService*/)
        {
            _logger = logger;
            //_databaseService = databaseService;
            _activeFilters = new List<WfpFilter>();
        }

        public bool IsRunning => _isRunning;

        public void Start()
        {
            try
            {
                _logger.LogInformation("Starting WFP Filter Service");

                // Create WFP engine session
                CreateWfpEngine();

                // Install base filters
                InstallBaseFilters();

                // Load user-specific filters
                Task.Run(async () => await LoadUserFiltersAsync());

                _isRunning = true;
                _logger.LogInformation("WFP Filter Service started successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start WFP Filter Service");
                throw;
            }
        }

        public void Stop()
        {
            try
            {
                _logger.LogInformation("Stopping WFP Filter Service");

                _isRunning = false;

                // Remove all filters
                RemoveAllFilters();

                // Destroy WFP engine
                DestroyWfpEngine();

                _logger.LogInformation("WFP Filter Service stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping WFP Filter Service");
            }
        }

        private void CreateWfpEngine()
        {
            var session = new FWPM_SESSION
            {
                displayData = new FWPM_DISPLAY_DATA
                {
                    name = "URL Filter Session",
                    description = "URL filtering and parental control session"
                },
                flags = FWPM_SESSION_FLAG_DYNAMIC,
                txnWaitTimeoutInMSec = 5000
            };

            uint result = FwpmEngineOpen(
                null,
                RPC_C_AUTHN_DEFAULT,
                IntPtr.Zero,
                ref session,
                out _engineHandle);

            if (result != ERROR_SUCCESS)
            {
                throw new InvalidOperationException($"Failed to open WFP engine. Error: 0x{result:X8}");
            }

            _logger.LogInformation("WFP engine session created");
        }

        private void DestroyWfpEngine()
        {
            if (_engineHandle != IntPtr.Zero)
            {
                FwpmEngineClose(_engineHandle);
                _engineHandle = IntPtr.Zero;
                _logger.LogInformation("WFP engine session closed");
            }
        }

        private void InstallBaseFilters()
        {
            try
            {
                // Install outbound HTTP filter
                InstallHttpOutboundFilter();

                // Install outbound HTTPS filter
                InstallHttpsOutboundFilter();

                // Install DNS filter
                InstallDnsFilter();

                // Install application filter
                InstallApplicationFilter();

                _logger.LogInformation("Base WFP filters installed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error installing base filters");
                throw;
            }
        }

        private void InstallHttpOutboundFilter()
        {
            var filter = new FWPM_FILTER
            {
                displayData = new FWPM_DISPLAY_DATA
                {
                    name = "URL Filter HTTP Outbound",
                    description = "Filters outbound HTTP traffic for URL filtering"
                },
                layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                weight = new FWP_VALUE { type = FWP_DATA_TYPE.FWP_UINT64, value = new FWP_VALUE_UNION { uint64 = 0x8000000000000000 } },
                numFilterConditions = 1
            };

            // Create condition for HTTP traffic (port 80)
            var conditions = new FWPM_FILTER_CONDITION[1];
            conditions[0] = new FWPM_FILTER_CONDITION
            {
                fieldKey = FWPM_CONDITION_IP_REMOTE_PORT,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE
                {
                    type = FWP_DATA_TYPE.FWP_UINT16,
                    value = new FWP_CONDITION_VALUE_UNION { uint16 = 80 }
                }
            };

            // Set callout for this filter
            filter.action = new FWPM_ACTION
            {
                type = FWP_ACTION_TYPE.FWP_ACTION_CALLOUT_INSPECTION,
                calloutKey = HttpCalloutGuid
            };

            IntPtr conditionsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER_CONDITION)));
            Marshal.StructureToPtr(conditions[0], conditionsPtr, false);
            filter.filterCondition = conditionsPtr;

            Guid filterId;
            uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);

            Marshal.FreeHGlobal(conditionsPtr);

            if (result == ERROR_SUCCESS)
            {
                _activeFilters.Add(new WfpFilter("HTTP_OUTBOUND", filterId));
                _logger.LogDebug("HTTP outbound filter installed");
            }
            else if (result != FWP_E_ALREADY_EXISTS)
            {
                _logger.LogError($"Failed to install HTTP outbound filter. Error: 0x{result:X8}");
            }
        }

        private void InstallHttpsOutboundFilter()
        {
            var filter = new FWPM_FILTER
            {
                displayData = new FWPM_DISPLAY_DATA
                {
                    name = "URL Filter HTTPS Outbound",
                    description = "Filters outbound HTTPS traffic for URL filtering"
                },
                layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                weight = new FWP_VALUE { type = FWP_DATA_TYPE.FWP_UINT64, value = new FWP_VALUE_UNION { uint64 = 0x8000000000000000 } },
                numFilterConditions = 1
            };

            // Create condition for HTTPS traffic (port 443)
            var conditions = new FWPM_FILTER_CONDITION[1];
            conditions[0] = new FWPM_FILTER_CONDITION
            {
                fieldKey = FWPM_CONDITION_IP_REMOTE_PORT,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE
                {
                    type = FWP_DATA_TYPE.FWP_UINT16,
                    value = new FWP_CONDITION_VALUE_UNION { uint16 = 443 }
                }
            };

            filter.action = new FWPM_ACTION
            {
                type = FWP_ACTION_TYPE.FWP_ACTION_CALLOUT_INSPECTION,
                calloutKey = HttpsCalloutGuid
            };

            IntPtr conditionsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER_CONDITION)));
            Marshal.StructureToPtr(conditions[0], conditionsPtr, false);
            filter.filterCondition = conditionsPtr;

            Guid filterId;
            uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);

            Marshal.FreeHGlobal(conditionsPtr);

            if (result == ERROR_SUCCESS)
            {
                _activeFilters.Add(new WfpFilter("HTTPS_OUTBOUND", filterId));
                _logger.LogDebug("HTTPS outbound filter installed");
            }
            else if (result != FWP_E_ALREADY_EXISTS)
            {
                _logger.LogError($"Failed to install HTTPS outbound filter. Error: 0x{result:X8}");
            }
        }

        private void InstallDnsFilter()
        {
            var filter = new FWPM_FILTER
            {
                displayData = new FWPM_DISPLAY_DATA
                {
                    name = "URL Filter DNS",
                    description = "Filters DNS queries for URL filtering"
                },
                layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                weight = new FWP_VALUE { type = FWP_DATA_TYPE.FWP_UINT64, value = new FWP_VALUE_UNION { uint64 = 0x8000000000000000 } },
                numFilterConditions = 1
            };

            // Create condition for DNS traffic (port 53)
            var conditions = new FWPM_FILTER_CONDITION[1];
            conditions[0] = new FWPM_FILTER_CONDITION
            {
                fieldKey = FWPM_CONDITION_IP_REMOTE_PORT,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE
                {
                    type = FWP_DATA_TYPE.FWP_UINT16,
                    value = new FWP_CONDITION_VALUE_UNION { uint16 = 53 }
                }
            };

            filter.action = new FWPM_ACTION
            {
                type = FWP_ACTION_TYPE.FWP_ACTION_CALLOUT_INSPECTION,
                calloutKey = DnsCalloutGuid
            };

            IntPtr conditionsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER_CONDITION)));
            Marshal.StructureToPtr(conditions[0], conditionsPtr, false);
            filter.filterCondition = conditionsPtr;

            Guid filterId;
            uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);

            Marshal.FreeHGlobal(conditionsPtr);

            if (result == ERROR_SUCCESS)
            {
                _activeFilters.Add(new WfpFilter("DNS", filterId));
                _logger.LogDebug("DNS filter installed");
            }
            else if (result != FWP_E_ALREADY_EXISTS)
            {
                _logger.LogError($"Failed to install DNS filter. Error: 0x{result:X8}");
            }
        }

        private void InstallApplicationFilter()
        {
            // This filter can block specific applications
            var filter = new FWPM_FILTER
            {
                displayData = new FWPM_DISPLAY_DATA
                {
                    name = "URL Filter Application Block",
                    description = "Blocks specific applications from network access"
                },
                layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                weight = new FWP_VALUE { type = FWP_DATA_TYPE.FWP_UINT64, value = new FWP_VALUE_UNION { uint64 = 0x9000000000000000 } },
                numFilterConditions = 0
            };

            filter.action = new FWPM_ACTION
            {
                type = FWP_ACTION_TYPE.FWP_ACTION_CALLOUT_INSPECTION,
                calloutKey = AppCalloutGuid
            };

            Guid filterId;
            uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);

            if (result == ERROR_SUCCESS)
            {
                _activeFilters.Add(new WfpFilter("APPLICATION", filterId));
                _logger.LogDebug("Application filter installed");
            }
            else if (result != FWP_E_ALREADY_EXISTS)
            {
                _logger.LogError($"Failed to install application filter. Error: 0x{result:X8}");
            }
        }

        private async Task LoadUserFiltersAsync()
        {
            try
            {
                //var urlLists = await _databaseService.GetAllUrlListsAsync();
                var urlLists = new List<UrlList>(); // Temporary placeholder

                foreach (var urlEntry in urlLists)
                {
                    if (urlEntry.IsActive)
                    {
                        await InstallUrlFilterAsync(urlEntry);
                    }
                }

                _logger.LogInformation($"Loaded {urlLists.Count} URL filters");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading user filters");
            }
        }

        private async Task InstallUrlFilterAsync(UrlList urlEntry)
        {
            try
            {
                // Create IP-based filter for the URL
                var ipAddresses = await ResolveHostToIpAsync(ExtractHostFromUrl(urlEntry.UrlPattern));

                foreach (var ipAddress in ipAddresses)
                {
                    InstallIpBasedFilter(ipAddress, urlEntry);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error installing URL filter for {urlEntry.UrlPattern}");
            }
        }

        private void InstallIpBasedFilter(IPAddress ipAddress, UrlList urlEntry)
        {
            var filter = new FWPM_FILTER
            {
                displayData = new FWPM_DISPLAY_DATA
                {
                    name = $"URL Filter - {urlEntry.UrlPattern}",
                    description = $"Filter for {urlEntry.UrlPattern}"
                },
                layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                weight = new FWP_VALUE { type = FWP_DATA_TYPE.FWP_UINT64, value = new FWP_VALUE_UNION { uint64 = 0x7000000000000000 } },
                numFilterConditions = 1
            };

            // Create condition for specific IP
            var conditions = new FWPM_FILTER_CONDITION[1];
            conditions[0] = new FWPM_FILTER_CONDITION
            {
                fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS,
                matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                conditionValue = new FWP_CONDITION_VALUE
                {
                    type = FWP_DATA_TYPE.FWP_UINT32,
                    value = new FWP_CONDITION_VALUE_UNION { uint32 = (uint)ipAddress.Address }
                }
            };

            // Set action based on list type
            if (urlEntry.ListType == ListType.Blacklist)
            {
                filter.action = new FWPM_ACTION { type = FWP_ACTION_TYPE.FWP_ACTION_BLOCK };
            }
            else
            {
                filter.action = new FWPM_ACTION
                {
                    type = FWP_ACTION_TYPE.FWP_ACTION_CALLOUT_INSPECTION,
                    calloutKey = AllowCalloutGuid
                };
            }

            IntPtr conditionsPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER_CONDITION)));
            Marshal.StructureToPtr(conditions[0], conditionsPtr, false);
            filter.filterCondition = conditionsPtr;

            Guid filterId;
            uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);

            Marshal.FreeHGlobal(conditionsPtr);

            if (result == ERROR_SUCCESS)
            {
                _activeFilters.Add(new WfpFilter($"URL_{urlEntry.Id}", filterId));
            }
        }

        public async Task BlockUserTrafficAsync(DeviceUser user)
        {
            try
            {
                // Install user-specific block filter
                var filter = new FWPM_FILTER
                {
                    displayData = new FWPM_DISPLAY_DATA
                    {
                        name = $"Block User - {user.Username}",
                        description = $"Blocks all traffic for user {user.Username}"
                    },
                    layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                    subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                    weight = new FWP_VALUE { type = FWP_DATA_TYPE.FWP_UINT64, value = new FWP_VALUE_UNION { uint64 = 0xA000000000000000 } },
                    numFilterConditions = 1
                };

                // Create condition for specific user (this would need user SID)
                // Simplified implementation - in practice, you'd use user SID
                filter.action = new FWPM_ACTION { type = FWP_ACTION_TYPE.FWP_ACTION_BLOCK };

                Guid filterId;
                uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);

                if (result == ERROR_SUCCESS)
                {
                    _activeFilters.Add(new WfpFilter($"BLOCK_USER_{user.Id}", filterId));
                    _logger.LogInformation($"Blocked traffic for user {user.Username}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error blocking user traffic for {user.Username}");
            }
        }

        public async Task UnblockUserTrafficAsync(DeviceUser user)
        {
            try
            {
                var filterName = $"BLOCK_USER_{user.Id}";
                var filter = _activeFilters.Find(f => f.Name == filterName);

                if (filter != null)
                {
                    uint result = FwpmFilterDeleteById(_engineHandle, filter.Id);
                    if (result == ERROR_SUCCESS)
                    {
                        _activeFilters.Remove(filter);
                        _logger.LogInformation($"Unblocked traffic for user {user.Username}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error unblocking user traffic for {user.Username}");
            }
        }

        private void RemoveAllFilters()
        {
            foreach (var filter in _activeFilters.ToArray())
            {
                try
                {
                    uint result = FwpmFilterDeleteById(_engineHandle, filter.Id);
                    if (result == ERROR_SUCCESS)
                    {
                        _activeFilters.Remove(filter);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error removing filter {filter.Name}");
                }
            }
        }

        private async Task<List<IPAddress>> ResolveHostToIpAsync(string hostname)
        {
            var ipAddresses = new List<IPAddress>();

            try
            {
                var addresses = await Dns.GetHostAddressesAsync(hostname);
                ipAddresses.AddRange(addresses);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to resolve hostname {hostname}");
            }

            return ipAddresses;
        }

        private string ExtractHostFromUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
                {
                    return uri.Host;
                }
                
                // Handle cases where URL doesn't have protocol
                if (!url.Contains("://"))
                {
                    url = "http://" + url;
                    if (Uri.TryCreate(url, UriKind.Absolute, out uri))
                    {
                        return uri.Host;
                    }
                }

                // Fallback - treat as hostname
                return url.Split('/')[0];
            }
            catch
            {
                return url;
            }
        }

        #region WFP API Declarations and Structures

        // WFP API imports
        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmEngineOpen(
            [MarshalAs(UnmanagedType.LPWStr)] string serverName,
            uint authnService,
            IntPtr authIdentity,
            ref FWPM_SESSION session,
            out IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmEngineClose(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmFilterAdd(
            IntPtr engineHandle,
            ref FWPM_FILTER filter,
            IntPtr sd,
            out Guid id);

        [DllImport("fwpuclnt.dll", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmFilterDeleteById(IntPtr engineHandle, Guid id);

        // Constants
        private const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;

        // GUIDs for WFP layers and callouts
        private static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 = new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");
        private static readonly Guid FWPM_SUBLAYER_UNIVERSAL = new Guid("eebecc03-ced4-4380-819a-2734397b2b74");
        private static readonly Guid FWPM_CONDITION_IP_REMOTE_PORT = new Guid("c35a604d-d22b-4e1a-91b4-68f674ee674b");
        private static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS = new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

        // Callout GUIDs (these would be registered separately)
        private static readonly Guid HttpCalloutGuid = new Guid("{12345678-1234-1234-1234-123456789012}");
        private static readonly Guid HttpsCalloutGuid = new Guid("{12345678-1234-1234-1234-123456789013}");
        private static readonly Guid DnsCalloutGuid = new Guid("{12345678-1234-1234-1234-123456789014}");
        private static readonly Guid AppCalloutGuid = new Guid("{12345678-1234-1234-1234-123456789015}");
        private static readonly Guid AllowCalloutGuid = new Guid("{12345678-1234-1234-1234-123456789016}");

        // Structures
        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_SESSION
        {
            public Guid sessionKey;
            public FWPM_DISPLAY_DATA displayData;
            public uint flags;
            public uint txnWaitTimeoutInMSec;
            public uint processId;
            public IntPtr sid;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string username;
            public bool kernelMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_DISPLAY_DATA
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string name;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string description;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_FILTER
        {
            public Guid filterKey;
            public FWPM_DISPLAY_DATA displayData;
            public uint flags;
            public IntPtr providerKey;
            public FWP_BYTE_BLOB providerData;
            public Guid layerKey;
            public Guid subLayerKey;
            public FWP_VALUE weight;
            public uint numFilterConditions;
            public IntPtr filterCondition;
            public FWPM_ACTION action;
            public IntPtr providerContextKey;
            public IntPtr reserved;
            public ulong filterId;
            public FWP_VALUE effectiveWeight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_FILTER_CONDITION
        {
            public Guid fieldKey;
            public FWP_MATCH_TYPE matchType;
            public FWP_CONDITION_VALUE conditionValue;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_ACTION
        {
            public FWP_ACTION_TYPE type;
            public Guid filterType;
            public Guid calloutKey;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWP_VALUE
        {
            public FWP_DATA_TYPE type;
            public FWP_VALUE_UNION value;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FWP_VALUE_UNION
        {
            [FieldOffset(0)] public byte uint8;
            [FieldOffset(0)] public ushort uint16;
            [FieldOffset(0)] public uint uint32;
            [FieldOffset(0)] public ulong uint64;
            [FieldOffset(0)] public IntPtr ptr;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWP_CONDITION_VALUE
        {
            public FWP_DATA_TYPE type;
            public FWP_CONDITION_VALUE_UNION value;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FWP_CONDITION_VALUE_UNION
        {
            [FieldOffset(0)] public byte uint8;
            [FieldOffset(0)] public ushort uint16;
            [FieldOffset(0)] public uint uint32;
            [FieldOffset(0)] public ulong uint64;
            [FieldOffset(0)] public IntPtr ptr;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWP_BYTE_BLOB
        {
            public uint size;
            public IntPtr data;
        }

        private enum FWP_DATA_TYPE : uint
        {
            FWP_EMPTY = 0,
            FWP_UINT8 = 1,
            FWP_UINT16 = 2,
            FWP_UINT32 = 3,
            FWP_UINT64 = 4
        }

        private enum FWP_MATCH_TYPE : uint
        {
            FWP_MATCH_EQUAL = 0,
            FWP_MATCH_GREATER = 1,
            FWP_MATCH_LESS = 2,
            FWP_MATCH_GREATER_OR_EQUAL = 3,
            FWP_MATCH_LESS_OR_EQUAL = 4,
            FWP_MATCH_RANGE = 5,
            FWP_MATCH_FLAGS_ALL_SET = 6,
            FWP_MATCH_FLAGS_ANY_SET = 7,
            FWP_MATCH_FLAGS_NONE_SET = 8,
            FWP_MATCH_EQUAL_CASE_INSENSITIVE = 9,
            FWP_MATCH_NOT_EQUAL = 10
        }

        private enum FWP_ACTION_TYPE : uint
        {
            FWP_ACTION_BLOCK = 0x00000001,
            FWP_ACTION_PERMIT = 0x00000002,
            FWP_ACTION_CALLOUT_TERMINATING = 0x00000003,
            FWP_ACTION_CALLOUT_INSPECTION = 0x00000004,
            FWP_ACTION_CALLOUT_UNKNOWN = 0x00000005,
            FWP_ACTION_CONTINUE = 0x00000006,
            FWP_ACTION_NONE = 0x00000007,
            FWP_ACTION_NONE_NO_MATCH = 0x00000008
        }

        #endregion

        private class WfpFilter
        {
            public string Name { get; set; }
            public Guid Id { get; set; }

            public WfpFilter(string name, Guid id)
            {
                Name = name;
                Id = id;
            }
        }
    }
}