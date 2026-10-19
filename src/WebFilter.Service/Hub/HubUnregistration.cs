using WebFilter.Contracts.Hub;

namespace WebFilter.Service.Hub;

/// <summary>What became of telling the hub that this computer leaves (uninstall).</summary>
public enum UnregisterOutcome
{
    /// <summary>This computer is not paired with a hub (it is the hub, or an agent that never paired).</summary>
    NotPaired,

    /// <summary>The hub revoked this computer; its Devices screen shows it as uninstalled.</summary>
    Unregistered,

    /// <summary>The hub no longer accepted this computer (a parent had revoked it).</summary>
    AlreadyRevoked,

    /// <summary>The hub did not answer in time, or the pairing could not be read; it still lists this computer as paired.</summary>
    Failed,
}

/// <summary>The uninstall's goodbye to the hub (MVP-PLAN §4.6): <see cref="HubUnregistration"/>, or a fake in tests.</summary>
public interface IHubUnregistration
{
    /// <summary>Never <see cref="UnregisterOutcome.Failed"/>: throws instead when the hub cannot be reached or the pairing cannot be read.</summary>
    Task<UnregisterOutcome> UnregisterAsync(CancellationToken ct);
}

/// <summary>Unregisters with the pairing stored in <paramref name="dataDirectory"/> (<see cref="HubLink"/>), over its mutual-TLS link.</summary>
public sealed class HubUnregistration(string dataDirectory) : IHubUnregistration
{
    public async Task<UnregisterOutcome> UnregisterAsync(CancellationToken ct)
    {
        var link = HubLink.Load(dataDirectory);
        if (link is null) return UnregisterOutcome.NotPaired;
        return await link.UnregisterAsync(UnregisterRequest.Uninstall, ct) ? UnregisterOutcome.Unregistered : UnregisterOutcome.AlreadyRevoked;
    }
}
