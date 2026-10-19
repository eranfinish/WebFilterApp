using WebFilter.Core.Decisions;
using WebFilter.Core.Model;

namespace WebFilter.Core.Engine;

/// <summary>
/// What WFP should let one restricted Windows account reach the network with (MVP-PLAN §5.3): an allowlist of program
/// paths and Store packages; everything else that account runs is blocked. <see cref="TimeUp"/> marks the reduced set
/// (browsers + "always allowed") used outside the time window or after the daily limit.
/// </summary>
public sealed record UserNetworkPolicy(string UserSid, bool TimeUp, IReadOnlyList<string> AllowedPaths, IReadOnlyList<string> AllowedPackages)
{
    public bool Equivalent(UserNetworkPolicy other) =>
        UserSid == other.UserSid && TimeUp == other.TimeUp &&
        AllowedPaths.SequenceEqual(other.AllowedPaths, StringComparer.OrdinalIgnoreCase) &&
        AllowedPackages.SequenceEqual(other.AllowedPackages, StringComparer.OrdinalIgnoreCase);
}

public static class NetworkPolicy
{
    /// <summary>
    /// One policy per restricted account on <paramref name="deviceId"/>: linked restricted members (unless paused) and
    /// unlinked non-admin accounts. Admins, unrestricted members and paused members get none (no filters).
    /// Each candidate program (supported browsers and the targets of app rules) is decided by <see cref="Decider.App"/>,
    /// so WFP follows exactly the engine's order. Publisher rules have no WFP condition and are not included (the program
    /// stays blocked — the safe side).
    /// </summary>
    public static IReadOnlyList<UserNetworkPolicy> Desired(PolicyView view, string deviceId, DateTimeOffset now)
    {
        var config = view.Config;
        var device = config.Devices.FirstOrDefault(d => d.Id == deviceId);
        var result = new List<UserNetworkPolicy>();
        foreach (var account in device?.Accounts ?? [])
        {
            if (account.IsAdmin) continue;
            if (account.MemberId is null)
            {
                // Unlinked: browsers and globally always-allowed programs only.
                var always = config.AppRules.Where(r => r.MemberId is null && r.AlwaysAllowed && r.Action == RuleAction.Allow && r.IsActiveAt(now)).ToList();
                result.Add(new UserNetworkPolicy(account.AccountId, TimeUp: true,
                    Sorted(config.SupportedBrowsers.Concat(always.Where(r => r.Match == AppMatchType.Path).Select(r => r.Value))),
                    Sorted(always.Where(r => r.Match == AppMatchType.Package).Select(r => r.Value))));
                continue;
            }

            var member = config.Member(account.MemberId);
            if (!member.IsRestricted || view.IsPaused(member.Id, now)) continue;
            var rules = config.AppRules.Where(r => (r.MemberId is null || r.MemberId == member.Id) && r.IsActiveAt(now)).ToList();
            var paths = config.SupportedBrowsers.Concat(rules.Where(r => r.Match == AppMatchType.Path).Select(r => r.Value))
                .Where(p => Allowed(new AppIdentity(Path: p)));
            var packages = rules.Where(r => r.Match == AppMatchType.Package).Select(r => r.Value)
                .Where(p => Allowed(new AppIdentity(Package: p)));
            var timeUp = Decider.Time(view, member.Id, now).State is Time.TimeState.OutsideWindow or Time.TimeState.Expired;
            result.Add(new UserNetworkPolicy(account.AccountId, timeUp, Sorted(paths), Sorted(packages)));

            bool Allowed(AppIdentity app) => Decider.App(view, member.Id, app, now).Decision == Decision.Allow;
        }
        return result;
    }

    private static List<string> Sorted(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
}
