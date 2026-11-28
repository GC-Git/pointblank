using PointBlank.Sim.Map;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Tests.UnitAi;

namespace PointBlank.Sim.Tests.Orders;

/// <summary>Item 2.9: a delivered Defend order halts a move under way, each man on his own reaction.</summary>
public class DefendHaltTests
{
    private static Fixed M(int metres) => Fixed.FromInt(metres);

    /// <summary>A US rifle squad (with its HQ near) well into a long move, on open ground with no enemy.</summary>
    private static (AiRig Rig, Squad Squad) Marching(ulong seed, OrderKind move = OrderKind.Move, bool ai = true)
    {
        var rig = AiRig.Create(seed, new GameMap("open", 300, 60), ai);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        rig.Org.SpawnSquad("us_platoon_hq", M(40), M(40));
        rig.Order(q, move, 500, 60);
        for (var i = 0; i < 600 && !rig.Move.IsMoving(q.Id); i++)
        {
            rig.World.Step();
        }

        rig.World.Run(100);
        Assert.True(rig.Move.IsMoving(q.Id));
        return (rig, q);
    }

    private static OrderView Status(AiRig rig, Squad q) => rig.Command.StatusOf(q.Id);

    /// <summary>Issues a Defend and steps until it is delivered; the squad marches on until then.</summary>
    private static void DeliverDefend(AiRig rig, Squad q)
    {
        rig.Order(q, OrderKind.Defend, 600, 60);
        rig.World.Step();
        while (Status(rig, q).Order!.Kind != OrderKind.Defend || Status(rig, q).Status == OrderStatus.Pending)
        {
            Assert.True(rig.Move.IsMoving(q.Id), "the move carries on until the Defend is delivered");
            rig.World.Step();
        }
    }

