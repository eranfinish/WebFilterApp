using WebFilter.Core.Model;

namespace WebFilter.Core.Engine;

/// <summary>
/// Reuses each member's <see cref="EffectiveUrlPolicy"/> (and with it the evaluator's compiled index) until the
/// settings change or a rule expires. One cache per settings snapshot; discard it when the snapshot is replaced.
/// </summary>
public sealed class PolicyCache(FamilyConfig config)
{
    // Baseline policies are kept under a key no member id can have (ids are never empty, and "\0" is not in one).
    private const string BaselineKey = "\0baseline:";

    private readonly Dictionary<string, (EffectiveUrlPolicy Policy, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil)> _entries = [];

    public FamilyConfig Config { get; } = config;

    public EffectiveUrlPolicy Get(string memberId, DateTimeOffset now) =>
        Get(memberId, memberId, now, () => EffectiveUrlPolicy.For(Config, memberId, now));

    /// <summary>The parents' baseline policy (<see cref="EffectiveUrlPolicy.ForBaseline"/>) of a member, or of administrators linked to nobody (null).</summary>
    public EffectiveUrlPolicy GetBaseline(string? memberId, DateTimeOffset now) =>
        Get(BaselineKey + memberId, memberId, now, () => EffectiveUrlPolicy.ForBaseline(Config, memberId, now));

    private EffectiveUrlPolicy Get(string key, string? memberId, DateTimeOffset now, Func<EffectiveUrlPolicy> build)
    {
        if (_entries.TryGetValue(key, out var entry) && now >= entry.ValidFrom && now < entry.ValidUntil) return entry.Policy;
        var policy = build();
        _entries[key] = (policy, now, EffectiveUrlPolicy.NextExpiry(Config, memberId, now));
        return policy;
    }
}
