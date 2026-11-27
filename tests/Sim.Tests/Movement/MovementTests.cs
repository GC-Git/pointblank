using System.Diagnostics;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Tests.Map;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Movement;

/// <summary>Item 1.4: orders, formation, speed, posture, deadlock, performance.</summary>
[Collection(TimingCollection.Name)]
public class MovementTests(ITestOutputHelper output)
{
    private static Fixed M(int metres) => Fixed.FromInt(metres);

    private static (World World, OrgSystem Org, MovementSystem Move) Setup(GameMap map, ulong seed = 1)
    {
        var org = new OrgSystem(OrgTables.FromData(PathFinderTests.Data));
        var move = new MovementSystem(map, org, PathFinderTests.Rules);
        return (new World(seed, new MapSystem(map), org, move), org, move);
    }

    private static int RunUntilArrived(World world, MovementSystem move, int squadId, int limit = 6000)
    {
        for (var i = 0; i < limit && move.IsMoving(squadId); i++)
        {
            world.Step();
        }

        return (int)world.Tick;
    }

    private static double Dist(Soldier a, Soldier b) =>
        Math.Sqrt(Math.Pow((double)(a.X - b.X).Raw, 2) + Math.Pow((double)(a.Y - b.Y).Raw, 2)) / (1L << 32);

    [Fact]
    public void Move_orders_walk_the_squad_to_the_target_in_distinct_tiles()
    {
        var map = new GameMap("open", 60, 30);
        var (world, org, move) = Setup(map);
        var squad = org.SpawnSquad("us_rifle_squad", M(20), M(30));

        Assert.Equal(MoveResult.Started, move.Execute(new MoveOrder(squad.Id, M(90), M(30), MoveMode.Move)));
        world.Step();
        var ticks = RunUntilArrived(world, move, squad.Id);

        Assert.False(move.IsMoving(squad.Id));
        Assert.True(ticks < 1500, $"took {ticks} ticks");
        Assert.Equal(12, squad.Soldiers.Select(s => (s.X.ToInt() / 2, s.Y.ToInt() / 2)).Distinct().Count());
        Assert.True(Dist(squad.Leader!, squad.Soldiers[1]) > 2, string.Join(" ", squad.Soldiers.Select(s => $"({s.X},{s.Y})")));
        Assert.InRange((double)squad.Leader!.X.Raw / (1L << 32), 88, 92);
    }

    [Fact]
    public void Sneak_is_slower_and_lower_than_Move_and_Move_is_slower_than_MoveFast()
    {
        int Arrival(MoveMode mode, out Posture posture)
        {
            var (world, org, move) = Setup(new GameMap("open", 80, 20));
            var squad = org.SpawnSquad("us_rifle_squad", M(20), M(20));
            move.Execute(new MoveOrder(squad.Id, M(120), M(20), mode));
            world.Run(10);
            posture = squad.Soldiers[0].Posture;
            var expected = posture;
            Assert.All(squad.Soldiers, s => Assert.Equal(expected, s.Posture));
            return RunUntilArrived(world, move, squad.Id);
        }

        var sneak = Arrival(MoveMode.Sneak, out var sneakPosture);
        var walk = Arrival(MoveMode.Move, out var walkPosture);
        var fast = Arrival(MoveMode.MoveFast, out var fastPosture);

        Assert.True(sneak > walk && walk > fast, $"sneak {sneak}, move {walk}, fast {fast}");
        Assert.Equal(Posture.Standing, walkPosture);
        Assert.Equal(Posture.Standing, fastPosture);
        var rules = SightRules.FromData(PathFinderTests.Data);
        Assert.True(rules.EyeHeight(sneakPosture) < rules.EyeHeight(walkPosture));
    }

    [Fact]
    public void The_squad_goes_round_woods_when_that_is_cheaper()
    {
        var map = new GameMap("woods", 80, 40);
        for (var y = 14; y <= 25; y++)
        {
            for (var x = 30; x <= 49; x++)
            {
                map.SetTerrain(x, y, TerrainType.LightWoods);
            }
        }

        var (world, org, move) = Setup(map);
        var squad = org.SpawnSquad("us_rifle_squad", M(30), M(40));
        move.Execute(new MoveOrder(squad.Id, M(130), M(40), MoveMode.Move));
        var leaderInWoods = 0;
        for (var i = 0; i < 4000 && move.IsMoving(squad.Id); i++)
        {
            world.Step();
            var l = squad.Soldiers[0];
            leaderInWoods += map.GetTerrain(l.X.ToInt() / 2, l.Y.ToInt() / 2) == TerrainType.LightWoods ? 1 : 0;
        }

        Assert.False(move.IsMoving(squad.Id));
        Assert.Equal(0, leaderInWoods);
    }

