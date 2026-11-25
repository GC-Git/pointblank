using PointBlank.Sim.Map;

namespace PointBlank.Sim.Sight;

/// <summary>A soldier's posture; it sets the eye height (data table <c>postures</c>).</summary>
public enum Posture : byte
{
    Standing = 0,
    Kneeling = 1,
    Prone = 2,
}

/// <summary>What stopped a line of sight.</summary>
public enum LosBlocker : byte
{
    None = 0,
    Ground = 1,
    Hedge = 2,
    Woods = 3,
    Building = 4,
}

/// <summary>
/// An eye position: metres from the map's north-west corner (x east, y south), a posture and a
/// floor (0 = ground). <c>Floor &gt; 0</c> needs a building on that tile with more floors.
/// </summary>
public readonly record struct Viewpoint(Fixed X, Fixed Y, Posture Posture, int Floor = 0);

/// <summary>
/// The graded result of a line-of-sight check. <see cref="Obscurement"/> (0 to 1) is what the
/// woods and hedges along the ray take away; the target's own concealment is separate. When
/// <see cref="Visible"/> is false, <see cref="Obscurement"/> is 1 and <see cref="Blocker"/> and
/// <see cref="BlockerTile"/> say what blocked (for a hedge: the tile west or north of it, which
/// owns the edge; for a wall: the building tile).
/// </summary>
public readonly record struct LosResult(bool Visible, Fixed Obscurement, LosBlocker Blocker, TilePos BlockerTile);

/// <summary>
/// One tile of a traced ray, for debugging and overlays: the ray's parameter (0 at the first
/// viewpoint, 1 at the second) and height entering and leaving it, the obscurement accumulated
/// so far, and what blocked the ray here (in this tile or on the edge leaving it), if anything.
/// </summary>
public readonly record struct LosStep(
    TilePos Tile,
    Fixed EnterT,
    Fixed ExitT,
    Fixed EnterHeight,
    Fixed ExitHeight,
    Fixed Obscurement,
    LosBlocker Blocker);
