using PointBlank.Sim;
using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;

namespace PointBlank.Runner;

/// <summary>
/// Item 2.1 "fix" test: a German MG team (the MG crew of a <c>de_gruppe</c>, prone, at x = 60 m on a 1.5 m
/// rise) and a US rifle squad 220 m east of it, ordered (scripted <c>order</c>, through the chain of command) to
/// <c>Move</c> to the MG. A fire controller for the Germans, using only their <see cref="SideView"/>, keeps
/// the MG team firing at the nearest tracked contact. With <c>hedges</c>, four hedge lines (<see cref="HedgeLines"/>)
/// cross the field between them. Both sides have their platoon HQ behind them. Start positions
/// jitter with the seed. Metrics: <c>pinned</c> (1 if the US squad was pinned within the run),
/// <c>ticks_to_pin</c> (-1 if never), <c>first_mg_shot</c>, <c>us_casualties</c>.
/// </summary>
public sealed class PinScenario : IScenario
{
    public const int MgX = 60;
    public const int SquadX = 280;
    public const int MidY = 60;
    public const int RunTicks = 300;

    /// <summary>The MG's rise, in decimetres, so it sees over the hedges.</summary>
    public const int RiseDm = 15;

    /// <summary>The hedge lines of <c>pin-hedges</c> (x in metres, each the full height of the map).</summary>
    public static readonly int[] HedgeLines = [150, 190, 230, 270];

    private static readonly Lazy<DataSet> Data = new(() => DataLoader.LoadDirectory(DataDirectory()));

    private readonly bool _hedges;

    public PinScenario(bool hedges)
    {
        _hedges = hedges;
        Map = new ContentRef(BuildMap().Id, 1, BuildMap().ComputeContentHash());
    }

    public string Id => _hedges ? "pin-hedges" : "pin-open";

    public ContentRef Map { get; }

    public IReadOnlyList<ContentRef> Forces { get; } =
        [new("us_rifle_squad", 1), new("us_platoon_hq", 1), new("de_gruppe:mg_team", 1), new("de_zugtrupp", 1)];

    public int Ticks => RunTicks;

    /// <summary>Args: [side (0 us, 1 de), unit, order kind, x mm, y mm].</summary>
    public IReadOnlyList<ReplayOrder> ScriptedOrders { get; } =
        [new(0, "order", [0, 0, (long)OrderKind.Move, (MgX + 10) * 1000L, MidY * 1000L])];

    public World CreateWorld(ulong seed)
    {
        var data = Data.Value;
        var map = BuildMap();
        var org = new OrgSystem(OrgTables.FromData(data));
        var suppression = new SuppressionSystem(org, SuppressionRules.FromData(data));
        var move = new MovementSystem(map, org, MovementRules.FromData(data), suppression);
        var sight = SightRules.FromData(data);
        var spot = new SpottingSystem(map, org, move, new LineOfSight(map, sight), SpottingRules.FromData(data), suppression);
        var fire = new FireSystem(map, org, sight, FireRules.FromData(data), spot, suppression);
        var command = new CommandSystem(org, move, CommandRules.FromData(data), fire, suppression);
        var watch = new PinWatch(org, fire, suppression);
        var world = new World(seed, new MapSystem(map), org, command, move, spot, fire, suppression, new MgFireControl(2), watch);

        var rng = new SimRng((seed * 7919UL) + 17UL);
        Fixed Jitter(int metres) => Fixed.FromRatio(rng.NextInt(-metres * 1000, (metres * 1000) + 1), 1000);
        var us = org.SpawnSquad("us_rifle_squad", Fixed.FromInt(SquadX) + Jitter(15), Fixed.FromInt(MidY) + Jitter(10)); // squad 0
        org.SpawnSquad("us_platoon_hq", Fixed.FromInt(SquadX + 60), Fixed.FromInt(MidY + 30)); // squad 1
        var de = org.SpawnSquad("de_gruppe", Fixed.FromInt(MgX), Fixed.FromInt(MidY), Posture.Prone); // squad 2
        org.SpawnSquad("de_zugtrupp", Fixed.FromInt(MgX - 30), Fixed.FromInt(MidY + 30), Posture.Prone); // squad 3
        var y = Fixed.FromInt(MidY) + Jitter(5);
        var crew = 0;
        foreach (var s in de.Soldiers)
        {
            if (s.Role is Role.MgGunner or Role.MgAssistant or Role.MgLoader)
            {
                s.X = Fixed.FromInt(MgX);
                s.Y = y + Fixed.FromInt(2 * crew++);
            }
            else
            {
                de.SetHealth(s.Id, HealthState.Dead);
            }
        }

        _ = us;
        return world;
    }

