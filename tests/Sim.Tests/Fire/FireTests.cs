using System.Diagnostics;
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

/// <summary>Item 1.7: small-arms fire.</summary>
public class FireTests(ITestOutputHelper output)
{
    private static readonly DataSet Data = PathFinderTests.Data;
    private static readonly FireRules Rules = FireRules.FromData(Data);
    private static readonly SightRules Sight = SightRules.FromData(Data);

    private static Fixed M(int metres) => Fixed.FromInt(metres);

    private sealed record Rig(World World, GameMap Map, OrgSystem Org, MovementSystem Move, CommandSystem Command, SpottingSystem Spot, FireSystem Fire);

    private static Rig Setup(GameMap? map = null, FireRules? rules = null, SpottingRules? spotting = null, ulong seed = 1)
    {
        map ??= new GameMap("open", 300, 60);
        var org = new OrgSystem(OrgTables.FromData(Data));
        var move = new MovementSystem(map, org, PathFinderTests.Rules);
        var los = new LineOfSight(map, Sight);
        var spot = new SpottingSystem(map, org, move, los, spotting ?? SpottingRules.FromData(Data));
        var fire = new FireSystem(map, org, Sight, rules ?? Rules, spot);
        var command = new CommandSystem(org, move, CommandRules.FromData(Data), fire);
        var world = new World(seed, new MapSystem(map), org, command, move, spot, fire);
        return new Rig(world, map, org, move, command, spot, fire);
    }

    /// <summary>Puts the squad's men in a north-south line, 2 m apart, so none masks another's fire east or west.</summary>
    private static void Line(Squad q, int x, int y0)
    {
        for (var i = 0; i < q.Soldiers.Count; i++)
        {
            q.Soldiers[i].X = M(x);
            q.Soldiers[i].Y = M(y0 + (2 * i));
        }
    }

