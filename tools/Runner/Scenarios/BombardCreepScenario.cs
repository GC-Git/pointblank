using PointBlank.Sim;
using PointBlank.Sim.Artillery;
using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.UnitAi;

namespace PointBlank.Runner;

/// <summary>
/// Item 2.4 "bombard and creep". On a 400 × 120 m field: a German Gruppe (squad 2, MG crew included) prone in a line
/// behind a hedge at x = 110 m (the men at x ≈ 104 m, y 42 to 78 m), under the unit AI; a US platoon HQ (squad 1) kneeling
/// on a 2 m rise at x ≈ 340 m; a US rifle squad (squad 0) prone beside it. With the barrage (<c>bombard-creep</c>) the HQ is
/// ordered at tick 0 to call a mission (<see cref="OrderKind.CallFire"/>, through the chain of command) on the hedge line
/// (<see cref="TargetX"/>, 60 m); in <c>bombard-creep-control</c> it is not. At <see cref="AdvanceTick"/>, a few seconds
/// after the last round, the rifle squad is ordered to Move to the hedge. Start points jitter with the seed. A watch
/// records, at <see cref="AdvanceTick"/>: <c>de_suppression</c> (the Gruppe's mean suppression), <c>de_morale</c>,
/// <c>de_casualties</c>, <c>de_survivors</c> and <c>de_unspotted</c> (survivors absent from the US side's known enemies:
/// truth, read by the runner only), plus <c>de_peak_suppression</c> before then. At the end: <c>us_casualties</c> and
/// <c>de_casualties_end</c> and <c>de_unspotted_end</c>.
/// </summary>
public sealed class BombardCreepScenario : IScenario
{
    public const int TargetX = 112;
    public const int AdvanceTick = 1600;
    public const int RunTicks = AdvanceTick + 300;

    private static readonly Lazy<DataSet> Data = new(() => DataLoader.LoadDirectory(DataDirectory()));

    private readonly bool _barrage;

    public BombardCreepScenario(bool barrage)
    {
        _barrage = barrage;
        Map = new ContentRef(BuildMap().Id, 1, BuildMap().ComputeContentHash());
        var orders = new List<ReplayOrder>();
        if (barrage)
        {
            orders.Add(new(0, "order", [0, 1, (long)OrderKind.CallFire, TargetX * 1000L, 60_000]));
        }

        orders.Add(new(AdvanceTick, "order", [0, 0, (long)OrderKind.Move, TargetX * 1000L, 60_000]));
        ScriptedOrders = orders;
    }

    public string Id => _barrage ? "bombard-creep" : "bombard-creep-control";

    public ContentRef Map { get; }

    public IReadOnlyList<ContentRef> Forces { get; } = [new("us_rifle_squad", 1), new("us_platoon_hq", 1), new("de_gruppe", 1)];

    public int Ticks => RunTicks;

    /// <summary>Args: [side (0 us, 1 de), unit, order kind, x mm, y mm].</summary>
    public IReadOnlyList<ReplayOrder> ScriptedOrders { get; }

    public World CreateWorld(ulong seed)
    {
        var data = Data.Value;
        var map = BuildMap();
        var org = new OrgSystem(OrgTables.FromData(data));
        var suppression = new SuppressionSystem(org, SuppressionRules.FromData(data));
        var move = new MovementSystem(map, org, MovementRules.FromData(data), suppression);
        var sight = SightRules.FromData(data);
        var los = new LineOfSight(map, sight);
        var spot = new SpottingSystem(map, org, move, los, SpottingRules.FromData(data), suppression);
        var fire = new FireSystem(map, org, sight, FireRules.FromData(data), spot, suppression);
        var command = new CommandSystem(org, move, CommandRules.FromData(data), fire, suppression);
        var ai = new UnitAiSystem(map, org, move, fire, command, UnitAiRules.FromData(data));
        var morale = new MoraleSystem(org, MoraleRules.FromData(data), suppression, command, move, spot);
        var arty = new ArtillerySystem(map, org, los, ArtilleryRules.FromData(data), command, suppression, morale);
        var watch = new Watch(org, suppression, spot);
        var world = new World(seed, new MapSystem(map), org, command, move, spot, ai, fire, arty, suppression, morale, watch);

        var rng = new SimRng((seed * 7919UL) + 43UL);
        Fixed Jitter(int metres) => Fixed.FromRatio(rng.NextInt(-metres * 1000, (metres * 1000) + 1), 1000);
        org.SpawnSquad("us_rifle_squad", Fixed.FromInt(335) + Jitter(3), Fixed.FromInt(75) + Jitter(5), Posture.Prone); // squad 0
        org.SpawnSquad("us_platoon_hq", Fixed.FromInt(340) + Jitter(3), Fixed.FromInt(55) + Jitter(5), Posture.Kneeling); // squad 1
        var de = org.SpawnSquad("de_gruppe", Fixed.FromInt(104), Fixed.FromInt(60), Posture.Prone); // squad 2
        var step = Fixed.FromInt(36) / Fixed.FromInt(Math.Max(1, de.Soldiers.Count - 1));
        for (var i = 0; i < de.Soldiers.Count; i++)
        {
            var s = de.Soldiers[i];
            s.X = Fixed.FromInt(104) + Jitter(1);
            s.Y = Fixed.FromInt(42) + (step * Fixed.FromInt(i)) + Jitter(1);
        }

        return world;
    }

