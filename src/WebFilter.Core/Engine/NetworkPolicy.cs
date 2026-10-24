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

/// <summary>
/// Where this computer's machine-wide programs that restricted accounts may use live: the supported browsers, and the
/// programs behind <see cref="WindowsFeatures"/> (PROTECTION-SPEC §2). The service resolves them from the real Windows
/// folders; <see cref="Default"/> is the usual layout.
/// </summary>
/// <param name="Svchost">svchost.exe, which runs per-user services (push notifications, settings sync) as the user.</param>
/// <param name="MachineOneDrive">
/// The machine-wide OneDrive (Program Files). Never a per-user install under %LOCALAPPDATA%: the child can write there, so
/// allowing it would let any program renamed to it reach the network.
/// </param>
/// <param name="MachineBrowsers">
/// The machine-wide Chrome and Edge (Program Files), allowed like <see cref="FamilyConfig.SupportedBrowsers"/>, so a family
/// whose settings list no browsers (a new hub's) can still browse. Never a per-user Chrome under %LOCALAPPDATA%, for the
/// same reason as OneDrive.
/// </param>
public sealed record WindowsPrograms(string Svchost, IReadOnlyList<string> MachineOneDrive, IReadOnlyList<string> MachineBrowsers)
{
    public static readonly WindowsPrograms Default = new(@"C:\Windows\System32\svchost.exe",
        [@"C:\Program Files\Microsoft OneDrive\OneDrive.exe", @"C:\Program Files (x86)\Microsoft OneDrive\OneDrive.exe"],
        [@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"]);
}

public static class NetworkPolicy
{
    /// <summary>
    /// One policy per restricted account on <paramref name="deviceId"/> (<see cref="FilteredAccounts"/>): linked restricted
    /// members (unless paused) and unlinked non-admin accounts. Admins, unrestricted members and paused members get none (no
    /// filters). Each candidate program (supported browsers and the targets of app rules) is decided by
    /// <see cref="Decider.App"/>, so WFP follows exactly the engine's order. Publisher rules have no WFP condition and are not
    /// included (the program stays blocked — the safe side). The machine-wide Chrome and Edge are allowed for every one of
    /// these accounts like the supported browsers, and the family-wide Windows features
    /// (<see cref="FamilyConfig.WindowsFeatures"/>) add svchost.exe and the machine-wide OneDrive; all of them also when time
    /// is up — except a browser the extension guard blocks for that account (<paramref name="blockedBrowsers"/>,
    /// PROTECTION-SPEC §14): its paths (<see cref="BrowserKinds.Paths"/>) are left out, and the rest of the policy is unchanged.
    /// </summary>
    /// <param name="programs">Where the browsers, svchost and OneDrive are; <see cref="WindowsPrograms.Default"/> when null.</param>
    /// <param name="blockedBrowsers">The browsers <see cref="ExtensionGuard"/> blocks now, by account; none when null.</param>
    public static IReadOnlyList<UserNetworkPolicy> Desired(PolicyView view, string deviceId, DateTimeOffset now, WindowsPrograms? programs = null,
        IReadOnlyCollection<AccountBrowser>? blockedBrowsers = null)
    {
        var config = view.Config;
        programs ??= WindowsPrograms.Default;
        var machine = programs.MachineBrowsers.Concat(WindowsFeaturePrograms(config.WindowsFeatures, programs)).ToList();
        var result = new List<UserNetworkPolicy>();
        foreach (var account in FilteredAccounts(view, deviceId, now))
        {
            var blocked = (blockedBrowsers ?? [])
                .Where(b => string.Equals(b.AccountId, account.AccountId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(b => b.Browser.Paths(config, programs))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (account.MemberId is null)
            {
                // Unlinked: browsers and globally always-allowed programs only.
                var always = config.AppRules.Where(r => r.MemberId is null && r.AlwaysAllowed && r.Action == RuleAction.Allow && r.IsActiveAt(now)).ToList();
                result.Add(new UserNetworkPolicy(account.AccountId, TimeUp: true,
                    Sorted(config.SupportedBrowsers.Concat(always.Where(r => r.Match == AppMatchType.Path).Select(r => r.Value)).Concat(machine), blocked),
                    Sorted(always.Where(r => r.Match == AppMatchType.Package).Select(r => r.Value))));
                continue;
            }

            var member = config.Member(account.MemberId);
            var rules = config.AppRules.Where(r => (r.MemberId is null || r.MemberId == member.Id) && r.IsActiveAt(now)).ToList();
            var paths = config.SupportedBrowsers.Concat(rules.Where(r => r.Match == AppMatchType.Path).Select(r => r.Value))
                .Where(p => Allowed(new AppIdentity(Path: p)));
            var packages = rules.Where(r => r.Match == AppMatchType.Package).Select(r => r.Value)
                .Where(p => Allowed(new AppIdentity(Package: p)));
            var timeUp = Decider.Time(view, member.Id, now).State is Time.TimeState.OutsideWindow or Time.TimeState.Expired;
            result.Add(new UserNetworkPolicy(account.AccountId, timeUp, Sorted(paths.Concat(machine), blocked), Sorted(packages)));

            bool Allowed(AppIdentity app) => Decider.App(view, member.Id, app, now).Decision == Decision.Allow;
        }
        return result;
    }

    /// <summary>
    /// The accounts on <paramref name="deviceId"/> that get network filters: linked to a restricted member who is not paused
    /// (by the hub or on this computer), or linked to nobody and not an administrator. The extension guard watches the same
    /// accounts (<see cref="ExtensionGuard.GuardedAccounts"/>).
    /// </summary>
    public static IEnumerable<DeviceAccount> FilteredAccounts(PolicyView view, string deviceId, DateTimeOffset now)
    {
        var device = view.Config.Devices.FirstOrDefault(d => d.Id == deviceId);
        foreach (var account in device?.Accounts ?? [])
        {
            if (account.IsAdmin) continue;
            if (account.MemberId is { } memberId && view.Config.Member(memberId) is var member && (!member.IsRestricted || view.IsPaused(member.Id, now))) continue;
            yield return account;
        }
    }

    /// <summary>The Windows programs the family's <see cref="WindowsFeatures"/> let restricted accounts use, whatever the time.</summary>
    public static IReadOnlyList<string> WindowsFeaturePrograms(WindowsFeatures features, WindowsPrograms programs)
    {
        var paths = new List<string>();
        if (features.NotificationsAndSync) paths.Add(programs.Svchost);
        if (features.OneDrive) paths.AddRange(programs.MachineOneDrive);
        return paths;
    }

    private static List<string> Sorted(IEnumerable<string> values, IReadOnlySet<string>? without = null) =>
        values.Where(v => without is null || !without.Contains(v)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
}
