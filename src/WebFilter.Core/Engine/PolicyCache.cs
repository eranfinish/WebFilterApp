using WebFilter.Core.Model;

namespace WebFilter.Core.Engine;

/// <summary>
/// Reuses each member's <see cref="EffectiveUrlPolicy"/> (and with it the evaluator's compiled index) until the
/// settings change or a rule expires. One cache per settings snapshot; discard it when the snapshot is replaced.
/// </summary>
public sealed class PolicyCache(FamilyConfig config)
{
    private readonly Dictionary<string, (EffectiveUrlPolicy Policy, DateTimeOffset ValidFrom, DateTimeOffset ValidUntil)> _entries = [];

    public FamilyConfig Config { get; } = config;

    public EffectiveUrlPolicy Get(string memberId, DateTimeOffset now)
    {
        if (_entries.TryGetValue(memberId, out var entry) && now >= entry.ValidFrom && now < entry.ValidUntil) return entry.Policy;
        var policy = EffectiveUrlPolicy.For(Config, memberId, now);
        _entries[memberId] = (policy, now, EffectiveUrlPolicy.NextExpiry(Config, memberId, now));
        return policy;
    }
}
