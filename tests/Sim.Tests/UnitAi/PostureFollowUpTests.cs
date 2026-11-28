using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Suppression;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>Item 2.4d: a new movement order against a pending get-down, and a Defend halt under fire.</summary>
public class PostureFollowUpTests(ITestOutputHelper output)
{
    private static Fixed M(int metres) => Fixed.FromInt(metres);

    /// <summary>
    /// A man under fire who is on his way down finishes getting down: a new movement order does not stand him up first.
    /// A calm man's pending change is simply overtaken by the order.
    /// </summary>
    [Theory]
    [InlineData(40, true)] // suppressed
    [InlineData(90, true)] // pinned
    [InlineData(0, false)]
    public void A_new_move_does_not_replace_the_get_down_of_a_man_under_fire(int level, bool getsDown)
    {
        var rig = AiRig.Create(1, new GameMap("open", 200, 60), ai: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        rig.World.Step();
        foreach (var s in q.Soldiers)
        {
            rig.Suppression.SetLevel(s.Id, M(level));
        }

        rig.World.Step(); // the suppression states follow the levels
        foreach (var s in q.Soldiers)
        {
            PostureChange.Request(s, Posture.Prone, rig.World.Tick, 0);
            s.PostureDueAt = rig.World.Tick + 5;
        }

        Assert.All(q.Soldiers, s => Assert.Equal(Posture.Standing, s.Posture));
        Assert.Equal(MoveResult.Started, rig.Move.Execute(new MoveOrder(q.Id, M(150), M(60), MoveMode.Move)));
        var wentDown = new HashSet<int>();
        for (var i = 0; i < 30; i++)
        {
            rig.World.Step();
            wentDown.UnionWith(q.Soldiers.Where(s => s.Posture == Posture.Prone).Select(s => s.Id));
        }

        Assert.Equal(getsDown ? q.Soldiers.Count : 0, wentDown.Count);
    }

    [Fact]
    public void A_halt_drops_a_pending_rise_and_the_man_does_not_set_off()
    {
        var rig = AiRig.Create(1, new GameMap("open", 200, 60), ai: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60), Posture.Prone);
        rig.World.Step();
        Assert.Equal(MoveResult.Started, rig.Move.Execute(new MoveOrder(q.Id, M(150), M(60), MoveMode.Move)));
        var at = q.Soldiers.Select(s => (s.X, s.Y)).ToList();
        rig.Move.Halt(q.Id, rig.World.Tick);
        for (var i = 0; i < 40; i++)
        {
            rig.World.Step();
            Assert.All(q.Soldiers, s => Assert.Equal(Posture.Prone, s.Posture));
        }

        Assert.Equal(at, q.Soldiers.Select(s => (s.X, s.Y)).ToList());
    }

    /// <summary>A Defend delivered to a squad marching under fire: no man gets up while it halts.</summary>
    [Trait(Speed.Name, Speed.Slow)]
    [Theory]
    [InlineData("fix-assault")]
    [InlineData("bombard-creep")]
    [InlineData("equal-fire")]
    public void No_man_springs_up_while_a_defend_halts_a_squad_under_fire(string id)
    {
        var runs = SeedRuns.Map(50, seed => HaltUnderFire(id, seed));
        var (rises, halts) = (runs.Sum(r => r.Rises), runs.Sum(r => r.Halts));
        output.WriteLine($"METRIC defend halt under fire {id}: rises {rises} in {halts} halts");
        Assert.True(halts >= 10, $"only {halts} halts under fire: too few to prove anything");
        Assert.Equal(0, rises);
    }

    /// <summary>Each marching squad is ordered to Defend the first time one of its men is under fire.</summary>
    private static (int Rises, int Halts) HaltUnderFire(string id, ulong seed)
    {
        var world = id == "equal-fire" ? StandingScanTests.Rig(id, seed, out var ticks) : StandingScanTests.Runner(id, seed, out ticks);
        var org = world.GetSystem<OrgSystem>();
        var move = world.GetSystem<MovementSystem>();
        var suppression = world.GetSystem<SuppressionSystem>();
        var ordered = new HashSet<int>();
        var halted = new HashSet<int>();
        var rises = 0;
        for (var i = 0; i < ticks; i++)
        {
            var was = org.Squads.SelectMany(q => q.Soldiers).ToDictionary(s => s.Id, s => s.Posture);
            world.Step();
            foreach (var q in org.Squads)
            {
                if (move.IsHalting(q.Id) && ordered.Contains(q.Id) && q.Morale != MoraleState.Routing)
                {
                    halted.Add(q.Id);
                    rises += q.Soldiers.Count(s => s.CanLead && s.Posture < was[s.Id]);
                }

                if (!ordered.Contains(q.Id) && move.IsMoving(q.Id) && q.Morale != MoraleState.Routing
                    && q.Soldiers.Any(s => s.CanLead && suppression.StateOf(s.Id) != SuppressionState.None))
                {
                    ordered.Add(q.Id);
                    world.Schedule(world.EarliestInputTick, new IssueOrderInput(q.Side, q.Id, new Order(OrderKind.Defend, q.Soldiers[0].X, q.Soldiers[0].Y, -1)));
                }
            }
        }

        return (rises, halted.Count);
    }
}