    [Fact]
    public void Hedges_and_buildings_are_never_crossed_off_a_path()
    {
        var map = TestMapBuilder.Build();
        var (world, org, move) = Setup(map);
        var squad = org.SpawnSquad("us_rifle_squad", M(140), M(60));
        move.Execute(new MoveOrder(squad.Id, M(140), M(120), MoveMode.Move));
        var crossingRows = new HashSet<int>();
        var previous = squad.Soldiers.Select(s => s.Y.ToInt() / 2).ToArray();
        for (var i = 0; i < 3000 && move.IsMoving(squad.Id); i++)
        {
            world.Step();
            for (var k = 0; k < squad.Soldiers.Count; k++)
            {
                var s = squad.Soldiers[k];
                var row = s.Y.ToInt() / 2;
                if (previous[k] <= 40 && row >= 41)
                {
                    crossingRows.Add(s.X.ToInt() / 2); // where each man went through the y = 40 hedge
                }

                previous[k] = row;
            }
        }

        Assert.False(move.IsMoving(squad.Id));
        Assert.All(crossingRows, x => Assert.InRange(x, 70, 73));
        Assert.NotEmpty(crossingRows);
    }

    [Fact]
    public void Men_who_are_dead_or_incapacitated_do_not_move_and_the_rest_still_arrive()
    {
        var map = new GameMap("open", 60, 20);
        var (world, org, move) = Setup(map);
        var squad = org.SpawnSquad("us_rifle_squad", M(20), M(20));
        squad.SetHealth(squad.Soldiers[5].Id, HealthState.Dead);
        squad.SetHealth(squad.Soldiers[6].Id, HealthState.Incapacitated);
        var dead = (squad.Soldiers[5].X, squad.Soldiers[5].Y);
        var down = (squad.Soldiers[6].X, squad.Soldiers[6].Y);

        move.Execute(new MoveOrder(squad.Id, M(90), M(20), MoveMode.MoveFast));
        world.Run(100);
        var late = squad.Soldiers[7];
        squad.SetHealth(late.Id, HealthState.Dead);
        var lateAt = (late.X, late.Y);
        RunUntilArrived(world, move, squad.Id);

        Assert.False(move.IsMoving(squad.Id));
        Assert.Equal(dead, (squad.Soldiers[5].X, squad.Soldiers[5].Y));
        Assert.Equal(down, (squad.Soldiers[6].X, squad.Soldiers[6].Y));
        Assert.Equal(lateAt, (late.X, late.Y));
        Assert.NotEqual(lateAt.Item1, squad.Soldiers[0].X);
    }

    [Fact]
    public void An_unreachable_target_gives_no_order()
    {
        var map = new GameMap("walled", 30, 20);
        map.AddBuilding(new Building("shed", "m", 1, [new TilePos(20, 10)], []));
        var (world, org, move) = Setup(map);
        var squad = org.SpawnSquad("us_rifle_squad", M(10), M(20));
        var before = world.ComputeStateHash();

        Assert.Equal(MoveResult.Unreachable, move.Execute(new MoveOrder(squad.Id, M(41), M(21), MoveMode.Move)));
        Assert.False(move.IsMoving(squad.Id));
        Assert.Equal(before, world.ComputeStateHash());
        Assert.Throws<InvalidOperationException>(() => move.Execute(new MoveOrder(9, M(1), M(1), MoveMode.Move)));
    }

    [Fact]
    public void A_target_off_the_map_is_rejected_not_clamped()
    {
        var (world, org, move) = Setup(new GameMap("open", 40, 20));
        var squad = org.SpawnSquad("us_rifle_squad", M(10), M(20));
        var before = world.ComputeStateHash();

        Assert.Equal(MoveResult.OffMap, move.Execute(new MoveOrder(squad.Id, M(80), M(20), MoveMode.Move)));
        Assert.Equal(MoveResult.OffMap, move.Execute(new MoveOrder(squad.Id, M(10), -Fixed.One, MoveMode.Move)));
        Assert.False(move.IsMoving(squad.Id));
        Assert.Equal(before, world.ComputeStateHash());
        Assert.Equal(MoveResult.Started, move.Execute(new MoveOrder(squad.Id, M(79), M(39), MoveMode.Move)));
    }

    [Fact]
    public void The_state_hash_covers_movement_state()
    {
        ulong Hash(MoveMode? mode, int ticks)
        {
            var (world, org, move) = Setup(new GameMap("open", 60, 20));
            var squad = org.SpawnSquad("us_rifle_squad", M(10), M(20));
            if (mode != null)
            {
                move.Execute(new MoveOrder(squad.Id, M(80), M(20), mode.Value));
            }

            world.Run(ticks);
            return world.ComputeStateHash();
        }

        Assert.Equal(Hash(MoveMode.Move, 40), Hash(MoveMode.Move, 40));
        Assert.NotEqual(Hash(MoveMode.Move, 40), Hash(MoveMode.Move, 41));
        Assert.NotEqual(Hash(MoveMode.Move, 40), Hash(MoveMode.Sneak, 40));
        Assert.NotEqual(Hash(MoveMode.Move, 0), Hash(null, 0));
    }

