using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Net;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UrlFilter.Shared.Models;

namespace UrlFilterService.Services
{
    /// <summary>
    /// Windows Filtering Platform (WFP) integration service.
    /// Uses FWP_ACTION_BLOCK to block traffic by IP address — no kernel driver needed.
    /// Works alongside DnsInterceptorService as a secondary enforcement layer.
    /// </summary>
    public class WfpFilterService
    {
        private readonly ILogger<WfpFilterService> _logger;
        private IntPtr _engineHandle;
        private readonly List<WfpFilter> _activeFilters = new();
        private bool _isRunning;

        private const uint ERROR_SUCCESS = 0;
        private const uint FWP_E_ALREADY_EXISTS = 0x80320009;
        private const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;

        public WfpFilterService(ILogger<WfpFilterService> logger)
        {
            _logger = logger;
        }

        public bool IsRunning => _isRunning;

        public void Start()
        {
            try
            {
                _logger.LogInformation("Starting WFP Filter Service");

                // BFE will let a non-elevated process open a session (read paths), but
                // FwpmFilterAdd0 corrupts memory on such a session and takes down the
                // process with AccessViolation instead of returning ERROR_ACCESS_DENIED.
                // In production the service runs as LOCAL SYSTEM; when run unelevated
                // (console/dev mode) we skip engine open so downstream filter calls no-op.
                if (!IsProcessElevated())
                {
                    _logger.LogWarning("WFP Filter Service disabled: process is not elevated. Install the service or run as administrator to enable WFP filtering.");
                    return;
                }

                CreateWfpEngine();
                _isRunning = true;
                _logger.LogInformation("WFP Filter Service started (IP-block mode)");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start WFP Filter Service");
                throw;
            }
        }

        private static bool IsProcessElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        public void Stop()
        {
            try
            {
                _logger.LogInformation("Stopping WFP Filter Service");
                _isRunning = false;
                RemoveAllFilters();
                DestroyWfpEngine();
                _logger.LogInformation("WFP Filter Service stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping WFP Filter Service");
            }
        }

        /// <summary>
        /// Block a specific IP address (all outbound traffic to that IP is dropped)
        /// </summary>
        public bool BlockIpAddress(IPAddress ipAddress, string description)
        {
            if (_engineHandle == IntPtr.Zero)
            {
                _logger.LogWarning("WFP engine not initialized, cannot block IP");
                return false;
            }

            try
            {
                var filter = new FWPM_FILTER
                {
                    displayData = new FWPM_DISPLAY_DATA
                    {
                        name = $"UrlFilter Block - {description}",
                        description = $"Blocks traffic to {ipAddress}"
                    },
                    layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                    subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                    weight = new FWP_VALUE
                    {
                        type = FWP_DATA_TYPE.FWP_UINT64,
                        value = new FWP_VALUE_UNION { uint64 = 0x7000000000000000 }
                    },
                    numFilterConditions = 1
                };

                // Condition: remote IP matches
                var condition = new FWPM_FILTER_CONDITION
                {
                    fieldKey = FWPM_CONDITION_IP_REMOTE_ADDRESS,
                    matchType = FWP_MATCH_TYPE.FWP_MATCH_EQUAL,
                    conditionValue = new FWP_CONDITION_VALUE
                    {
                        type = FWP_DATA_TYPE.FWP_UINT32,
                        value = new FWP_CONDITION_VALUE_UNION
                        {
                            uint32 = BitConverter.ToUInt32(ipAddress.GetAddressBytes(), 0)
                        }
                    }
                };

                filter.action = new FWPM_ACTION { type = FWP_ACTION_TYPE.FWP_ACTION_BLOCK };

                IntPtr condPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER_CONDITION)));
                Marshal.StructureToPtr(condition, condPtr, false);
                filter.filterCondition = condPtr;

