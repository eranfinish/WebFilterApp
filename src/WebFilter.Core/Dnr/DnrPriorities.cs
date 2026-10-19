namespace WebFilter.Core.Dnr;

/// <summary>
/// Fixed priority layout of the compiled rules. DNR applies the highest-priority matching rule, and at equal priority
/// <c>allow</c> beats <c>block</c>/<c>redirect</c>; the layout turns the evaluator's order (README §5) into priorities:
/// <code>
///   262,145  time-up session: always-allowed rules (allow)
///   262,144  time-up session: everything else (block)
///   196,608  YouTube hosts: allow (mode ≠ off) or block (mode = off)
///   131,072+ member band  — any matching member rule beats every global rule or category
///    65,536+ global band  — global rules and the member's category domains
///         2  IP-literal hosts (block, blocklist mode)
///         1  everything (block, allowlist mode)
/// </code>
/// Inside a band each specificity level gets two adjacent priorities, <c>allow = band + 2·level</c> and
/// <c>block = allow + 1</c>, so the most specific match wins and a tie goes to block. Levels:
/// regex = 1; domain / wildcard / category with k labels = 2 + k; prefix with k host labels and path length L =
/// 130 + 256·(k − 1) + L. A domain rule that matches the host exactly sits at the same level as its subdomain
/// matches: for a host with n labels nothing else can score between "subdomain of an (n−1)-label domain" and
/// "exact n-label host", so the evaluator's exact-host tier never changes an outcome and domain lists need no regex.
/// The layout is a formula of the rule's kind, never of the other rules, so priorities and ids stay stable as a
/// policy changes and the extension can update only the rules that changed.
/// </summary>
public static class DnrPriorities
{
    public const int AllowlistDefault = 1;
    public const int IpLiteralGate = 2;
    public const int BandSize = 1 << 16;
    public const int GlobalBand = BandSize;
    public const int MemberBand = 2 * BandSize;
    public const int YouTube = 3 * BandSize;
    public const int TimeUpBlock = 4 * BandSize;
    public const int TimeUpAlwaysAllowed = TimeUpBlock + 1;

    internal const int RegexLevel = 1;
    private const int MaxLabels = 127;
    private const int MaxPathLength = 255;
    private const int PrefixLevelBase = 130;

    internal static int HostLevel(int labels) => 2 + Math.Clamp(labels, 1, MaxLabels);

    internal static int PrefixLevel(int labels, int pathLength) =>
        PrefixLevelBase + (Math.Clamp(labels, 1, MaxLabels) - 1) * 256 + Math.Clamp(pathLength, 1, MaxPathLength);

    internal static int InBand(int band, int level, bool block) => band + level * 2 + (block ? 1 : 0);
}

/// <summary>Ids of the structural rules. Every other rule id is a stable hash of its priority, action and condition (≥ 1000).</summary>
public static class DnrRuleIds
{
    public const int AllowlistDefault = 1;
    public const int IpLiteralGate = 2;
    /// <summary>youtube.com, youtube-nocookie.com and their subdomains.</summary>
    public const int YouTube = 3;
    /// <summary>youtu.be (the host itself only).</summary>
    public const int YouTubeShort = 4;
    public const int TimeUpBlock = 10;
    internal const int FirstHashed = 1_000;
    internal const int HashedRange = 999_999_000;
}
