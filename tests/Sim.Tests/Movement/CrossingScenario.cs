using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Org;
using PointBlank.Sim.Replays;

namespace PointBlank.Sim.Tests.Movement;

/// <summary>
/// Test scenario: a US and a German squad start 60 m apart, either side of the 8 m gap in the
/// test map's hedge (x = 140..148 m, y = 82 m) and are ordered to swap sides, so they meet in the
/// gap. Start positions vary with the seed. Order <c>move</c> has args [squad, x mm, y mm, mode].
/// </summary>
internal sealed class CrossingScenario(GameMap map, int ticks = 2500) : IScenario
{
    public const int NorthY = 62;
    public const int SouthY = 112;
    public const int GapX = 144;

    public string Id => "crossing";

    public ContentRef Map { get; } = new(map.Id, 1, map.ComputeContentHash());

    public IReadOnlyList<ContentRef> Forces { get; } = [new("us_rifle_squad", 1), new("de_gruppe", 1)];

    public int Ticks => ticks;

    public IReadOnlyList<ReplayOrder> ScriptedOrders { get; } =
    [
        new(0, "move", [0, GapX * 1000L, SouthY * 1000L, (long)MoveMode.Move]),
        new(0, "move", [1, GapX * 1000L, NorthY * 1000L, (long)MoveMode.Move]),
    ];

    public World CreateWorld(ulong seed)
    {
        var org = new OrgSystem(OrgTables.FromData(PathFinderTests.Data));
        var world = new World(seed, new MapSystem(map), org, new MovementSystem(map, org, PathFinderTests.Rules));
        var rng = new SimRng(seed * 31UL);
        Fixed Jitter() => Fixed.FromRatio(rng.NextInt(-6000, 6001), 1000);
        org.SpawnSquad("us_rifle_squad", Fixed.FromInt(GapX) + Jitter(), Fixed.FromInt(NorthY) + Jitter());
        org.SpawnSquad("de_gruppe", Fixed.FromInt(GapX) + Jitter(), Fixed.FromInt(SouthY) + Jitter());
        return world;
    }

    public ISimInput CreateInput(ReplayOrder order)
    {
        if (order.Type == "move" && order.Args is [var squad, var x, var y, var mode])
        {
            return new MoveOrderInput(new MoveOrder(
                (int)squad, Fixed.FromRatio(x, 1000), Fixed.FromRatio(y, 1000), (MoveMode)mode));
        }

        throw new FormatException($"Bad order '{order.Type}'.");
    }

    public IReadOnlyList<Metric> Measure(World world)
    {
        var org = world.GetSystem<OrgSystem>();
        return [new("moving", Fixed.FromInt(org.Squads.Count(s => world.GetSystem<MovementSystem>().IsMoving(s.Id))))];
    }
}
