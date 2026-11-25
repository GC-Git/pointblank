using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Tests.Data;
using Xunit;

namespace PointBlank.Sim.Tests.Map;

public class TestMapTests
{
    private static string MapPath() =>
        Path.Combine(DataPaths.DataDirectory(), "maps", "test_300.map.json");

    private static readonly DataSet Data = DataLoader.LoadDirectory(DataPaths.DataDirectory());

    [Fact]
    public void MapLoader_RejectsAWallMaterialThatIsNotInTheData()
    {
        var text = File.ReadAllText(MapPath());
        var e = Assert.Throws<MapException>(() => MapLoader.Parse(text.Replace("brick_wall_sample", "no_such_material"), Data));
        Assert.Contains("no_such_material", e.Message);
    }

    [Fact]
    public void TestMapFile_IsThe300MetreMapAndMatchesTheGenerator()
    {
        var expected = MapFile.Write(TestMapBuilder.Build());
        if (Environment.GetEnvironmentVariable("PB_WRITE_TEST_MAP") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MapPath())!);
            File.WriteAllText(MapPath(), expected);
        }

        var text = File.ReadAllText(MapPath());
        Assert.Equal(expected, text);

        var map = MapLoader.Parse(text, Data);
        Assert.Equal(150, map.Width);
        Assert.Equal(150, map.Height);
        Assert.Equal(Fixed.FromInt(300), map.TileSize * Fixed.FromInt(map.Width));
        Assert.Equal(text, MapFile.Write(map));
    }

    [Fact]
    public void TestMap_HasEveryFeature()
    {
        var map = MapLoader.Load(MapPath(), Data);
        var types = new HashSet<TerrainType>();
        var elevations = new HashSet<int>();
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                types.Add(map.GetTerrain(x, y));
                elevations.Add(map.GetElevationDm(x, y));
            }
        }

        Assert.Equal(3, types.Count);
        Assert.True(elevations.Count > 10);
        Assert.NotEmpty(map.Buildings);
        Assert.Contains(map.Buildings, b => b.Floors == 1);
        Assert.Contains(map.Buildings, b => b.Floors == 3);
        Assert.Contains(Enumerable.Range(0, map.Width), x => map.HasHedge(x, 40, EdgeSide.South));
    }
}
