using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Tests.Movement;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Orders;

/// <summary>Item 1.5: the order pipeline and the chain of command.</summary>
public class OrderPipelineTests(ITestOutputHelper output)
{
    private static readonly DataSet Data = PathFinderTests.Data;

    private static Fixed M(int metres) => Fixed.FromInt(metres);

    private sealed record Rig(World World, OrgSystem Org, CommandSystem Command, MovementSystem Move);

    private static Rig Setup(ulong seed = 1, OrgTables? tables = null)
    {
        var map = new GameMap("open", 300, 60);
        var org = new OrgSystem(tables ?? OrgTables.FromData(Data));
        var move = new MovementSystem(map, org, PathFinderTests.Rules);
        var command = new CommandSystem(org, move, CommandRules.FromData(Data));
        return new Rig(new World(seed, new MapSystem(map), org, command, move), org, command, move);
    }

    private enum Situation
    {
        Baseline,
        Leaderless,
        NoHq,
        FarHq,
    }

    /// <summary>A US squad at (20, 60) with an HQ 20 m away (or none, or one 400 m away).</summary>
    private static (Rig Rig, Squad Squad) Scene(ulong seed, Situation situation = Situation.Baseline, MoraleState morale = MoraleState.Steady)
    {
        var rig = Setup(seed);
        var squad = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        if (situation != Situation.NoHq)
        {
            rig.Org.SpawnSquad("us_platoon_hq", M(situation == Situation.FarHq ? 420 : 40), M(60));
        }

        if (situation == Situation.Leaderless)
        {
            squad.SetHealth(squad.Leader!.Id, HealthState.Dead);
        }

        squad.SetMorale(morale);
        return (rig, squad);
    }

    private static OrderView IssueAndStep(Rig rig, Squad squad, Order order)
    {
        rig.World.Schedule(rig.World.Tick, new IssueOrderInput(squad.Side, squad.Id, order));
        rig.World.Step();
        return rig.Command.StatusOf(squad.Id);
    }

    /// <summary>Steps until the order has been delivered (it is no longer pending or hesitating).</summary>
    private static OrderView RunToDelivery(Rig rig, Squad squad, Order order)
    {
        var view = IssueAndStep(rig, squad, order);
        for (var i = 0; i < 2000 && view.Status is OrderStatus.Pending or OrderStatus.Hesitating; i++)
        {
            rig.World.Step();
            view = rig.Command.StatusOf(squad.Id);
        }

        return view;
    }

    [Fact]
    public void Every_order_kind_has_a_class_and_only_FallBack_is_a_fall_back()
    {
        foreach (var kind in Enum.GetValues<OrderKind>())
        {
            var expected = kind switch
            {
                OrderKind.Move or OrderKind.MoveFast or OrderKind.Sneak => OrderClass.Advance,
                OrderKind.FallBack => OrderClass.FallBack,
                _ => OrderClass.Neutral,
            };
            Assert.Equal(expected, OrderKinds.ClassOf(kind));
        }

        Assert.Equal(9, Enum.GetValues<OrderKind>().Length);
    }

    [Fact]
    public void An_order_is_pending_for_its_delay_then_executes_and_finishes()
    {
        var (rig, squad) = Scene(1);
        var view = IssueAndStep(rig, squad, new Order(OrderKind.Move, M(40), M(70)));

        Assert.Equal(OrderStatus.Pending, view.Status);
        Assert.Equal(DelayReason.None, view.Reasons);
        Assert.Equal(0, view.IssuedTick);
        var delay = view.TotalDelayTicks;
        Assert.InRange(delay, 15, 25); // base 2 s, spread 0.75..1.25
        Assert.Equal(delay - 1, view.TicksRemaining);

        rig.World.Run(delay - 2);
        Assert.Equal(1, rig.Command.StatusOf(squad.Id).TicksRemaining);
        Assert.False(rig.Move.IsMoving(squad.Id));

        rig.World.Step();
        Assert.Equal(OrderStatus.Executing, rig.Command.StatusOf(squad.Id).Status);
        Assert.True(rig.Move.IsMoving(squad.Id));

        for (var i = 0; i < 3000 && rig.Command.StatusOf(squad.Id).Status == OrderStatus.Executing; i++)
        {
            rig.World.Step();
        }

        Assert.Equal(OrderStatus.Done, rig.Command.StatusOf(squad.Id).Status);
        Assert.InRange(squad.Leader!.X.ToInt(), 36, 44);
    }

