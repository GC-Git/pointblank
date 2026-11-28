using PointBlank.Sim.Orders;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Scenarios;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim;

/// <summary>
/// A battle as the player's program holds it (item 2.5): it can be stepped one tick at a time and seen only through the
/// player's <see cref="SideView"/>. It never hands out the <see cref="PointBlank.Sim.World"/>, a scenario or its metrics,
/// so UI code cannot reach the truth through it, except as read-only copies for debug overlays
/// (<see cref="DebugTruth"/>). The player is always <see cref="PlayerSide"/>.
/// </summary>
public sealed class PlayerBattle
{
    public const string PlayerSide = "us";

    internal PlayerBattle(string scenarioId, ulong seed, World world)
    {
        ScenarioId = scenarioId;
        Seed = seed;
        World = world;
        View = world.ViewFor(PlayerSide);
    }

    /// <summary>The setups <see cref="Create"/> accepts.</summary>
    public static IReadOnlyList<string> ScenarioIds { get; } =
        ["fix-assault", "bombard-creep", ViewSandboxScenario.ScenarioId];

    public string ScenarioId { get; }

    public ulong Seed { get; }

    public SideView View { get; }

    /// <summary>For the tests only (fog of war).</summary>
    internal World World { get; }

    /// <summary>
    /// A new battle at tick 0. The other side's scripted orders are given as in a headless run; the player's own scripted
    /// orders are left to the player, unless <paramref name="playOwnScript"/> is set (screenshots and demonstrations).
    /// </summary>
    /// <exception cref="ArgumentException">Unknown scenario.</exception>
    public static PlayerBattle Create(string scenarioId, ulong seed, bool playOwnScript = false)
    {
        IScenario scenario = scenarioId switch
        {
            "fix-assault" => new FixAssaultScenario(FixAssaultMode.BaseOfFire),
            "bombard-creep" => new BombardCreepScenario(barrage: true),
            ViewSandboxScenario.ScenarioId => new ViewSandboxScenario(),
            _ => throw new ArgumentException($"Unknown scenario '{scenarioId}'. Known: {string.Join(", ", ScenarioIds)}."),
        };

        var world = scenario.CreateWorld(seed);
        foreach (var order in scenario.ScriptedOrders)
        {
            var input = scenario.CreateInput(order);
            if (playOwnScript || input is not IssueOrderInput { Side: PlayerSide })
            {
                world.Schedule(order.Tick, input);
            }
        }

        return new PlayerBattle(scenarioId, seed, world);
    }


    /// <summary>
    /// DEBUG OVERLAYS ONLY (item 2.6, CLAUDE.md rule 11): <see cref="SpottingSystem.DebugTruth()"/> as copies of both
    /// sides. Nothing else may call it; a boundary test enforces this. Development builds only (configurations Debug and
    /// ExportDebug, see Sim.csproj); in any other build it is always null.
    /// </summary>
    public SpottingDebug? DebugTruth()
    {
#if PB_DEV_BUILD
        return World.GetSystem<SpottingSystem>().DebugTruth(World);
#else
        return null;
#endif
    }

    /// <summary>Advances the battle one tick (a tenth of a second of battle time).</summary>
    public void Step() => World.Step();
}
