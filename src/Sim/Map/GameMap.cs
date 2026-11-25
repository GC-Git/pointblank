using System.Text;

namespace PointBlank.Sim.Map;

/// <summary>
/// A tile map. Each tile has a terrain type and an elevation; hedges are features on tile
/// edges; buildings are footprints of tiles. Elevation is stored as whole decimetres at the
/// tile centre (so it round-trips exactly) and read as <see cref="Fixed"/> metres.
/// </summary>
public sealed class GameMap
{
    public const int DefaultTileSizeMm = 2000;
    public const int MaxSide = 2000;
    public const int MaxElevationDm = 100000;

    /// <summary>Bump when the layout written by <see cref="ComputeContentHash"/> changes.</summary>
    private const int HashFormatVersion = 1;

    private readonly TerrainType[] _terrain;
    private readonly int[] _elevationDm;
    private readonly bool[] _hedgeEast;
    private readonly bool[] _hedgeSouth;
    private readonly List<Building> _buildings = [];
    private readonly Dictionary<TilePos, Building> _buildingAt = [];

    public GameMap(string id, int width, int height, int tileSizeMm = DefaultTileSizeMm)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("A map needs an id.", nameof(id));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaxSide);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaxSide);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileSizeMm, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tileSizeMm, 100000);

        Id = id;
        Width = width;
        Height = height;
        TileSizeMm = tileSizeMm;
        _terrain = new TerrainType[width * height];
        _elevationDm = new int[width * height];
        _hedgeEast = new bool[width * height];
        _hedgeSouth = new bool[width * height];
    }

    public string Id { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Side of one tile in millimetres. The one place the tile size lives.</summary>
    public int TileSizeMm { get; }

    /// <summary>Side of one tile in metres.</summary>
    public Fixed TileSize => Fixed.FromRatio(TileSizeMm, 1000);

    /// <summary>Buildings in ordinal id order.</summary>
    public IReadOnlyList<Building> Buildings => _buildings;

    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

    public TerrainType GetTerrain(int x, int y) => _terrain[Index(x, y)];

    public void SetTerrain(int x, int y, TerrainType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        _terrain[Index(x, y)] = type;
    }

    /// <summary>Ground height at the tile centre, in whole decimetres.</summary>
    public int GetElevationDm(int x, int y) => _elevationDm[Index(x, y)];

    public void SetElevationDm(int x, int y, int decimetres)
    {
        if (Math.Abs(decimetres) > MaxElevationDm)
        {
            throw new ArgumentOutOfRangeException(nameof(decimetres));
        }

        _elevationDm[Index(x, y)] = decimetres;
    }

    /// <summary>Ground height at the tile centre, in metres.</summary>
    public Fixed Elevation(int x, int y) => Fixed.FromRatio(GetElevationDm(x, y), 10);

    public bool HasHedge(int x, int y, EdgeSide side) =>
        (side == EdgeSide.East ? _hedgeEast : _hedgeSouth)[Index(x, y)];

    /// <summary>Sets the hedge on the east or south edge of a tile. The edge must be inside the map.</summary>
    public void SetHedge(int x, int y, EdgeSide side, bool present)
    {
        var index = Index(x, y);
        if (side == EdgeSide.East ? x + 1 >= Width : y + 1 >= Height)
        {
            throw new ArgumentOutOfRangeException(nameof(side), "A hedge edge must lie between two tiles of the map.");
        }

        (side == EdgeSide.East ? _hedgeEast : _hedgeSouth)[index] = present;
    }

    /// <summary>Whether a hedge stands on the edge between two neighbouring tiles (either order).</summary>
    public bool HasHedgeBetween(TilePos a, TilePos b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (Math.Abs(dx) + Math.Abs(dy) != 1)
        {
            throw new ArgumentException("The tiles must be neighbours (no diagonals).");
        }

        var owner = dx + dy > 0 ? a : b;
        return HasHedge(owner.X, owner.Y, dx != 0 ? EdgeSide.East : EdgeSide.South);
    }

    public Building? BuildingAt(int x, int y) =>
        _buildingAt.TryGetValue(new TilePos(x, y), out var b) ? b : null;

    public void AddBuilding(Building building)
    {
        if (_buildings.Any(b => b.Id == building.Id))
        {
            throw new ArgumentException($"A building with id '{building.Id}' already exists.");
        }

        foreach (var tile in building.Tiles)
        {
            if (!InBounds(tile.X, tile.Y))
            {
                throw new ArgumentException($"Building '{building.Id}': tile {tile} is off the map.");
            }

            if (_buildingAt.TryGetValue(tile, out var other))
            {
                throw new ArgumentException(
                    $"Building '{building.Id}' overlaps '{other.Id}' at {tile}.");
            }
        }

        foreach (var tile in building.Tiles)
        {
            _buildingAt[tile] = building;
        }

        _buildings.Add(building);
        _buildings.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
    }

    /// <summary>
    /// FNV-1a fingerprint of everything in the map (not its id: replays carry that). The same
    /// content always gives the same value, however it was built.
    /// </summary>
    public ulong ComputeContentHash()
    {
        var h = new StateHasher();
        h.Add(HashFormatVersion);
        h.Add(Width);
        h.Add(Height);
        h.Add(TileSizeMm);
        foreach (var t in _terrain)
        {
            h.Add((byte)t);
        }

        foreach (var e in _elevationDm)
        {
            h.Add(e);
        }

        foreach (var f in _hedgeEast)
        {
            h.Add(f);
        }

        foreach (var f in _hedgeSouth)
        {
            h.Add(f);
        }

        h.Add(_buildings.Count);
        foreach (var b in _buildings)
        {
            AddText(h, b.Id);
            AddText(h, b.WallMaterial);
            h.Add(b.Floors);
            h.Add(b.Tiles.Count);
            foreach (var t in b.Tiles)
            {
                h.Add(t.X);
                h.Add(t.Y);
            }

            h.Add(b.Openings.Count);
            foreach (var o in b.Openings)
            {
                h.Add(o.Tile.X);
                h.Add(o.Tile.Y);
                h.Add((byte)o.Side);
                h.Add((byte)o.Kind);
                h.Add(o.Floor);
            }
        }

        return h.Value;
    }

    private static void AddText(StateHasher h, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        h.Add(bytes.Length);
        foreach (var b in bytes)
        {
            h.Add(b);
        }
    }

    private int Index(int x, int y)
    {
        if (!InBounds(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Tile ({x}, {y}) is off the {Width} x {Height} map.");
        }

        return (y * Width) + x;
    }
}