    [Fact]
    public void Delay_reasons_cover_leader_contact_and_morale()
    {
        DelayReason Reasons(Situation s, MoraleState m = MoraleState.Steady)
        {
            var (rig, squad) = Scene(1, s, m);
            return IssueAndStep(rig, squad, new Order(OrderKind.Move, M(30), M(60))).Reasons;
        }

        Assert.Equal(DelayReason.None, Reasons(Situation.Baseline));
        Assert.Equal(DelayReason.NoLeader, Reasons(Situation.Leaderless));
        Assert.Equal(DelayReason.OutOfContact, Reasons(Situation.NoHq));
        Assert.Equal(DelayReason.OutOfContact, Reasons(Situation.FarHq));
        Assert.Equal(DelayReason.Shaken, Reasons(Situation.Baseline, MoraleState.Shaken));
        Assert.Equal(DelayReason.Broken | DelayReason.OutOfContact, Reasons(Situation.NoHq, MoraleState.Broken));
    }

    [Fact]
    public void A_dead_HQ_gives_no_contact_and_a_radio_gives_contact_at_any_distance()
    {
        var (rig, squad) = Scene(1);
        var hq = rig.Org.Squads.Single(s => s.IsHq);
        Assert.True(rig.Command.InContact(squad));
        Assert.True(rig.Command.InContact(hq));
        foreach (var man in hq.Soldiers)
        {
            hq.SetHealth(man.Id, HealthState.Dead);
        }

        Assert.False(rig.Command.InContact(squad));
        Assert.Equal(DelayReason.OutOfContact, rig.Command.ReasonsFor(squad.Id));

        // The same squad with a radio (both ends have one) is in contact 400 m from its HQ.
        var radioRig = Setup(1, WithRadio("us_rifle_squad"));
        var radioSquad = radioRig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        radioRig.Org.SpawnSquad("us_platoon_hq", M(420), M(60));
        Assert.True(radioSquad.HasRadio);
        Assert.True(radioRig.Command.InContact(radioSquad));

        // A German HQ never puts a US squad in contact.
        var mixed = Setup(1);
        var us = mixed.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        mixed.Org.SpawnSquad("de_zugtrupp", M(30), M(60));
        Assert.False(mixed.Command.InContact(us));
    }

    [Fact]
    public void Platoon_HQs_spawn_from_the_organisation_data()
    {
        var rig = Setup();
        var us = rig.Org.SpawnSquad("us_platoon_hq", M(10), M(10));
        var de = rig.Org.SpawnSquad("de_zugtrupp", M(50), M(10));
        var rifles = rig.Org.SpawnSquad("us_rifle_squad", M(90), M(10));

        Assert.True(us.IsHq && de.IsHq);
        Assert.True(us.HasRadio);
        Assert.False(rifles.IsHq || rifles.HasRadio);
        Assert.Equal("de", de.Side);
        Assert.True(us.HasEffectiveLeader);
        Assert.Equal(MoraleState.Steady, rifles.Morale);
    }

    [Fact]
    public void A_new_order_replaces_the_pending_one()
    {
        var (rig, squad) = Scene(3);
        IssueAndStep(rig, squad, new Order(OrderKind.Move, M(60), M(60)));
        var second = IssueAndStep(rig, squad, new Order(OrderKind.Sneak, M(20), M(80)));

        Assert.Equal(OrderKind.Sneak, second.Order!.Kind);
        Assert.Equal(OrderStatus.Pending, second.Status);
        Assert.Equal(1, second.IssuedTick);
        rig.World.Run(1500);
        var view = rig.Command.StatusOf(squad.Id);
        Assert.Equal(OrderKind.Sneak, view.Order!.Kind);
        Assert.Equal(OrderStatus.Done, view.Status);
        Assert.InRange(squad.Leader!.Y.ToInt(), 76, 84);
        Assert.InRange(squad.Leader!.X.ToInt(), 16, 24);
    }

