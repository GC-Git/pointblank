using System.Diagnostics;
using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Tests.Data;
using PointBlank.Sim.Tests.Map;
using Xunit;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Sight;

/// <summary>Item 1.2: line of sight.</summary>
public class LineOfSightTests(ITestOutputHelper output)
{
    private static readonly SightRules Rules =
        SightRules.FromData(DataLoader.LoadDirectory(DataPaths.DataDirectory()));

    private static Fixed M(int metres) => Fixed.FromInt(metres);

    /// <summary>A flat 160 m x 20 m strip (80 x 10 tiles); row 5 spans y = 10..12 m.</summary>
    private static GameMap Strip() => new("strip", 80, 10);

    private static Viewpoint At(int x, Posture p = Posture.Standing, int floor = 0, int y = 11) =>
        new(M(x), M(y), p, floor);

    private static LosResult Check(GameMap map, Viewpoint a, Viewpoint b, SightRules? rules = null) =>
        new LineOfSight(map, rules ?? Rules).Check(a, b);

    private static void HedgeLine(GameMap map, int tileX)
    {
        for (var y = 0; y < map.Height; y++)
        {
            map.SetHedge(tileX, y, EdgeSide.East, true);
        }
    }

    [Fact]
    public void Rules_come_from_the_data_files()
    {
        Assert.Equal(Fixed.FromRatio(16, 10), Rules.EyeHeight(Posture.Standing));
        Assert.Equal(Fixed.One, Rules.EyeHeight(Posture.Kneeling));
        Assert.Equal(Fixed.FromRatio(3, 10), Rules.EyeHeight(Posture.Prone));
        Assert.Equal(M(3), Rules.FloorHeight);
        Assert.Equal(Fixed.One, Rules.BlockThreshold);
        Assert.Equal(Fixed.FromRatio(14, 10), Rules.HedgeHeight);
        Assert.Equal(Fixed.One, Rules.HedgeObscurement);
        Assert.Equal(M(8), Rules.CanopyHeight(TerrainType.LightWoods));
        Assert.Equal(Fixed.FromRatio(4, 100), Rules.ObscurementPerMetre(TerrainType.LightWoods));
        Assert.Equal(Fixed.Zero, Rules.ObscurementPerMetre(TerrainType.Open));
    }

    [Fact]
    public void Eye_height_adds_ground_floor_and_posture()
    {
        var map = Strip();
        map.SetElevationDm(1, 5, 25);
        map.AddBuilding(new Building("h", "brick_wall_sample", 3, [new TilePos(1, 5)], []));
        var los = new LineOfSight(map, Rules);

        Assert.Equal(Fixed.FromRatio(41, 10), los.EyeHeight(At(3)));
        Assert.Equal(Fixed.FromRatio(35, 10), los.EyeHeight(At(3, Posture.Kneeling)));
        Assert.Equal(Fixed.FromRatio(28, 10), los.EyeHeight(At(3, Posture.Prone)));
        Assert.Equal(Fixed.FromRatio(101, 10), los.EyeHeight(At(3, Posture.Standing, 2)));
    }

    [Fact]
    public void Viewpoints_are_validated()
    {
        var los = new LineOfSight(Strip(), Rules);
        Assert.Throws<ArgumentException>(() => los.EyeHeight(At(3, Posture.Standing, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => los.EyeHeight(At(161)));
        Assert.Throws<ArgumentOutOfRangeException>(() => los.EyeHeight(new Viewpoint(-Fixed.One, M(1), Posture.Prone)));
    }

    [Fact]
    public void Flat_open_ground_is_clear()
    {
        var r = Check(Strip(), At(1, Posture.Prone), At(150, Posture.Prone));
        Assert.Equal(new LosResult(true, Fixed.Zero, LosBlocker.None, default), r);
    }

    [Fact]
    public void Prone_soldier_behind_a_hedge_is_unseen_from_100m_but_standing_is_seen()
    {
        var map = Strip();
        HedgeLine(map, 49); // the line x = 100 m

        var prone = Check(map, At(1), At(101, Posture.Prone));
        Assert.False(prone.Visible);
        Assert.Equal(LosBlocker.Hedge, prone.Blocker);
        Assert.Equal(new TilePos(49, 5), prone.BlockerTile);

        Assert.False(Check(map, At(1), At(101, Posture.Kneeling)).Visible);

        var standing = Check(map, At(1), At(101));
        Assert.True(standing.Visible);
        Assert.Equal(Fixed.Zero, standing.Obscurement);
    }

    [Fact]
    public void Second_floor_sees_over_the_hedge_that_hides_the_target_from_the_ground_floor()
    {
        var map = Strip();
        var tiles = new List<TilePos>();
        for (var y = 4; y <= 6; y++)
        {
            for (var x = 0; x <= 2; x++)
            {
                tiles.Add(new TilePos(x, y));
            }
        }

        map.AddBuilding(new Building("house", "brick_wall_sample", 2, tiles,
        [
            new Opening(new TilePos(2, 5), WallSide.East, OpeningKind.Window, 0),
            new Opening(new TilePos(2, 5), WallSide.East, OpeningKind.Window, 1),
        ]));
        HedgeLine(map, 40); // x = 82 m
        var target = At(102, Posture.Kneeling);

        var ground = Check(map, At(3), target);
        Assert.False(ground.Visible);
        Assert.Equal(LosBlocker.Hedge, ground.Blocker);

        Assert.True(Check(map, At(3, Posture.Standing, 1), target).Visible);
    }

    [Fact]
    public void A_hill_crest_blocks()
    {
        var map = Strip();
        Assert.True(Check(map, At(1), At(101)).Visible);
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 30; x <= 32; x++)
            {
                map.SetElevationDm(x, y, 30);
            }
        }

        var r = Check(map, At(1), At(101));
        Assert.False(r.Visible);
        Assert.Equal(LosBlocker.Ground, r.Blocker);
        Assert.Equal(new TilePos(30, 5), r.BlockerTile);
    }

