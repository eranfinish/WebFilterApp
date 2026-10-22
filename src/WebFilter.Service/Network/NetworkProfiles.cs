using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WebFilter.Service.Network;

/// <summary>
/// Changes Windows' category of the networks this computer is connected to. The hub's firewall rule is for Private
/// networks only, so on a network Windows marks Public the agents cannot reach the hub (found in the first lab install);
/// the admin app offers to fix it.
/// </summary>
public interface INetworkProfiles
{
    /// <summary>Sets every connected network that is Public to Private; returns their names (none if there were none).</summary>
    IReadOnlyList<string> MakePublicNetworksPrivate();
}

/// <summary>Through the Network List Manager, as LocalSystem (the same as Settings → Network → Private network).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkProfiles : INetworkProfiles
{
    private static readonly Guid NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");
    private const int Connected = 1, Public = 0, Private = 1; // NLM_ENUM_NETWORK_CONNECTED, NLM_NETWORK_CATEGORY

    public IReadOnlyList<string> MakePublicNetworksPrivate()
    {
        var changed = new List<string>();
        try
        {
            dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManager, throwOnError: true)!)!;
            foreach (var network in manager.GetNetworks(Connected))
            {
                if ((int)network.GetCategory() != Public) continue;
                network.SetCategory(Private);
                changed.Add((string)network.GetName());
            }
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException($"Windows did not change the network category ({ex.Message}).", ex);
        }
        return changed;
    }
}
