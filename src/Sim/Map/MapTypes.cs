namespace PointBlank.Sim.Map;

/// <summary>A map file is malformed or describes an impossible map.</summary>
public sealed class MapException(string message) : FormatException(message);

/// <summary>What a tile is made of. Hedges are not here: they are edge features.</summary>
public enum TerrainType : byte
{
    Open = 0,
    Road = 1,
    LightWoods = 2,
}

/// <summary>
/// Which edge of a tile a hedge sits on. Only east and south exist, so every edge between two
/// tiles has exactly one owner (the tile to its west or north).
/// </summary>
public enum EdgeSide : byte
{
    East = 0,
    South = 1,
}

/// <summary>A side of a building tile, for doors and windows.</summary>
public enum WallSide : byte
{
    North = 0,
    East = 1,
    South = 2,
    West = 3,
}

public enum OpeningKind : byte
{
    Door = 0,
    Window = 1,
}

/// <summary>A tile position: x grows east, y grows south.</summary>
public readonly record struct TilePos(int X, int Y) : IComparable<TilePos>
{
    /// <summary>Row-major order: by y, then x.</summary>
    public int CompareTo(TilePos other)
    {
        var c = Y.CompareTo(other.Y);
        return c != 0 ? c : X.CompareTo(other.X);
    }
}

/// <summary>A door or window on one edge of a building tile, on one floor.</summary>
public sealed record Opening(TilePos Tile, WallSide Side, OpeningKind Kind, int Floor);

/// <summary>
/// The cover, concealment, movement cost, block height and line-of-sight obscurement of one
/// terrain type, from the data file. Obscurement is per metre for tiles, per crossing for hedges.
/// </summary>
public sealed record TerrainProperties(
    Fixed Cover,
    Fixed Concealment,
    Fixed MoveCostWalk,
    Fixed MoveCostFast,
    Fixed MoveCostSneak,
    Fixed BlockHeight,
    Fixed LosObscurement);