    public ISimInput CreateInput(ReplayOrder order)
    {
        if (order.Type == "order" && order.Args is [var side, var unit, var kind, var x, var y]
            && side is 0 or 1 && unit is >= 0 and <= 3 && kind is >= 0 and <= byte.MaxValue && Enum.IsDefined((OrderKind)kind)
            && x is >= 0 and <= 1_000_000 && y is >= 0 and <= 1_000_000)
        {
            return new IssueOrderInput(
                side == 0 ? "us" : "de", (int)unit, new Order((OrderKind)kind, Fixed.FromRatio(x, 1000), Fixed.FromRatio(y, 1000)));
        }

        throw new FormatException($"Invalid order '{order.Type}' [{string.Join(", ", order.Args)}] for scenario '{Id}'.");
    }

    public IReadOnlyList<Metric> Measure(World world)
    {
        var w = world.GetSystem<PinWatch>();
        var org = world.GetSystem<OrgSystem>();
        var down = org.Squads[0].Soldiers.Count(s => !s.CanLead);
        return
        [
            new("pinned", w.PinnedTick >= 0 ? Fixed.One : Fixed.Zero),
            new("ticks_to_pin", Fixed.FromInt((int)w.PinnedTick)),
            new("first_mg_shot", Fixed.FromInt((int)w.FirstShotTick)),
            new("us_casualties", Fixed.FromInt(down)),
        ];
    }

    private GameMap BuildMap()
    {
        var map = new GameMap(_hedges ? "pin-hedges" : "pin-open", 200, 60);
        for (var tx = 0; tx < 50; tx++)
        {
            // The MG's rise: 1.5 m up to x = 80 m, then a slope down to the field at x = 100 m.
            var dm = tx < 40 ? RiseDm : RiseDm * (50 - tx) / 10;
            for (var ty = 0; ty < map.Height; ty++)
            {
                map.SetElevationDm(tx, ty, dm);
            }
        }

        if (_hedges)
        {
            foreach (var x in HedgeLines)
            {
                for (var ty = 0; ty < map.Height; ty++)
                {
                    map.SetHedge((x / 2) - 1, ty, EdgeSide.East, true);
                }
            }
        }

        return map;
    }

    /// <summary>The nearest <c>data</c> folder holding <c>suppression.json</c>, above the binaries or the working directory.</summary>
    private static string DataDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "suppression.json")))
                {
                    return candidate;
                }
            }
        }

        return "data";
    }

    /// <summary>
    /// The Germans' scripted fire control, through their <see cref="SideView"/> only: every second, unless
    /// an order is on its way or the team is firing at a tracked contact, fire at the nearest tracked one.
    /// Stateless, so it adds nothing to the hash; its orders are deterministic, so a replay reproduces them.
    /// </summary>
    private sealed class MgFireControl(int squadId) : ISimSystem
    {
        public void Tick(World world)
        {
            if (world.Tick % World.TicksPerSecond != 0)
            {
                return;
            }

            var view = world.ViewFor("de");
            var status = view.StatusOf(squadId);
            if (status is { Status: OrderStatus.Pending or OrderStatus.Hesitating })
            {
                return;
            }

            var known = view.KnownEnemies;
            if (status is { Status: OrderStatus.Executing, Order: { Kind: OrderKind.Fire } o }
                && known.Any(c => c.EnemyId == o.TargetId && c.Quality == ContactQuality.Tracked))
            {
                return;
            }

            var squad = view.Squad(squadId);
            var able = squad?.Soldiers.Where(s => s.Health is HealthState.Ok or HealthState.Wounded).ToList();
            if (able is not { Count: > 0 })
            {
                return;
            }

            var me = able[0];

            ContactView? best = null;
            var bestD = Fixed.MaxValue;
            foreach (var c in known)
            {
                var d = ((c.X - me.X) * (c.X - me.X)) + ((c.Y - me.Y) * (c.Y - me.Y));
                if (c.Quality == ContactQuality.Tracked && d < bestD)
                {
                    (best, bestD) = (c, d);
                }
            }

            if (best is { } t)
            {
                view.Issue(squadId, new Order(OrderKind.Fire, t.X, t.Y, t.EnemyId));
            }
        }

        public void AppendState(StateHasher hasher)
        {
        }
    }

    /// <summary>Records when the MG first fired and when the US squad was first pinned (hashed).</summary>
    internal sealed class PinWatch(OrgSystem org, FireSystem fire, SuppressionSystem suppression) : ISimSystem
    {
        public long PinnedTick { get; private set; } = -1;

        public long FirstShotTick { get; private set; } = -1;

        public void Tick(World world)
        {
            if (FirstShotTick < 0 && fire.Shots.Count > 0)
            {
                FirstShotTick = world.Tick;
            }

            if (PinnedTick < 0 && suppression.IsPinned(org.Squads[0].Id))
            {
                PinnedTick = world.Tick;
            }
        }

        public void AppendState(StateHasher hasher)
        {
            hasher.Add(PinnedTick);
            hasher.Add(FirstShotTick);
        }
    }
}
