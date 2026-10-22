using WebFilter.Core.Engine;

namespace WebFilter.Service.Network;

/// <summary>
/// The WFP side (MVP-PLAN §5.3), implemented by <c>WebFilter.Wfp.Native.dll</c> (<see cref="NativeWfpEngine"/>).
/// Filters are persistent under our own provider and sublayer, so they survive a service crash or reboot.
/// </summary>
public interface IWfpEngine
{
    /// <summary>Accounts that currently have our filters, read from WFP itself.</summary>
    IReadOnlyCollection<string> FilteredUsers();

    /// <summary>
    /// Replaces one account's filters, atomically (one WFP transaction). An allowed program or package that cannot get a
    /// filter (not installed, not a valid name) is skipped and returned; it stays blocked, and the rest is applied.
    /// </summary>
    IReadOnlyList<SkippedApp> SetUserPolicy(UserNetworkPolicy policy);

    void RemoveUser(string userSid);

    /// <summary>Removes our provider, sublayer and every filter (uninstall).</summary>
    void RemoveAll();
}

/// <summary>An allowed program path or package that got no filter, with the Win32 error that says why.</summary>
public sealed record SkippedApp(string Value, int Error)
{
    /// <summary>ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND: the program is not installed (yet).</summary>
    public bool NotInstalled => Error is 2 or 3;
}

/// <summary>
/// Keeps WFP equal to <see cref="NetworkPolicy.Desired"/>: sets changed accounts, removes accounts that no longer need
/// filters, and puts back filters that disappeared (deleted by someone — reported as repaired). Call it on every settings
/// change and tick; it touches WFP only when something differs, or when an allowed program that was not installed when
/// its account was set has appeared since.
/// </summary>
/// <param name="programExists">Whether a program file exists now (File.Exists; replaced in tests).</param>
public sealed class WfpManager(IWfpEngine engine, Func<string, bool>? programExists = null)
{
    private readonly Func<string, bool> _exists = programExists ?? File.Exists;
    private readonly Dictionary<string, UserNetworkPolicy> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<SkippedApp>> _skipped = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Skipped">Programs and packages left without a filter, reported when an account's list of them changes.</param>
    /// <param name="Failed">
    /// Accounts WFP refused (an id that is not a SID, an error of the engine): the others are still applied, and these are
    /// tried again on the next pass.
    /// </param>
    public sealed record ApplyResult(int Set, int Removed, IReadOnlyList<string> Repaired, IReadOnlyList<(string UserSid, SkippedApp App)> Skipped,
        IReadOnlyList<(string UserSid, Exception Error)> Failed);

    public ApplyResult Apply(IReadOnlyList<UserNetworkPolicy> desired)
    {
        var present = engine.FilteredUsers().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = desired.ToDictionary(p => p.UserSid, StringComparer.OrdinalIgnoreCase);
        int set = 0, removed = 0;
        var repaired = new List<string>();
        var skippedNow = new List<(string, SkippedApp)>();
        var failed = new List<(string, Exception)>();

        foreach (var sid in present.Where(s => !wanted.ContainsKey(s)))
        {
            try { engine.RemoveUser(sid); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add((sid, ex));
                continue;
            }
            _applied.Remove(sid);
            _skipped.Remove(sid);
            removed++;
        }
        foreach (var sid in _applied.Keys.Where(s => !wanted.ContainsKey(s)).ToList())
        {
            _applied.Remove(sid);
            _skipped.Remove(sid);
        }
        foreach (var policy in desired)
        {
            var known = _applied.TryGetValue(policy.UserSid, out var applied);
            if (known && present.Contains(policy.UserSid) && applied!.Equivalent(policy) && !InstalledSince(policy)) continue;
            IReadOnlyList<SkippedApp> skipped;
            try { skipped = engine.SetUserPolicy(policy); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Not recorded as applied: the next pass tries again. The account keeps the filters it had (one transaction).
                failed.Add((policy.UserSid, ex));
                continue;
            }
            if (known && !present.Contains(policy.UserSid)) repaired.Add(policy.UserSid);
            _applied[policy.UserSid] = policy;
            var before = _skipped.GetValueOrDefault(policy.UserSid) ?? [];
            if (!skipped.Select(s => s.Value).SequenceEqual(before.Select(s => s.Value), StringComparer.OrdinalIgnoreCase))
                skippedNow.AddRange(skipped.Select(s => (policy.UserSid, s)));
            if (skipped.Count > 0) _skipped[policy.UserSid] = skipped;
            else _skipped.Remove(policy.UserSid);
            set++;
        }
        return new ApplyResult(set, removed, repaired, skippedNow, failed);
    }

    /// <summary>An allowed program of this account that was not installed when it was set exists now.</summary>
    private bool InstalledSince(UserNetworkPolicy policy) =>
        _skipped.TryGetValue(policy.UserSid, out var skipped) &&
        skipped.Any(s => s.NotInstalled && policy.AllowedPaths.Contains(s.Value, StringComparer.OrdinalIgnoreCase) && _exists(s.Value));
}
