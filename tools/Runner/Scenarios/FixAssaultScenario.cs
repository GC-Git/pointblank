using PointBlank.Sim;
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

/// <summary>Who is on the field in a <see cref="FixAssaultScenario"/>.</summary>
public enum FixAssaultMode
{
    /// <summary>Assault squad, German squad and a US base of fire (<c>fix-assault</c>).</summary>
    BaseOfFire,

    /// <summary>Assault squad against the German squad alone (<c>fix-assault-nobase</c>).</summary>
    NoBase,

    /// <summary>The assault squad alone (<c>fix-assault-unopposed</c>): the distance an unopposed squad covers.</summary>
    Unopposed,
}

/// <summary>
/// Item 2.3 "fix and assault" (owner decision: an advance against equal fire needs fire support). On an open 400 × 120 m
/// field: the US assault squad (squad 0) at x ≈ 330 m, y ≈ 60 m; a German Gruppe (squad 1) kneeling at x = 100 m with its MG
/// crew out, firing at will under the unit AI; and, in <see cref="FixAssaultMode.BaseOfFire"/>, a second US rifle squad
/// (squad 2) kneeling as a base of fire at (290, 16) m, 45 m off the assault lane. Scripted orders, through the chain of
/// command: the base of fire gets area Fire on the German position at tick 0; the assault squad gets Move to x = 180 m at
/// tick <see cref="AssaultTick"/>. Everyone else acts on the unit AI alone. Rounds are real (casualties happen). Start points
/// jitter with the seed. Metrics: <c>advance</c> (metres the assault squad's able men moved west from tick
/// <see cref="AssaultTick"/>), <c>reached</c> (1 if its move finished), <c>de_suppressed</c> (1 if the German squad was
/// suppressed or pinned after the base of fire opened), <c>de_pinned_ticks</c>, <c>us_held_ticks</c> (ticks the assault
/// squad's move was held by pinning), <c>us_casualties</c>.
/// </summary>
public sealed class FixAssaultScenario : IScenario
{
    public const int AssaultTick = 100;
    public const int RunTicks = AssaultTick + 1000;
    public const int ObjectiveX = 180;

    private static readonly Lazy<DataSet> Data = new(() => DataLoader.LoadDirectory(DataDirectory()));

    private readonly FixAssaultMode _mode;

    public FixAssaultScenario(FixAssaultMode mode)
    {
        _mode = mode;
        Map = new ContentRef(BuildMap().Id, 1, BuildMap().ComputeContentHash());
        var orders = new List<ReplayOrder>();
        if (mode == FixAssaultMode.BaseOfFire)
        {
            orders.Add(new(0, "order", [0, 2, (long)OrderKind.Fire, 100_000, 60_000]));
        }

        orders.Add(new(AssaultTick, "order", [0, 0, (long)OrderKind.Move, ObjectiveX * 1000L, 60_000]));
        ScriptedOrders = orders;
    }

    public string Id => _mode switch
    {
        FixAssaultMode.BaseOfFire => "fix-assault",
        FixAssaultMode.NoBase => "fix-assault-nobase",
        _ => "fix-assault-unopposed",
    };

    public ContentRef Map { get; }

    public IReadOnlyList<ContentRef> Forces => _mode switch
    {
        FixAssaultMode.BaseOfFire => [new("us_rifle_squad", 2), new("de_gruppe", 1)],
        FixAssaultMode.NoBase => [new("us_rifle_squad", 1), new("de_gruppe", 1)],
        _ => [new("us_rifle_squad", 1)],
    };

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
        var spot = new SpottingSystem(map, org, move, new LineOfSight(map, sight), SpottingRules.FromData(data), suppression);
        var fire = new FireSystem(map, org, sight, FireRules.FromData(data), spot, suppression);
        var command = new CommandSystem(org, move, CommandRules.FromData(data), fire, suppression);
        var ai = new UnitAiSystem(map, org, move, fire, command, UnitAiRules.FromData(data));
        var morale = new MoraleSystem(org, MoraleRules.FromData(data), suppression, command, move, spot);
        var watch = new Watch(org, command, suppression);
        var world = new World(seed, new MapSystem(map), org, command, move, spot, ai, fire, suppression, morale, watch);

