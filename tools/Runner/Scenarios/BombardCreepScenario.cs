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
/// Item 2.4 "bombard and creep". On a 400 × 120 m field with a hedge at x = <see cref="HedgeX"/> m:
/// <list type="bullet">
/// <item>A German Gruppe (squad 3, MG crew included; its platoon HQ, squad 2, lies 50 m behind it) starts prone in a line at x ≈ 104 m, y 42 to 78 m, under the unit AI,
/// and is ordered at tick 0 (scripted, through its chain of command) to Defend facing east: its men take firing positions
/// at the hedge, where they see and fire through it.</item>
/// <item>A US platoon HQ (squad 1) kneels on a 4 m plateau at x ≈ 340 m. With the barrage (<c>bombard-creep</c>) it is ordered
/// at tick 0 to call a mission (<see cref="OrderKind.CallFire"/>) on the hedge line; in <c>bombard-creep-control</c> it is not.</item>
/// <item>A US rifle squad (squad 0) lies prone at x ≈ <see cref="CreepStartX"/> m, in a dip behind a 2.5 m ridge (x 280 to 296 m) that hides it from the hedge. At <see cref="AdvanceTick"/>, a few seconds
/// after the last round, it is ordered to Sneak to the hedge.</item>
/// </list>
/// Start points jitter with the seed. A watch reads the truth (the runner only) against the US side's known enemies:
/// <c>known_at_start</c>, <c>survivors</c> (able defenders) and <c>suppressed</c> (able defenders suppressed or pinned) at
/// <see cref="AdvanceTick"/>; <c>known_150</c>, <c>known_100</c>, <c>known_50</c>, <c>known_20</c> (the number of able defenders discovered, known at any time so far,
/// when the creeping squad's centre first comes within that distance of the hedge; -1 if it never does); <c>de_firers</c> and
/// <c>fire_spread</c> (seconds from the first to the last defender's first shot in the creep); <c>found_firing</c> (defenders
/// the US first knew within 3 s of their firing), <c>found_close</c> (first known, not by fire, with the squad within 50 m)
/// and <c>never_found</c>;
/// <c>de_suppression</c>, <c>de_peak_suppression</c> and <c>de_morale</c> at the advance; <c>de_casualties</c>;
/// <c>us_creep_casualties</c> (US squad men down during the creep).
/// </summary>
public sealed class BombardCreepScenario : IScenario
{
    public const int TargetX = 112;
    public const int HedgeX = 110;
    public const int CreepStartX = 308;
    public const int AdvanceTick = 1600;
    public const int RunTicks = AdvanceTick + 4400;

    /// <summary>The distances to the hedge at which the creeping squad's knowledge is read.</summary>
    public static readonly int[] Bands = [150, 100, 50, 20];

    private static readonly Lazy<DataSet> Data = new(() => DataLoader.LoadDirectory(DataDirectory()));

    private readonly bool _barrage;

    public BombardCreepScenario(bool barrage)
    {
        _barrage = barrage;
        Map = new ContentRef(BuildMap().Id, 1, BuildMap().ComputeContentHash());
        var orders = new List<ReplayOrder> { new(0, "order", [1, 3, (long)OrderKind.Defend, 340_000, 60_000]) };
        if (barrage)
        {
            orders.Add(new(0, "order", [0, 1, (long)OrderKind.CallFire, TargetX * 1000L, 60_000]));
        }

        orders.Add(new(AdvanceTick, "order", [0, 0, (long)OrderKind.Sneak, (HedgeX + 2) * 1000L, 60_000]));
        ScriptedOrders = orders;
    }

    public string Id => _barrage ? "bombard-creep" : "bombard-creep-control";

    public ContentRef Map { get; }

    public IReadOnlyList<ContentRef> Forces { get; } = [new("us_rifle_squad", 1), new("us_platoon_hq", 1), new("de_gruppe", 1), new("de_zugtrupp", 1)];

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
        var watch = new Watch(org, suppression, spot, fire);
        var world = new World(seed, new MapSystem(map), org, command, move, spot, ai, fire, arty, suppression, morale, watch);

