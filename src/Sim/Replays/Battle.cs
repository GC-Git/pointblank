namespace PointBlank.Sim.Replays;

/// <summary>A finished world and the replay that reproduces it.</summary>
public sealed record BattleResult(World World, Replay Replay);

/// <summary>Runs scenarios and replays them.</summary>
public static class Battle
{
    /// <summary>Runs a scenario with its scripted orders and records a replay.</summary>
    public static BattleResult Run(IScenario scenario, ulong seed)
    {
        var orders = scenario.ScriptedOrders.ToArray();
        var world = Simulate(scenario, seed, scenario.Ticks, orders);
        var replay = new Replay(
            scenario.Id, seed, scenario.Map, scenario.Forces.ToArray(), scenario.Ticks,
            orders, world.ComputeStateHash());

        return new BattleResult(world, replay);
    }

    /// <summary>
    /// Re-runs a replay and returns the finished world. The caller compares its state hash
    /// with <see cref="Replay.FinalHash"/> to detect divergence.
    /// </summary>
    /// <exception cref="FormatException">
    /// The replay does not fit this scenario, or an order in it is invalid.
    /// </exception>
    public static World Play(IScenario scenario, Replay replay)
    {
        if (replay.Scenario != scenario.Id)
        {
            throw new FormatException(
                $"The replay is of scenario '{replay.Scenario}', not '{scenario.Id}'.");
        }

        if (replay.Map != scenario.Map)
        {
            throw new FormatException(
                $"The replay uses map {replay.Map}; the scenario has {scenario.Map}.");
        }

        if (!replay.Forces.SequenceEqual(scenario.Forces))
        {
            throw new FormatException("The replay's forces differ from the scenario's.");
        }

        return Simulate(scenario, replay.Seed, replay.Ticks, replay.Orders);
    }

    private static World Simulate(
        IScenario scenario, ulong seed, int ticks, IReadOnlyList<ReplayOrder> orders)
    {
        if (ticks < 0)
        {
            throw new FormatException($"A battle cannot last {ticks} ticks.");
        }

        var world = scenario.CreateWorld(seed);
        foreach (var order in orders)
        {
            if (order.Tick < 0 || order.Tick >= ticks)
            {
                throw new FormatException(
                    $"Order '{order.Type}' at tick {order.Tick} is outside the {ticks}-tick battle.");
            }

            world.Schedule(order.Tick, scenario.CreateInput(order));
        }

        world.Run(ticks);
        return world;
    }
}
