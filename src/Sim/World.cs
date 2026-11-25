namespace PointBlank.Sim;

/// <summary>
/// The world state and its fixed-timestep loop. The sim has no clock of its own: the caller
/// decides when to <see cref="Step"/>, and each step is exactly one tick of simulated time.
/// </summary>
public sealed class World
{
    public const int TicksPerSecond = 10;

    /// <summary>Bump when the layout written by <see cref="ComputeStateHash"/> changes.</summary>
    private const int HashFormatVersion = 1;

    private readonly ISimSystem[] _systems;

    // Only ever looked up by tick, never enumerated, so dictionary order cannot matter.
    private readonly Dictionary<long, List<ISimInput>> _scheduled = new();

    private bool _stepping;

    /// <param name="systems">Ticked and hashed in the order given.</param>
    public World(ulong seed, params ISimSystem[] systems)
    {
        Seed = seed;
        Rng = new SimRng(seed);
        _systems = systems.ToArray();
    }

    /// <summary>Simulated seconds per tick (rounded down to <see cref="Fixed"/> precision).</summary>
    public static Fixed TickDuration { get; } = Fixed.FromRatio(1, TicksPerSecond);

    public ulong Seed { get; }

    /// <summary>The number of completed ticks, and so the number of the next one to run.</summary>
    public long Tick { get; private set; }

    public SimRng Rng { get; }

    /// <summary>
    /// The view of one side: the only API for UI and AI code (see <see cref="SideView"/>). Needs an
    /// <see cref="Org.OrgSystem"/>; orders and spotting are empty without their systems.
    /// </summary>
    public SideView ViewFor(string side) => new(this, side);

    /// <summary>The earliest tick <see cref="Schedule"/> accepts now.</summary>
    internal long EarliestInputTick => _stepping ? Tick + 1 : Tick;

    /// <summary>The first registered system of type <typeparamref name="T"/>, or null.</summary>
    internal T? FindSystem<T>()
        where T : class, ISimSystem => _systems.OfType<T>().FirstOrDefault();

    /// <summary>The first registered system of type <typeparamref name="T"/>. Internal: systems hold the truth of both sides.</summary>
    internal T GetSystem<T>()
        where T : ISimSystem => _systems.OfType<T>().First();

    /// <summary>
    /// Applies <paramref name="input"/> at the start of the given tick. Inputs for the same
    /// tick apply in the order they were scheduled.
    /// </summary>
    public void Schedule(long tick, ISimInput input)
    {
        long earliest = _stepping ? Tick + 1 : Tick;
        if (tick < earliest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tick), $"Tick {tick} is too late; the earliest possible is {earliest}.");
        }

        if (!_scheduled.TryGetValue(tick, out var inputs))
        {
            _scheduled[tick] = inputs = new List<ISimInput>();
        }

        inputs.Add(input);
    }

    /// <summary>Runs one tick: the inputs due now, then every system.</summary>
    public void Step()
    {
        if (_stepping)
        {
            throw new InvalidOperationException("Step cannot be called during a step.");
        }

        _stepping = true;
        try
        {
            if (_scheduled.Remove(Tick, out var inputs))
            {
                foreach (var input in inputs)
                {
                    input.Apply(this);
                }
            }

            foreach (var system in _systems)
            {
                system.Tick(this);
            }

            Tick++;
        }
        finally
        {
            _stepping = false;
        }
    }

    public void Run(int ticks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);

        for (int i = 0; i < ticks; i++)
        {
            Step();
        }
    }

    /// <summary>
    /// A fingerprint of the world state: seed, tick, RNG and every system. Inputs still
    /// waiting to run are not state; a replay stores them.
    /// </summary>
    public ulong ComputeStateHash()
    {
        var hasher = new StateHasher();
        hasher.Add(HashFormatVersion);
        hasher.Add(Seed);
        hasher.Add(Tick);
        hasher.Add(Rng.State.State);
        hasher.Add(Rng.State.Increment);
        hasher.Add(_systems.Length);
        foreach (var system in _systems)
        {
            system.AppendState(hasher);
        }

        return hasher.Value;
    }
}