    [Fact]
    public void Orders_that_are_not_built_yet_are_acknowledged_and_Hold_stops_a_move()
    {
        foreach (var kind in new[] { OrderKind.Defend, OrderKind.Ambush, OrderKind.Fire, OrderKind.Smoke })
        {
            var (rig, squad) = Scene(5);
            var view = RunToDelivery(rig, squad, new Order(kind, M(100), M(60)));
            Assert.Equal(OrderStatus.Done, view.Status);
            Assert.Equal(OrderNote.NotImplemented, view.Note);
        }

        var (r, s) = Scene(5);
        RunToDelivery(r, s, new Order(OrderKind.Move, M(200), M(60)));
        r.World.Run(20);
        Assert.True(r.Move.IsMoving(s.Id));
        var hold = RunToDelivery(r, s, new Order(OrderKind.Hold, Fixed.Zero, Fixed.Zero));
        Assert.Equal(OrderStatus.Done, hold.Status);
        Assert.Equal(OrderNote.None, hold.Note);
        Assert.False(r.Move.IsMoving(s.Id));
        var x = s.Leader!.X;
        r.World.Run(50);
        Assert.Equal(x, s.Leader!.X);
    }

    [Fact]
    public void A_move_to_an_off_map_goal_is_rejected_with_a_reason()
    {
        var (rig, squad) = Scene(1);
        var before = rig.Command.StatusOf(squad.Id);
        Assert.Equal(OrderStatus.None, before.Status);

        foreach (var (x, y) in new[] { (-1, 30), (30, -1), (600, 30), (30, 120), (10_000, 10_000) })
        {
            var view = IssueAndStep(rig, squad, new Order(OrderKind.Move, M(x), M(y)));
            Assert.Equal(OrderStatus.Rejected, view.Status);
            Assert.Equal(OrderNote.OffMap, view.Note);
            Assert.False(rig.Move.IsMoving(squad.Id));
        }

        Assert.Equal(MoveResult.OffMap, rig.Move.Execute(new MoveOrder(squad.Id, M(600), M(30), MoveMode.Move)));
        Assert.Equal(MoveResult.Started, rig.Move.Execute(new MoveOrder(squad.Id, M(599), M(119), MoveMode.Move)));
    }

    [Fact]
    public void An_order_for_another_sides_unit_or_no_unit_throws()
    {
        var (rig, squad) = Scene(1);
        rig.World.Schedule(0, new IssueOrderInput("de", squad.Id, new Order(OrderKind.Move, M(30), M(60))));
        Assert.Throws<InvalidOperationException>(() => rig.World.Step());
        var (rig2, _) = Scene(1);
        rig2.World.Schedule(0, new IssueOrderInput("us", 99, new Order(OrderKind.Move, M(30), M(60))));
        Assert.Throws<InvalidOperationException>(() => rig2.World.Step());
    }

    [Fact]
    public void Mean_delay_is_higher_without_a_leader_and_out_of_HQ_contact()
    {
        double Mean(Situation s) => Enumerable.Range(1, 100).Average(seed =>
        {
            var (rig, squad) = Scene((ulong)seed, s);
            return IssueAndStep(rig, squad, new Order(OrderKind.Move, M(30), M(60))).TotalDelayTicks;
        });

        var baseline = Mean(Situation.Baseline);
        var leaderless = Mean(Situation.Leaderless);
        var noHq = Mean(Situation.NoHq);
        var farHq = Mean(Situation.FarHq);
        output.WriteLine($"mean delay (ticks, seeds 1-100): baseline {baseline}, leaderless {leaderless}, no HQ {noHq}, HQ 400 m away {farHq}");

        Assert.True(baseline < leaderless, $"{baseline} vs {leaderless}");
        Assert.True(baseline < noHq, $"{baseline} vs {noHq}");
        Assert.True(baseline < farHq, $"{baseline} vs {farHq}");
    }

