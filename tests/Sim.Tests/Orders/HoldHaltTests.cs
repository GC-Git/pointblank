using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Tests.UnitAi;

namespace PointBlank.Sim.Tests.Orders;

/// <summary>Item 2.4d (owner decision): a delivered Hold stops each man on his own reaction, not all on one tick.</summary>
public class HoldHaltTests
{
    private static Fixed M(int metres) => Fixed.FromInt(metres);

    private static (AiRig Rig, Squad Squad) Marching(ulong seed, OrderKind move, bool ai)
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

    private static void DeliverHold(AiRig rig, Squad q)
    {
        rig.Order(q, OrderKind.Hold, 0, 0);
        rig.World.Step();
        while (rig.Command.StatusOf(q.Id) is var v && (v.Order!.Kind != OrderKind.Hold || v.Status == OrderStatus.Pending))
        {
            Assert.True(rig.Move.IsMoving(q.Id), "the move carries on until the Hold is delivered");
            rig.World.Step();
        }
    }

    [Theory]
    [InlineData(OrderKind.Move, true)]
    [InlineData(OrderKind.MoveFast, true)]
    [InlineData(OrderKind.Sneak, true)]
    [InlineData(OrderKind.Move, false)]
    public void A_delivered_hold_stops_the_men_one_by_one_and_nobody_gets_up(OrderKind move, bool ai)
    {
        for (ulong seed = 1; seed <= 4; seed++)
        {
            var (rig, q) = Marching(seed, move, ai);
            var react = rig.Move.Rules.OrderPostureDelayTicks;
            DeliverHold(rig, q);
            var delivered = rig.World.Tick;
            var men = q.Soldiers.Where(s => s.CanLead).ToList();
            var last = men.ToDictionary(s => s.Id, s => (s.X, s.Y));
            var posture = men.ToDictionary(s => s.Id, s => s.Posture);
            var stoppedAt = men.ToDictionary(s => s.Id, _ => delivered);
            while (rig.Command.StatusOf(q.Id).Status != OrderStatus.Done)
            {
                Assert.Equal(OrderStatus.Executing, rig.Command.StatusOf(q.Id).Status);
                Assert.True(rig.World.Tick - delivered <= react + 2, "the squad did not stop in time");
                rig.World.Step();
                foreach (var s in men)
                {
                    if (last[s.Id] != (s.X, s.Y))
                    {
                        (stoppedAt[s.Id], last[s.Id]) = (rig.World.Tick, (s.X, s.Y));
                    }

                    Assert.True(s.Posture >= posture[s.Id], $"seed {seed}: man {s.Id} rose to {s.Posture}");
                    posture[s.Id] = s.Posture;
                }
            }

            Assert.False(rig.Move.IsMoving(q.Id));
            Assert.True(stoppedAt.Values.Distinct().Count() >= 3, $"seed {seed}: stop ticks {string.Join(",", stoppedAt.Values)}");
            if (!ai)
            {
                // A Hold, unlike a Defend, does not put anyone down; and nobody walks on afterwards.
                var held = men.Select(s => (s.X, s.Y, s.Posture)).ToList();
                rig.World.Run(50);
                Assert.Equal(held, men.Select(s => (s.X, s.Y, s.Posture)).ToList());
                Assert.Equal(rig.Move.Rules.PostureOf(MoveMode.Move), men[0].Posture);
            }
        }
    }

    /// <summary>Item 2.4b still holds on the Hold order's path: men about to get lower do.</summary>
    [Fact]
    public void A_hold_just_after_sneak_leaves_the_men_low()
    {
        var rig = AiRig.Create(1, new GameMap("open", 300, 60), ai: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        rig.World.Step();
        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(60), MoveMode.Sneak));
        rig.Move.Halt(q.Id, rig.World.Tick, goDown: false); // before anyone has got down
        rig.World.Run(rig.Move.Rules.OrderPostureDelayTicks + 2);
        Assert.False(rig.Move.IsMoving(q.Id));
        Assert.All(q.Soldiers, s => Assert.Equal(rig.Move.Rules.PostureOf(MoveMode.Sneak), s.Posture));
        Assert.All(q.Soldiers, s => Assert.False(s.AwaitingPosture));
    }

    [Fact]
    public void A_hold_stops_men_on_moves_of_their_own_one_by_one()
    {
        var rig = AiRig.Create(1, new GameMap("open", 300, 60), ai: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        rig.World.Step();
        foreach (var s in q.Soldiers)
        {
            Assert.True(rig.Move.MoveSoldier(q.Id, s.Id, new TilePos(60, 5 + s.Id), MoveMode.Move));
        }

        rig.World.Run(10);
        rig.Move.Halt(q.Id, rig.World.Tick, goDown: false);
        var stops = new Dictionary<int, long>();
        for (var i = 0; i <= rig.Move.Rules.OrderPostureDelayTicks + 2; i++)
        {
            rig.World.Step();
            foreach (var s in q.Soldiers.Where(s => !rig.Move.IsSoldierMoving(s.Id)))
            {
                stops.TryAdd(s.Id, rig.World.Tick);
            }
        }

        Assert.Equal(q.Soldiers.Count, stops.Count);
        Assert.True(stops.Values.Distinct().Count() >= 3, string.Join(",", stops.Values));
    }

    [Fact]
    public void Holding_runs_are_deterministic()
    {
        ulong Run()
        {
            var (rig, q) = Marching(6, OrderKind.Move, ai: true);
            DeliverHold(rig, q);
            rig.World.Run(3);
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Run(), Run());
    }
}
