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
        var nextExpiry = Config.Rules
            .Where(r => (r.MemberId is null || r.MemberId == memberId) && r.ExpiresAt > now)
            .Select(r => r.ExpiresAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();
        _entries[memberId] = (policy, now, nextExpiry);
        return policy;
    }
}
