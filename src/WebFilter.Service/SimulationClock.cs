namespace WebFilter.Service;

/// <summary>
/// A settable clock for level-2 simulations (admin <c>setClock</c>). Compiled into Simulation builds only
/// (CLAUDE.md rule 7); Release services use <see cref="TimeProvider.System"/>.
/// </summary>
#if SIMULATION
public sealed class SimulationClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset now) => _now = now.ToUniversalTime();
}
#endif
