using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.Tests.Movement;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Suppression;

/// <summary>Item 2.1: suppression.</summary>
public class SuppressionTests(ITestOutputHelper output)
{
    internal static readonly DataSet Data = PathFinderTests.Data;
    internal static readonly SuppressionRules Rules = SuppressionRules.FromData(Data);
    private static readonly FireRules Fire = FireRules.FromData(Data);
    private static readonly SightRules Sight = SightRules.FromData(Data);

    private static Fixed M(int metres) => Fixed.FromInt(metres);

    internal sealed record Rig(
        World World, OrgSystem Org, MovementSystem Move, CommandSystem Command, SpottingSystem Spot, FireSystem Fire, SuppressionSystem Sup);

    internal static Rig Setup(ulong seed = 1, SuppressionRules? rules = null, FireRules? fire = null, GameMap? map = null)
    {
        map ??= new GameMap("open", 300, 60);
        var org = new OrgSystem(OrgTables.FromData(Data));
        var sup = new SuppressionSystem(org, rules ?? Rules);
        var move = new MovementSystem(map, org, PathFinderTests.Rules, sup);
        var spot = new SpottingSystem(map, org, move, new LineOfSight(map, Sight), SpottingRules.FromData(Data), sup);
        var fs = new FireSystem(map, org, Sight, fire ?? Fire, spot, sup);
        var command = new CommandSystem(org, move, CommandRules.FromData(Data), fs, sup);
        var world = new World(seed, new MapSystem(map), org, command, move, spot, fs, sup);
        return new Rig(world, org, move, command, spot, fs, sup);
    }

    /// <summary>Puts the squad's men in a north-south line, 2 m apart.</summary>
    private static void Line(Squad q, int x, int y0)
    {
        for (var i = 0; i < q.Soldiers.Count; i++)
        {
            q.Soldiers[i].X = M(x);
            q.Soldiers[i].Y = M(y0 + (2 * i));
        }
    }

    private static void Near(Fixed expected, Fixed actual)
    {
        var d = expected - actual;
        Assert.True(d < Fixed.FromRatio(1, 1000) && -d < Fixed.FromRatio(1, 1000), $"expected {expected}, got {actual}");
    }

    private static NearMissEvent Miss(Soldier s, CalibreClass c, Fixed distance) => new(0, s.Id, -1, Fixed.Zero, Fixed.Zero, c, distance);

    [Fact]
    public void Rules_come_from_the_data_file()
    {
        Assert.Equal(M(6), Rules.NearMiss);
        Assert.Equal(M(60), Rules.PinnedThreshold);
        Assert.Equal(M(40), Rules.PinnedRelease);
        Assert.Equal(30, Rules.UnderFireWindowTicks);
        Assert.Equal(Fixed.FromInt(2), Rules.CalibreWeight(CalibreClass.Heavy));
        Assert.Contains("suppression", DataLoader.TableNames);
    }

    [Fact]
    public void Near_misses_build_suppression_by_calibre_and_distance()
    {
        var rig = Setup();
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(20));
        Line(q, 20, 0); // 2 m apart: beyond share radius only for the far end, so read single men
        var r = Fire.NearMissRadius;
        var a = q.Soldiers[0];
        var b = q.Soldiers[11];
        rig.Sup.OnNearMiss(rig.World, Miss(a, CalibreClass.Rifle, Fixed.Zero), r);
        Assert.Equal(Rules.NearMiss, rig.Sup.LevelOf(a.Id));
        rig.Sup.OnNearMiss(rig.World, Miss(b, CalibreClass.Rifle, r), r);
        Assert.Equal(Rules.NearMiss * (Fixed.One - Rules.DistanceFalloff), rig.Sup.LevelOf(b.Id)); // farther: less

        var levels = new List<Fixed>();
        foreach (var c in new[] { CalibreClass.Pistol, CalibreClass.Rifle, CalibreClass.Heavy })
        {
            var rr = Setup();
            var s = rr.Org.SpawnSquad("us_rifle_squad", M(20), M(20)).Soldiers[0];
            rr.Sup.OnNearMiss(rr.World, Miss(s, c, Fixed.Zero), r);
            levels.Add(rr.Sup.LevelOf(s.Id));
        }

        Assert.True(levels[0] < levels[1] && levels[1] < levels[2], string.Join(", ", levels));

        // Volume: more near misses, more suppression, capped at 100.
        for (var i = 0; i < 5; i++)
        {
            rig.Sup.OnNearMiss(rig.World, Miss(a, CalibreClass.Rifle, Fixed.Zero), r);
        }

