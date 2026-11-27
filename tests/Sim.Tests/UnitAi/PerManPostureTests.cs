using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using static PointBlank.Sim.Tests.UnitAi.AiRig;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>Item 2.4a: how a man handles a posture change he has not made yet.</summary>
public class PerManPostureTests
{
    private static (AiRig Rig, Squad Squad) Prone()
    {
        var rig = Create(1, new GameMap("open", 80, 20), ai: false, morale: false);
        return (rig, rig.Org.SpawnSquad("us_rifle_squad", M(20), M(20), Posture.Prone));
    }

    [Fact]
    public void A_man_gets_up_on_his_own_reaction_and_does_not_move_off_before_it()
    {
        var (rig, q) = Prone();
        var start = q.Soldiers.ToDictionary(s => s.Id, s => (s.X, s.Y));
        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(20), MoveMode.Move));
        Assert.All(q.Soldiers, s => Assert.Equal(Posture.Prone, s.Posture)); // nothing changes with the order itself

        var delay = rig.Move.Rules.OrderPostureDelayTicks;
        for (var i = 0; i <= delay; i++)
        {
            rig.World.Step();
            foreach (var s in q.Soldiers)
            {
                if (s.Posture == Posture.Prone)
                {
                    Assert.Equal(start[s.Id], (s.X, s.Y)); // still down: still where he was
                }
            }
        }

        Assert.All(q.Soldiers, s => Assert.Equal(Posture.Standing, s.Posture)); // all up within the delay
    }

    [Fact]
    public void A_later_order_is_not_overridden_by_an_older_pending_change()
    {
        var (rig, q) = Prone();
        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(20), MoveMode.Move)); // stand
        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(20), MoveMode.Sneak)); // before anyone has: crouch instead
        rig.World.Run(rig.Move.Rules.OrderPostureDelayTicks + 1);
        Assert.All(q.Soldiers, s => Assert.Equal(rig.Move.Rules.PostureOf(MoveMode.Sneak), s.Posture));

        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(20), MoveMode.Move));
        rig.Move.Stop(q.Id); // a Hold before anyone has stood
        rig.World.Run(rig.Move.Rules.OrderPostureDelayTicks + 1);
        Assert.All(q.Soldiers, s => Assert.Equal(rig.Move.Rules.PostureOf(MoveMode.Sneak), s.Posture));
    }

    [Fact]
    public void A_pending_change_is_part_of_the_state_hash()
    {
        var (a, qa) = Prone();
        var (b, _) = Prone();
        Assert.Equal(a.World.ComputeStateHash(), b.World.ComputeStateHash());
        PostureChange.Request(qa.Soldiers[0], Posture.Standing, a.World.Tick, a.Move.Rules.OrderPostureDelayTicks);
        Assert.NotEqual(a.World.ComputeStateHash(), b.World.ComputeStateHash());
    }
}