    [Fact]
    public void Standing_on_the_crest_sees_down_both_sides()
    {
        var map = Strip();
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 30; x <= 32; x++)
            {
                map.SetElevationDm(x, y, 30);
            }
        }

        Assert.True(Check(map, At(63), At(1)).Visible);
        Assert.True(Check(map, At(63), At(101)).Visible);
    }

    [Fact]
    public void Woods_obscure_with_length_and_block_past_the_threshold()
    {
        var map = Strip();
        for (var x = 10; x < 20; x++)
        {
            map.SetTerrain(x, 5, TerrainType.LightWoods); // 20 m of woods
        }

        var thin = Check(map, At(1), At(101));
        Assert.True(thin.Visible);
        Assert.InRange(thin.Obscurement.Raw, Fixed.FromRatio(79, 100).Raw, Fixed.FromRatio(81, 100).Raw);

        for (var x = 20; x < 40; x++)
        {
            map.SetTerrain(x, 5, TerrainType.LightWoods);
        }

        var thick = Check(map, At(1), At(101));
        Assert.False(thick.Visible);
        Assert.Equal(LosBlocker.Woods, thick.Blocker);
        Assert.Equal(Fixed.One, thick.Obscurement);
    }

    [Fact]
    public void A_thinner_hedge_partially_obscures()
    {
        var map = Strip();
        HedgeLine(map, 49);
        var rules = Rules with { HedgeObscurement = Fixed.FromRatio(1, 2) };

        var r = Check(map, At(1), At(101, Posture.Prone), rules);
        Assert.True(r.Visible);
        Assert.Equal(Fixed.FromRatio(1, 2), r.Obscurement);

        HedgeLine(map, 30);
        var two = Check(map, At(1), At(101, Posture.Prone), rules);
        Assert.False(two.Visible);
        Assert.Equal(LosBlocker.Hedge, two.Blocker);
        Assert.Equal(new TilePos(49, 5), two.BlockerTile);
    }

    [Fact]
    public void A_closed_building_blocks_and_its_roof_can_be_seen_over()
    {
        var map = Strip();
        map.AddBuilding(new Building("barn", "brick_wall_sample", 1,
            [new TilePos(40, 4), new TilePos(40, 5), new TilePos(40, 6)], []));

        var r = Check(map, At(1), At(101));
        Assert.False(r.Visible);
        Assert.Equal(LosBlocker.Building, r.Blocker);
        Assert.Equal(new TilePos(40, 5), r.BlockerTile);

        // From a 3 m bank on both sides the ray passes over the 3 m roof.
        map.SetElevationDm(0, 5, 30);
        map.SetElevationDm(50, 5, 30);
        Assert.True(Check(map, At(1), At(101)).Visible);
    }

    [Fact]
    public void Inside_a_building_one_sees_out_through_a_window_but_not_a_wall()
    {
        var map = Strip();
        var tiles = new[] { new TilePos(0, 5), new TilePos(1, 5), new TilePos(2, 5) };
        map.AddBuilding(new Building("walled", "brick_wall_sample", 1, tiles, []));
        Assert.Equal(LosBlocker.Building, Check(map, At(3), At(60)).Blocker);

        var open = new GameMap("open", 80, 10);
        open.AddBuilding(new Building("windowed", "brick_wall_sample", 1, tiles,
            [new Opening(new TilePos(2, 5), WallSide.East, OpeningKind.Window, 0)]));
        Assert.True(Check(open, At(3), At(60)).Visible);
        Assert.True(Check(open, At(60), At(3)).Visible);
        Assert.True(Check(open, At(1), At(5)).Visible); // both inside: no interior walls
    }

    [Fact]
    public void Seeing_through_two_windows_of_another_building_is_blocked()
    {
        var map = Strip();
        map.AddBuilding(new Building("between", "brick_wall_sample", 1, [new TilePos(40, 5)],
        [
            new Opening(new TilePos(40, 5), WallSide.West, OpeningKind.Window, 0),
            new Opening(new TilePos(40, 5), WallSide.East, OpeningKind.Window, 0),
        ]));
        Assert.Equal(LosBlocker.Building, Check(map, At(1), At(101)).Blocker);
    }

    [Fact]
    public void Corner_crossings_do_not_leak_through_a_hedge_line_but_pass_a_hedge_end()
    {
        // The ray (1,1) -> (41,41) passes exactly through the corner (20, 20).
        var a = new Viewpoint(M(1), M(1), Posture.Kneeling);
        var b = new Viewpoint(M(41), M(41), Posture.Kneeling);

        var line = new GameMap("line", 30, 30);
        HedgeLine(line, 9);
        Assert.Equal(LosBlocker.Hedge, Check(line, a, b).Blocker);
        Assert.Equal(LosBlocker.Hedge, Check(line, b, a).Blocker);

        var end = new GameMap("end", 30, 30);
        for (var y = 0; y <= 9; y++)
        {
            end.SetHedge(9, y, EdgeSide.East, true); // stops at the corner
        }

        Assert.True(Check(end, a, b).Visible);
        Assert.True(Check(end, b, a).Visible);
    }

    [Fact]
    public void Los_is_symmetric_on_the_test_map()
    {
        var map = TestMapBuilder.Build();
        var los = new LineOfSight(map, Rules);
        var points = RandomViewpoints(map, 200, 7);
        var blocked = 0;
        for (var i = 0; i < points.Length; i++)
        {
            for (var j = i + 1; j < points.Length; j += 7)
            {
                var ab = los.Check(points[i], points[j]);
                Assert.Equal(ab, los.Check(points[j], points[i]));
                blocked += ab.Visible ? 0 : 1;
            }
        }

        Assert.True(blocked > 0, "the test map should block some pairs");
    }

    [Fact]
    public void Trace_lists_every_tile_from_start_to_end()
    {
        var map = Strip();
        var los = new LineOfSight(map, Rules);
        var steps = los.Trace(At(1), new Viewpoint(M(22), M(15), Posture.Standing));

        Assert.Equal(new TilePos(0, 5), steps[0].Tile);
        Assert.Equal(new TilePos(11, 7), steps[^1].Tile);
        Assert.Equal(11 + 2 + 1, steps.Count);
        Assert.Equal(Fixed.Zero, steps[0].EnterT);
        Assert.Equal(Fixed.One, steps[^1].ExitT);
        Assert.All(steps, s => Assert.Equal(LosBlocker.None, s.Blocker));
    }

    [Fact]
    public void Visible_tiles_marks_the_area_behind_a_crest_as_hidden()
    {
        var map = Strip();
        for (var y = 0; y < map.Height; y++)
        {
            map.SetElevationDm(30, y, 30);
        }

        var grid = new LineOfSight(map, Rules).VisibleTiles(At(1), Posture.Prone);
        Assert.Equal(80 * 10, grid.Length);
        Assert.True(grid[(5 * 80) + 10]);
        Assert.False(grid[(5 * 80) + 60]);
    }

    [Fact]
    public void Three_hundred_by_three_hundred_checks_fit_the_budget()
    {
        // Budget and scheduling: docs/superpowers/plans/2026-11-25-item-1.2-los.md. The budget
        // is 1 s for the whole batch in a Debug test build; the test allows three times that
        // (best of three runs) so a slower or busy machine does not make it flaky.
        const long budgetMs = 1000;
        const long allowedMs = 3 * budgetMs;
        var map = TestMapBuilder.Build();
        var los = new LineOfSight(map, Rules);
        var points = RandomViewpoints(map, 300, 11);

        ulong? firstHash = null;
        var best = long.MaxValue;
        for (var run = 0; run < 3; run++)
        {
            var hasher = new StateHasher();
            var watch = Stopwatch.StartNew();
            foreach (var a in points)
            {
                foreach (var b in points)
                {
                    var r = los.Check(a, b);
                    hasher.Add(r.Visible);
                    hasher.Add(r.Obscurement.Raw);
                }
            }

            watch.Stop();
            best = Math.Min(best, watch.ElapsedMilliseconds);
            firstHash ??= hasher.Value;
            Assert.Equal(firstHash, hasher.Value);
        }

        output.WriteLine($"90,000 LOS checks: best of 3 = {best} ms (budget {budgetMs} ms, allowed {allowedMs} ms)");
        Assert.True(best < allowedMs, $"90,000 LOS checks took {best} ms; budget {budgetMs} ms, allowed {allowedMs} ms.");
    }

    private static Viewpoint[] RandomViewpoints(GameMap map, int count, ulong seed)
    {
        var rng = new SimRng(seed);
        var sideMm = map.Width * map.TileSizeMm;
        var points = new Viewpoint[count];
        for (var i = 0; i < count; i++)
        {
            points[i] = new Viewpoint(
                Fixed.FromRatio(rng.NextInt(0, sideMm), 1000),
                Fixed.FromRatio(rng.NextInt(0, sideMm), 1000),
                (Posture)rng.NextInt(0, 3));
        }

        return points;
    }
}