        Assert.Equal(Rules.NearMiss * Fixed.FromInt(6), rig.Sup.LevelOf(a.Id));
        for (var i = 0; i < 100; i++)
        {
            rig.Sup.OnNearMiss(rig.World, Miss(a, CalibreClass.Heavy, Fixed.Zero), r);
        }

        Assert.Equal(SuppressionRules.MaxLevel, rig.Sup.LevelOf(a.Id));

        // Squad-mates close by share it.
        Assert.True(rig.Sup.LevelOf(q.Soldiers[1].Id) > Fixed.Zero);
    }

    [Fact]
    public void Real_shots_build_suppression_through_near_misses_and_hits_on_nearby_friendlies()
    {
        var rig = Setup();
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(150), M(60));
        Line(de, 150, 40);
        var firer = us.Soldiers[0];
        (firer.X, firer.Y) = (M(20), M(50));
        var target = de.Soldiers[5];
        for (var i = 0; i < 30; i++)
        {
            rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target);
        }

        Assert.True(rig.Sup.LevelOf(target.Id) > Fixed.Zero);
        Assert.Equal(Fixed.Zero, rig.Sup.LevelOf(firer.Id));

        var rig2 = Setup();
        var hit = rig2.Org.SpawnSquad("us_rifle_squad", M(20), M(20));
        Line(hit, 20, 0); // 2 m apart: 0..22 m
        rig2.Sup.OnHit(rig2.World, hit.Soldiers[0]);
        Assert.Equal(Rules.HitNearby, rig2.Sup.LevelOf(hit.Soldiers[0].Id));
        Assert.Equal(Rules.HitNearby, rig2.Sup.LevelOf(hit.Soldiers[5].Id)); // 10 m: inside the hit radius
        Assert.Equal(Fixed.Zero, rig2.Sup.LevelOf(hit.Soldiers[6].Id)); // 12 m: outside

        var rig3 = Setup();
        var q3 = rig3.Org.SpawnSquad("us_rifle_squad", M(20), M(20));
        rig3.Sup.AddExplosion(rig3.World, q3.Soldiers[0].X, q3.Soldiers[0].Y, M(50), M(20));
        Assert.Equal(M(50), rig3.Sup.LevelOf(q3.Soldiers[0].Id)); // the hook for item 2.4
    }

    [Fact]
    public void Suppression_decays_over_time_and_slower_under_fire()
    {
        var rig = Setup();
        var s = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(20)).Soldiers[0];
        rig.Sup.OnNearMiss(rig.World, Miss(s, CalibreClass.Heavy, Fixed.Zero), Fire.NearMissRadius); // under fire now
        var start = rig.Sup.LevelOf(s.Id);
        rig.World.Run(10);
        var underFire = start - rig.Sup.LevelOf(s.Id);
        rig.World.Run(Rules.UnderFireWindowTicks);
        var mid = rig.Sup.LevelOf(s.Id);
        rig.World.Run(10);
        var calm = mid - rig.Sup.LevelOf(s.Id);
        output.WriteLine($"start {start}; lost {underFire} in 1 s under fire, {calm} in 1 s calm");
        Near(Rules.DecayRate * Rules.UnderFireDecayFactor, underFire);
        Near(Rules.DecayRate, calm);
        rig.World.Run(600);
        Assert.Equal(Fixed.Zero, rig.Sup.LevelOf(s.Id));
    }

    [Fact]
    public void States_follow_the_thresholds_with_hysteresis_and_pinned_men_go_prone()
    {
        var rig = Setup();
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(20));
        var s = q.Soldiers[0];
        rig.Sup.SetLevel(s.Id, Rules.SuppressedThreshold + M(1));
        rig.World.Step();
        Assert.Equal(SuppressionState.Suppressed, rig.Sup.StateOf(s.Id));
        Assert.Equal(Posture.Standing, s.Posture);
        rig.Sup.SetLevel(s.Id, Rules.PinnedThreshold + M(1));
        rig.World.Step();
        Assert.Equal(SuppressionState.Pinned, rig.Sup.StateOf(s.Id));
        Assert.Equal(Posture.Prone, s.Posture);
        rig.Sup.SetLevel(s.Id, Rules.PinnedRelease + M(1)); // below pinned, above release: still pinned
        rig.World.Step();
        Assert.Equal(SuppressionState.Pinned, rig.Sup.StateOf(s.Id));
        rig.Sup.SetLevel(s.Id, Rules.PinnedRelease - M(1));
        rig.World.Step();
        Assert.Equal(SuppressionState.Suppressed, rig.Sup.StateOf(s.Id));

        // The squad: the mean of its able men.
        foreach (var m in q.Soldiers)
        {
            rig.Sup.SetLevel(m.Id, M(90));
        }

        q.SetHealth(q.Soldiers[1].Id, HealthState.Dead);
        rig.Sup.SetLevel(q.Soldiers[1].Id, Fixed.Zero); // the dead do not count
        rig.World.Step();
        Assert.Equal(SuppressionState.Pinned, rig.Sup.SquadStateOf(q.Id));
        Assert.True(rig.Sup.SquadLevelOf(q.Id) > M(85));
        Assert.All(q.Soldiers.Where(m => m.CanLead), m => Assert.Equal(Posture.Prone, m.Posture));
    }

    private static int ShotsIn30s(ulong seed, Fixed level)
    {
        var rig = Setup(seed);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        Line(us, 20, 48);
        rig.World.ViewFor("us").Issue(us.Id, new Order(OrderKind.Fire, M(250), M(60)));
        var shots = 0;
        for (var i = 0; i < 300; i++)
        {
            foreach (var s in us.Soldiers)
            {
                rig.Sup.SetLevel(s.Id, level);
            }

            rig.World.Step();
            shots += rig.Fire.Shots.Count;
        }

        return shots;
    }

    [Fact]
    public void A_suppressed_squad_fires_less_often()
    {
        int calm = 0, suppressed = 0, pinned = 0;
        for (ulong seed = 1; seed <= 5; seed++)
        {
            calm += ShotsIn30s(seed, Fixed.Zero);
            suppressed += ShotsIn30s(seed, M(50));
            pinned += ShotsIn30s(seed, M(100));
        }

        output.WriteLine($"shots in 30 s: calm {calm}, level 50 {suppressed}, pinned {pinned}");
        Assert.True(suppressed < calm * 0.85, $"{suppressed} vs {calm}");
        Assert.True(pinned < calm * 0.3, $"{pinned} vs {calm}");
    }

    [Fact]
    public void A_suppressed_firer_is_less_accurate()
    {
        double Rate(Fixed level)
        {
            var hits = 0;
            var shots = 0;
            for (ulong seed = 1; seed <= 10; seed++)
            {
                var rig = Setup(seed);
                var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
                var de = rig.Org.SpawnSquad("de_gruppe", M(120), M(60));
                var firer = us.Soldiers.First(s => s.WeaponId == "us_rifle_sample");
                var target = de.Soldiers[0];
                (firer.X, firer.Y, target.X, target.Y) = (M(20), M(30), M(120), M(30));
                foreach (var s in de.Soldiers.Where(s => s != target))
                {
                    (s.X, s.Y) = (M(200), M(5));
                }

                rig.Sup.SetLevel(firer.Id, level);
                for (var i = 0; i < 300; i++)
                {
                    shots++;
                    hits += rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target).Outcome == ShotOutcome.Hit ? 1 : 0;
                    target.Health = HealthState.Ok;
                }
            }

            return hits / (double)shots;
        }

        var calm = Rate(Fixed.Zero);
        var pinned = Rate(M(100));
        output.WriteLine($"hit rate at 100 m: calm {calm:P1}, level 100 {pinned:P1}");
        Assert.True(pinned < calm * 0.7, $"{pinned} vs {calm}");
    }

    [Fact]
    public void A_suppressed_observer_spots_more_slowly()
    {
        Fixed Progress(Fixed level)
        {
            var rig = Setup();
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
            var de = rig.Org.SpawnSquad("de_gruppe", M(400), M(60), Posture.Prone);
            var o = us.Soldiers[0];
            foreach (var s in us.Soldiers.Where(s => s != o))
            {
                us.SetHealth(s.Id, HealthState.Dead);
            }

            for (var i = 0; i < 20; i++)
            {
                rig.Sup.SetLevel(o.Id, level);
                rig.World.Step();
            }

            return rig.Spot.DebugTruth().Progress(o.Id, de.Soldiers[0].Id);
        }

        var calm = Progress(Fixed.Zero);
        var suppressed = Progress(M(100));
        output.WriteLine($"spotting progress after 2 s: calm {calm}, suppressed {suppressed}");
        Assert.True(calm > Fixed.Zero);
        Assert.True(suppressed < calm * Fixed.FromRatio(1, 2));
    }

    [Fact]
    public void A_pinned_squad_holds_its_advance_and_resumes_when_it_recovers()
    {
        var rig = Setup();
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(200), M(60));
        var view = rig.World.ViewFor("us");
        view.Issue(us.Id, new Order(OrderKind.Move, M(40), M(60)));
        Deliver(rig, us.Id);
        rig.World.Run(30);
        Assert.Equal(OrderStatus.Executing, view.StatusOf(us.Id)!.Status);
        foreach (var s in us.Soldiers)
        {
            rig.Sup.SetLevel(s.Id, M(100));
        }

        rig.World.Run(2);
        Assert.Equal(SuppressionState.Pinned, view.Squad(us.Id)!.Value.Suppression);
        Assert.Equal(OrderStatus.Held, view.StatusOf(us.Id)!.Status);
        Assert.False(view.Squad(us.Id)!.Value.IsMoving);
        var x = us.Soldiers.Select(s => s.X).ToArray();
        rig.World.Run(20);
        Assert.Equal(x, us.Soldiers.Select(s => s.X).ToArray());
        Assert.All(us.Soldiers, s => Assert.Equal(Posture.Prone, s.Posture));

        // A new advance order while pinned is held too; a fall-back is not.
        view.Issue(us.Id, new Order(OrderKind.MoveFast, M(40), M(60)));
        Deliver(rig, us.Id);
        Assert.Equal(OrderStatus.Held, view.StatusOf(us.Id)!.Status);

        // Recovers (decay): the held move starts again.
        rig.World.Run(300);
        Assert.NotEqual(SuppressionState.Pinned, view.Squad(us.Id)!.Value.Suppression);
        Assert.True(view.StatusOf(us.Id)!.Status is OrderStatus.Executing or OrderStatus.Done);
        Assert.True(us.Soldiers.Min(s => s.X) < x.Min() - M(5));

        foreach (var s in us.Soldiers)
        {
            rig.Sup.SetLevel(s.Id, M(100));
        }

        rig.World.Step();
        view.Issue(us.Id, new Order(OrderKind.FallBack, M(280), M(60)));
        Deliver(rig, us.Id);
        Assert.Equal(OrderStatus.Executing, view.StatusOf(us.Id)!.Status);
    }

    /// <summary>Steps until the unit's order has come down the chain of command.</summary>
    private static void Deliver(Rig rig, int unitId)
    {
        rig.World.Step();
        for (var i = 0; i < 300 && rig.World.ViewFor("us").StatusOf(unitId)!.Status is OrderStatus.Pending or OrderStatus.Hesitating; i++)
        {
            rig.World.Step();
        }
    }

    [Fact]
    public void A_pinned_man_crawls()
    {
        Fixed Travel(Fixed level)
        {
            var rig = Setup();
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(200), M(60));
            var s = us.Soldiers[0];
            foreach (var m in us.Soldiers.Where(m => m != s))
            {
                us.SetHealth(m.Id, HealthState.Dead);
            }

            rig.Move.Execute(new MoveOrder(us.Id, M(100), s.Y, MoveMode.Move));
            var x0 = s.X;
            for (var i = 0; i < 50; i++)
            {
                rig.Sup.SetLevel(s.Id, level);
                rig.World.Step();
            }

            return x0 - s.X;
        }

        var calm = Travel(Fixed.Zero);
        var suppressed = Travel(Rules.SuppressedThreshold + M(1));
        output.WriteLine($"5 s travel: calm {calm} m, suppressed {suppressed} m");
        Assert.True(calm > M(3));
        Assert.True(suppressed < calm);
    }

    [Fact]
    public void Side_view_shows_own_suppression_only()
    {
        var rig = Setup();
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(60), M(60));
        foreach (var s in us.Soldiers)
        {
            rig.Sup.SetLevel(s.Id, M(70));
        }

        rig.World.Step();
        var view = rig.World.ViewFor("us");
        var squad = view.Squad(us.Id)!.Value;
        Assert.Equal(SuppressionState.Pinned, squad.Suppression);
        Assert.True(squad.SuppressionLevel > M(60));
        Assert.All(squad.Soldiers, s => Assert.True(s.Suppression > M(60) && s.SuppressionState == SuppressionState.Pinned));
        Assert.Null(view.Squad(de.Id));
        Assert.DoesNotContain(typeof(ContactView).GetProperties(), p => p.Name.Contains("Suppress", StringComparison.Ordinal));
    }

    [Fact]
    public void Suppression_is_deterministic_and_hashed()
    {
        ulong Run(Action<Rig>? poke)
        {
            var rig = Setup(5);
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
            rig.World.Step();
            poke?.Invoke(rig);
            rig.World.Step();
            _ = us;
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Run(null), Run(null));
        Assert.NotEqual(Run(null), Run(r => r.Sup.SetLevel(r.Org.Squads[0].Soldiers[3].Id, M(10))));
    }
}
