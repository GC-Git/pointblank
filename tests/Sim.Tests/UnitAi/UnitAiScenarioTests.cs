using System.Diagnostics;
using PointBlank.Sim.Map;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.UnitAi;
using Xunit.Abstractions;
using static PointBlank.Sim.Tests.UnitAi.AiRig;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>Item 2.3 "Done when", over seeds 1 to 100.</summary>
public class UnitAiScenarioTests(ITestOutputHelper output)
{
    private const int Seeds = 100;

    /// <summary>
    /// An idle US rifle squad standing in the open at x ≈ 250 m and a German Gruppe at x = 100 m, prone, in the open
    /// or (<paramref name="hidden"/>) in a wood. Both sides run the unit AI, nobody has orders. With
    /// <paramref name="advance"/>, the US squad is ordered to Move to x = 120 m at once.
    /// </summary>
    private static (AiRig Rig, Squad Us, Squad De) Contact(ulong seed, bool hidden, bool advance = false, bool noCasualties = false)
    {
        var map = new GameMap("contact", 200, 60);
        if (hidden)
        {
            for (var tx = 40; tx < 56; tx++)
            {
                for (var ty = 0; ty < map.Height; ty++)
                {
                    map.SetTerrain(tx, ty, TerrainType.LightWoods);
                }
            }
        }

        var rig = Create(seed, map, noCasualties: noCasualties);
        var j = Jitter(seed);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(250) + j(10), M(60) + j(8));
        var de = rig.Org.SpawnSquad("de_gruppe", M(100) + j(5), M(60) + j(5), Posture.Prone);
        if (advance)
        {
            rig.Order(us, OrderKind.Move, 120, 60);
        }

