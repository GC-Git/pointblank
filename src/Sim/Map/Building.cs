namespace PointBlank.Sim.Map;

/// <summary>
/// A building: a footprint of tiles, 1 to 3 floors, a wall material (an id in
/// <c>data/materials.json</c>) and its doors and windows. Behaviour (occupying, damage) comes
/// in later items. Tiles and openings are kept in a canonical order.
/// </summary>
public sealed class Building
{
    public const int MinFloors = 1;
    public const int MaxFloors = 3;

    public Building(
        string id,
        string wallMaterial,
        int floors,
        IEnumerable<TilePos> tiles,
        IEnumerable<Opening> openings)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(wallMaterial))
        {
            throw new ArgumentException("A building needs an id and a wall material.");
        }

        if (floors is < MinFloors or > MaxFloors)
        {
            throw new ArgumentOutOfRangeException(
                nameof(floors), $"A building has {MinFloors} to {MaxFloors} floors.");
        }

        var sorted = tiles.OrderBy(t => t).ToArray();
        if (sorted.Length == 0)
        {
            throw new ArgumentException("A building needs at least one tile.", nameof(tiles));
        }

        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i] == sorted[i - 1])
            {
                throw new ArgumentException($"Building '{id}' lists tile {sorted[i]} twice.");
            }
        }

        var footprint = sorted.ToHashSet();
        var ops = openings
            .OrderBy(o => o.Tile).ThenBy(o => o.Side).ThenBy(o => o.Floor).ThenBy(o => o.Kind)
            .ToArray();
        for (var i = 0; i < ops.Length; i++)
        {
            var o = ops[i];
            if (!footprint.Contains(o.Tile))
            {
                throw new ArgumentException($"Building '{id}': opening at {o.Tile} is outside the footprint.");
            }

            if (o.Floor < 0 || o.Floor >= floors)
            {
                throw new ArgumentException($"Building '{id}': opening floor {o.Floor} does not exist.");
            }

            if (i > 0 && ops[i - 1].Tile == o.Tile && ops[i - 1].Side == o.Side && ops[i - 1].Floor == o.Floor)
            {
                throw new ArgumentException($"Building '{id}': two openings on one edge and floor at {o.Tile}.");
            }
        }

        Id = id;
        WallMaterial = wallMaterial;
        Floors = floors;
        Tiles = sorted;
        Openings = ops;
    }

    public string Id { get; }

    public string WallMaterial { get; }

    public int Floors { get; }

    /// <summary>Footprint, in row-major order.</summary>
    public IReadOnlyList<TilePos> Tiles { get; }

    public IReadOnlyList<Opening> Openings { get; }
}