        var rng = new SimRng((seed * 7919UL) + 43UL);
        Fixed Jitter(int metres) => Fixed.FromRatio(rng.NextInt(-metres * 1000, (metres * 1000) + 1), 1000);
        org.SpawnSquad("us_rifle_squad", Fixed.FromInt(CreepStartX) + Jitter(3), Fixed.FromInt(60) + Jitter(5), Posture.Prone); // squad 0
        org.SpawnSquad("us_platoon_hq", Fixed.FromInt(340) + Jitter(3), Fixed.FromInt(55) + Jitter(5), Posture.Kneeling); // squad 1
        org.SpawnSquad("de_zugtrupp", Fixed.FromInt(60) + Jitter(3), Fixed.FromInt(60) + Jitter(5), Posture.Prone); // squad 2: the German platoon HQ, 50 m back
        var de = org.SpawnSquad("de_gruppe", Fixed.FromInt(104), Fixed.FromInt(60), Posture.Prone); // squad 3
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
        var w = world.GetSystem<Watch>();
        var org = world.GetSystem<OrgSystem>();
        var list = new List<Metric>
        {
            new("known_at_start", Fixed.FromInt(w.KnownAtStart)),
            new("survivors", Fixed.FromInt(w.Survivors)),
            new("suppressed", Fixed.FromInt(w.Suppressed)),
        };
        for (var i = 0; i < Bands.Length; i++)
        {
            // Discovered able defenders when the squad first came within the band; -1 if it never did.
            list.Add(new($"known_{Bands[i]}", w.Share[i] ?? Fixed.FromInt(-1)));
        }