        return (rig, us, de);
    }

    private static bool Able(Soldier s) => s.CanLead;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_idle_squad_that_comes_under_fire_takes_cover_or_returns_fire_within_2_s(bool hidden)
    {
        int underFire = 0, reacted = 0, allDown = 0, returned = 0;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            var (rig, us, _) = Contact(seed, hidden);
            long t0 = -1;
            var ok = false;
            for (var i = 0; i < 300 && !ok; i++)
            {
                rig.World.Step();
                if (t0 < 0 && rig.Watch.FirstUnderFire.TryGetValue("us", out var t))
                {
                    t0 = t;
                }

                if (t0 >= 0 && rig.World.Tick > t0 + (2 * World.TicksPerSecond))
                {
                    var down = us.Soldiers.Where(Able).All(s => s.Posture != Posture.Standing);
                    var fired = rig.Watch.FirstShot.TryGetValue("us", out var f) && f <= t0 + (2 * World.TicksPerSecond);
                    allDown += down ? 1 : 0;
                    returned += fired ? 1 : 0;
                    reacted += down || fired ? 1 : 0;
                    ok = true;
                }
            }

            underFire += t0 >= 0 ? 1 : 0;
            Assert.True(t0 < 0 || ok, $"seed {seed}: under fire at {t0} but not checked");
        }

        output.WriteLine($"hidden={hidden}: under fire in {underFire}/{Seeds}; reacted within 2 s {reacted}/{underFire} (all down {allDown}, returned fire {returned})");
        Assert.True(underFire >= 90, $"only {underFire} seeds came under fire");
        Assert.Equal(underFire, reacted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void No_soldier_under_fire_stands_in_the_open_for_more_than_3_s(bool hidden, bool advance)
    {
        var violations = 0;
        var longest = 0;
        var underFire = 0;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            var (rig, _, _) = Contact(seed, hidden, advance, noCasualties: advance);
            rig.World.Run(advance ? 450 : 250);
            violations += rig.Watch.Violations.Count;
            longest = Math.Max(longest, rig.Watch.LongestOpenRun);
            underFire += rig.Watch.FirstUnderFire.ContainsKey("us") ? 1 : 0;
            Assert.True(rig.Watch.Violations.Count == 0, $"seed {seed}: soldier {rig.Watch.Violations.FirstOrDefault()} stood in the open under fire > 3 s");
        }

        output.WriteLine($"hidden={hidden} advance={advance}: US under fire in {underFire}/{Seeds}; violations {violations}; longest open run under fire {longest / (double)World.TicksPerSecond:F1} s");
        Assert.True(underFire >= 90);
    }

    /// <summary>
    /// The US squad idle in an east-west column (the worst case for the hold-fire rule), a German Gruppe kneeling in
    /// the open 150 m west with HoldFire, so it never shoots. Returns the share of able riflemen who fired within
    /// 10 s of the squad's first AI fire task.
    /// </summary>
    private static double FiringLine(ulong seed)
    {
        var map = new GameMap("line", 200, 60);
        var rig = Create(seed, map);
        var j = Jitter(seed);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(250), M(60));
        var y = M(60) + j(5);
        for (var i = 0; i < us.Soldiers.Count; i++)
        {
            us.Soldiers[i].X = M(240 + (2 * i)) + j(1);
            us.Soldiers[i].Y = y;
        }

        var de = rig.Org.SpawnSquad("de_gruppe", M(100) + j(5), M(60) + j(5), Posture.Kneeling);
        rig.Command.SetRoe(de.Id, RoeMode.HoldFire);
        long engaged = -1;
        for (var i = 0; i < 600 && (engaged < 0 || rig.World.Tick <= engaged + 100); i++)
        {
            rig.World.Step();
            if (engaged < 0 && rig.Fire.IsAiFiring(us.Id))
            {
                engaged = rig.World.Tick;
            }
        }

        Assert.True(engaged >= 0, $"seed {seed}: never engaged");
        var riflemen = us.Soldiers.Where(s => s.CanLead && s.Role == Role.Rifleman).ToList();
        return riflemen.Count(s => rig.Fire.LastFiredTick(s.Id) >= engaged) / (double)riflemen.Count;
    }

    [Fact]
    public void When_an_idle_squad_engages_the_leader_forms_a_firing_line_and_most_riflemen_fire_within_10_s()
    {
        var shares = Enumerable.Range(1, Seeds).Select(s => FiringLine((ulong)s)).ToList();
        output.WriteLine($"riflemen firing within 10 s: mean {shares.Average():P0}, min {shares.Min():P0}, seeds >= 75%: {shares.Count(x => x >= 0.75)}/{Seeds}");
        Assert.True(shares.Average() >= 0.75, $"mean {shares.Average():P0}");
        Assert.True(shares.Count(x => x >= 0.75) >= 95, $"{shares.Count(x => x >= 0.75)} seeds");
    }

    [Fact]
    public void Fire_and_movement_one_group_fires_while_the_other_moves()
    {
        var fractions = new List<double>();
        var advanced = new List<double>();
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            var map = new GameMap("fm", 200, 60);
            var rig = Create(seed, map);
            var j = Jitter(seed);
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(330) + j(5), M(60) + j(5));
            var de = rig.Org.SpawnSquad("de_gruppe", M(100) + j(5), M(60) + j(5), Posture.Kneeling);
            rig.Command.SetRoe(de.Id, RoeMode.HoldFire);
            rig.World.Run(10 * World.TicksPerSecond); // spot the Germans
            var x0 = us.Soldiers.Average(s => s.X.ToInt());
            rig.Order(us, OrderKind.Move, 180, 60);
            for (var i = 0; i < 300 && !rig.Move.IsMoving(us.Id); i++)
            {
                rig.World.Step(); // the order travels down the chain of command
            }

            var prev = us.Soldiers.ToDictionary(s => s.Id, s => (s.X, s.Y));
            int advance = 0, both = 0;
            for (var i = 0; i < 900 && rig.Move.IsMoving(us.Id); i++)
            {
                rig.World.Step();
                var moved = us.Soldiers.Where(s => (s.X, s.Y) != prev[s.Id]).Select(s => s.TeamIndex).ToHashSet();
                var fired = us.Soldiers.Where(s => rig.World.Tick - rig.Fire.LastFiredTick(s.Id) <= 10).Select(s => s.TeamIndex).ToHashSet();
                prev = us.Soldiers.ToDictionary(s => s.Id, s => (s.X, s.Y));
                advance++;
                if (moved.Count > 0 && fired.Any(t => !moved.Contains(t)))
                {
                    both++;
                }
            }

            fractions.Add(advance == 0 ? 0 : both / (double)advance);
            advanced.Add(x0 - us.Soldiers.Where(s => s.CanLead).Average(s => s.X.ToInt()));
        }

        output.WriteLine($"fire and movement: a team fired while another moved in {fractions.Average():P0} of advance ticks (min {fractions.Min():P0}); mean advance {advanced.Average():F0} m in 90 s; advances: {string.Join(" ", advanced.Select(a => a.ToString("F0")))}");
        Assert.True(fractions.Average() >= 0.3, $"{fractions.Average():P0}");
        Assert.True(fractions.Count(f => f >= 0.1) >= 90);
        Assert.True(advanced.Average() >= 30, $"advanced {advanced.Average():F0} m"); // bounding is slow: each group covers half the time
    }

    [Fact]
    public void The_leader_sends_the_BAR_team_to_a_hedge_on_the_flank_facing_the_threat()
    {
        var hits = 0;
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var map = new GameMap("task", 200, 60);
            for (var ty = 12; ty < 24; ty++)
            {
                map.SetHedge(119, ty, EdgeSide.East, true); // a hedge on the west edge of tiles x = 120, y = 24..48 m
            }

            var rig = Create(seed, map);
            var us = rig.Org.SpawnSquad("us_rifle_squad", M(250), M(70));
            var de = rig.Org.SpawnSquad("de_gruppe", M(100), M(70), Posture.Kneeling);
            rig.Command.SetRoe(de.Id, RoeMode.HoldFire);
            rig.World.Run(40 * World.TicksPerSecond);
            var bar = us.Soldiers.First(s => s.Role == Role.BarGunner);
            var tx = (bar.X / map.TileSize).ToInt();
            var ty2 = (bar.Y / map.TileSize).ToInt();
            hits += map.HasHedge(tx - 1, ty2, EdgeSide.East) ? 1 : 0;
        }

        output.WriteLine($"BAR at the flank hedge in {hits}/20 seeds");
        Assert.True(hits >= 18, $"{hits}/20");
    }

    [Fact]
    public void The_AI_for_40_squads_stays_within_its_budget()
    {
        var map = new GameMap("perf", 300, 300);
        var rig = Create(1, map, ai: false);
        var ai = new UnitAiSystem(map, rig.Org, rig.Move, rig.Fire, rig.Command, UnitAiRules.FromData(AiRig.Data));
        for (var i = 0; i < 20; i++)
        {
            rig.Org.SpawnSquad("us_rifle_squad", M(400), M(20 + (28 * i)));
            rig.Org.SpawnSquad("de_gruppe", M(150), M(20 + (28 * i)), Posture.Kneeling);
        }

        rig.World.Run(100); // contacts and fire exist
        var sw = new Stopwatch();
        const int Ticks = 100;
        for (var i = 0; i < Ticks; i++)
        {
            rig.World.Step();
            sw.Start();
            ai.Tick(rig.World);
            sw.Stop();
        }

        var ms = sw.Elapsed.TotalMilliseconds / Ticks;
        output.WriteLine($"unit AI, 40 squads: {ms:F2} ms per tick");
        Assert.True(ms < 10, $"{ms:F2} ms per tick (budget 10 ms, a tenth of the 100 ms tick)");
    }
}
