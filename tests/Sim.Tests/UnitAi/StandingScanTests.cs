using PointBlank.Runner;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using Xunit.Abstractions;
using static PointBlank.Sim.Tests.UnitAi.AiRig;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Item 2.3 "Done when", criterion 3, strict scan with real rounds: no unit under fire stays standing in the open for more
/// than 3 s unless ordered MoveFast. Every soldier, every tick, seeds 1 to 100.
/// <list type="bullet">
/// <item>Under fire: a near miss on him or a hit on him by an enemy round within the last 3 s (a friend's round is not
/// enemy fire).</item>
/// <item>In the open: his tile is Open or Road terrain, not in a building, with no hedge on any of its four edges
/// (<see cref="AiWatch.IsOpen"/>).</item>
/// <item>Standing: <see cref="Posture.Standing"/>, while able (dead and incapacitated men are not counted).</item>
/// <item>Exempt: only a man whose squad is carrying out a player MoveFast or FallBack order (FallBack moves at MoveFast).
/// AI rushes and a routing squad's flee are <b>not</b> exempt: each dash must end within the limit.</item>
/// </list>
/// A run of more than 30 consecutive ticks (3 s) is a violation.
/// </summary>
public class StandingScanTests(ITestOutputHelper output)
{
    private const int Seeds = 100;
    private const int Limit = 3 * World.TicksPerSecond;

    /// <summary>Steps a world and scans it after every tick.</summary>
    private sealed class Scan(World world)
    {
        private readonly GameMap _map = world.GetSystem<MapSystem>().Map;
        private readonly OrgSystem _org = world.GetSystem<OrgSystem>();
        private readonly FireSystem _fire = world.GetSystem<FireSystem>();
        private readonly CommandSystem _command = world.GetSystem<CommandSystem>();
        private readonly Dictionary<int, long> _last = [];
        private readonly Dictionary<int, int> _run = [];
        private readonly Dictionary<int, int> _routRun = [];

        public int Violations { get; private set; }

        public int Worst { get; private set; }

        public int RoutingOver { get; private set; }

        public string? First { get; private set; }

        public void Run(int ticks)
        {
            for (var i = 0; i < ticks; i++)
            {
                world.Step();
                Tick();
            }
        }

        private bool Enemy(int firerId, int soldierId) => _org.FindSquadOf(firerId)!.Side != _org.FindSquadOf(soldierId)!.Side;

        private void Tick()
        {
            foreach (var n in _fire.NearMisses)
            {
                if (Enemy(n.FirerId, n.SoldierId))
                {
                    _last[n.SoldierId] = world.Tick;
                }
            }

            foreach (var s in _fire.Shots)
            {
                if (s.Outcome == ShotOutcome.Hit && Enemy(s.FirerId, s.HitSoldierId))
                {
                    _last[s.HitSoldierId] = world.Tick;
                }
            }

            foreach (var q in _org.Squads)
            {
                var o = _command.StatusOf(q.Id);
                var player = o.Status == OrderStatus.Executing && o.Order?.Kind is OrderKind.MoveFast or OrderKind.FallBack;
                var routing = q.Morale == MoraleState.Routing;
                foreach (var s in q.Soldiers)
                {
                    var exposed = s.CanLead && s.Posture == Posture.Standing && AiWatch.IsOpen(_map, s)
                        && _last.TryGetValue(s.Id, out var t) && world.Tick - t <= Limit;
                    var run = exposed && !player ? _run.GetValueOrDefault(s.Id) + 1 : 0;
                    _run[s.Id] = run;
                    Worst = Math.Max(Worst, run);
                    if (run == Limit + 1)
                    {
                        Violations++;
                        First ??= $"soldier {s.Id} ({s.Role}) of squad {q.Id} at tick {world.Tick}, order {o.Order?.Kind} {o.Status}";
                    }

                    var rr = exposed && routing ? _routRun.GetValueOrDefault(s.Id) + 1 : 0;
                    _routRun[s.Id] = rr;
                    RoutingOver += rr == Limit + 1 ? 1 : 0;
                }
            }
        }
    }