        var first = w.FirstShots;
        list.AddRange(
        [
            new("de_firers", Fixed.FromInt(first.Count)),
            new("fire_spread", first.Count >= 2 ? Fixed.FromRatio(first.Max() - first.Min(), World.TicksPerSecond) : Fixed.FromInt(-1)),
            new("found_firing", Fixed.FromInt(w.FoundFiring)),
            new("found_close", Fixed.FromInt(w.FoundClose)),
            new("never_found", Fixed.FromInt(org.Squads[3].Soldiers.Count(s => s.CanLead && !w.Ever(s.Id)))),
            new("de_suppression", w.Suppression),
            new("de_peak_suppression", w.PeakSuppression),
            new("de_morale", w.Morale),
            new("de_routing", w.Routing ? Fixed.One : Fixed.Zero),
            new("de_casualties", Fixed.FromInt(org.Squads[3].Soldiers.Count(s => !s.CanLead))),
            new("us_creep_casualties", Fixed.FromInt(org.Squads[0].Soldiers.Count(s => !s.CanLead) - w.UsDownAtStart)),
        ]);
        return list;
    }

    private static GameMap BuildMap()
    {
        var map = new GameMap("bombard-creep", 200, 60);
        for (var tx = 140; tx < map.Width; tx++)
        {
            for (var ty = 0; ty < map.Height; ty++)
            {
                // A 2.5 m ridge at x 280 to 296 m hides the dip behind it (the US squad's start) from the hedge; the HQ's
                // 4 m plateau east of x = 320 m sees over it.
                map.SetElevationDm(tx, ty, tx < 148 ? 25 : tx < 160 ? 0 : 40);
            }
        }

        for (var ty = 0; ty < map.Height; ty++)
        {
            map.SetHedge((HedgeX / 2) - 1, ty, EdgeSide.East, true);
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

    /// <summary>The truth against the US side's knowledge, through the creep (hashed). Runner-only: it reads both sides.</summary>
    internal sealed class Watch(OrgSystem org, SuppressionSystem suppression, SpottingSystem spotting, FireSystem fire) : ISimSystem
    {
        private const int FiringWindowTicks = 3 * World.TicksPerSecond;

        private readonly SortedSet<int> _known = [];
        private readonly SortedDictionary<int, long> _firstShot = [];
        private Fixed _closest = Fixed.FromInt(100000);

        public Fixed Suppression { get; private set; }

        public Fixed PeakSuppression { get; private set; }

        public Fixed Morale { get; private set; }

        public bool Routing { get; private set; }

        public int KnownAtStart { get; private set; }

        public int Survivors { get; private set; }

        public int Suppressed { get; private set; }

        public int UsDownAtStart { get; private set; }

        public int FoundFiring { get; private set; }

        public Fixed?[] Share { get; } = new Fixed?[Bands.Length];

        /// <summary>The share of the able defenders the US side knows (1 if none is able).</summary>
        public static Fixed KnownShare(OrgSystem org, SpottingSystem spotting)
        {
            var known = spotting.KnownEnemies("us");
            var able = org.Squads[3].Soldiers.Where(s => s.CanLead).ToList();
            return able.Count == 0 ? Fixed.One : Fixed.FromRatio(able.Count(s => known.Any(c => c.EnemyId == s.Id)), able.Count);
        }

        /// <summary>The share of the able defenders the US side has discovered (known at any time so far); 1 if none is able.</summary>
        public Fixed Discovered()
        {
            var able = org.Squads[3].Soldiers.Where(s => s.CanLead).ToList();
            return able.Count == 0 ? Fixed.One : Fixed.FromRatio(able.Count(s => _known.Contains(s.Id)), able.Count);
        }

        /// <summary>Whether the US side has ever known this defender.</summary>
        public bool Ever(int soldierId) => _known.Contains(soldierId);

        /// <summary>Each defender's first shot (tick) from the start of the creep.</summary>
        public IReadOnlyCollection<long> FirstShots => _firstShot.Values;

        /// <summary>Defenders first discovered with the creeping squad within 50 m, and not by their own fire.</summary>
        public int FoundClose { get; private set; }

        public void Tick(World world)
        {
            var de = org.Squads[3];
            var known = spotting.KnownEnemies("us");

            // The creeping squad's distance to the hedge (its able men's mean x), from the start of the creep.
            Fixed? d = null;
            var us = org.Squads[0].Soldiers.Where(s => s.CanLead).ToList();
            if (world.Tick >= AdvanceTick && us.Count > 0)
            {
                var x = Fixed.Zero;
                foreach (var s in us)
                {
                    x += s.X;
                }

                d = (x / Fixed.FromInt(us.Count)) - Fixed.FromInt(HedgeX);
                _closest = d.Value < _closest ? d.Value : _closest;
            }

            foreach (var s in de.Soldiers)
            {
                var fired = fire.LastFiredTick(s.Id);
                if (world.Tick >= AdvanceTick && fired >= AdvanceTick && !_firstShot.ContainsKey(s.Id))
                {
                    _firstShot[s.Id] = fired;
                }

                if (!_known.Contains(s.Id) && known.Any(c => c.EnemyId == s.Id))
                {
                    _known.Add(s.Id);
                    var byFire = world.Tick - fired <= FiringWindowTicks;
                    FoundFiring += byFire ? 1 : 0;
                    FoundClose += !byFire && world.Tick > AdvanceTick && _closest <= Fixed.FromInt(50) ? 1 : 0;
                }
            }

            if (world.Tick <= AdvanceTick)
            {
                var level = suppression.SquadLevelOf(de.Id);
                PeakSuppression = level > PeakSuppression ? level : PeakSuppression;
            }

            if (world.Tick == AdvanceTick)
            {
                Suppression = suppression.SquadLevelOf(de.Id);
                Morale = de.MoraleLevel;
                Routing = de.Morale == MoraleState.Routing;
                var able = de.Soldiers.Where(s => s.CanLead).ToList();
                Survivors = able.Count;
                KnownAtStart = able.Count(s => known.Any(c => c.EnemyId == s.Id));
                Suppressed = able.Count(s => suppression.StateOf(s.Id) != SuppressionState.None);
                UsDownAtStart = org.Squads[0].Soldiers.Count(s => !s.CanLead);
            }

            for (var i = 0; d != null && i < Bands.Length; i++)
            {
                if (Share[i] == null && d.Value <= Fixed.FromInt(Bands[i]))
                {
                    Share[i] = Fixed.FromInt(de.Soldiers.Count(s => s.CanLead && _known.Contains(s.Id)));
                }
            }
        }

        public void AppendState(StateHasher hasher)
        {
            hasher.Add(_firstShot.Count);
            foreach (var (id, t) in _firstShot)
            {
                hasher.Add(id);
                hasher.Add(t);
            }

            hasher.Add(_closest);
            hasher.Add(FoundClose);
            hasher.Add(_known.Count);
            foreach (var id in _known)
            {
                hasher.Add(id);
            }

            hasher.Add(Suppression);
            hasher.Add(PeakSuppression);
            hasher.Add(Morale);
            hasher.Add(Routing);
            hasher.Add(KnownAtStart);
            hasher.Add(Survivors);
            hasher.Add(Suppressed);
            hasher.Add(UsDownAtStart);
            hasher.Add(FoundFiring);
            foreach (var s in Share)
            {
                hasher.Add(s ?? Fixed.FromInt(-1));
            }
        }
    }
}