    [Fact]
    public void Two_squads_crossing_in_a_hedge_gap_both_arrive_within_a_bounded_time_for_seeds_1_to_100()
    {
        var map = TestMapBuilder.Build();
        var scenario = new CrossingScenario(map);
        var worst = 0L;
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var world = scenario.CreateWorld(seed);
            foreach (var o in scenario.ScriptedOrders)
            {
                world.Schedule(o.Tick, scenario.CreateInput(o));
            }

            var move = world.GetSystem<MovementSystem>();
            var org = world.GetSystem<OrgSystem>();
            // The tick-0 orders go through the chain of command (no HQ on the map, so a few seconds).
            var command = world.GetSystem<CommandSystem>();
            do
            {
                world.Step();
            }
            while (world.Tick < 200 && org.Squads.Any(s => command.StatusOf(s.Id).Status == OrderStatus.Pending));

            Assert.True(org.Squads.All(s => move.IsMoving(s.Id)), "both squads got their orders");
            while (world.Tick < scenario.Ticks && org.Squads.Any(s => move.IsMoving(s.Id)))
            {
                world.Step();
            }

            Assert.True(org.Squads.All(s => !move.IsMoving(s.Id)), $"seed {seed}: still moving at tick {world.Tick}");
            var tiles = org.Squads.SelectMany(s => s.Soldiers).Select(s => (s.X.ToInt() / 2, s.Y.ToInt() / 2)).ToList();
            Assert.Equal(tiles.Count, tiles.Distinct().Count());
            worst = Math.Max(worst, world.Tick);
        }

        output.WriteLine($"Slowest crossing over seeds 1-100: {worst} ticks (bound {scenario.Ticks}).");
        Assert.True(worst < 1000, $"slowest crossing took {worst} ticks");
    }

    [Fact]
    public void A_crossing_battle_replays_to_the_same_hash()
    {
        var scenario = new CrossingScenario(TestMapBuilder.Build(), 600);
        var result = Battle.Run(scenario, 5);

        Assert.Equal(result.Replay.FinalHash, Battle.Play(scenario, ReplayJson.Read(ReplayJson.Write(result.Replay))).ComputeStateHash());
        Assert.Equal(result.Replay.FinalHash, Battle.Run(scenario, 5).Replay.FinalHash);
        Assert.NotEqual(result.Replay.FinalHash, Battle.Run(scenario, 6).Replay.FinalHash);
    }

    [Fact]
    public void Forty_squads_are_planned_across_a_150_by_150_map_within_budget()
    {
        // Budget: planning 40 squad paths (the tiles-and-terrain A*) in 1 s on the 150 x 150 test map
        // in a Debug build; allowed three times that (best of three) so a busy machine cannot flake it.
        const long budgetMs = 1000;
        const long allowedMs = 3 * budgetMs;
        var map = TestMapBuilder.Build();
        var rng = new SimRng(5);
        var pairs = Enumerable.Range(0, 40).Select(_ =>
            (new TilePos(rng.NextInt(0, 150), rng.NextInt(0, 150)), new TilePos(rng.NextInt(0, 150), rng.NextInt(0, 150)))).ToList();
        var pf = new PathFinder(map, PathFinderTests.Rules);

        ulong? first = null;
        var best = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var hasher = new StateHasher();
            var watch = Stopwatch.StartNew();
            foreach (var (a, b) in pairs)
            {
                var path = pf.FindPath(a, b, MoveMode.Move);
                hasher.Add(path?.Count ?? -1);
            }

            watch.Stop();
            best = Math.Min(best, watch.ElapsedMilliseconds);
            first ??= hasher.Value;
            Assert.Equal(first, hasher.Value);
        }

        output.WriteLine($"40 paths on 150 x 150: best of 3 = {best} ms (budget {budgetMs} ms, allowed {allowedMs} ms)");
        Assert.True(best < allowedMs, $"40 paths took {best} ms; budget {budgetMs} ms, allowed {allowedMs} ms.");
    }

    [Fact]
    public void Forty_squads_move_a_hundred_ticks_within_budget()
    {
        // Budget: 40 squads (480 men) ordered across the map and run for 100 ticks in 2 s in a
        // Debug build; allowed three times that, best of two.
        const long budgetMs = 2000;
        const long allowedMs = 3 * budgetMs;
        var best = long.MaxValue;
        for (var run = 0; run < 2; run++)
        {
            var map = TestMapBuilder.Build();
            var (world, org, move) = Setup(map);
            var rng = new SimRng(8);
            for (var i = 0; i < 40; i++)
            {
                var x = 10 + rng.NextInt(0, 280);
                var y = 10 + rng.NextInt(0, 280);
                org.SpawnSquad(i % 2 == 0 ? "us_rifle_squad" : "de_gruppe", M(x), M(y));
            }

            var watch = Stopwatch.StartNew();
            foreach (var squad in org.Squads)
            {
                move.Execute(new MoveOrder(squad.Id, M(10 + rng.NextInt(0, 280)), M(10 + rng.NextInt(0, 280)), MoveMode.Move));
            }

            world.Run(100);
            watch.Stop();
            best = Math.Min(best, watch.ElapsedMilliseconds);
        }

        output.WriteLine($"40 squads, orders + 100 ticks: best of 2 = {best} ms (budget {budgetMs} ms, allowed {allowedMs} ms)");
        Assert.True(best < allowedMs, $"took {best} ms; budget {budgetMs} ms, allowed {allowedMs} ms.");
    }
}
