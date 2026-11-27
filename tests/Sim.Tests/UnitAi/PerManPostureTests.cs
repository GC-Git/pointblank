using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Suppression;
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

    /// <summary>Item 2.4b: a Hold is no reason to stay up. Men who were about to get lower still do.</summary>
    [Fact]
    public void A_hold_just_after_sneak_leaves_the_men_low()
    {
        var (rig, q) = Prone();
        foreach (var s in q.Soldiers)
        {
            s.Posture = Posture.Standing;
        }

        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(20), MoveMode.Sneak));
        rig.Move.Stop(q.Id); // before anyone has crouched
        rig.World.Run(rig.Move.Rules.OrderPostureDelayTicks + 1);
        Assert.All(q.Soldiers, s => Assert.Equal(rig.Move.Rules.PostureOf(MoveMode.Sneak), s.Posture));
        Assert.All(q.Soldiers, s => Assert.False(s.AwaitingPosture));
    }

    /// <summary>Item 2.4b: the AI's get-down made while a man still awaits an order's posture survives a Hold.</summary>
    [Fact]
    public void An_ai_get_down_is_not_cancelled_by_a_pending_order()
    {
        var (rig, q) = Prone();
        var man = q.Soldiers[0];
        man.Posture = Posture.Standing;
        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(20), MoveMode.Sneak)); // he awaits: crouch
        PostureChange.Request(man, Posture.Prone, rig.World.Tick, rig.Move.Rules.OrderPostureDelayTicks);
        rig.Move.Stop(q.Id);
        rig.World.Run(rig.Move.Rules.OrderPostureDelayTicks + 1);
        Assert.Equal(Posture.Prone, man.Posture);
    }

    /// <summary>Item 2.4b: while the squad is pinned even a man who is not pinned himself stays down.</summary>
    [Fact]
    public void A_man_does_not_get_up_while_his_squad_is_pinned()
    {
        var (rig, q) = Prone();
        var man = q.Soldiers[0];
        for (var i = 0; i <= rig.Move.Rules.OrderPostureDelayTicks + 1; i++)
        {
            foreach (var s in q.Soldiers.Skip(1))
            {
                rig.Suppression.SetLevel(s.Id, Fixed.FromInt(100));
            }

            rig.World.Step();
            if (i == 0)
            {
                Assert.True(rig.Suppression.IsPinned(q.Id));
                Assert.NotEqual(SuppressionState.Pinned, rig.Suppression.StateOf(man.Id));
                PostureChange.Request(man, Posture.Kneeling, rig.World.Tick, rig.Move.Rules.OrderPostureDelayTicks);
            }

            Assert.Equal(Posture.Prone, man.Posture);
        }
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

    [Fact]
    public void The_newest_decision_wins_over_a_pending_change()
    {
        var (rig, q) = Prone();
        var man = q.Soldiers[0];
        rig.Move.Execute(new MoveOrder(q.Id, M(120), M(20), MoveMode.Move)); // stand
        PostureChange.Request(man, Posture.Prone, rig.World.Tick, rig.Move.Rules.OrderPostureDelayTicks); // later: stay down
        Assert.Null(man.PendingPosture);
        Assert.False(man.AwaitingPosture);

        var other = q.Soldiers[1];
        PostureChange.Request(other, Posture.Prone, rig.World.Tick, rig.Move.Rules.OrderPostureDelayTicks);
        Assert.Null(other.PendingPosture);
    }

    [Fact]
    public void A_stop_does_not_cancel_a_decision_to_get_down()
    {
        var (rig, q) = Prone();
        var man = q.Soldiers[0];
        man.Posture = Posture.Standing;
        PostureChange.Request(man, Posture.Prone, rig.World.Tick, rig.Move.Rules.OrderPostureDelayTicks);
        PostureChange.Halt(man);
        Assert.Equal(Posture.Prone, man.PendingPosture);
    }

    [Fact]
    public void A_stop_or_new_order_does_not_cancel_a_pin_drop()
    {
        var (rig, q) = Prone();
        foreach (var s in q.Soldiers)
        {
            s.Posture = Posture.Standing;
        }

        var man = q.Soldiers[0];
        PostureChange.Drop(man);
        PostureChange.Cancel(man); // what a Hold or a rout does
        PostureChange.Order(man, Posture.Kneeling);
        rig.World.Step();
        Assert.Equal(Posture.Prone, man.Posture);
    }

    [Fact]
    public void A_pinned_man_does_not_get_up_on_an_older_or_newer_decision()
    {
        var (rig, q) = Prone();
        var man = q.Soldiers[0];
        rig.Suppression.SetLevel(man.Id, Fixed.FromInt(100));
        rig.World.Step();
        PostureChange.Request(man, Posture.Standing, rig.World.Tick, rig.Move.Rules.OrderPostureDelayTicks);
        for (var i = 0; i <= rig.Move.Rules.OrderPostureDelayTicks; i++)
        {
            rig.Suppression.SetLevel(man.Id, Fixed.FromInt(100));
            rig.World.Step();
            Assert.Equal(Posture.Prone, man.Posture);
        }
    }

    /// <summary>Item 2.4a review: when the fire on a routing squad lifts, its men do not all get up together.</summary>
    [Fact]
    public void Routing_men_get_up_one_by_one_when_the_fire_lifts()
    {
        var together = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var rig = Create(seed, new GameMap("open", 200, 60), noCasualties: true);
            var de = rig.Org.SpawnSquad("de_gruppe", M(150), M(60), Posture.Prone);
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(260), M(60), Posture.Kneeling);
            foreach (var s in us.Soldiers.Skip(1))
            {
                us.SetHealth(s.Id, HealthState.Dead); // one rifle: it keeps heads down without pinning
            }

            rig.Org.SpawnSquad("de_zugtrupp", M(30), M(60), Posture.Prone); // somewhere to run to
            var morale = rig.World.GetSystem<MoraleSystem>();
            var last = de.Soldiers.ToDictionary(s => s.Id, s => s.Posture);
            for (var i = 0; i < 400; i++)
            {
                if (i < 50)
                {
                    morale.SetLevel(de.Id, M(5));
                }

                if (i == 50)
                {
                    foreach (var s in us.Soldiers)
                    {
                        us.SetHealth(s.Id, HealthState.Dead); // the fire lifts
                    }
                }

                rig.World.Step();
                var able = de.Soldiers.Count(s => s.CanLead);
                var up = de.Soldiers.Count(s => s.CanLead && s.Posture == Posture.Standing && last[s.Id] != Posture.Standing);
                together += i >= 50 && up >= 4 ? 1 : 0;
                foreach (var s in de.Soldiers)
                {
                    last[s.Id] = s.Posture;
                }
            }
        }

        Assert.Equal(0, together);
    }
}
