using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Tests.Data;
using Xunit;

namespace PointBlank.Sim.Tests.Map;

public class MapTests
{
    private static readonly HashSet<string> Materials = ["brick_wall_sample", "hedge_sample"];

    private static GameMap Small()
    {
        var map = new GameMap("small", 6, 4);
        map.SetTerrain(1, 1, TerrainType.Road);
        map.SetTerrain(2, 1, TerrainType.LightWoods);
        map.SetElevationDm(3, 2, -25);
        map.SetElevationDm(4, 2, 137);
        map.SetHedge(0, 0, EdgeSide.East, true);
        map.SetHedge(5, 2, EdgeSide.South, true);
        map.AddBuilding(new Building(
            "house",
            "brick_wall_sample",
            2,
            [new TilePos(3, 0), new TilePos(4, 0), new TilePos(3, 1)],
            [
                new Opening(new TilePos(3, 1), WallSide.South, OpeningKind.Door, 0),
                new Opening(new TilePos(4, 0), WallSide.North, OpeningKind.Window, 1),
            ]));
        return map;
    }

    [Fact]
    public void TerrainLookups_ReturnTheDataFileValues()
    {
        var table = TerrainTable.FromData(DataLoader.LoadDirectory(DataPaths.DataDirectory()));

        var open = table.Get(TerrainType.Open);
        Assert.Equal(Fixed.FromRatio(1, 10), open.Cover);
        Assert.Equal(Fixed.Zero, open.Concealment);
        Assert.Equal(Fixed.One, open.MoveCostWalk);
        Assert.Equal(Fixed.FromRatio(3, 2), open.MoveCostFast);
        Assert.Equal(Fixed.FromInt(3), open.MoveCostSneak);

        var road = table.Get(TerrainType.Road);
        Assert.Equal(Fixed.FromRatio(4, 5), road.MoveCostWalk);
        Assert.True(road.MoveCostWalk < open.MoveCostWalk);

        var woods = table.Get(TerrainType.LightWoods);
        Assert.Equal(Fixed.FromRatio(2, 5), woods.Cover);
        Assert.Equal(Fixed.FromRatio(1, 2), woods.Concealment);
        Assert.Equal(Fixed.FromInt(8), woods.BlockHeight);

        var hedge = table.Hedge;
        Assert.Equal(Fixed.FromRatio(3, 5), hedge.Cover);
        Assert.Equal(Fixed.FromInt(2), hedge.BlockHeight);
    }

    [Fact]
    public void TerrainTable_RejectsADataSetWithoutTerrain()
    {
        Assert.Throws<DataException>(() => TerrainTable.FromData(new DataSet([])));
    }

    [Fact]
    public void NewMap_IsOpenGroundAtZeroWithTwoMetreTiles()
    {
        var map = new GameMap("m", 3, 2);
        Assert.Equal(TerrainType.Open, map.GetTerrain(2, 1));
        Assert.Equal(Fixed.Zero, map.Elevation(2, 1));
        Assert.Equal(2000, map.TileSizeMm);
        Assert.Equal(Fixed.FromInt(2), map.TileSize);
    }

    [Fact]
    public void Elevation_IsDecimetresReturnedAsMetres()
    {
        var map = Small();
        Assert.Equal(Fixed.FromRatio(137, 10), map.Elevation(4, 2));
        Assert.Equal(Fixed.FromRatio(-25, 10), map.Elevation(3, 2));
    }

    [Fact]
    public void Hedges_AreEdgeFeaturesSeenFromBothSides()
    {
        var map = Small();
        Assert.True(map.HasHedgeBetween(new TilePos(0, 0), new TilePos(1, 0)));
        Assert.True(map.HasHedgeBetween(new TilePos(1, 0), new TilePos(0, 0)));
        Assert.False(map.HasHedgeBetween(new TilePos(0, 0), new TilePos(0, 1)));
        Assert.True(map.HasHedgeBetween(new TilePos(5, 3), new TilePos(5, 2)));
        Assert.Throws<ArgumentException>(() => map.HasHedgeBetween(new TilePos(0, 0), new TilePos(2, 0)));
    }

    [Fact]
    public void Hedge_CannotLeaveTheMap()
    {
        var map = Small();
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetHedge(5, 0, EdgeSide.East, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetHedge(0, 3, EdgeSide.South, true));
    }

    [Fact]
    public void Buildings_CoverTheirFootprintAndKeepOpenings()
    {
        var map = Small();
        var house = map.BuildingAt(3, 1);
        Assert.NotNull(house);
        Assert.Equal("house", house!.Id);
        Assert.Equal(2, house.Floors);
        Assert.Equal(3, house.Tiles.Count);
        Assert.Null(map.BuildingAt(4, 1));
        Assert.Equal(2, house.Openings.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Building_NeedsOneToThreeFloors(int floors)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Building("b", "brick_wall_sample", floors, [new TilePos(0, 0)], []));
    }

