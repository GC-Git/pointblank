using System.Diagnostics;
using PointBlank.Sim.Artillery;
using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.Tests.Data;
using PointBlank.Sim.UnitAi;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Artillery;

/// <summary>Item 2.4: call for fire, missions, dispersion, HE effects and what each side sees.</summary>
public class ArtilleryTests(ITestOutputHelper output)
{
    private static readonly Lazy<DataSet> Data = new(() => DataLoader.LoadDirectory(DataPaths.DataDirectory()));

    private static Fixed M(int metres) => Fixed.FromInt(metres);

    private sealed class Rig
    {
        public Rig(GameMap map, ulong seed = 1, ArtilleryRules? rules = null, bool ai = false)
        {
            var data = Data.Value;
            Map = map;
            Org = new OrgSystem(OrgTables.FromData(data));
            Suppression = new SuppressionSystem(Org, SuppressionRules.FromData(data));
            var move = new MovementSystem(map, Org, MovementRules.FromData(data), Suppression);
            var sight = SightRules.FromData(data);
            var los = new LineOfSight(map, sight);
            var spot = new SpottingSystem(map, Org, move, los, SpottingRules.FromData(data), Suppression);
            var fire = new FireSystem(map, Org, sight, FireRules.FromData(data), spot, Suppression);
            Command = new CommandSystem(Org, move, CommandRules.FromData(data), fire, Suppression);
            Morale = new MoraleSystem(Org, MoraleRules.FromData(data), Suppression, Command, move, spot);
            Arty = new ArtillerySystem(map, Org, los, rules ?? ArtilleryRules.FromData(data), Command, Suppression, Morale);
            var systems = new List<ISimSystem> { new MapSystem(map), Org, Command, move, spot };
            if (ai)
            {
                systems.Add(new UnitAiSystem(map, Org, move, fire, Command, UnitAiRules.FromData(data)));
            }

            systems.AddRange([fire, Arty, Suppression, Morale]);
            World = new World(seed, systems.ToArray());
        }

        public GameMap Map { get; }

        public OrgSystem Org { get; }

        public SuppressionSystem Suppression { get; }

        public CommandSystem Command { get; }

        public MoraleSystem Morale { get; }

        public ArtillerySystem Arty { get; }

        public World World { get; }

        public OrderView Call(string side, int unit, int x, int y)
        {
            var view = World.ViewFor(side);
            Assert.Equal(IssueResult.Accepted, view.Issue(unit, new Order(OrderKind.CallFire, M(x), M(y))));
            for (var i = 0; i < 600; i++)
            {
                World.Step();
                var st = view.StatusOf(unit)!;
                if (st.Status is OrderStatus.Done or OrderStatus.Rejected)
                {
                    return st;
                }
            }

            throw new InvalidOperationException("The call was never delivered.");
        }
    }

    private static BatteryRules Us => ArtilleryRules.FromData(Data.Value).For("us")!;

    [Fact]
    public void The_artillery_table_loads_a_battery_per_side()
    {
        var rules = ArtilleryRules.FromData(Data.Value);
        Assert.Equal(["us", "de"], rules.Batteries.Select(b => b.Side).ToArray());
        Assert.All(rules.Batteries, b => Assert.True(b.Rounds > 0 && b.Missions > 0 && b.LethalRadius <= b.CasualtyRadius));
        Assert.Null(rules.For("fr"));
    }

