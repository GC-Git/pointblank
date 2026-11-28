using PointBlank.Sim.Artillery;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim.Tests.Boundary;

/// <summary>
/// Item 2.13: what a side may see of fire, shell bursts and enemy casualties. Each rule is checked against the truth,
/// tick by tick, in the playable scenarios.
/// </summary>
public class SideViewEventTests
{
    private static float F(Fixed v) => v.Raw / 4294967296f;

    private static bool Near(Fixed ax, Fixed ay, Fixed bx, Fixed by, float metres) =>
        MathF.Abs(F(ax - bx)) <= metres && MathF.Abs(F(ay - by)) <= metres;

    private sealed class Tally
    {
        public int OwnShots, EnemyShots, EnemyEnds, HiddenEnemyShots, Bursts, UnseenBursts, Fallen, UnknownFallen;
    }

    private static Tally Run(string scenario, ulong seed, int ticks)
    {
        var battle = PlayerBattle.Create(scenario, seed, playOwnScript: true);
        var (world, view) = (battle.World, battle.View);
        var org = world.GetSystem<OrgSystem>();
        var fire = world.GetSystem<FireSystem>();
        var spotting = world.GetSystem<SpottingSystem>();
        var artillery = world.FindSystem<ArtillerySystem>();
        var tally = new Tally();
        for (var t = 0; t < ticks; t++)
        {
            world.Step();
            var men = org.Squads.SelectMany(q => q.Soldiers.Select(s => (q.Side, Man: s))).ToDictionary(p => p.Man.Id);
            var own = men.Values.Where(m => m.Side == "us" && m.Man.CanLead).Select(m => m.Man).ToArray();
            bool SeenByOwn(Fixed x, Fixed y, Posture p) => own.Any(s => spotting.Sees(s, x, y, p));

            // Shots: every own shot, and an enemy shot only from a man the side is tracking right now.
            var truth = fire.Shots.ToArray();
            var seen = view.SeenShots;
            var expected = truth.Where(s => men[s.FirerId].Side == "us"
                || spotting.ContactOf("us", s.FirerId) is { Quality: ContactQuality.Tracked }).ToArray();
            Assert.Equal(expected.Length, seen.Count);
            tally.HiddenEnemyShots += truth.Length - expected.Length;
            foreach (var (shot, got) in expected.Zip(seen))
            {
                var mine = men[shot.FirerId].Side == "us";
                Assert.Equal(mine, got.Own);
                Assert.Equal(mine ? shot.FirerId : -1, got.FirerId);
                if (mine)
                {
                    tally.OwnShots++;
                    Assert.Equal((shot.OriginX, shot.OriginY), (got.X, got.Y));
                }
                else
                {
                    // Where the side knows him to be, never the live origin.
                    tally.EnemyShots++;
                    var contact = spotting.ContactOf("us", shot.FirerId)!.Value;
                    Assert.Equal((contact.X, contact.Y), (got.X, got.Y));
                }

                if (!got.HasEnd)
                {
                    Assert.Equal((Fixed.Zero, Fixed.Zero), (got.EndX, got.EndY));
                    continue;
                }

                tally.EnemyEnds += mine ? 0 : 1;
                Assert.Equal((shot.EndX, shot.EndY), (got.EndX, got.EndY));
                Assert.True(SeenByOwn(shot.EndX, shot.EndY, Posture.Prone)
                    || (shot.HitSoldierId >= 0 && men[shot.HitSoldierId].Side == "us"), "an end nobody of the side saw");
                if (shot.HitSoldierId >= 0 && men[shot.HitSoldierId].Side != "us")
                {
                    Assert.Equal(ContactQuality.Tracked, spotting.ContactOf("us", shot.HitSoldierId)?.Quality);
                }
            }

            // Bursts: only felt (within its radius of an own able man) or in an own man's sight.
            var bursts = artillery?.LastImpacts ?? [];
            var seenBursts = view.SeenBursts;
            Assert.All(seenBursts, b => Assert.Contains(bursts, i => i.X == b.X && i.Y == b.Y));
            foreach (var i in bursts)
            {
                var felt = own.Any(s => ((s.X - i.X) * (s.X - i.X)) + ((s.Y - i.Y) * (s.Y - i.Y)) <= i.Radius * i.Radius);
                if (seenBursts.Any(b => b.X == i.X && b.Y == i.Y))
                {
                    tally.Bursts++;
                    Assert.True(felt || own.Any(s => spotting.SeesRaised(s, i.X, i.Y, i.Plume)), "a burst nobody of the side saw or felt");
                }
                else
                {
                    tally.UnseenBursts++;
                    Assert.False(felt, "a burst on the side's own men is missing");
                }
            }

            // Enemy casualties: only a man the side was tracking, whose body one of its men can see.
            foreach (var f in view.SeenCasualties)
            {
                tally.Fallen++;
                var (side, man) = men[f.EnemyId];
                Assert.NotEqual("us", side);
                Assert.False(man.CanLead);
                Assert.Equal(ContactQuality.Tracked, spotting.ContactOf("us", man.Id)?.Quality);
                Assert.Equal((man.X, man.Y), (f.X, f.Y));
                Assert.True(SeenByOwn(man.X, man.Y, Posture.Prone), "a body nobody of the side can see");
            }

            tally.UnknownFallen += men.Values.Count(m => m.Side != "us" && !m.Man.CanLead && spotting.ContactOf("us", m.Man.Id) == null);
        }

        return tally;
    }

