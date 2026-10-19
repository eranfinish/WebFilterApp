using WebFilter.Core.Engine;

namespace WebFilter.Service.Network;

/// <summary>
/// The WFP side (MVP-PLAN §5.3), implemented by <c>WebFilter.Wfp.Native.dll</c> (needs Visual Studio's C++ workload; not
/// built yet). Filters are persistent under our own provider and sublayer, so they survive a service crash or reboot.
/// </summary>
public interface IWfpEngine
{
    /// <summary>Accounts that currently have our filters, read from WFP itself.</summary>
    IReadOnlyCollection<string> FilteredUsers();

    /// <summary>Replaces one account's filters, atomically (one WFP transaction).</summary>
    void SetUserPolicy(UserNetworkPolicy policy);

    void RemoveUser(string userSid);

    /// <summary>Removes our provider, sublayer and every filter (uninstall).</summary>
    void RemoveAll();
}

/// <summary>
/// Keeps WFP equal to <see cref="NetworkPolicy.Desired"/>: sets changed accounts, removes accounts that no longer need
/// filters, and puts back filters that disappeared (deleted by someone — reported as repaired). Call it on every settings
/// change and tick; it touches WFP only when something differs.
/// </summary>
public sealed class WfpManager(IWfpEngine engine)
{
    private readonly Dictionary<string, UserNetworkPolicy> _applied = new(StringComparer.OrdinalIgnoreCase);

    public sealed record ApplyResult(int Set, int Removed, IReadOnlyList<string> Repaired);

    public ApplyResult Apply(IReadOnlyList<UserNetworkPolicy> desired)
    {
        var present = engine.FilteredUsers().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = desired.ToDictionary(p => p.UserSid, StringComparer.OrdinalIgnoreCase);
        int set = 0, removed = 0;
        var repaired = new List<string>();

        foreach (var sid in present.Where(s => !wanted.ContainsKey(s)))
        {
            engine.RemoveUser(sid);
            _applied.Remove(sid);
            removed++;
        }
        foreach (var policy in desired)
        {
            var known = _applied.TryGetValue(policy.UserSid, out var applied);
            if (known && present.Contains(policy.UserSid) && applied!.Equivalent(policy)) continue;
            if (known && !present.Contains(policy.UserSid)) repaired.Add(policy.UserSid);
            engine.SetUserPolicy(policy);
            _applied[policy.UserSid] = policy;
            set++;
        }
        return new ApplyResult(set, removed, repaired);
    }
}
