using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Tests.Movement;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Fire;

/// <summary>Follow-ups from the item 1.7 review (tested in item 2.1).</summary>
public class FireFollowUpTests(ITestOutputHelper output)
{
    private static readonly DataSet Data = PathFinderTests.Data;
    private static readonly FireRules Rules = FireRules.FromData(Data);
    private static readonly SightRules Sight = SightRules.FromData(Data);

    private static Fixed M(int metres) => Fixed.FromInt(metres);

    private sealed record Rig(World World, OrgSystem Org, SpottingSystem Spot, FireSystem Fire);

    private static Rig Setup(GameMap? map = null, FireRules? rules = null, SpottingRules? spotting = null, ulong seed = 1)
    {
        map ??= new GameMap("open", 300, 60);
        var org = new OrgSystem(OrgTables.FromData(Data));
        var move = new MovementSystem(map, org, PathFinderTests.Rules);
        var spot = new SpottingSystem(map, org, move, new LineOfSight(map, Sight), spotting ?? SpottingRules.FromData(Data));
        var fire = new FireSystem(map, org, Sight, rules ?? Rules, spot);
        var command = new CommandSystem(org, move, CommandRules.FromData(Data), fire);
        return new Rig(new World(seed, new MapSystem(map), org, command, move, spot, fire), org, spot, fire);
    }

    /// <summary>Kills everyone in the squad but <paramref name="keep"/>.</summary>
    private static void Only(Squad q, params Soldier[] keep)
    {
        foreach (var s in q.Soldiers.Where(s => !keep.Contains(s)))
        {
            q.SetHealth(s.Id, HealthState.Dead);
        }
    }

    [Fact]
    public void A_soldier_holds_fire_while_a_friendly_is_in_his_line_of_fire()
    {
        int Shots(bool masked)
        {
            var rig = Setup();
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
            var firer = us.Soldiers.First(s => s.WeaponId == "us_rifle_sample");
            var friend = us.Soldiers.First(s => s != firer);
            Only(us, firer, friend);
            (firer.X, firer.Y) = (M(20), M(60));
            (friend.X, friend.Y) = masked ? (M(60), M(60) + Fixed.FromRatio(1, 2)) : (M(20), M(70));
            rig.World.ViewFor("us").Issue(us.Id, new Order(OrderKind.Fire, M(200), M(60)));
            var n = 0;
            for (var i = 0; i < 200; i++)
            {
                rig.World.Step();
                n += rig.Fire.Shots.Count(s => s.FirerId == firer.Id);
            }

            return n;
        }

        Assert.True(Shots(masked: false) > 0);
        Assert.Equal(0, Shots(masked: true));
    }