                Guid filterId;
                uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);
                Marshal.FreeHGlobal(condPtr);

                if (result == ERROR_SUCCESS)
                {
                    _activeFilters.Add(new WfpFilter($"IP_{ipAddress}", filterId));
                    _logger.LogInformation("Blocked IP: {Ip} ({Description})", ipAddress, description);
                    return true;
                }
                else if (result == FWP_E_ALREADY_EXISTS)
                {
                    return true; // Already blocked
                }
                else
                {
                    _logger.LogError("Failed to block IP {Ip}. Error: 0x{Error:X8}", ipAddress, result);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error blocking IP {Ip}", ipAddress);
                return false;
            }
        }

        /// <summary>
        /// Block all outbound traffic for a specific user (by installing a high-priority block filter)
        /// </summary>
        public Task BlockUserTrafficAsync(DeviceUser user)
        {
            return Task.Run(() =>
            {
                if (_engineHandle == IntPtr.Zero) return;

                try
                {
                    var filter = new FWPM_FILTER
                    {
                        displayData = new FWPM_DISPLAY_DATA
                        {
                            name = $"Block User - {user.Username}",
                            description = $"Blocks all traffic for user {user.Username}"
                        },
                        layerKey = FWPM_LAYER_ALE_AUTH_CONNECT_V4,
                        subLayerKey = FWPM_SUBLAYER_UNIVERSAL,
                        weight = new FWP_VALUE
                        {
                            type = FWP_DATA_TYPE.FWP_UINT64,
                            value = new FWP_VALUE_UNION { uint64 = 0xA000000000000000 }
                        },
                        numFilterConditions = 0
                    };

                    filter.action = new FWPM_ACTION { type = FWP_ACTION_TYPE.FWP_ACTION_BLOCK };

                    Guid filterId;
                    uint result = FwpmFilterAdd(_engineHandle, ref filter, IntPtr.Zero, out filterId);

                    if (result == ERROR_SUCCESS)
                    {
                        _activeFilters.Add(new WfpFilter($"BLOCK_USER_{user.Id}", filterId));
                        _logger.LogInformation("Blocked traffic for user {Username}", user.Username);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error blocking user traffic for {Username}", user.Username);
                }
            });
        }

        /// <summary>
        /// Unblock a user's traffic
        /// </summary>
        public Task UnblockUserTrafficAsync(DeviceUser user)
        {
            return Task.Run(() =>
            {
                var filterName = $"BLOCK_USER_{user.Id}";
                RemoveFilterByName(filterName);
            });
        }

        /// <summary>
        /// Remove block for a specific IP
        /// </summary>
        public bool UnblockIpAddress(IPAddress ipAddress)
        {
            return RemoveFilterByName($"IP_{ipAddress}");
        }

        /// <summary>
        /// Remove all IP block filters
        /// </summary>
        public void RemoveAllIpBlocks()
        {
            var ipFilters = _activeFilters.FindAll(f => f.Name.StartsWith("IP_"));
            foreach (var filter in ipFilters)
            {
                RemoveFilter(filter);
            }
            _logger.LogInformation("Removed all IP block filters");
        }

        #region Internal

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

            uint result = FwpmEngineOpen(null, RPC_C_AUTHN_DEFAULT, IntPtr.Zero, ref session, out _engineHandle);

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

        private bool RemoveFilterByName(string name)
        {
            var filter = _activeFilters.Find(f => f.Name == name);
            if (filter != null)
            {
                return RemoveFilter(filter);
            }
            return false;
        }

        private bool RemoveFilter(WfpFilter filter)
        {
            try
            {
                uint result = FwpmFilterDeleteById(_engineHandle, filter.Id);
                if (result == ERROR_SUCCESS)
                {
                    _activeFilters.Remove(filter);
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing filter {Name}", filter.Name);
            }
            return false;
        }

        private void RemoveAllFilters()
        {
            foreach (var filter in _activeFilters.ToArray())
            {
                RemoveFilter(filter);
            }
        }

        #endregion

        #region P/Invoke

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmEngineOpen0", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmEngineOpen(
            [MarshalAs(UnmanagedType.LPWStr)] string? serverName,
            uint authnService,
            IntPtr authIdentity,
            ref FWPM_SESSION session,
            out IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmEngineClose0", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmEngineClose(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmFilterAdd0", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmFilterAdd(
            IntPtr engineHandle,
            ref FWPM_FILTER filter,
            IntPtr sd,
            out Guid id);

        [DllImport("fwpuclnt.dll", EntryPoint = "FwpmFilterDeleteById0", CallingConvention = CallingConvention.StdCall)]
        private static extern uint FwpmFilterDeleteById(IntPtr engineHandle, Guid id);

        private const uint RPC_C_AUTHN_DEFAULT = 0xFFFFFFFF;

        private static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
        private static readonly Guid FWPM_SUBLAYER_UNIVERSAL = new("eebecc03-ced4-4380-819a-2734397b2b74");
        private static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045");

        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_SESSION
        {
            public Guid sessionKey;
            public FWPM_DISPLAY_DATA displayData;
            public uint flags;
            public uint txnWaitTimeoutInMSec;
            public uint processId;
            public IntPtr sid;
            [MarshalAs(UnmanagedType.LPWStr)] public string username;
            public bool kernelMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_DISPLAY_DATA
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string name;
            [MarshalAs(UnmanagedType.LPWStr)] public string description;
        }

        // Native FWPM_FILTER0.providerContextKey is a union { UINT64 rawContext; GUID providerContextKey; }
        // — 16 bytes. Declaring it as IntPtr (8 bytes) truncates the struct by 8 bytes and
        // corrupts the tail (reserved, filterId, effectiveWeight), which fwpuclnt reads/writes
        // and causes AccessViolation on FwpmFilterAdd. Use Guid to match the 16-byte union size.
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
            public Guid providerContextKey;
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

        // Native FWPM_ACTION0: { FWP_ACTION_TYPE type; union { GUID filterType; GUID calloutKey; }; }
        // The two GUIDs are a C union in the SDK header — one 16-byte slot, not two.
        // Prior declaration of both as separate fields inflated the struct by 16 bytes and
        // corrupted every offset after `action` inside FWPM_FILTER, producing AccessViolation
        // on FwpmFilterAdd. We only need FWP_ACTION_BLOCK / PERMIT so the union body is unused.
        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_ACTION
        {
            public FWP_ACTION_TYPE type;
            public Guid filterTypeOrCalloutKey;
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
            FWP_EMPTY = 0, FWP_UINT8 = 1, FWP_UINT16 = 2, FWP_UINT32 = 3, FWP_UINT64 = 4
        }

        private enum FWP_MATCH_TYPE : uint
        {
            FWP_MATCH_EQUAL = 0, FWP_MATCH_GREATER = 1, FWP_MATCH_LESS = 2,
            FWP_MATCH_GREATER_OR_EQUAL = 3, FWP_MATCH_LESS_OR_EQUAL = 4,
            FWP_MATCH_RANGE = 5, FWP_MATCH_NOT_EQUAL = 10
        }

        private enum FWP_ACTION_TYPE : uint
        {
            FWP_ACTION_BLOCK = 0x00000001,
            FWP_ACTION_PERMIT = 0x00000002
        }

        #endregion

        private class WfpFilter
        {
            public string Name { get; set; }
            public Guid Id { get; set; }
            public WfpFilter(string name, Guid id) { Name = name; Id = id; }
        }
    }
}
