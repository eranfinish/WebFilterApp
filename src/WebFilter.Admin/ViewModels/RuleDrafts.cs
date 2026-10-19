using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.ViewModels;

/// <summary>How long a new rule lasts: always, for an hour, or until the end of the family's day.</summary>
public static class RuleExpiry
{
    public const string Permanent = "permanent";
    public const string Hour = "hour";
    public const string Today = "today";

    public static IReadOnlyList<Option> Options { get; } = Texts.Options("RuleExpiry_", Permanent, Hour, Today);

    /// <summary>When a rule added now with <paramref name="choice"/> expires, by the service's clock; null = never.</summary>
    public static DateTimeOffset? ExpiresAt(string? choice, FamilyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return choice switch
        {
            Hour => context.ServiceNow.AddHours(1),
            Today => FamilyTime.EndOfDay(context.ServiceNow, context.TimeZone),
            _ => null,
        };
    }
}

/// <summary>The checks of an "add a site rule" form, shared by the rules page and the member page.</summary>
internal static class RuleDrafts
{
    /// <summary>
    /// Builds the rule the form describes, or returns why not (the pattern, or the same rule exists and has not expired).
    /// "Always allowed" is kept only for an allow rule.
    /// </summary>
    public static RuleDto? TryCreate(string? input, string type, string action, string? member, bool alwaysAllowed, string? expiry,
        IEnumerable<RuleDto> existing, FamilyContext context, Func<string> newId, out string? error)
    {
        if (!RulePatternValidator.TryNormalize(input, type, out var pattern, out error)) return null;
        var now = context.ServiceNow;
        if (existing.Any(r => r.Member == member && r.Type == type && r.Action == action && !(r.ExpiresAt <= now)
                              && string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase)))
        {
            error = Strings.Validation_Duplicate;
            return null;
        }
        return new RuleDto(newId(), member, pattern, type, action, AlwaysAllowed: action == "allow" && alwaysAllowed,
            ExpiresAt: RuleExpiry.ExpiresAt(expiry, context));
    }

    /// <summary>"until 18:00" / "until the end of the day" / "permanent" / "expired", for a rule's expiry.</summary>
    public static string ExpiryText(DateTimeOffset? expiresAt, FamilyContext context, DateTimeOffset now) => expiresAt switch
    {
        null => Strings.Rule_NoExpiry,
        { } at when at <= now => Strings.Rule_Expired,
        { } at => Texts.Format(Strings.Rule_ExpiresAt, context.FormatUntil(at)),
    };
}