        var rng = new SimRng((seed * 7919UL) + 31UL);
        Fixed Jitter(int metres) => Fixed.FromRatio(rng.NextInt(-metres * 1000, (metres * 1000) + 1), 1000);
        org.SpawnSquad("us_rifle_squad", Fixed.FromInt(330) + Jitter(5), Fixed.FromInt(60) + Jitter(5)); // squad 0
        if (_mode != FixAssaultMode.Unopposed)
        {
            var de = org.SpawnSquad("de_gruppe", Fixed.FromInt(100) + Jitter(5), Fixed.FromInt(60) + Jitter(5), Posture.Kneeling); // squad 1
            foreach (var s in de.Soldiers)
            {
                if (s.Role is Role.MgGunner or Role.MgAssistant or Role.MgLoader)
                {
                    de.SetHealth(s.Id, HealthState.Dead);
                }
            }
        }

        if (_mode == FixAssaultMode.BaseOfFire)
        {
            org.SpawnSquad("us_rifle_squad", Fixed.FromInt(290) + Jitter(3), Fixed.FromInt(16) + Jitter(2), Posture.Kneeling); // squad 2
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
        var q = world.GetSystem<OrgSystem>().Squads[0];
        var x = Fixed.Zero;
        var n = 0;
        foreach (var s in q.Soldiers)
        {
            if (s.CanLead)
            {
                x += s.X;
                n++;
            }
        }

        var advance = n == 0 ? Fixed.Zero : w.StartX - (x / Fixed.FromInt(n));
        return
        [
            new("advance", advance),
            new("reached", w.Reached ? Fixed.One : Fixed.Zero),
            new("de_suppressed", w.DeSuppressed ? Fixed.One : Fixed.Zero),
            new("de_pinned_ticks", Fixed.FromInt(w.DePinnedTicks)),
            new("us_held_ticks", Fixed.FromInt(w.UsHeldTicks)),
            new("us_casualties", Fixed.FromInt(q.Soldiers.Count(s => !s.CanLead))),
        ];
    }

    private static GameMap BuildMap() => new("fix-assault", 200, 60);

    /// <summary>The nearest <c>data</c> folder holding <c>unit_ai.json</c>, above the binaries or the working directory.</summary>
    private static string DataDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "unit_ai.json")))
                {
                    return candidate;
                }
            }
        }

        return "data";
    }

    /// <summary>Records the assault squad's start point and progress and the German squad's suppression (hashed).</summary>
    internal sealed class Watch(OrgSystem org, CommandSystem command, SuppressionSystem suppression) : ISimSystem
    {
        public Fixed StartX { get; private set; }

        public bool Reached { get; private set; }

        public bool DeSuppressed { get; private set; }

        public int DePinnedTicks { get; private set; }

        public int UsHeldTicks { get; private set; }

        public void Tick(World world)
        {
            var us = org.Squads[0];
            if (world.Tick == AssaultTick)
            {
                var x = Fixed.Zero;
                foreach (var s in us.Soldiers)
                {
                    x += s.X;
                }

                StartX = x / Fixed.FromInt(us.Soldiers.Count);
            }

            if (world.Tick < AssaultTick)
            {
                return;
            }

            var status = command.StatusOf(us.Id).Status;
            Reached |= status == OrderStatus.Done && command.StatusOf(us.Id).Order?.Kind == OrderKind.Move;
            UsHeldTicks += status == OrderStatus.Held ? 1 : 0;
            if (org.Squads.Count > 1 && org.Squads[1].Side == "de")
            {
                var st = suppression.SquadStateOf(org.Squads[1].Id);
                DeSuppressed |= st != SuppressionState.None;
                DePinnedTicks += st == SuppressionState.Pinned ? 1 : 0;
            }
        }

        public void AppendState(StateHasher hasher)
        {
            hasher.Add(StartX);
            hasher.Add(Reached);
            hasher.Add(DeSuppressed);
            hasher.Add(DePinnedTicks);
            hasher.Add(UsHeldTicks);
        }
    }
}
