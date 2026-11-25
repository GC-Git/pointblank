namespace PointBlank.Sim.Replays;

/// <summary>A number read from a finished battle, for statistics.</summary>
public sealed record Metric(string Name, Fixed Value);

/// <summary>
/// How to set up and read one kind of battle. Implementations are part of the simulation:
/// the same seed and orders must always give the same battle.
/// </summary>
public interface IScenario
{
    string Id { get; }

    ContentRef Map { get; }

    IReadOnlyList<ContentRef> Forces { get; }

    /// <summary>How long the scenario runs.</summary>
    int Ticks { get; }

    /// <summary>The orders a headless run gives.</summary>
    IReadOnlyList<ReplayOrder> ScriptedOrders { get; }

    /// <summary>A new battle at tick 0 with no orders scheduled.</summary>
    World CreateWorld(ulong seed);

    /// <exception cref="FormatException">The order type or its arguments are invalid.</exception>
    ISimInput CreateInput(ReplayOrder order);

    /// <summary>The same names in the same order for every battle.</summary>
    IReadOnlyList<Metric> Measure(World world);
}
