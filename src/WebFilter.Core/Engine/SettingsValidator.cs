using WebFilter.Core.Model;

namespace WebFilter.Core.Engine;

/// <summary>
/// Checks a whole settings replacement before the hub takes it: every reference to a member names a known member, ids
/// are unique, and times and limits are within a day. Decisions look members up by id, so a dangling reference would
/// fail later on every computer instead of now, with a reason, at the admin.
/// </summary>
public static class SettingsValidator
{
    /// <summary>Why <paramref name="config"/> cannot be used, or null when it is fine.</summary>
    public static string? Validate(FamilyConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var members = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in config.Members)
        {
            if (string.IsNullOrWhiteSpace(member.Id)) return "A family member has no id.";
            if (!members.Add(member.Id)) return $"Two family members have the id '{member.Id}'.";
            if (string.IsNullOrWhiteSpace(member.DisplayName)) return $"Family member '{member.Id}' has no name.";
        }

        string? Unknown(string? memberId, string what) =>
            memberId is null || members.Contains(memberId) ? null : $"{what} refers to unknown member '{memberId}'.";

        var problems = config.Devices.SelectMany(d => d.Accounts.Select(a => Unknown(a.MemberId, $"Account '{a.Name}' on '{d.Id}'")))
            .Concat(config.Rules.Select(r => Unknown(r.MemberId, $"Rule '{r.Id}'")))
            .Concat(config.AppRules.Select(r => Unknown(r.MemberId, $"App rule '{r.Id}'")))
            .Concat(config.Schedules.Select(s => Unknown(s.MemberId, "A schedule")))
            .Concat(config.DailyLimits.Select(l => Unknown(l.MemberId, "A daily limit")))
            .Concat(config.YouTubeChannels.Select(c => Unknown(c.MemberId, $"YouTube channel '{c.ChannelId}'")))
            .Concat(config.MemberCategories.Select(c => Unknown(c.MemberId, $"Category '{c.Category}'")));
        if (problems.FirstOrDefault(p => p is not null) is { } unknown) return unknown;

        if (Duplicate(config.Rules.Select(r => r.Id)) is { } rule) return $"Two rules have the id '{rule}'.";
        if (Duplicate(config.AppRules.Select(r => r.Id)) is { } app) return $"Two app rules have the id '{app}'.";
        if (config.Schedules.FirstOrDefault(s => s.StartMinute < 0 || s.EndMinute > 24 * 60 || s.StartMinute >= s.EndMinute) is { } window)
            return $"A schedule of '{window.MemberId}' does not start before it ends within one day.";
        if (config.DailyLimits.FirstOrDefault(l => l.Minutes is < 0 or > 24 * 60) is { } limit)
            return $"A daily limit of '{limit.MemberId}' is not between 0 and 1440 minutes.";
        return null;
    }

    /// <exception cref="ArgumentException">The settings are not valid; the message says why.</exception>
    public static void EnsureValid(FamilyConfig config)
    {
        if (Validate(config) is { } error) throw new ArgumentException(error, nameof(config));
    }

    private static string? Duplicate(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return ids.FirstOrDefault(id => !seen.Add(id));
    }
}
