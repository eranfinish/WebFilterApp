using WebFilter.Core.Decisions;

namespace WebFilter.Core.Model;

/// <summary>
/// A block the product adds to a member's URL policy by itself, with its own reason code. Built-in blocks win over every
/// rule of the member's policy, always-allowed ones included, and apply in every state the policy applies in (normal,
/// time up, homework); a parent turns them off with the member's settings, not with a rule.
/// </summary>
public sealed record BuiltinBlock(Rule Rule, ReasonCode Reason);

/// <summary>
/// The built-in blocks of PROTECTION-SPEC §4 (image search) and §11 (Google's AI Mode), as ordinary rule patterns so that
/// the DNR compiler and the Core evaluator read them the same way.
/// </summary>
public static class BuiltinRules
{
    public const string ImageSearchIdPrefix = "builtin-image-search-";
    public const string AiModeIdPrefix = "builtin-ai-mode-";

    /// <summary>The starter-list category of AI chatbots (data/starter-lists/ai-chat.json).</summary>
    public const string AiChatCategory = "ai-chat";

    // After the host: an optional trailing dot (Chrome keeps it in the URL a regexFilter sees) and an optional port.
    private const string HostEnd = @"\.?(?::[0-9]*)?";

    // A country-code or generic top-level domain, with an optional second level: google.com, google.de, google.co.il.
    private const string AnyTld = @"[a-z]{2,3}(?:\.[a-z]{2})?";

    // One query parameter, in any position: right after '?' or after an '&', ending at '&', the fragment or the end — never
    // inside another parameter's value or in the fragment.
    private static string Param(string parameter) => @"\?(?:[^#]*&)?" + parameter + "(?:[&#].*)?";

    /// <summary>
    /// Google image results (<c>/search</c> with <c>udm=2</c> or <c>tbm=isch</c>, the images start page <c>/imghp</c>),
    /// <c>images.google.*</c>, Google Lens, Bing images, DuckDuckGo with <c>ia=images</c>/<c>iax=images</c>, and Yandex
    /// images, with reason <c>blocked_image_search</c>. Images inside ordinary result pages are not blocked (forced
    /// SafeSearch covers those).
    /// </summary>
    public static readonly IReadOnlyList<BuiltinBlock> ImageSearch =
    [
        Builtin(ImageSearchIdPrefix + "google", PatternType.Regex,
            $@"^https?://(?:www\.)?google\.{AnyTld}{HostEnd}/(?:search{Param("(?:udm=2|tbm=isch)")}|imghp(?:[/?#].*)?)$", ReasonCode.BlockedImageSearch),
        Builtin(ImageSearchIdPrefix + "google-images", PatternType.Regex, $@"^https?://images\.google\.{AnyTld}{HostEnd}/", ReasonCode.BlockedImageSearch),
        Builtin(ImageSearchIdPrefix + "google-lens", PatternType.Domain, "lens.google.com", ReasonCode.BlockedImageSearch),
        Builtin(ImageSearchIdPrefix + "bing", PatternType.Prefix, "bing.com/images", ReasonCode.BlockedImageSearch),
        Builtin(ImageSearchIdPrefix + "duckduckgo", PatternType.Regex, $@"^https?://(?:[a-z0-9-]+\.)*duckduckgo\.com{HostEnd}/[^?#]*{Param("iax?=images")}$", ReasonCode.BlockedImageSearch),
        Builtin(ImageSearchIdPrefix + "yandex", PatternType.Regex, $@"^https?://(?:www\.)?yandex\.{AnyTld}{HostEnd}/images(?:[/?#].*)?$", ReasonCode.BlockedImageSearch),
    ];

    /// <summary>
    /// Google's AI Mode, a chatbot inside Search (<c>/search</c> with <c>udm=50</c>, in any parameter order; plain search is
    /// not matched), with reason <c>blocked_rule</c>: blocked like the chatbots of the <c>ai-chat</c> list.
    /// </summary>
    public static readonly IReadOnlyList<BuiltinBlock> AiMode =
    [
        Builtin(AiModeIdPrefix + "google", PatternType.Regex, $@"^https?://(?:www\.)?google\.{AnyTld}{HostEnd}/search{Param("udm=50")}$", ReasonCode.BlockedRule),
    ];

    /// <summary>
    /// The built-in blocks of one member: image search with <see cref="FamilyMember.BlockImageSearch"/>, AI Mode when the
    /// member's categories block <see cref="AiChatCategory"/> (<see cref="BlocksCategory"/>). None for an unrestricted member.
    /// </summary>
    public static IReadOnlyList<BuiltinBlock> For(FamilyConfig config, FamilyMember member)
    {
        if (!member.IsRestricted) return [];
        var images = member.BlockImageSearch;
        var aiMode = BlocksCategory(config, member, AiChatCategory);
        return images && aiMode ? [.. ImageSearch, .. AiMode] : images ? ImageSearch : aiMode ? AiMode : [];
    }

    /// <summary>
    /// Whether a restricted member's categories block <paramref name="category"/>: the parent blocked it for the member, or
    /// the member is in allowlist mode ("only approved") and the parent did not allow it.
    /// </summary>
    public static bool BlocksCategory(FamilyConfig config, FamilyMember member, string category)
    {
        if (!member.IsRestricted) return false;
        var choices = config.MemberCategories
            .Where(mc => mc.MemberId == member.Id && string.Equals(mc.Category, category, StringComparison.OrdinalIgnoreCase))
            .Select(mc => mc.Action)
            .ToList();
        return choices.Contains(RuleAction.Block) || (choices.Count == 0 && member.FilterMode == FilterMode.Allowlist);
    }

    /// <summary>True for the id of a built-in block (a parent's approval of the site cannot lift it).</summary>
    public static bool IsBuiltinBlock(string? ruleId) =>
        ruleId is not null && (ruleId.StartsWith(ImageSearchIdPrefix, StringComparison.Ordinal) || ruleId.StartsWith(AiModeIdPrefix, StringComparison.Ordinal));

    private static BuiltinBlock Builtin(string id, PatternType type, string pattern, ReasonCode reason) =>
        new(new Rule(id, null, pattern, type, RuleAction.Block), reason);
}