    [Fact]
    public void An_HQ_with_line_of_sight_gets_a_mission_whose_rounds_land_after_the_delay_and_over_the_duration()
    {
        var rig = new Rig(new GameMap("open", 150, 60));
        var hq = rig.Org.SpawnSquad("us_platoon_hq", M(250), M(60), Posture.Kneeling);
        var st = rig.Call("us", hq.Id, 80, 60);
        Assert.Equal(OrderStatus.Done, st.Status);
        Assert.True(st.TotalDelayTicks > 0); // the chain-of-command delay applies before the call

        var view = rig.World.ViewFor("us");
        var mission = Assert.Single(view.FireMissions);
        var b = Us;
        Assert.Equal(MissionStatus.Incoming, mission.Status);
        Assert.Equal(b.CallTicks + b.FlightTicks - 1, mission.EtaTicks);
        Assert.Equal(b.Rounds, mission.RoundsLeft);
        Assert.Equal(b.Missions - 1, view.MissionsLeft);

        var delivered = rig.World.Tick - 1;
        var impactTicks = new List<long>();
        for (var t = 0; t < b.CallTicks + b.FlightTicks + b.DurationTicks + 20; t++)
        {
            rig.World.Step();
            impactTicks.AddRange(rig.Arty.LastImpacts.Select(i => i.Tick));
        }

        Assert.Equal(b.Rounds, impactTicks.Count);
        Assert.Equal(delivered + b.CallTicks + b.FlightTicks, impactTicks[0]);
        Assert.True(impactTicks[^1] < impactTicks[0] + b.DurationTicks);
        Assert.True(impactTicks[^1] - impactTicks[0] >= b.DurationTicks * 9 / 10, "rounds spread over the duration");
        Assert.Equal(MissionStatus.Complete, view.FireMissions[0].Status);
        Assert.Equal(0, view.FireMissions[0].RoundsLeft);
    }

    [Fact]
    public void A_squad_without_HQ_or_radio_cannot_call()
    {
        var rig = new Rig(new GameMap("open", 150, 60));
        var squad = rig.Org.SpawnSquad("us_rifle_squad", M(250), M(60));
        Assert.False(squad.HasRadio);
        var st = rig.Call("us", squad.Id, 80, 60);
        Assert.Equal((OrderStatus.Rejected, OrderNote.NotObserver), (st.Status, st.Note));
        Assert.Empty(rig.World.ViewFor("us").FireMissions);
    }

    [Fact]
    public void An_HQ_that_cannot_see_the_point_cannot_call()
    {
        var map = new GameMap("woods", 150, 60);
        for (var tx = 60; tx < 90; tx++)
        {
            for (var ty = 0; ty < 60; ty++)
            {
                map.SetTerrain(tx, ty, TerrainType.LightWoods); // 60 m of woods between the HQ and the point
            }
        }

        var rig = new Rig(map);
        var hq = rig.Org.SpawnSquad("us_platoon_hq", M(250), M(60), Posture.Kneeling);
        var st = rig.Call("us", hq.Id, 60, 60);
        Assert.Equal((OrderStatus.Rejected, OrderNote.NoLineOfSight), (st.Status, st.Note));
        Assert.Equal(Us.Missions, rig.World.ViewFor("us").MissionsLeft);
    }

    [Fact]
    public void Missions_are_limited_per_side_and_off_map_calls_are_rejected()
    {
        var rig = new Rig(new GameMap("open", 150, 60));
        var hq = rig.Org.SpawnSquad("us_platoon_hq", M(250), M(60), Posture.Kneeling);
        for (var i = 0; i < Us.Missions; i++)
        {
            Assert.Equal(OrderStatus.Done, rig.Call("us", hq.Id, 80 + (i * 10), 60).Status);
        }

        Assert.Equal(0, rig.World.ViewFor("us").MissionsLeft);
        var st = rig.Call("us", hq.Id, 80, 60);
        Assert.Equal((OrderStatus.Rejected, OrderNote.NoMissionsLeft), (st.Status, st.Note));
        Assert.Equal(Us.Missions, rig.World.ViewFor("us").FireMissions.Count);

        rig.World.ViewFor("us").Issue(hq.Id, new Order(OrderKind.CallFire, M(-5), M(60)));
        rig.World.Step();
        Assert.Equal(OrderNote.OffMap, rig.World.ViewFor("us").StatusOf(hq.Id)!.Note);
    }