    public ISimInput CreateInput(ReplayOrder order)
    {
        if (order.Type == "order" && order.Args is [var side, var unit, var kind, var x, var y]
            && side is 0 or 1 && unit is >= 0 and <= 2 && kind is >= 0 and <= byte.MaxValue && Enum.IsDefined((OrderKind)kind)
            && x is >= 0 and <= 1_000_000 && y is >= 0 and <= 1_000_000)
        {
            return new IssueOrderInput(
                side == 0 ? "us" : "de", (int)unit, new Order((OrderKind)kind, Fixed.FromRatio(x, 1000), Fixed.FromRatio(y, 1000)));
        }

        throw new FormatException($"Invalid order '{order.Type}' [{string.Join(", ", order.Args)}] for scenario '{Id}'.");
    }

    public IReadOnlyList<Metric> Measure(World world)
    {
        var w = world.GetSystem<Watch>();
        var org = world.GetSystem<OrgSystem>();
        return
        [
            new("de_suppression", w.Suppression),
            new("de_peak_suppression", w.PeakSuppression),
            new("de_morale", w.Morale),
            new("de_casualties", Fixed.FromInt(w.Casualties)),
            new("de_survivors", Fixed.FromInt(w.Survivors)),
            new("de_unspotted", Fixed.FromInt(w.Unspotted)),
            new("us_casualties", Fixed.FromInt(org.Squads[0].Soldiers.Count(s => !s.CanLead))),
            new("de_casualties_end", Fixed.FromInt(org.Squads[2].Soldiers.Count(s => !s.CanLead))),
            new("de_unspotted_end", Fixed.FromInt(Unspotted(world, org))),
        ];
    }

    /// <summary>Able defenders absent from the US side.s known enemies (truth: the runner only).</summary>
    private static int Unspotted(World world, OrgSystem org)
    {
        var known = world.GetSystem<SpottingSystem>().KnownEnemies("us");
        return org.Squads[2].Soldiers.Count(s => s.CanLead && !known.Any(c => c.EnemyId == s.Id));
    }

    private static GameMap BuildMap()
    {
        var map = new GameMap("bombard-creep", 200, 60);
        for (var tx = 150; tx < map.Width; tx++)
        {
            for (var ty = 0; ty < map.Height; ty++)
            {
                map.SetElevationDm(tx, ty, tx < 160 ? (tx - 150) * 2 : 20); // a 2 m rise east of x = 320 m
            }
        }

        for (var ty = 0; ty < map.Height; ty++)
        {
            map.SetHedge(54, ty, EdgeSide.East, true); // the hedge at x = 110 m
        }

        return map;
    }

    /// <summary>The nearest <c>data</c> folder holding <c>artillery.json</c>, above the binaries or the working directory.</summary>
    private static string DataDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "artillery.json")))
                {
                    return candidate;
                }
            }
        }

        return "data";
    }

    /// <summary>Records the Gruppe's state when the advance begins, and its peak suppression before (hashed).</summary>
    internal sealed class Watch(OrgSystem org, SuppressionSystem suppression, SpottingSystem spotting) : ISimSystem
    {
        public Fixed Suppression { get; private set; }

        public Fixed PeakSuppression { get; private set; }

        public Fixed Morale { get; private set; }

        public int Casualties { get; private set; }

        public int Survivors { get; private set; }

        public int Unspotted { get; private set; }

        public void Tick(World world)
        {
            if (world.Tick > AdvanceTick)
            {
                return;
            }

            var de = org.Squads[2];
            var level = suppression.SquadLevelOf(de.Id);
            PeakSuppression = level > PeakSuppression ? level : PeakSuppression;
            if (world.Tick != AdvanceTick)
            {
                return;
            }

            Suppression = level;
            Morale = de.MoraleLevel;
            Casualties = de.Soldiers.Count(s => !s.CanLead);
            var known = spotting.KnownEnemies("us");
            var alive = de.Soldiers.Where(s => s.CanLead).ToList(); // a surviving defender: able to fight (ok or wounded)
            Survivors = alive.Count;
            Unspotted = alive.Count(s => !known.Any(c => c.EnemyId == s.Id));
        }

        public void AppendState(StateHasher hasher)
        {
            hasher.Add(Suppression);
            hasher.Add(PeakSuppression);
            hasher.Add(Morale);
            hasher.Add(Casualties);
            hasher.Add(Survivors);
            hasher.Add(Unspotted);
        }
    }
}
