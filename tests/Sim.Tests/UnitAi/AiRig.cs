using PointBlank.Sim.Artillery;
using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.Tests.Movement;
using PointBlank.Sim.UnitAi;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>A full world (org, command, movement, spotting, unit AI, fire, suppression, morale) and a watcher.</summary>
internal sealed record AiRig(
    World World, GameMap Map, OrgSystem Org, CommandSystem Command, MovementSystem Move, SpottingSystem Spot,
    FireSystem Fire, SuppressionSystem Suppression, UnitAiSystem? Ai, AiWatch Watch)
{
    public static readonly DataSet Data = PathFinderTests.Data;

    public ArtillerySystem? Arty { get; init; }

    public static Fixed M(int metres) => Fixed.FromInt(metres);

    /// <summary>Takes all suppression off the squad's men (call before a step).</summary>
    public void Calm(Squad q)
    {
        foreach (var s in q.Soldiers)
        {
            Suppression.SetLevel(s.Id, Fixed.Zero);
        }
    }

    public static AiRig Create(ulong seed, GameMap map, bool ai = true, bool morale = true, bool noCasualties = false, bool artillery = false)
    {
        var org = new OrgSystem(OrgTables.FromData(Data));
        var suppression = new SuppressionSystem(org, SuppressionRules.FromData(Data));
        var move = new MovementSystem(map, org, MovementRules.FromData(Data), suppression);
        var sight = SightRules.FromData(Data);
        var spot = new SpottingSystem(map, org, move, new LineOfSight(map, sight), SpottingRules.FromData(Data), suppression);
        var fire = new FireSystem(map, org, sight, FireRules.FromData(Data) with { HitsAsNearMisses = noCasualties }, spot, suppression);
        var command = new CommandSystem(org, move, CommandRules.FromData(Data), fire, suppression);
        var unitAi = ai ? new UnitAiSystem(map, org, move, fire, command, UnitAiRules.FromData(Data)) : null;
        var watch = new AiWatch(map, org, move, fire);
        var systems = new List<ISimSystem> { new MapSystem(map), org, command, move, spot };
        if (unitAi != null)
        {
            systems.Add(unitAi);
        }

        systems.Add(fire);
        systems.Add(suppression);
        if (morale)
        {
            systems.Add(new MoraleSystem(org, MoraleRules.FromData(Data), suppression, command, move, spot));
        }

        systems.Add(watch);
        var arty = artillery
            ? new ArtillerySystem(
                map, org, new LineOfSight(map, sight), ArtilleryRules.FromData(Data), command, suppression,
                new MoraleSystem(org, MoraleRules.FromData(Data), suppression, command, move, spot))
            : null;
        if (arty != null)
        {
            systems.Add(arty);
        }

        return new AiRig(new World(seed, [.. systems]), map, org, command, move, spot, fire, suppression, unitAi, watch) { Arty = arty };
    }

    /// <summary>A seeded jitter in whole millimetres, within ±<paramref name="metres"/>.</summary>
    public static Func<int, Fixed> Jitter(ulong seed)
    {
        var rng = new SimRng((seed * 7919UL) + 31UL);
        return metres => Fixed.FromRatio(rng.NextInt(-metres * 1000, (metres * 1000) + 1), 1000);
    }

    public void Order(Squad q, OrderKind kind, int x, int y, int target = -1) =>
        World.Schedule(World.EarliestInputTick, new IssueOrderInput(q.Side, q.Id, new Order(kind, M(x), M(y), target)));
}

/// <summary>
/// Watches every soldier every tick (registered last): when each was last under fire (a near miss or a hit on
/// him), the first tick each side came under fire, and how long each has stood on an open tile while under
/// fire, unless his squad moves at MoveFast. A run longer than <see cref="MaxOpenTicks"/> is a violation.
/// </summary>
internal sealed class AiWatch(GameMap map, OrgSystem org, MovementSystem move, FireSystem fire) : ISimSystem
{
    public const int UnderFireTicks = 3 * World.TicksPerSecond;
    public const int MaxOpenTicks = 3 * World.TicksPerSecond;

    private readonly Dictionary<int, long> _lastUnderFire = [];
    private readonly Dictionary<int, int> _openRun = [];

    public Dictionary<string, long> FirstUnderFire { get; } = [];

    public Dictionary<string, long> FirstShot { get; } = [];

    public List<(int SoldierId, long Tick)> Violations { get; } = [];

    public int LongestOpenRun { get; private set; }

    public static bool IsOpen(GameMap map, Soldier s)
    {
        var tx = (s.X / map.TileSize).ToInt();
        var ty = (s.Y / map.TileSize).ToInt();
        if (!map.InBounds(tx, ty) || map.BuildingAt(tx, ty) != null || map.GetTerrain(tx, ty) is not (TerrainType.Open or TerrainType.Road))
        {
            return false;
        }

        return !map.HasHedge(tx, ty, EdgeSide.East) && !map.HasHedge(tx, ty, EdgeSide.South)
            && !(tx > 0 && map.HasHedge(tx - 1, ty, EdgeSide.East)) && !(ty > 0 && map.HasHedge(tx, ty - 1, EdgeSide.South));
    }

    public void Tick(World world)
    {
        foreach (var n in fire.NearMisses)
        {
            Mark(world, n.SoldierId);
        }

        foreach (var shot in fire.Shots)
        {
            if (shot.Outcome == ShotOutcome.Hit)
            {
                Mark(world, shot.HitSoldierId);
            }

            var side = org.FindSquadOf(shot.FirerId)!.Side;
            FirstShot.TryAdd(side, world.Tick);
        }

        foreach (var q in org.Squads)
        {
            var exempt = move.ModeOf(q.Id) == MoveMode.MoveFast;
            foreach (var s in q.Soldiers)
            {
                var under = _lastUnderFire.TryGetValue(s.Id, out var t) && world.Tick - t <= UnderFireTicks;
                var run = s.CanLead && under && !exempt && s.Posture == Posture.Standing && IsOpen(map, s)
                    ? _openRun.GetValueOrDefault(s.Id) + 1
                    : 0;
                _openRun[s.Id] = run;
                LongestOpenRun = Math.Max(LongestOpenRun, run);
                if (run == MaxOpenTicks + 1)
                {
                    Violations.Add((s.Id, world.Tick));
                }
            }
        }
    }

    public void AppendState(StateHasher hasher)
    {
    }

    private void Mark(World world, int soldierId)
    {
        _lastUnderFire[soldierId] = world.Tick;
        FirstUnderFire.TryAdd(org.FindSquadOf(soldierId)!.Side, world.Tick);
    }
}