    [Fact]
    public void Rounds_disperse_around_the_called_point()
    {
        var offsets = new List<double>();
        var spreads = new List<double>();
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var rig = new Rig(new GameMap("open", 150, 60), seed);
            var hq = rig.Org.SpawnSquad("us_platoon_hq", M(250), M(60), Posture.Kneeling);
            rig.Call("us", hq.Id, 80, 60);
            var pts = new List<(double X, double Y)>();
            var b = Us;
            for (var t = 0; t < b.CallTicks + b.FlightTicks + b.DurationTicks + 20; t++)
            {
                rig.World.Step();
                pts.AddRange(rig.Arty.LastImpacts.Select(i => (i.X.Raw / 4294967296.0, i.Y.Raw / 4294967296.0)));
            }

            var mx = pts.Average(p => p.X);
            var my = pts.Average(p => p.Y);
            offsets.Add(Math.Sqrt(((mx - 80) * (mx - 80)) + ((my - 60) * (my - 60))));
            spreads.Add(Math.Sqrt(pts.Average(p => ((p.X - mx) * (p.X - mx)) + ((p.Y - my) * (p.Y - my)))));
        }

        output.WriteLine($"mean aim offset {offsets.Average():F1} m, mean round spread {spreads.Average():F1} m");
        Assert.InRange(offsets.Average(), 4, 40); // per-mission aim error
        Assert.InRange(spreads.Average(), 10, 50); // per-round scatter
        Assert.True(offsets.Distinct().Count() > 15);
    }

    private static double CasualtyRate(Func<GameMap, Rig> make, Posture posture, int manX, int burstX)
    {
        var rig = make(new GameMap("effects", 100, 40));
        var q = rig.Org.SpawnSquad("de_gruppe", M(manX), M(40), posture);
        var b = Us;
        var hits = 0;
        var trials = 0;
        for (var n = 0; n < 200; n++)
        {
            for (var i = 0; i < q.Soldiers.Count; i++)
            {
                var s = q.Soldiers[i];
                q.SetHealth(s.Id, HealthState.Ok);
                s.X = M(manX);
                s.Y = M(20 + (i * 4));
                s.Posture = posture;
            }

            rig.Arty.Burst(rig.World, b, M(burstX), M(20 + (q.Soldiers.Count * 2)));
            hits += q.Soldiers.Count(s => s.Health != HealthState.Ok);
            trials += q.Soldiers.Count;
        }

        return hits / (double)trials;
    }

    [Fact]
    public void Prone_men_behind_a_hedge_or_in_a_building_are_much_safer_than_men_standing_in_the_open()
    {
        Rig Open(GameMap map) => new(map);
        Rig Hedge(GameMap map)
        {
            for (var ty = 0; ty < map.Height; ty++)
            {
                map.SetHedge(52, ty, EdgeSide.East, true); // the hedge at x = 106 m, between the men (x 104) and the bursts (x 110)
            }

            return new Rig(map);
        }

        Rig House(GameMap map)
        {
            var tiles = new List<TilePos>();
            for (var tx = 50; tx < 54; tx++)
            {
                for (var ty = 8; ty < 32; ty++)
                {
                    tiles.Add(new TilePos(tx, ty));
                }
            }

            map.AddBuilding(new Building("house", Data.Value.Table("materials").Entries[0].Id, 1, tiles, []));
            return new Rig(map);
        }

        var open = CasualtyRate(Open, Posture.Standing, 104, 110);
        var proneOpen = CasualtyRate(Open, Posture.Prone, 104, 110);
        var proneHedge = CasualtyRate(Hedge, Posture.Prone, 104, 110);
        var house = CasualtyRate(House, Posture.Standing, 104, 110);
        output.WriteLine($"casualty rate per man per burst: standing open {open:P1}, prone open {proneOpen:P1}, prone behind hedge {proneHedge:P1}, in a building {house:P1}");
        Assert.True(open > 0.05, $"open {open}");
        Assert.True(proneOpen < open * 0.6);
        Assert.True(proneHedge < open * 0.2, $"hedge {proneHedge} vs open {open}");
        Assert.True(house < open * 0.4, $"house {house} vs open {open}");
    }

    [Fact]
    public void A_burst_suppresses_far_more_than_a_near_miss_and_shakes_morale()
    {
        var rig = new Rig(new GameMap("open", 100, 40));
        var q = rig.Org.SpawnSquad("de_gruppe", M(100), M(40), Posture.Prone);
        rig.World.Step();
        var near = rig.Suppression.Rules.NearMiss;
        var before = q.MoraleLevel;
        rig.Arty.Burst(rig.World, Us with { LethalChance = Fixed.Zero }, M(110), M(40));
        var sup = q.Soldiers.Max(s => rig.Suppression.LevelOf(s.Id));
        output.WriteLine($"max suppression after one burst at ~10 m: {sup} (rifle near miss: {near}); morale {before} -> {q.MoraleLevel}");
        Assert.True(sup > near * Fixed.FromInt(4));
        Assert.True(q.MoraleLevel < before);
    }

    [Fact]
    public void Each_side_sees_only_its_own_missions_but_both_see_the_impacts_and_feel_the_bursts()
    {
        var rig = new Rig(new GameMap("open", 150, 60), ai: true);
        var hq = rig.Org.SpawnSquad("us_platoon_hq", M(250), M(60), Posture.Kneeling);
        var de = rig.Org.SpawnSquad("de_gruppe", M(80), M(60), Posture.Prone);
        rig.Call("us", hq.Id, 80, 60);
        var us = rig.World.ViewFor("us");
        var ger = rig.World.ViewFor("de");
        Assert.Single(us.FireMissions);
        Assert.Empty(ger.FireMissions);
        Assert.Equal(ArtilleryRules.FromData(Data.Value).For("de")!.Missions, ger.MissionsLeft);

        var felt = false;
        for (var t = 0; t < Us.CallTicks + Us.FlightTicks + 50; t++)
        {
            rig.World.Step();
            felt |= ger.IncomingFire.Any(e => e.Explosion && e.SourceId == -1 && de.Soldiers.Any(s => s.Id == e.SoldierId));
        }

        Assert.True(felt, "German men near the bursts notice them as incoming fire");
        Assert.NotEmpty(us.ShellImpacts);
        Assert.Equal(us.ShellImpacts, ger.ShellImpacts);
    }

    [Fact]
    public void Mission_state_is_in_the_hash_and_same_seed_gives_same_battle()
    {
        ulong Run(bool call)
        {
            var rig = new Rig(new GameMap("open", 150, 60), 5);
            var hq = rig.Org.SpawnSquad("us_platoon_hq", M(250), M(60), Posture.Kneeling);
            rig.Org.SpawnSquad("de_gruppe", M(80), M(60), Posture.Prone);
            if (call)
            {
                rig.World.ViewFor("us").Issue(hq.Id, new Order(OrderKind.CallFire, M(80), M(60)));
            }

            rig.World.Run(900);
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Run(true), Run(true));
        Assert.NotEqual(Run(true), Run(false));
    }

    [Fact]
    public void A_full_four_gun_mission_on_a_busy_battlefield_fits_the_budget()
    {
        // A whole mission's bursts on 17 squads packed around the target, timed on their own: a full run of the world
        // here is dominated by spotting (about 35 ms a tick for 17 squads, before this item), which would hide them.
        var b = Us;
        var rig = new Rig(new GameMap("busy", 200, 80));
        rig.Org.SpawnSquad("us_platoon_hq", M(360), M(80), Posture.Kneeling);
        for (var i = 0; i < 8; i++)
        {
            rig.Org.SpawnSquad("us_rifle_squad", M(130), M(20 + (i * 16)), Posture.Prone);
            rig.Org.SpawnSquad("de_gruppe", M(100), M(20 + (i * 16)), Posture.Prone);
        }

        rig.World.Step();
        var men = rig.Org.Squads.Sum(q => q.Soldiers.Count);
        long worst = 0;
        var clock = Stopwatch.StartNew();
        for (var r = 0; r < b.Rounds * 4; r++) // four missions' worth, for a stable time
        {
            var start = clock.ElapsedTicks;
            rig.Arty.Burst(rig.World, b, M(100 + ((r * 7) % 40)), M(20 + ((r * 13) % 120)));
            worst = Math.Max(worst, clock.ElapsedTicks - start);
        }

        var totalMs = clock.ElapsedMilliseconds;
        var worstMs = worst * 1000.0 / Stopwatch.Frequency;
        output.WriteLine($"{b.Rounds * 4} bursts among {men} men: {totalMs} ms in all, worst burst {worstMs:F2} ms");
        Assert.True(totalMs < 500, $"{totalMs} ms");
        Assert.True(worstMs < 50, $"worst burst {worstMs} ms (one tick is 100 ms)");
    }
}