    /// <summary>A lone US rifleman at (20, 60) and a lone German at (20 + range, 60); everyone else is dead.</summary>
    private static (Rig Rig, Soldier Firer, Soldier Target) Duel(int range, ulong seed, GameMap? map = null, Posture posture = Posture.Standing, FireRules? rules = null)
    {
        var rig = Setup(map, rules, seed: seed);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60), posture);
        var de = rig.Org.SpawnSquad("de_gruppe", M(20 + range), M(60), posture);
        var firer = us.Soldiers.First(s => s.WeaponId == "us_rifle_sample");
        var target = de.Soldiers.First(s => s.WeaponId == "de_rifle_sample");
        foreach (var (q, keep) in new[] { (us, firer), (de, target) })
        {
            foreach (var s in q.Soldiers.Where(s => s != keep))
            {
                q.SetHealth(s.Id, HealthState.Dead);
            }
        }

        firer.X = M(20);
        firer.Y = M(61);
        target.X = M(20 + range);
        target.Y = M(61);
        return (rig, firer, target);
    }

    /// <summary>Shots at the target over several seeds; the target is healed after each so he stays in place.</summary>
    private static List<ShotEvent> Volley(int range, int seeds, int perSeed, GameMap? map = null, Posture posture = Posture.Standing, Action<GameMap>? paint = null)
    {
        var shots = new List<ShotEvent>();
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var m = map ?? new GameMap("open", 300, 60);
            paint?.Invoke(m);
            var (rig, firer, target) = Duel(range, seed, m, posture);
            for (var i = 0; i < perSeed; i++)
            {
                shots.Add(rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target));
                target.Health = HealthState.Ok;
            }
        }

        return shots;
    }

    private static double HitRate(List<ShotEvent> shots, int targetId) =>
        shots.Count(s => s.Outcome == ShotOutcome.Hit && s.HitSoldierId == targetId) / (double)shots.Count;

    [Fact]
    public void Rules_come_from_the_data_file()
    {
        Assert.Equal(M(2), Rules.NearMissRadius);
        Assert.Equal(Fixed.FromRatio(8, 10), Rules.HedgeBlockChance); // tuned in item 2.2
        Assert.Equal(Fixed.FromRatio(17, 10), Rules.BodyHeight(Posture.Standing));
        var rifle = Rules.Weapon("us_rifle_sample");
        Assert.Equal(80, rifle.CarriedRounds);
        Assert.Equal(8, rifle.MagazineCapacity);
        Assert.Equal(30, rifle.ReloadTicks);
        Assert.Equal(M(800), rifle.MaxRange);
        Assert.Equal(Fixed.FromRatio(25, 10_000), rifle.Dispersion); // 2.5 mrad in radians
        Assert.Equal(CalibreClass.Rifle, rifle.Calibre);
        Assert.Equal(8, Rules.Weapon("de_mg_sample").BurstLength);

        Fixed Sigma(Posture p = Posture.Standing, bool moving = false, int range = 100) =>
            Rules.Sigma(rifle, p, moving, M(range), Fixed.One, Fixed.One);
        Assert.True(Sigma(moving: true) > Sigma());
        Assert.True(Sigma(Posture.Prone) < Sigma(Posture.Kneeling));
        Assert.True(Sigma(Posture.Kneeling) < Sigma());
        Assert.True(Sigma(range: 300) > Sigma(range: 50));
        Assert.True(Rules.Sigma(rifle, Posture.Standing, false, M(100), Fixed.FromInt(2), Fixed.One) > Sigma()); // the suppression hook (2.1)
    }

    [Fact]
    public void Hit_rate_falls_with_range()
    {
        var targetIds = new Dictionary<int, double>();
        foreach (var range in new[] { 50, 150, 300 })
        {
            var (_, _, target) = Duel(range, 1);
            targetIds[range] = HitRate(Volley(range, 5, 400), target.Id);
        }

        output.WriteLine($"hit rate 50 m {targetIds[50]:P1}, 150 m {targetIds[150]:P1}, 300 m {targetIds[300]:P1}");
        Assert.True(targetIds[50] > targetIds[150] + 0.1);
        Assert.True(targetIds[150] > targetIds[300] + 0.05);
        Assert.True(targetIds[300] > 0);
    }

    [Fact]
    public void Hit_rate_falls_with_target_cover()
    {
        var target = Duel(100, 1).Target;
        var open = HitRate(Volley(100, 5, 400, posture: Posture.Prone), target.Id);
        var woods = HitRate(Volley(100, 5, 400, posture: Posture.Prone, paint: m => m.SetTerrain(60, 30, TerrainType.LightWoods)), target.Id);
        var covered = Volley(100, 1, 400, posture: Posture.Prone, paint: m => m.SetTerrain(60, 30, TerrainType.LightWoods));
        output.WriteLine($"hit rate at 100 m, prone: open (cover {Rules.Cover(TerrainType.Open)}) {open:P1}, light woods (cover {Rules.Cover(TerrainType.LightWoods)}) {woods:P1}");
        Assert.True(woods < open - 0.1);
        Assert.Contains(covered, s => s.Outcome == ShotOutcome.Cover);
    }

    [Fact]
    public void Hedges_block_shots_with_the_configured_probability()
    {
        void Hedge(GameMap m)
        {
            for (var y = 0; y < m.Height; y++)
            {
                m.SetHedge(24, y, EdgeSide.East, true); // the edge at x = 50 m
            }
        }

        var shots = Volley(60, 10, 400, posture: Posture.Prone, paint: Hedge);
        var reached = shots.Where(s => s.HedgeRolls > 0).ToList();
        var rate = reached.Count(s => s.Outcome == ShotOutcome.Hedge) / (double)reached.Count;
        output.WriteLine($"hedge block rate {rate:P1} over {reached.Count} shots (configured {Rules.HedgeBlockChance})");
        Assert.True(reached.Count > 3000);
        var p = Rules.HedgeBlockChance.Raw / 4294967296.0;
        Assert.InRange(rate, p - 0.05, p + 0.05);
    }

    [Fact]
    public void A_building_wall_stops_shots()
    {
        void Building(GameMap m) => m.AddBuilding(new Building("b", "brick_wall_sample", 2, [new TilePos(30, 29), new TilePos(30, 30), new TilePos(30, 31)], []));
        var shots = Volley(60, 1, 200, paint: Building);
        Assert.True(shots.Count(s => s.Outcome == ShotOutcome.Wall) > 190);
        Assert.DoesNotContain(shots, s => s.Outcome == ShotOutcome.Hit);
    }

    [Fact]
    public void Shots_passing_close_to_soldiers_of_either_side_are_near_misses()
    {
        var (rig, firer, target) = Duel(150, 3);
        var friend = rig.Org.FindSquadOf(target.Id)!.Soldiers.First(s => s != target);
        rig.Org.FindSquadOf(target.Id)!.SetHealth(friend.Id, HealthState.Ok);
        friend.X = target.X;
        friend.Y = target.Y + Fixed.One;
        for (var i = 0; i < 100; i++)
        {
            rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target);
            target.Health = friend.Health = HealthState.Ok;
        }

        var misses = rig.Fire.NearMisses;
        Assert.Contains(misses, n => n.SoldierId == friend.Id);
        Assert.Contains(misses, n => n.SoldierId == target.Id);
        Assert.DoesNotContain(misses, n => n.SoldierId == firer.Id);
        Assert.All(misses, n =>
        {
            Assert.Equal(firer.Id, n.FirerId);
            Assert.Equal(CalibreClass.Rifle, n.Calibre);
            Assert.True(n.Distance <= Rules.NearMissRadius);
            Assert.Equal(firer.X, n.OriginX);
        });
    }

    [Fact]
    public void A_hit_wounds_or_kills_and_succession_reacts()
    {
        var rig = Setup(rules: Rules with { KillChance = Fixed.One, IncapacitateChance = Fixed.Zero });
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(30), M(60));
        var firer = us.Soldiers[0];
        var leader = de.Leader!;
        (firer.X, firer.Y, leader.X, leader.Y) = (M(20), M(40), M(30), M(40));
        for (var i = 0; i < 50 && leader.Health == HealthState.Ok; i++)
        {
            rig.Fire.Shoot(rig.World, firer, leader.X, leader.Y, leader);
        }

        Assert.Equal(HealthState.Dead, leader.Health);
        Assert.Null(de.Leader);
        Assert.True(de.TicksToSuccession > 0);
    }

    [Fact]
    public void Wounds_follow_the_placeholder_odds_and_a_second_wound_incapacitates()
    {
        var rules = Rules with { KillChance = Fixed.Zero, IncapacitateChance = Fixed.Zero };
        var (rig, firer, target) = Duel(10, 2, rules: rules);
        var seen = new List<HealthState>();
        for (var i = 0; i < 100 && target.Health != HealthState.Incapacitated; i++)
        {
            if (rig.Fire.Shoot(rig.World, firer, target.X, target.Y, target).Outcome == ShotOutcome.Hit)
            {
                seen.Add(target.Health);
            }
        }

        Assert.Equal([HealthState.Wounded, HealthState.Incapacitated], seen);
    }

    [Fact]
    public void A_fire_order_at_a_known_contact_fires_uses_ammo_and_makes_the_firer_easier_to_spot()
    {
        var rig = Setup();
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        Line(us, 20, 48);
        rig.Org.SpawnSquad("de_gruppe", M(140), M(60));
        var view = rig.World.ViewFor("us");
        for (var i = 0; i < 300 && view.KnownEnemies.Count == 0; i++)
        {
            rig.World.Step();
        }

        var contact = view.KnownEnemies[0];
        var before = view.Squad(us.Id)!.Value.Soldiers.Sum(s => s.Rounds);
        Assert.Equal(IssueResult.Accepted, view.Issue(us.Id, new Order(OrderKind.Fire, contact.X, contact.Y, contact.EnemyId)));
        var shots = 0;
        for (var i = 0; i < 300; i++)
        {
            rig.World.Step();
            shots += rig.Fire.Shots.Count;
            Assert.All(rig.Fire.Shots, s => Assert.Equal(contact.EnemyId, s.TargetId));
        }

        var status = view.StatusOf(us.Id)!;
        var target = rig.Org.FindSoldier(contact.EnemyId)!;
        output.WriteLine($"{shots} shots in 30 s; order {status.Status} {status.Note}; target {target.Health}");
        Assert.True(shots > 0);
        Assert.True(
            status.Status == OrderStatus.Executing || (status is { Status: OrderStatus.Done, Note: OrderNote.TargetGone } && !target.CanLead),
            $"{status.Status} {status.Note}");
        Assert.True(view.Squad(us.Id)!.Value.Soldiers.Sum(s => s.Rounds) <= before - shots);
        Assert.Contains(us.Soldiers, s => rig.Spot.LastFiredTick(s.Id) > 0);
        Assert.Equal(AmmoState.Ok, view.Squad(us.Id)!.Value.Ammo);
    }

    [Fact]
    public void A_fire_order_at_an_unspotted_enemy_is_rejected()
    {
        var rig = Setup(spotting: SpottingRules.FromData(Data) with { MaxRange = M(100) });
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(400), M(60));
        var view = rig.World.ViewFor("us");
        view.Issue(us.Id, new Order(OrderKind.Fire, M(400), M(60), de.Soldiers[0].Id));
        rig.World.Run(100);
        Assert.Equal(OrderStatus.Rejected, view.StatusOf(us.Id)!.Status);
        Assert.Equal(OrderNote.UnknownTarget, view.StatusOf(us.Id)!.Note);
        Assert.Empty(rig.Fire.Shots);
    }

    [Fact]
    public void A_squad_out_of_ammo_stops_firing_and_reports_it()
    {
        var rules = Rules;
        foreach (var id in new[] { "us_rifle_sample", "us_bar_sample" })
        {
            rules = rules.WithWeapon(id, w => w with { CarriedRounds = 3 });
        }

        var rig = Setup(rules: rules);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
        Line(us, 20, 48);
        var view = rig.World.ViewFor("us");
        view.Issue(us.Id, new Order(OrderKind.Fire, M(200), M(60)));
        var shots = 0;
        for (var i = 0; i < 400; i++)
        {
            rig.World.Step();
            shots += rig.Fire.Shots.Count;
        }

        var squad = view.Squad(us.Id)!.Value;
        Assert.Equal(3 * us.Soldiers.Count, shots);
        Assert.All(squad.Soldiers, s => Assert.Equal(0, s.Rounds));
        Assert.Equal(AmmoState.OutOfAmmo, squad.Ammo);
        Assert.Equal(OrderStatus.Done, view.StatusOf(us.Id)!.Status);
        Assert.Equal(OrderNote.OutOfAmmo, view.StatusOf(us.Id)!.Note);
        rig.World.Run(20);
        Assert.Empty(rig.Fire.Shots);
    }

    [Fact]
    public void Fire_is_deterministic_and_its_state_is_hashed()
    {
        ulong Run(ulong seed)
        {
            var rig = Setup(seed: seed);
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60));
            rig.Org.SpawnSquad("de_gruppe", M(150), M(60));
            rig.World.ViewFor("us").Issue(us.Id, new Order(OrderKind.Fire, M(150), M(60)));
            rig.World.Run(150);
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Run(7), Run(7));
        Assert.NotEqual(Run(7), Run(8));

        var rig = Setup();
        var (a, b) = (rig.Org.SpawnSquad("us_rifle_squad", M(20), M(60)), rig.Org.SpawnSquad("de_gruppe", M(150), M(60)));
        rig.World.Step();
        var h0 = rig.World.ComputeStateHash();
        rig.Fire.Shoot(rig.World, a.Soldiers[0], b.Soldiers[0].X, b.Soldiers[0].Y, b.Soldiers[0]);
        Assert.NotEqual(h0, rig.World.ComputeStateHash());
    }

    [Fact]
    public void Firefight_of_four_squads_a_side_for_60_seconds_fits_the_budget()
    {
        (Rig Rig, List<Squad> Squads) Scene()
        {
            var rig = Setup(new GameMap("open", 300, 120));
            var squads = new List<Squad>();
            for (var i = 0; i < 4; i++)
            {
                squads.Add(rig.Org.SpawnSquad("us_rifle_squad", M(100), M(40 + (i * 50))));
                Line(squads[^1], 100, 10 + (i * 55));
                squads.Add(rig.Org.SpawnSquad("de_gruppe", M(300), M(40 + (i * 50)), Posture.Prone));
                Line(squads[^1], 300, 12 + (i * 55));
            }

            return (rig, squads);
        }

        (long Ms, long WorstMs) Time(World world)
        {
            var clock = Stopwatch.StartNew();
            long worst = 0;
            for (var t = 0; t < 600; t++)
            {
                var start = clock.ElapsedMilliseconds;
                world.Step();
                worst = Math.Max(worst, clock.ElapsedMilliseconds - start);
            }

            return (clock.ElapsedMilliseconds, worst);
        }

        var quiet = Scene();
        var baseline = Time(quiet.Rig.World);

        var (rig, squads) = Scene();
        for (var i = 0; i < squads.Count; i++)
        {
            // Each squad fires at the centre of the enemy line facing it.
            var q = squads[i];
            var x = q.Side == "us" ? M(300) : M(100);
            rig.World.ViewFor(q.Side).Issue(q.Id, new Order(OrderKind.Fire, x, M(22 + (i / 2 * 55))));
        }

        for (var t = 0; t < 600 && squads.Any(q => rig.World.ViewFor(q.Side).StatusOf(q.Id)!.Status != OrderStatus.Executing); t++)
        {
            rig.World.Step(); // deliver every order before timing
        }

        Assert.All(squads, q => Assert.Equal(OrderStatus.Executing, rig.World.ViewFor(q.Side).StatusOf(q.Id)!.Status));
        var shots = 0;
        var clock = Stopwatch.StartNew();
        long worst = 0;
        for (var t = 0; t < 600; t++)
        {
            var start = clock.ElapsedMilliseconds;
            rig.World.Step();
            worst = Math.Max(worst, clock.ElapsedMilliseconds - start);
            shots += rig.Fire.Shots.Count;
        }

        clock.Stop();
        var down = squads.Sum(q => q.Soldiers.Count(s => !s.CanLead));
        output.WriteLine(
            $"{squads.Sum(q => q.Soldiers.Count)} soldiers, {shots} shots, {down} down; 600 ticks in {clock.ElapsedMilliseconds} ms "
            + $"(worst tick {worst} ms); same scene without fire {baseline.Ms} ms (worst {baseline.WorstMs} ms)");
        Assert.True(shots > 2000);
        Assert.True(down > 0);
        Assert.True(clock.ElapsedMilliseconds < 30_000, $"{clock.ElapsedMilliseconds} ms"); // 60 s of battle in under 30 s, even in Debug
    }
}