    [Fact]
    public void Building_RejectsBadOpeningsAndOverlap()
    {
        Assert.Throws<ArgumentException>(() => new Building(
            "b", "brick_wall_sample", 1, [new TilePos(0, 0)],
            [new Opening(new TilePos(1, 0), WallSide.North, OpeningKind.Door, 0)]));
        Assert.Throws<ArgumentException>(() => new Building(
            "b", "brick_wall_sample", 1, [new TilePos(0, 0)],
            [new Opening(new TilePos(0, 0), WallSide.North, OpeningKind.Door, 1)]));
        Assert.Throws<ArgumentException>(() => new Building(
            "b", "brick_wall_sample", 1, [new TilePos(0, 0), new TilePos(0, 0)], []));

        var map = Small();
        Assert.Throws<ArgumentException>(() => map.AddBuilding(
            new Building("other", "brick_wall_sample", 1, [new TilePos(4, 0)], [])));
        Assert.Throws<ArgumentException>(() => map.AddBuilding(
            new Building("house", "brick_wall_sample", 1, [new TilePos(0, 3)], [])));
        Assert.Throws<ArgumentException>(() => map.AddBuilding(
            new Building("off", "brick_wall_sample", 1, [new TilePos(6, 0)], [])));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsTheMap()
    {
        var map = Small();
        var text = MapFile.Write(map);
        var loaded = MapFile.Parse(text, Materials);

        Assert.Equal(map.Id, loaded.Id);
        Assert.Equal(map.Width, loaded.Width);
        Assert.Equal(map.Height, loaded.Height);
        Assert.Equal(TerrainType.LightWoods, loaded.GetTerrain(2, 1));
        Assert.Equal(137, loaded.GetElevationDm(4, 2));
        Assert.True(loaded.HasHedge(5, 2, EdgeSide.South));
        Assert.Equal("brick_wall_sample", loaded.BuildingAt(4, 0)!.WallMaterial);
        Assert.Equal(map.BuildingAt(3, 0)!.Openings, loaded.BuildingAt(3, 0)!.Openings);
        Assert.Equal(map.ComputeContentHash(), loaded.ComputeContentHash());
        Assert.Equal(text, MapFile.Write(loaded));
    }

    [Fact]
    public void Save_IsIndependentOfInsertionOrder()
    {
        var a = Small();
        var b = new GameMap("small", 6, 4);
        b.SetElevationDm(4, 2, 137);
        b.SetElevationDm(3, 2, -25);
        b.SetHedge(5, 2, EdgeSide.South, true);
        b.SetHedge(0, 0, EdgeSide.East, true);
        b.SetTerrain(2, 1, TerrainType.LightWoods);
        b.SetTerrain(1, 1, TerrainType.Road);
        b.AddBuilding(new Building(
            "house",
            "brick_wall_sample",
            2,
            [new TilePos(3, 1), new TilePos(4, 0), new TilePos(3, 0)],
            [
                new Opening(new TilePos(4, 0), WallSide.North, OpeningKind.Window, 1),
                new Opening(new TilePos(3, 1), WallSide.South, OpeningKind.Door, 0),
            ]));
        Assert.Equal(MapFile.Write(a), MapFile.Write(b));
        Assert.Equal(a.ComputeContentHash(), b.ComputeContentHash());
    }

    [Fact]
    public void Loader_ChecksWallMaterialsWhenGivenTheList()
    {
        var text = MapFile.Write(Small());
        var e = Assert.Throws<MapException>(() => MapFile.Parse(text, new HashSet<string> { "hedge_sample" }));
        Assert.Contains("brick_wall_sample", e.Message);
        Assert.NotNull(MapFile.Parse(text));
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 2", "version")]
    [InlineData("\"width\": 6", "\"width\": 7", "terrain")]
    [InlineData("\"......\"", "\"..x...\"", "unknown terrain")]
    [InlineData("\"floors\": 2", "\"floors\": 4", "floors")]
    [InlineData("\"tile_size_mm\": 2000", "\"tile_size_mm\": 0", "tile_size_mm")]
    public void Loader_RejectsDamagedFiles(string from, string to, string expected)
    {
        var text = MapFile.Write(Small());
        Assert.Contains(from, text);
        var e = Assert.Throws<MapException>(() => MapFile.Parse(text.Replace(from, to)));
        Assert.Contains(expected, e.Message);
    }

    [Fact]
    public void Loader_RejectsGarbage()
    {
        Assert.Throws<MapException>(() => MapFile.Parse("not json"));
        Assert.Throws<MapException>(() => MapFile.Parse("[]"));
    }

    [Fact]
    public void ContentHash_ChangesWithAnyContent()
    {
        var baseline = Small().ComputeContentHash();
        Assert.Equal(baseline, Small().ComputeContentHash());

        var terrain = Small();
        terrain.SetTerrain(0, 3, TerrainType.Road);
        var elevation = Small();
        elevation.SetElevationDm(0, 3, 1);
        var hedge = Small();
        hedge.SetHedge(0, 3, EdgeSide.East, true);
        var building = Small();
        building.AddBuilding(new Building("shed", "brick_wall_sample", 1, [new TilePos(0, 3)], []));

        var hashes = new[] { baseline, terrain.ComputeContentHash(), elevation.ComputeContentHash(),
            hedge.ComputeContentHash(), building.ComputeContentHash() };
        Assert.Equal(hashes.Length, hashes.Distinct().Count());
    }

    [Fact]
    public void MapSystem_FeedsTheWorldStateHash()
    {
        var a = new World(1, new MapSystem(Small()));
        var other = Small();
        other.SetTerrain(0, 3, TerrainType.Road);
        var b = new World(1, new MapSystem(other));
        var c = new World(1, new MapSystem(Small()));

        Assert.NotEqual(a.ComputeStateHash(), b.ComputeStateHash());
        Assert.Equal(a.ComputeStateHash(), c.ComputeStateHash());
        a.Run(5);
        c.Run(5);
        Assert.Equal(a.ComputeStateHash(), c.ComputeStateHash());
    }
}