    [Fact]
    public void A_broken_squad_refuses_most_advances_and_never_a_fall_back()
    {
        int Refusals(MoraleState morale, OrderKind kind) => Enumerable.Range(1, 100).Count(seed =>
        {
            var (rig, squad) = Scene((ulong)seed, Situation.Baseline, morale);
            return RunToDelivery(rig, squad, new Order(kind, M(30), M(70))).Status == OrderStatus.Refused;
        });

        var brokenAdvance = Refusals(MoraleState.Broken, OrderKind.Move);
        var brokenFast = Refusals(MoraleState.Broken, OrderKind.MoveFast);
        var brokenFallBack = Refusals(MoraleState.Broken, OrderKind.FallBack);
        var shakenFallBack = Refusals(MoraleState.Shaken, OrderKind.FallBack);
        output.WriteLine($"refusals of 100: broken Move {brokenAdvance}, broken MoveFast {brokenFast}, broken FallBack {brokenFallBack}, shaken FallBack {shakenFallBack}");

        Assert.True(brokenAdvance >= 50, $"{brokenAdvance}");
        Assert.True(brokenFast >= 50, $"{brokenFast}");
        Assert.Equal(0, brokenFallBack);
        Assert.Equal(0, shakenFallBack);
        foreach (var kind in Enum.GetValues<OrderKind>())
        {
            Assert.Equal(0, Refusals(MoraleState.Steady, kind));
        }
    }

    [Fact]
    public void Shaken_squads_sometimes_hesitate_and_steady_ones_never_do()
    {
        int Hesitations(MoraleState morale, OrderKind kind) => Enumerable.Range(1, 100).Count(seed =>
        {
            var (rig, squad) = Scene((ulong)seed, Situation.Baseline, morale);
            var view = IssueAndStep(rig, squad, new Order(kind, M(30), M(70)));
            for (var i = 0; i < 2000 && view.Status == OrderStatus.Pending; i++)
            {
                rig.World.Step();
                view = rig.Command.StatusOf(squad.Id);
            }

            return view.Status == OrderStatus.Hesitating;
        });

        var shaken = Hesitations(MoraleState.Shaken, OrderKind.Defend);
        output.WriteLine($"hesitations of 100: shaken Defend {shaken}");
        Assert.InRange(shaken, 10, 90);
        Assert.Equal(0, Hesitations(MoraleState.Steady, OrderKind.Move));
        Assert.Equal(0, Hesitations(MoraleState.Shaken, OrderKind.FallBack));
    }

    [Fact]
    public void SetMoraleInput_sets_morale_and_the_state_hash_covers_the_pipeline()
    {
        ulong Hash(bool order, MoraleState morale, int ticks)
        {
            var (rig, squad) = Scene(7);
            rig.World.Schedule(0, new SetMoraleInput(squad.Id, morale));
            if (order)
            {
                rig.World.Schedule(0, new IssueOrderInput("us", squad.Id, new Order(OrderKind.Move, M(30), M(60))));
            }

            rig.World.Run(ticks);
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Hash(true, MoraleState.Steady, 5), Hash(true, MoraleState.Steady, 5));
        Assert.NotEqual(Hash(true, MoraleState.Steady, 5), Hash(false, MoraleState.Steady, 5));
        Assert.NotEqual(Hash(true, MoraleState.Steady, 5), Hash(true, MoraleState.Shaken, 5));
        Assert.NotEqual(Hash(true, MoraleState.Steady, 5), Hash(true, MoraleState.Steady, 6));

        var (r, s) = Scene(7);
        r.World.Schedule(0, new SetMoraleInput(s.Id, MoraleState.Broken));
        r.World.Step();
        Assert.Equal(MoraleState.Broken, s.Morale);
    }

    private static OrgTables WithRadio(string squadId)
    {
        var tables = Data.Tables.Select(t => t.Name != "organisation"
            ? t
            : new DataTable(t.Name, t.Entries.Select(e => e.Id != squadId
                ? e
                : new DataEntry(e.Id, e.Values.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Key == "radio" ? kv.Value with { Text = "1", Number = Fixed.One } : kv.Value))).ToList()))
            .ToList();
        return OrgTables.FromData(new DataSet(tables));
    }
}