    [Fact]
    public void Shots_that_miss_the_target_can_hit_a_soldier_behind_it()
    {
        var rig = Setup(rules: Rules with { KillChance = Fixed.Zero, IncapacitateChance = Fixed.Zero });
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(80), M(60));
        var firer = us.Soldiers.First(s => s.WeaponId == "us_rifle_sample");
        var behind = us.Soldiers.First(s => s != firer);
        var target = de.Soldiers[0];
        Only(us, firer, behind);
        Only(de, target);
        (firer.X, firer.Y, target.X, target.Y, behind.X, behind.Y) = (M(20), M(30), M(80), M(30), M(100), M(30) + Fixed.FromRatio(6, 10)); // just off the target's line
        var hitBehind = 0;
        for (var i = 0; i < 2000; i++)
        {
            var shot = rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target);
            hitBehind += shot.Outcome == ShotOutcome.Hit && shot.HitSoldierId == behind.Id ? 1 : 0;
            target.Health = behind.Health = HealthState.Ok;
        }

        output.WriteLine($"friendly hits behind the target: {hitBehind} / 2000");
        Assert.True(hitBehind > 0);
    }

    [Fact]
    public void A_window_lets_shots_through_where_a_solid_wall_stops_them()
    {
        List<ShotEvent> Fire(bool window)
        {
            var map = new GameMap("open", 300, 60);
            Opening[] openings = window
                ? [new(new TilePos(30, 30), WallSide.West, OpeningKind.Window, 0), new(new TilePos(30, 30), WallSide.East, OpeningKind.Window, 0)]
                : [];
            map.AddBuilding(new Building("b", "brick_wall_sample", 2, [new TilePos(30, 29), new TilePos(30, 30), new TilePos(30, 31)], openings));
            var rig = Setup(map);
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(61));
            var de = rig.Org.SpawnSquad("de_gruppe", M(80), M(61));
            var firer = us.Soldiers.First(s => s.WeaponId == "us_rifle_sample");
            var target = de.Soldiers[0];
            Only(us, firer);
            Only(de, target);
            (firer.X, firer.Y, target.X, target.Y) = (M(20), M(61), M(80), M(61));
            var shots = new List<ShotEvent>();
            for (var i = 0; i < 300; i++)
            {
                shots.Add(rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target));
                target.Health = HealthState.Ok;
            }

            return shots;
        }

        var solid = Fire(window: false);
        var open = Fire(window: true);
        output.WriteLine($"wall stops: solid {solid.Count(s => s.Outcome == ShotOutcome.Wall)}, window {open.Count(s => s.Outcome == ShotOutcome.Wall)}; hits through window {open.Count(s => s.Outcome == ShotOutcome.Hit)}");
        Assert.DoesNotContain(solid, s => s.Outcome == ShotOutcome.Hit);
        Assert.Contains(open, s => s.Outcome == ShotOutcome.Hit);
        Assert.True(open.Count(s => s.Outcome == ShotOutcome.Wall) < solid.Count(s => s.Outcome == ShotOutcome.Wall));
    }

    [Fact]
    public void A_machine_gun_fires_in_bursts_and_reloads_an_empty_belt()
    {
        var rig = Setup();
        var de = rig.Org.SpawnSquad("de_gruppe", M(20), M(60));
        var mg = de.Soldiers.First(s => s.WeaponId == "de_mg_sample");
        Only(de, mg);
        (mg.X, mg.Y) = (M(20), M(60));
        var w = Rules.Weapon("de_mg_sample");
        rig.World.ViewFor("de").Issue(de.Id, new Order(OrderKind.Fire, M(300), M(60)));
        var ticks = new List<long>();
        for (var i = 0; i < 300; i++)
        {
            rig.World.Step();
            ticks.AddRange(rig.Fire.Shots.Where(s => s.FirerId == mg.Id).Select(s => s.Tick));
        }

        Assert.True(ticks.Count > w.MagazineCapacity + w.BurstLength);
        var gaps = ticks.Zip(ticks.Skip(1), (a, b) => b - a).ToList();
        var maxPerTick = (int)((w.RoundsPerMinute.ToInt() / 600) + 1);

        // Within a burst shots come at the cyclic rate; after each full burst there is at least the burst pause.
        for (var i = 0; i < gaps.Count; i++)
        {
            var shotNo = i + 1; // shots fired before the gap
            if (shotNo % w.MagazineCapacity == 0)
            {
                Assert.True(gaps[i] >= w.ReloadTicks, $"reload gap {gaps[i]} after shot {shotNo}");
            }
            else if (shotNo % w.MagazineCapacity % w.BurstLength == 0)
            {
                Assert.True(gaps[i] >= w.BurstPauseTicks, $"burst gap {gaps[i]} after shot {shotNo}");
            }
            else
            {
                Assert.True(gaps[i] <= 1, $"gap {gaps[i]} inside a burst after shot {shotNo}");
            }
        }

        Assert.All(ticks.GroupBy(t => t), g => Assert.True(g.Count() <= maxPerTick));
        Assert.Equal(w.CarriedRounds - ticks.Count, rig.Fire.RoundsOf(mg.Id));
    }

    [Fact]
    public void A_squad_with_mixed_ammo_reads_partial_then_out_of_ammo()
    {
        var rules = Rules.WithWeapon("us_rifle_sample", w => w with { CarriedRounds = 2 }).WithWeapon("us_bar_sample", w => w with { CarriedRounds = 40 });
        var rig = Setup(rules: rules);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        for (var i = 0; i < us.Soldiers.Count; i++)
        {
            (us.Soldiers[i].X, us.Soldiers[i].Y) = (M(20), M(48 + (2 * i)));
        }

        var view = rig.World.ViewFor("us");
        view.Issue(us.Id, new Order(OrderKind.Fire, M(200), M(60)));
        var sawPartial = false;
        for (var i = 0; i < 1500 && view.Squad(us.Id)!.Value.Ammo != AmmoState.OutOfAmmo; i++)
        {
            rig.World.Step();
            if (view.Squad(us.Id)!.Value.Ammo == AmmoState.Partial)
            {
                sawPartial = true;
                Assert.Equal(OrderStatus.Executing, view.StatusOf(us.Id)!.Status); // the BARs keep firing
            }
        }

        rig.World.Run(2); // the order ends on the fire tick; the ticket reads it on the next
        Assert.True(sawPartial);
        Assert.Equal(AmmoState.OutOfAmmo, view.Squad(us.Id)!.Value.Ammo);
        Assert.Equal(OrderNote.OutOfAmmo, view.StatusOf(us.Id)!.Note);
    }

    [Fact]
    public void The_near_miss_radius_comes_from_data()
    {
        Assert.Equal(Data.Table("fire").Entries.First(e => e.Id == "default").Get("near_miss_radius").Number!.Value, Rules.NearMissRadius);
        int Misses(Fixed radius)
        {
            var rig = Setup(rules: Rules with { NearMissRadius = radius });
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
            var de = rig.Org.SpawnSquad("de_gruppe", M(150), M(60));
            var firer = us.Soldiers[0];
            var target = de.Soldiers[0];
            Only(us, firer);
            Only(de, target);
            (firer.X, firer.Y, target.X, target.Y) = (M(20), M(30), M(150), M(30));
            var n = 0;
            for (var i = 0; i < 300; i++)
            {
                rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target);
                target.Health = HealthState.Ok;
            }

            Assert.All(rig.Fire.NearMisses, m => Assert.True(m.Distance <= radius));
            n = rig.Fire.NearMisses.Count;
            return n;
        }

        Assert.True(Misses(Fixed.FromRatio(1, 2)) < Misses(M(5)));
    }

    [Fact]
    public void Fire_at_a_contact_out_of_sight_is_area_fire_and_reveals_no_unseen_death()
    {
        var rig = Setup(spotting: SpottingRules.FromData(Data) with { MaxRange = M(150) });
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(120), M(60));
        var target = de.Soldiers[0];
        Only(de, target);
        for (var i = 0; i < us.Soldiers.Count; i++)
        {
            (us.Soldiers[i].X, us.Soldiers[i].Y) = (M(20), M(48 + (2 * i)));
        }

        (target.X, target.Y) = (M(120), M(60));
        var view = rig.World.ViewFor("us");
        for (var i = 0; i < 600 && view.KnownEnemies.Count == 0; i++)
        {
            rig.World.Step();
        }

        var contact = view.KnownEnemies.Single();
        view.Issue(us.Id, new Order(OrderKind.Fire, contact.X, contact.Y, contact.EnemyId));
        for (var i = 0; i < 100 && view.StatusOf(us.Id)!.Status != OrderStatus.Executing; i++)
        {
            rig.World.Step();
        }

        Assert.Equal(OrderStatus.Executing, view.StatusOf(us.Id)!.Status);
        (target.X, target.Y) = (M(250), M(20)); // out of spotting range, unseen
        for (var i = 0; i < 100 && view.KnownEnemies.Single().Quality == ContactQuality.Tracked; i++)
        {
            rig.World.Step();
        }

        var stale = view.KnownEnemies.Single();
        Assert.NotEqual(ContactQuality.Tracked, stale.Quality);
        de.SetHealth(target.Id, HealthState.Dead); // dies where the side cannot see
        for (var i = 0; i < 50; i++)
        {
            rig.World.Step();
            Assert.All(rig.Fire.Shots, s => Assert.Equal(-1, s.TargetId)); // area fire at the last-known point
        }

        Assert.Equal(OrderStatus.Executing, view.StatusOf(us.Id)!.Status);
        Assert.Equal(OrderNote.None, view.StatusOf(us.Id)!.Note);
    }
}
