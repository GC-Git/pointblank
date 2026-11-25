using PointBlank.Sim.Map;

namespace PointBlank.Sim.Tests.Map;

/// <summary>
/// Builds the 300 x 300 m test map (150 x 150 tiles of 2 m) with integer arithmetic only. The
/// committed file <c>data/maps/test_300.map.json</c> must equal this map's saved text. To
/// regenerate it after changing this class, run the tests once with PB_WRITE_TEST_MAP=1.
/// </summary>
internal static class TestMapBuilder
{
    public static GameMap Build()
    {
        const int size = 150;
        var map = new GameMap("test_300", size, size);

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                // A hill centred on (105, 110), 15 m high, falling to flat ground; plus a
                // gentle slope across the west side.
                var dx = x - 105;
                var dy = y - 110;
                var hill = Math.Max(0, 150 - (((dx * dx) + (dy * dy)) / 20));
                map.SetElevationDm(x, y, hill + (Math.Max(0, 40 - x) / 4));
            }
        }

        // Road: east-west, two tiles wide.
        for (var x = 0; x < size; x++)
        {
            map.SetTerrain(x, 74, TerrainType.Road);
            map.SetTerrain(x, 75, TerrainType.Road);
        }

        // Light woods in the south-west.
        for (var y = 100; y < 130; y++)
        {
            for (var x = 10; x < 30; x++)
            {
                map.SetTerrain(x, y, TerrainType.LightWoods);
            }
        }

        // A hedge along a field boundary with a gap for a gate, and one running north-south.
        for (var x = 0; x < size; x++)
        {
            if (x is < 70 or > 73)
            {
                map.SetHedge(x, 40, EdgeSide.South, true);
            }
        }

        for (var y = 80; y < 140; y++)
        {
            map.SetHedge(50, y, EdgeSide.East, true);
        }

        map.AddBuilding(Block("farmhouse", 1, 60, 50, 5, 4));
        map.AddBuilding(Block("barn", 2, 90, 50, 8, 5));
        map.AddBuilding(Block("manor", 3, 80, 80, 6, 6));
        return map;
    }

    private static Building Block(string id, int floors, int x0, int y0, int w, int h)
    {
        var tiles = new List<TilePos>();
        for (var y = y0; y < y0 + h; y++)
        {
            for (var x = x0; x < x0 + w; x++)
            {
                tiles.Add(new TilePos(x, y));
            }
        }

        var openings = new List<Opening>
        {
            new(new TilePos(x0 + (w / 2), y0 + h - 1), WallSide.South, OpeningKind.Door, 0),
            new(new TilePos(x0, y0), WallSide.West, OpeningKind.Window, 0),
            new(new TilePos(x0 + w - 1, y0), WallSide.East, OpeningKind.Window, floors - 1),
        };
        return new Building(id, "brick_wall_sample", floors, tiles, openings);
    }
}
