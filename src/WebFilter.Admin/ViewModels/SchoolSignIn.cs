using WebFilter.Admin.Services;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// The global allow rules Ministry of Education Google sign-in needs in allowlist mode (PROTECTION-SPEC §11): it goes
/// through <c>www.google.com/a/&lt;domain&gt;/…</c>, and sometimes a captcha frame, on the host of Google Search, which a
/// domain list cannot allow without allowing Search. The rules are there exactly while some member has the
/// <see cref="StarterLists.SchoolTools"/> list allowed: every save that changes category choices (setup wizard, member
/// page) passes its settings through <see cref="Apply"/>.
/// </summary>
internal static class SchoolSignIn
{
    public const string IdPrefix = "builtin-school-signin-";

    public static readonly IReadOnlyList<RuleDto> Rules =
    [
        new(IdPrefix + "google-a", null, "www.google.com/a/", "prefix", "allow"),
        new(IdPrefix + "recaptcha", null, "www.google.com/recaptcha/", "prefix", "allow"),
    ];

    /// <summary>One of these rules (the rules page marks them).</summary>
    public static bool IsOurs(RuleDto rule) => rule.Id.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>Some family member has the school-tools list allowed.</summary>
    public static bool IsNeeded(FamilyBundle settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.MemberCategories.Any(c => c.Action == "allow" && string.Equals(c.Category, StarterLists.SchoolTools, StringComparison.OrdinalIgnoreCase)
                                                  && settings.Members.Any(m => m.Id == c.Member));
    }

    /// <summary>
    /// <paramref name="settings"/> with the rules added when needed and removed when not. Idempotent: a rule already there
    /// (by its id, or a permanent family-wide rule of the parent's that does the same) is not added again; the parent's
    /// own rules are never removed.
    /// </summary>
    public static FamilyBundle Apply(FamilyBundle settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsNeeded(settings))
            return settings.Rules.Any(IsOurs) ? settings with { Rules = [.. settings.Rules.Where(r => !IsOurs(r))] } : settings;
        var missing = Rules.Where(rule => !settings.Rules.Any(r => r.Id == rule.Id || Same(r, rule))).ToList();
        return missing.Count == 0 ? settings : settings with { Rules = [.. settings.Rules, .. missing] };
    }

    private static bool Same(RuleDto existing, RuleDto rule) =>
        existing.Member is null && existing.Type == rule.Type && existing.Action == rule.Action && existing.ExpiresAt is null
        && string.Equals(existing.Pattern, rule.Pattern, StringComparison.OrdinalIgnoreCase);
}
