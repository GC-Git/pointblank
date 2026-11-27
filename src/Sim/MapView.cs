using PointBlank.Sim.Map;

namespace PointBlank.Sim;

/// <summary>One tile as drawn: terrain, ground elevation in decimetres, and the hedges it owns on its east and south edges.</summary>
public readonly record struct MapTileView(TerrainType Terrain, int ElevationDm, bool HedgeEast, bool HedgeSouth);

/// <summary>A copy of a building: its footprint and openings. <see cref="Floors"/> counts storeys.</summary>
public sealed record BuildingView(string Id, int Floors, IReadOnlyList<TilePos> Tiles, IReadOnlyList<Opening> Openings);

/// <summary>
/// A copy of the map (item 2.5). Both sides know the ground, so this holds nothing about units. <see cref="Tiles"/> is in
/// row-major order; <see cref="TileSize"/> is in metres. It is a snapshot: ask again to see later changes to the map.
/// </summary>
public sealed record MapView(
    string Id, int Width, int Height, Fixed TileSize, IReadOnlyList<MapTileView> Tiles, IReadOnlyList<BuildingView> Buildings)
{
    public MapTileView Tile(int x, int y) => Tiles[(y * Width) + x];

    internal static MapView Of(GameMap map)
    {
        var tiles = new MapTileView[map.Width * map.Height];
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                tiles[(y * map.Width) + x] = new MapTileView(
                    map.GetTerrain(x, y), map.GetElevationDm(x, y), map.HasHedge(x, y, EdgeSide.East), map.HasHedge(x, y, EdgeSide.South));
            }
        }

        return new MapView(
            map.Id, map.Width, map.Height, map.TileSize, Array.AsReadOnly(tiles),
            map.Buildings.Select(b => new BuildingView(b.Id, b.Floors, b.Tiles.ToArray(), b.Openings.ToArray())).ToArray());
    }
}