    [Theory]
    [InlineData(OrderKind.Move, true)]
    [InlineData(OrderKind.MoveFast, true)]
    [InlineData(OrderKind.Sneak, true)]
    [InlineData(OrderKind.Move, false)]
    public void A_delivered_defend_halts_the_move_man_by_man_and_reads_done_only_once_halted(OrderKind move, bool ai)
    {
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var (rig, q) = Marching(seed, move, ai);
            var react = rig.Move.Rules.OrderPostureDelayTicks;
            DeliverDefend(rig, q);
            var delivered = rig.World.Tick;
            Assert.Equal(OrderStatus.Executing, Status(rig, q).Status);

            var men = q.Soldiers.Where(s => s.CanLead).ToList();
            var prone = men.ToDictionary(s => s.Id, s => s.Posture == Posture.Prone ? delivered : -1L);
            var last = men.ToDictionary(s => s.Id, s => (s.X, s.Y));
            var stoppedAt = new Dictionary<int, long>();
            var posture = men.ToDictionary(s => s.Id, s => s.Posture);
            while (Status(rig, q).Status != OrderStatus.Done)
            {
                Assert.Equal(OrderStatus.Executing, Status(rig, q).Status);
                Assert.True(rig.World.Tick - delivered <= react + 2, "the squad did not halt in time");
                rig.World.Step();
                foreach (var s in men)
                {
                    if (last[s.Id] != (s.X, s.Y))
                    {
                        stoppedAt[s.Id] = rig.World.Tick;
                        last[s.Id] = (s.X, s.Y);
                    }

                    Assert.True(s.Posture >= posture[s.Id], $"seed {seed}: man {s.Id} rose to {s.Posture} while halting");
                    posture[s.Id] = s.Posture;
                }
            }

            Assert.False(rig.Move.IsMoving(q.Id));
            Assert.True(stoppedAt.Values.Distinct().Count() >= 2, $"seed {seed}: every man stopped on one tick");

            // No man gets up on the way down, and each lies down on his own tick, soon after.
            var was = men.ToDictionary(s => s.Id, s => s.Posture);
            for (var i = 0; i <= react + 1; i++)
            {
                rig.World.Step();
                foreach (var s in men)
                {
                    Assert.True(s.Posture >= was[s.Id], $"seed {seed}: man {s.Id} rose to {s.Posture}");
                    was[s.Id] = s.Posture;
                    if (prone[s.Id] < 0 && s.Posture == Posture.Prone)
                    {
                        prone[s.Id] = rig.World.Tick;
                    }
                }
            }

            Assert.All(men, s => Assert.Equal(Posture.Prone, s.Posture));
            if (!ai)
            {
                var settled = men.Select(s => (s.X, s.Y)).ToList();
                rig.World.Run(50);
                Assert.Equal(settled, men.Select(s => (s.X, s.Y)).ToList());
            }
        }
    }

    [Fact]
    public void A_halting_man_walks_on_until_his_own_reaction_and_then_stays_put()
    {
        var (rig, q) = Marching(2, ai: false);
        DeliverDefend(rig, q);
        var men = q.Soldiers.Where(s => s.CanLead).ToList();
        var tracks = men.ToDictionary(s => s.Id, s => new List<(Fixed, Fixed)> { (s.X, s.Y) });
        for (var i = 0; i < 40; i++)
        {
            rig.World.Step();
            men.ForEach(s => tracks[s.Id].Add((s.X, s.Y)));
        }

        var walkedOn = 0;
        foreach (var track in tracks.Values)
        {
            var moved = Enumerable.Range(1, track.Count - 1).Where(i => track[i] != track[i - 1]).ToList();
            walkedOn += moved.Count > 0 ? 1 : 0;
            Assert.True(moved.Count == 0 || moved[^1] <= rig.Move.Rules.OrderPostureDelayTicks + 1);
        }

        Assert.True(walkedOn >= 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_move_issued_after_a_defend_works(bool whileHalting)
    {
        var (rig, q) = Marching(3);
        DeliverDefend(rig, q);
        if (!whileHalting)
        {
            rig.World.Run(50);
        }

        rig.Order(q, OrderKind.MoveFast, 120, 60);
        rig.World.Run(900);
        Assert.Equal((OrderKind.MoveFast, OrderStatus.Done), (Status(rig, q).Order!.Kind, Status(rig, q).Status));
        Assert.InRange(q.Leader!.X.ToInt(), 115, 125);
    }

    [Fact]
    public void A_newer_move_replaces_a_pending_defend()
    {
        var (rig, q) = Marching(4);
        rig.Order(q, OrderKind.Defend, 600, 60);
        rig.World.Step();
        rig.Order(q, OrderKind.MoveFast, 200, 60);
        for (var i = 0; i < 1500 && !(Status(rig, q) is { Status: OrderStatus.Done } v && v.Order!.Kind == OrderKind.MoveFast); i++)
        {
            rig.World.Step();
            Assert.True(rig.Move.IsMoving(q.Id) || Status(rig, q).Order!.Kind == OrderKind.MoveFast, "the replaced Defend halted the squad");
        }

        Assert.InRange(q.Leader!.X.ToInt(), 195, 205);
    }

    [Fact]
    public void A_newer_order_that_is_still_pending_does_not_get_halting_men_up()
    {
        var (rig, q) = Marching(5, OrderKind.Sneak);
        DeliverDefend(rig, q);
        rig.Order(q, OrderKind.Move, 500, 60);
        rig.World.Step();
        var men = q.Soldiers.Where(s => s.CanLead).ToList();
        var was = men.ToDictionary(s => s.Id, s => s.Posture);
        while (Status(rig, q).Status == OrderStatus.Pending)
        {
            rig.World.Step();
            if (Status(rig, q).Status == OrderStatus.Pending)
            {
                Assert.All(men, s => Assert.True(s.Posture >= was[s.Id], $"man {s.Id} rose to {s.Posture}"));
                men.ForEach(s => was[s.Id] = s.Posture);
            }
        }
    }

    [Fact]
    public void Halting_runs_are_deterministic()
    {
        ulong Run()
        {
            var (rig, q) = Marching(6);
            DeliverDefend(rig, q);
            rig.World.Run(3);
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Run(), Run());
    }
}
