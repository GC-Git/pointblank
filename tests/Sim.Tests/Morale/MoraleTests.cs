using PointBlank.Sim.Morale;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Suppression;
using static PointBlank.Sim.Tests.Morale.MoraleRig;

namespace PointBlank.Sim.Tests.Morale;

/// <summary>Item 2.2: the morale model, its states and reasons, and what the chain of command does with them.</summary>
public class MoraleTests
{
    private static Squad Rifles(MoraleRig rig, int x = 300, int y = 200) => rig.Org.SpawnSquad("us_rifle_squad", M(x), M(y));

    private static void Kill(MoraleRig rig, Soldier s) => rig.Org.FindSquadOf(s.Id)!.SetHealth(s.Id, HealthState.Dead);

    [Fact]
    public void Rules_load_from_data_with_ordered_thresholds()
    {
        var r = MoraleRules.FromData(MoraleRig.Data);
        Assert.Equal(Fixed.FromInt(100), r.StartLevel);
        Assert.True(r.RoutBelow < r.BrokenBelow && r.BrokenBelow < r.BrokenRelease && r.BrokenRelease <= r.ShakenBelow);
        Assert.True(r.ShakenBelow < r.ShakenRelease);
        Assert.True(r.SupportRadius > Fixed.Zero && r.HqRallyRadius > Fixed.Zero);
    }

    [Fact]
    public void A_fresh_squad_is_steady_at_full_morale()
    {
        var rig = Create();
        var q = Rifles(rig);
        rig.Org.SpawnSquad("us_platoon_hq", M(320), M(200)); // supported and in contact: nothing lowers the ceiling
        rig.Run(5);
        Assert.Equal(MoraleState.Steady, q.Morale);
        Assert.Equal(Fixed.FromInt(100), q.MoraleLevel);
    }

    [Fact]
    public void Each_casualty_is_a_shock_and_the_leader_a_bigger_one()
    {
        var rig = Create();
        var q = Rifles(rig);
        rig.Run(1);
        Kill(rig, q.Soldiers.Last());
        rig.Run(1);
        var afterRifleman = q.MoraleLevel;
        Assert.True(afterRifleman < Fixed.FromInt(100));
        Assert.True(rig.Morale.ReasonsOf(q.Id).HasFlag(MoraleReason.Casualties));

        var rig2 = Create();
        var q2 = Rifles(rig2);
        rig2.Run(1);
        Kill(rig2, q2.Leader!);
        rig2.Run(1);
        Assert.True(q2.MoraleLevel < afterRifleman, $"{q2.MoraleLevel} vs {afterRifleman}");
        var reasons = rig2.Morale.ReasonsOf(q2.Id);
        Assert.True(reasons.HasFlag(MoraleReason.LeaderKilled));
        Assert.True(reasons.HasFlag(MoraleReason.Leaderless));
    }

    [Fact]
    public void Sustained_suppression_drains_morale_and_shows_as_under_fire()
    {
        var rig = Create();
        var q = Rifles(rig);
        for (var t = 0; t < 100; t++)
        {
            foreach (var s in q.Soldiers)
            {
                rig.Suppression.SetLevel(s.Id, Fixed.FromInt(50));
            }

            rig.Run(1);
        }

        Assert.True(q.MoraleLevel < Fixed.FromInt(100));
        Assert.True(rig.Morale.ReasonsOf(q.Id).HasFlag(MoraleReason.UnderFire));
    }

    [Fact]
    public void Morale_pinned_mirrors_suppression_pinned_unless_broken()
    {
        var rig = Create();
        var q = Rifles(rig);
        rig.Run(1);
        foreach (var s in q.Soldiers)
        {
            rig.Suppression.SetLevel(s.Id, Fixed.FromInt(90));
        }

        rig.Run(1);
        Assert.True(rig.Suppression.IsPinned(q.Id));
        Assert.Equal(MoraleState.Pinned, q.Morale);
        Assert.True(rig.Morale.ReasonsOf(q.Id).HasFlag(MoraleReason.Pinned));

        rig.Morale.SetLevel(q.Id, Fixed.FromInt(20));
        rig.Run(1);
        Assert.Equal(MoraleState.Broken, q.Morale);
    }

    [Fact]
    public void States_have_hysteresis()
    {
        var rig = Create();
        var q = Rifles(rig);
        rig.Org.SpawnSquad("us_platoon_hq", M(320), M(200));
        rig.Run(1);
        var r = rig.Morale.Rules;
        rig.Morale.SetLevel(q.Id, r.ShakenBelow - Fixed.One);
        rig.Run(1);
        Assert.Equal(MoraleState.Shaken, q.Morale);
        rig.Morale.SetLevel(q.Id, r.ShakenBelow + Fixed.FromRatio(1, 10));
        rig.Run(1);
        Assert.Equal(MoraleState.Shaken, q.Morale); // still below the release
        rig.Morale.SetLevel(q.Id, r.BrokenBelow - Fixed.One);
        rig.Run(1);
        Assert.Equal(MoraleState.Broken, q.Morale);
        rig.Morale.SetLevel(q.Id, r.BrokenRelease - Fixed.One);
        rig.Run(1);
        Assert.Equal(MoraleState.Broken, q.Morale);
        rig.Morale.SetLevel(q.Id, r.BrokenRelease + Fixed.One);
        rig.Run(1);
        Assert.Equal(MoraleState.Rallying, q.Morale);
        rig.Run(World.TicksPerSecond * 120);
        Assert.Equal(MoraleState.Steady, q.Morale);
    }

