namespace PointBlank.Sim.Replays;

/// <summary>A versioned reference to content such as a map or a force list.</summary>
public sealed record ContentRef(string Id, int Version);

/// <summary>
/// An order and the tick it takes effect. The scenario turns <paramref name="Type"/> and
/// <paramref name="Args"/> into an <see cref="ISimInput"/>.
/// </summary>
public sealed record ReplayOrder(long Tick, string Type, IReadOnlyList<long> Args);

/// <summary>
/// A saved battle: seed, map, forces and timestamped orders, plus the length it ran and the
/// state hash it ended on. Orders for the same tick apply in list order.
/// </summary>
public sealed record Replay(
    string Scenario,
    ulong Seed,
    ContentRef Map,
    IReadOnlyList<ContentRef> Forces,
    int Ticks,
    IReadOnlyList<ReplayOrder> Orders,
    ulong FinalHash);
