using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Tests.Data;

namespace PointBlank.Sim.Tests.Movement;

/// <summary>Item 1.4: A* on tiles.</summary>
public class PathFinderTests
{
    internal static readonly DataSet Data = DataLoader.LoadDirectory(DataPaths.DataDirectory());
    internal static readonly MovementRules Rules = MovementRules.FromData(Data);

    private static Fixed Cost(PathFinder pf, List<TilePos> path, MoveMode mode)
    {
        var total = Fixed.Zero;
        for (var i = 1; i < path.Count; i++)
        {
            Assert.True(pf.CanStep(path[i - 1], path[i]), $"illegal step {path[i - 1]} -> {path[i]}");
            total += pf.StepCost(path[i - 1], path[i], mode);
        }

        return total;
    }

    private static void Woods(GameMap map, int x0, int y0, int x1, int y1)
    {
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                map.SetTerrain(x, y, TerrainType.LightWoods);
            }
        }
    }

    [Fact]
    public void Rules_come_from_the_data_files()
    {
        Assert.Equal(Fixed.FromRatio(3, 2), Rules.Speed(MoveMode.Move));
        Assert.True(Rules.Speed(MoveMode.Sneak) < Rules.Speed(MoveMode.Move));
        Assert.True(Rules.Speed(MoveMode.Move) < Rules.Speed(MoveMode.MoveFast));
        Assert.Equal(Fixed.One, Rules.TerrainCost(MoveMode.Move, TerrainType.Open));
        Assert.Equal(Fixed.FromInt(2), Rules.TerrainCost(MoveMode.Move, TerrainType.LightWoods));
        Assert.Equal(Fixed.FromRatio(8, 10), Rules.MinTerrainCost(MoveMode.Move));
        Assert.Equal(Fixed.FromInt(3), Rules.HedgeCost(MoveMode.Move));
    }

    [Fact]
    public void Open_ground_path_is_the_octile_line()
    {
        var map = new GameMap("flat", 20, 20);
        var pf = new PathFinder(map, Rules);

        var path = pf.FindPath(new TilePos(2, 2), new TilePos(12, 7), MoveMode.Move)!;

        Assert.Equal(new TilePos(2, 2), path[0]);
        Assert.Equal(new TilePos(12, 7), path[^1]);
        Assert.Equal(11, path.Count); // 5 diagonals + 5 straight steps
        Assert.Equal((PathFinder.Diagonal * Fixed.FromInt(5)) + Fixed.FromInt(5), Cost(pf, path, MoveMode.Move));
    }

    [Fact]
    public void Takes_the_cheaper_route_around_woods_but_through_them_when_the_detour_is_dearer()
    {
        var around = new GameMap("a", 40, 20);
        Woods(around, 15, 6, 24, 13);
        var pf = new PathFinder(around, Rules);
        var path = pf.FindPath(new TilePos(8, 10), new TilePos(30, 10), MoveMode.Move)!;
        Assert.DoesNotContain(path, t => around.GetTerrain(t.X, t.Y) == TerrainType.LightWoods);

        // The same wood with no way round but a long detour: straight through is cheaper.
        var through = new GameMap("t", 40, 20);
        Woods(through, 15, 0, 24, 18);
        var pf2 = new PathFinder(through, Rules);
        var path2 = pf2.FindPath(new TilePos(8, 10), new TilePos(30, 10), MoveMode.Move)!;
        Assert.Contains(path2, t => through.GetTerrain(t.X, t.Y) == TerrainType.LightWoods);
    }

    [Fact]
    public void Prefers_a_nearby_hedge_gap_but_crosses_a_hedge_when_the_gap_is_far()
    {
        GameMap Field(int gapFromY)
        {
            var map = new GameMap("f", 24, 14);
            for (var y = 0; y < 14; y++)
            {
                if (y < gapFromY)
                {
                    map.SetHedge(10, y, EdgeSide.East, true);
                }
            }

            return map;
        }

        int Crossings(GameMap map, List<TilePos> path) =>
            Enumerable.Range(1, path.Count - 1).Count(i =>
                Math.Abs(path[i].X - path[i - 1].X) + Math.Abs(path[i].Y - path[i - 1].Y) == 1
                && map.HasHedgeBetween(path[i - 1], path[i]));

        var near = Field(4);
        var nearPath = new PathFinder(near, Rules).FindPath(new TilePos(5, 2), new TilePos(15, 2), MoveMode.Move)!;
        Assert.Equal(0, Crossings(near, nearPath));

        var far = Field(13);
        var farPath = new PathFinder(far, Rules).FindPath(new TilePos(5, 2), new TilePos(15, 2), MoveMode.Move)!;
        Assert.Equal(1, Crossings(far, farPath));
    }

    [Fact]
    public void A_diagonal_never_cuts_through_a_hedge_corner_or_a_building_corner()
    {
        var map = new GameMap("c", 8, 8);
        map.SetHedge(2, 2, EdgeSide.East, true);
        var pf = new PathFinder(map, Rules);

        Assert.False(pf.CanStep(new TilePos(2, 3), new TilePos(3, 2))); // hedge on the far edge of the corner
        Assert.False(pf.CanStep(new TilePos(3, 2), new TilePos(2, 3)));
        Assert.True(pf.CanStep(new TilePos(5, 5), new TilePos(6, 6)));

        map.AddBuilding(new Building("h", "m", 1, [new TilePos(6, 5)], []));
        Assert.False(pf.CanStep(new TilePos(5, 5), new TilePos(6, 6))); // building tile at one side of the corner
    }

    [Fact]
    public void A_building_is_entered_only_through_a_door()
    {
        var map = new GameMap("b", 14, 10);
        var tiles = new List<TilePos>();
        for (var y = 2; y <= 4; y++)
        {
            for (var x = 4; x <= 6; x++)
            {
                tiles.Add(new TilePos(x, y));
            }
        }

        map.AddBuilding(new Building(
            "house", "m", 1, tiles,
            [new Opening(new TilePos(5, 4), WallSide.South, OpeningKind.Door, 0),
             new Opening(new TilePos(4, 3), WallSide.West, OpeningKind.Window, 0)]));
        var pf = new PathFinder(map, Rules);

        var across = pf.FindPath(new TilePos(1, 3), new TilePos(10, 3), MoveMode.Move)!;
        Assert.DoesNotContain(across, t => map.BuildingAt(t.X, t.Y) != null);

        var inside = pf.FindPath(new TilePos(1, 3), new TilePos(5, 3), MoveMode.Move)!;
        var entry = inside.Zip(inside.Skip(1)).First(p => map.BuildingAt(p.Second.X, p.Second.Y) != null);
        Assert.Equal((new TilePos(5, 5), new TilePos(5, 4)), entry);
        Assert.False(pf.CanStep(new TilePos(3, 3), new TilePos(4, 3))); // a window is not a door
        Assert.True(pf.CanStep(new TilePos(5, 3), new TilePos(5, 4))); // inside the building

        map.AddBuilding(new Building("shed", "m", 1, [new TilePos(11, 8)], []));
        Assert.Null(pf.FindPath(new TilePos(1, 3), new TilePos(11, 8), MoveMode.Move)); // no door: unreachable
    }

    [Fact]
    public void Search_is_optimal_against_a_brute_force_search_on_random_maps()
    {
        var rng = new SimRng(99);
        for (var round = 0; round < 6; round++)
        {
            var map = new GameMap("r", 24, 24);
            for (var i = 0; i < 150; i++)
            {
                map.SetTerrain(rng.NextInt(0, 24), rng.NextInt(0, 24), (TerrainType)rng.NextInt(0, 3));
            }

            for (var i = 0; i < 40; i++)
            {
                map.SetHedge(rng.NextInt(0, 23), rng.NextInt(0, 23), (EdgeSide)rng.NextInt(0, 2), true);
            }

            map.AddBuilding(new Building("b", "m", 1, [new TilePos(10, 10), new TilePos(11, 10)],
                [new Opening(new TilePos(10, 10), WallSide.North, OpeningKind.Door, 0)]));
            var pf = new PathFinder(map, Rules);
            foreach (var mode in Enum.GetValues<MoveMode>())
            {
                for (var q = 0; q < 8; q++)
                {
                    var a = new TilePos(rng.NextInt(0, 24), rng.NextInt(0, 24));
                    var b = new TilePos(rng.NextInt(0, 24), rng.NextInt(0, 24));
                    var path = pf.FindPath(a, b, mode);
                    var best = Dijkstra(map, pf, a, b, mode);
                    if (best == null)
                    {
                        Assert.Null(path);
                    }
                    else
                    {
                        Assert.Equal(best.Value, Cost(pf, path!, mode));
                    }
                }
            }
        }
    }

    private static Fixed? Dijkstra(GameMap map, PathFinder pf, TilePos a, TilePos b, MoveMode mode)
    {
        var n = map.Width * map.Height;
        var dist = new Fixed?[n];
        var done = new bool[n];
        dist[(a.Y * map.Width) + a.X] = Fixed.Zero;
        while (true)
        {
            var u = -1;
            for (var i = 0; i < n; i++)
            {
                if (!done[i] && dist[i] != null && (u < 0 || dist[i]!.Value < dist[u]!.Value))
                {
                    u = i;
                }
            }

            if (u < 0)
            {
                return null;
            }

            done[u] = true;
            var t = new TilePos(u % map.Width, u / map.Width);
            if (t == b)
            {
                return dist[u];
            }

            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var v = new TilePos(t.X + dx, t.Y + dy);
                    if ((dx == 0 && dy == 0) || !pf.CanStep(t, v))
                    {
                        continue;
                    }

                    var vi = (v.Y * map.Width) + v.X;
                    var d = dist[u]!.Value + pf.StepCost(t, v, mode);
                    if (dist[vi] == null || d < dist[vi]!.Value)
                    {
                        dist[vi] = d;
                    }
                }
            }
        }
    }

    [Fact]
    public void The_same_search_gives_the_same_path_on_a_fresh_finder()
    {
        var map = new GameMap("flat", 30, 30); // many equal-cost paths: ties must break the same way
        var a = new PathFinder(map, Rules).FindPath(new TilePos(1, 1), new TilePos(20, 17), MoveMode.Move)!;
        var pf = new PathFinder(map, Rules);
        pf.FindPath(new TilePos(5, 5), new TilePos(6, 6), MoveMode.Move);
        var b = pf.FindPath(new TilePos(1, 1), new TilePos(20, 17), MoveMode.Move)!;

        Assert.Equal(a, b);
    }

    [Fact]
    public void Same_tile_and_off_map_requests_are_handled()
    {
        var map = new GameMap("flat", 5, 5);
        var pf = new PathFinder(map, Rules);

        Assert.Equal([new TilePos(2, 2)], pf.FindPath(new TilePos(2, 2), new TilePos(2, 2), MoveMode.Move));
        Assert.Null(pf.FindPath(new TilePos(2, 2), new TilePos(9, 9), MoveMode.Move));
    }
}