    [Fact]
    public void A_broken_squad_refuses_advances_with_a_reason_note()
    {
        var refused = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var rig = Create(seed, rules: MoraleRules.FromData(MoraleRig.Data) with { RecoveryRate = Fixed.Zero });
            var q = Rifles(rig);
            rig.World.Schedule(0, new SetMoraleInput(q.Id, MoraleState.Broken));
            rig.World.Schedule(1, new IssueOrderInput("us", q.Id, new Order(OrderKind.Move, M(350), M(200))));
            rig.Run(200);
            var v = rig.Command.StatusOf(q.Id);
            if (v.Status == OrderStatus.Refused)
            {
                refused++;
                Assert.Equal(OrderNote.MoraleBroken, v.Note);
            }
        }

        Assert.InRange(refused, 12, 40); // broken_refuse_chance 0.6 (item 1.5's rule)
    }

    [Fact]
    public void Held_orders_carry_the_pinned_note()
    {
        var rig = Create();
        var q = Rifles(rig);
        rig.Org.SpawnSquad("us_platoon_hq", M(320), M(200));
        rig.World.Schedule(0, new IssueOrderInput("us", q.Id, new Order(OrderKind.Move, M(400), M(200))));
        rig.Run(50); // the order is delivered and under way
        for (var t = 0; t < 100; t++)
        {
            foreach (var s in q.Soldiers)
            {
                rig.Suppression.SetLevel(s.Id, Fixed.FromInt(90));
            }

            rig.Run(1);
        }

        var v = rig.Command.StatusOf(q.Id);
        Assert.Equal(OrderStatus.Held, v.Status);
        Assert.Equal(OrderNote.Pinned, v.Note);
    }

    [Fact]
    public void Low_morale_slows_orders()
    {
        int Delay(int level)
        {
            var rig = Create(3);
            var q = Rifles(rig);
            rig.Org.SpawnSquad("us_platoon_hq", M(320), M(200));
            rig.Run(1);
            rig.Morale.SetLevel(q.Id, Fixed.FromInt(level));
            rig.Morale.SetState(q.Id, MoraleState.Steady);
            rig.World.Schedule(rig.World.Tick, new IssueOrderInput("us", q.Id, new Order(OrderKind.Defend, M(300), M(200))));
            rig.Run(1);
            return rig.Command.StatusOf(q.Id).TotalDelayTicks;
        }

        Assert.True(Delay(70) > Delay(100));
    }

    [Fact]
    public void A_routing_squad_runs_away_from_the_known_enemy_and_ignores_orders_but_fall_back()
    {
        var rig = Create(5, spotting: true, rules: MoraleRules.FromData(MoraleRig.Data) with { RecoveryRate = Fixed.Zero });
        var q = Rifles(rig, 300, 200);
        rig.Org.SpawnSquad("de_gruppe", M(200), M(200));
        rig.Run(World.TicksPerSecond * 10); // the Germans get spotted
        Assert.NotEmpty(rig.World.ViewFor("us").KnownEnemies);
        var x0 = q.Soldiers.Where(s => s.CanLead).Average(s => s.X.ToInt());
        rig.Morale.SetLevel(q.Id, Fixed.FromInt(5));
        rig.Morale.SetState(q.Id, MoraleState.Broken);
        rig.Run(2);
        Assert.Equal(MoraleState.Routing, q.Morale);
        rig.World.Schedule(rig.World.Tick, new IssueOrderInput("us", q.Id, new Order(OrderKind.Defend, M(300), M(200))));
        rig.Run(World.TicksPerSecond * 30);
        var x1 = q.Soldiers.Where(s => s.CanLead).Average(s => s.X.ToInt());
        Assert.True(x1 > x0 + 20, $"ran from {x0} to {x1}");
        var v = rig.Command.StatusOf(q.Id);
        Assert.Equal(OrderStatus.Refused, v.Status);
        Assert.Equal(OrderNote.Routing, v.Note);
    }

    [Fact]
    public void SideView_shows_own_morale_level_state_and_reasons_only()
    {
        var rig = Create();
        var q = Rifles(rig);
        var de = rig.Org.SpawnSquad("de_gruppe", M(100), M(100));
        rig.Run(1);
        Kill(rig, q.Leader!);
        rig.Run(1);
        var view = rig.World.ViewFor("us").Squad(q.Id)!.Value;
        Assert.Equal(q.MoraleLevel, view.MoraleLevel);
        Assert.Equal(q.Morale, view.Morale);
        Assert.True(view.MoraleReasons.HasFlag(MoraleReason.LeaderKilled));
        Assert.Equal(MoraleReason.LeaderKilled, view.MoraleTopReasons[0]);
        Assert.Contains(MoraleReason.Isolated, MoraleReasons.Top(view.MoraleReasons, 10));
        Assert.Null(rig.World.ViewFor("us").Squad(de.Id));
    }

    [Fact]
    public void The_state_hash_covers_morale()
    {
        ulong Hash(bool kill)
        {
            var rig = Create(9);
            var q = Rifles(rig);
            if (kill)
            {
                rig.World.Schedule(3, new SetHealthInput(q.Soldiers.Last().Id, HealthState.Dead));
            }

            rig.Run(10);
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Hash(true), Hash(true));
        Assert.NotEqual(Hash(true), Hash(false));

        var r1 = Create(9);
        var a = Rifles(r1);
        r1.Run(1);
        var h = r1.World.ComputeStateHash();
        r1.Morale.SetLevel(a.Id, Fixed.FromInt(99));
        Assert.NotEqual(h, r1.World.ComputeStateHash());
    }
}