    internal static World Runner(string id, ulong seed, out int ticks)
    {
        var scenario = ScenarioCatalog.Find(id)!;
        var world = scenario.CreateWorld(seed);
        foreach (var o in scenario.ScriptedOrders)
        {
            world.Schedule(o.Tick, scenario.CreateInput(o));
        }

        ticks = scenario.Ticks;
        return world;
    }

    /// <summary>The idle-reaction and advance scenarios of <see cref="UnitAiScenarioTests"/>, and equal fire, all with real rounds.</summary>
    internal static World Rig(string id, ulong seed, out int ticks)
    {
        var map = new GameMap(id, 200, 60);
        if (id == "idle-hidden")
        {
            for (var tx = 40; tx < 56; tx++)
            {
                for (var ty = 0; ty < map.Height; ty++)
                {
                    map.SetTerrain(tx, ty, TerrainType.LightWoods);
                }
            }
        }

        var rig = Create(seed, map);
        var j = Jitter(seed);
        if (id == "equal-fire")
        {
            // The equal-fire case of the fire-and-movement test: the US squad moves on a German Gruppe (MG crew out) firing at will.
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(330) + j(5), M(60) + j(5));
            var de = rig.Org.SpawnSquad("de_gruppe", M(100) + j(5), M(60) + j(5), Posture.Kneeling);
            foreach (var s in de.Soldiers.Where(s => s.Role is Role.MgGunner or Role.MgAssistant or Role.MgLoader))
            {
                de.SetHealth(s.Id, HealthState.Dead);
            }

            rig.World.Schedule(100, new IssueOrderInput("us", us.Id, new Order(OrderKind.Move, M(180), M(60))));
            ticks = 1100;
            return rig.World;
        }

        var u = rig.Org.SpawnSquad("us_rifle_squad", M(250) + j(10), M(60) + j(8));
        rig.Org.SpawnSquad("de_gruppe", M(100) + j(5), M(60) + j(5), Posture.Prone);
        if (id == "advance")
        {
            rig.Order(u, OrderKind.Move, 120, 60);
        }

        ticks = id == "advance" ? 600 : 300;
        return rig.World;
    }

    [Trait(Speed.Name, Speed.Slow)]
    [Theory]
    [InlineData("fix-assault")]
    [InlineData("fix-assault-nobase")]
    [InlineData("equal-fire")]
    [InlineData("idle-open")]
    [InlineData("idle-hidden")]
    [InlineData("advance")]
    public void No_soldier_under_fire_stands_in_the_open_for_more_than_3_s(string id)
    {
        int violations = 0, worst = 0, routing = 0;
        string? first = null;
        var scans = SeedRuns.Map(Seeds, seed =>
        {
            var world = id.StartsWith("fix-", StringComparison.Ordinal) ? Runner(id, seed, out var ticks) : Rig(id, seed, out ticks);
            var scan = new Scan(world);
            scan.Run(ticks);
            return scan;
        });
        for (var seed = 1; seed <= scans.Count; seed++)
        {
            var scan = scans[seed - 1];
            violations += scan.Violations;
            worst = Math.Max(worst, scan.Worst);
            routing += scan.RoutingOver;
            first ??= scan.First is { } f ? $"seed {seed}: {f}" : null;
        }

        output.WriteLine($"{id}: {violations} violations over {Seeds} seeds; longest run standing in the open under fire {worst / (double)World.TicksPerSecond:F1} s; of them routing: {routing}");
        Assert.True(violations == 0, first);
    }

    /// <summary>The quick set's smaller check (item 2.12): the first 3 seeds.</summary>
    [Theory]
    [InlineData("fix-assault")]
    [InlineData("equal-fire")]
    public void Quick_check_no_soldier_under_fire_stays_standing_in_the_open(string id) => SeedRuns.ForEach(3, seed =>
    {
        var world = id.StartsWith("fix-", StringComparison.Ordinal) ? Runner(id, seed, out var ticks) : Rig(id, seed, out ticks);
        var scan = new Scan(world);
        scan.Run(ticks);
        Assert.True(scan.Violations == 0, scan.First);
    });
}