    [Fact]
    public void Fix_assault_shows_own_fire_and_only_tracked_enemies_fire_and_casualties()
    {
        var t = Run("fix-assault", 1, 1200);
        Assert.True(t.OwnShots > 0 && t.EnemyShots > 0 && t.HiddenEnemyShots > 0, $"{t.OwnShots} own, {t.EnemyShots} enemy, {t.HiddenEnemyShots} hidden");
        Assert.True(t.Fallen > 0, "no enemy was ever seen to fall");
    }

    [Fact]
    public void Bombard_creep_shows_only_bursts_seen_or_felt()
    {
        var t = Run("bombard-creep", 1, 1200);
        Assert.True(t.Bursts > 0, $"{t.Bursts} bursts seen, {t.UnseenBursts} not");
    }

    [Fact]
    public void A_burst_out_of_sight_and_far_from_every_man_is_not_shown()
    {
        var (world, org, artillery) = Burst();
        org.SpawnSquad("us_rifle_squad", Fixed.FromInt(390), Fixed.FromInt(30));
        world.Step();
        var view = world.ViewFor("us");
        var battery = artillery.Rules.For("us")!;
        artillery.Burst(world, battery, Fixed.FromInt(203), Fixed.FromInt(30));
        Assert.Empty(view.SeenBursts); // beyond sight and beyond anyone who could feel it
        artillery.Burst(world, battery, Fixed.FromInt(300), Fixed.FromInt(30));
        Assert.Equal(Fixed.FromInt(300), Assert.Single(view.SeenBursts).X);
        Assert.Empty(world.ViewFor("de").SeenBursts); // no German is there to see or feel it
    }

    private static (World, OrgSystem, ArtillerySystem) Burst()
    {
        var data = Movement.PathFinderTests.Data;
        var map = new PointBlank.Sim.Map.GameMap("open", 200, 60);

        var org = new OrgSystem(PointBlank.Sim.Org.OrgTables.FromData(data));
        var move = new PointBlank.Sim.Movement.MovementSystem(map, org, Movement.PathFinderTests.Rules);
        var command = new PointBlank.Sim.Orders.CommandSystem(org, move, PointBlank.Sim.Orders.CommandRules.FromData(data));
        var los = new LineOfSight(map, SightRules.FromData(data));
        var spot = new SpottingSystem(map, org, move, los, SpottingRules.FromData(data) with { MaxRange = Fixed.FromInt(100) });
        var rules = ArtilleryRules.FromData(data);
        var artillery = new ArtillerySystem(map, org, los, rules, command);
        return (new World(1, new PointBlank.Sim.Map.MapSystem(map), org, command, move, artillery, spot), org, artillery);
    }

    [Theory]
    [InlineData(ContactQuality.Stale)]
    [InlineData(ContactQuality.Lost)]
    public void A_shooter_the_side_is_no_longer_tracking_is_not_shown(ContactQuality quality)
    {
        var battle = PlayerBattle.Create("fix-assault", 1, playOwnScript: true);
        var spotting = battle.World.GetSystem<SpottingSystem>();
        for (var t = 0; t < 1800 && !battle.View.SeenShots.Any(s => !s.Own); t++)
        {
            battle.Step();
        }

        var before = battle.View.SeenShots;
        Assert.Contains(before, s => !s.Own);

        // An enemy's round starts where the side believes he is, even if that is not where he is.
        var contacts = spotting.Sides.Single(k => k.Side == "us").Contacts.Values;
        foreach (var contact in contacts)
        {
            contact.X += Fixed.FromInt(5);
        }

        var believed = contacts.Select(k => (k.X, k.Y)).ToHashSet();
        Assert.All(battle.View.SeenShots.Where(s => !s.Own), s => Assert.Contains((s.X, s.Y), believed));

        // The same tick, with every enemy contact gone cold (edited directly: the open field seldom does it by itself).
        foreach (var contact in contacts)
        {
            contact.Quality = quality;
        }

        Assert.Equal(before.Where(s => s.Own).Select(s => s.FirerId), battle.View.SeenShots.Select(s => s.FirerId));
        Assert.Empty(battle.View.SeenCasualties);
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("bombard-creep")]
    public void More_seeds_hold_the_rules(string scenario)
    {
        for (var seed = 2UL; seed <= 4; seed++)
        {
            Run(scenario, seed, 1800);
        }
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("bombard-creep")]
    public void Reading_the_events_changes_nothing(string scenario)
    {
        var quiet = PlayerBattle.Create(scenario, 3, playOwnScript: true);
        var read = PlayerBattle.Create(scenario, 3, playOwnScript: true);
        for (var t = 0; t < 900; t++)
        {
            quiet.Step();
            read.Step();
            _ = (read.View.SeenShots, read.View.SeenBursts, read.View.SeenCasualties);
            
            Assert.Equal(quiet.World.ComputeStateHash(), read.World.ComputeStateHash());
        }
    }
}
