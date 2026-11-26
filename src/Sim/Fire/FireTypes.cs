using PointBlank.Sim.Org;

namespace PointBlank.Sim.Fire;

/// <summary>A round's calibre class (ammo data <c>calibre_class</c>), for suppression (item 2.1). Values are hashed, so never renumber.</summary>
public enum CalibreClass : byte
{
    Pistol = 0,
    Rifle = 1,
    Heavy = 2,
}

/// <summary>What stopped a shot. Values are hashed, so never renumber.</summary>
public enum ShotOutcome : byte
{
    /// <summary>Flew to the weapon's max range without hitting anything.</summary>
    Spent = 0,

    /// <summary>Hit a soldier (<see cref="ShotEvent.HitSoldierId"/>), friend or foe.</summary>
    Hit = 1,

    /// <summary>Stopped by the terrain cover of the soldier it reached (the covered part of his body).</summary>
    Cover = 2,

    Hedge = 3,

    /// <summary>A building's outer wall (no penetration until item 4.3).</summary>
    Wall = 4,

    Ground = 5,

    OffMap = 6,
}

/// <summary>A squad's ammunition, as its side sees it. Values are hashed, so never renumber.</summary>
public enum AmmoState : byte
{
    Ok = 0,

    /// <summary>Some of its able, armed men have no rounds left.</summary>
    Partial = 1,

    /// <summary>None of its able, armed men has a round left.</summary>
    OutOfAmmo = 2,
}

/// <summary>
/// One weapon's fire numbers, from <c>weapons.json</c> and its round in <c>ammo.json</c>.
/// <see cref="Dispersion"/> is one standard deviation of angular error, in radians.
/// </summary>
public sealed record WeaponStats(
    string Id,
    Fixed Dispersion,
    Fixed MaxRange,
    Fixed RoundsPerMinute,
    int MagazineCapacity,
    int BurstLength,
    int BurstPauseTicks,
    int ReloadTicks,
    int CarriedRounds,
    CalibreClass Calibre);

/// <summary>
/// One resolved shot (this tick). <see cref="TargetId"/> is the soldier aimed at, or -1 for area fire;
/// <see cref="HitSoldierId"/> the soldier hit (or whose cover stopped it), else -1, with
/// <see cref="HitResult"/> his health after a hit. <see cref="HedgeRolls"/> counts the hedges the shot
/// reached below their top (each one rolled the block chance).
/// </summary>
public readonly record struct ShotEvent(
    long Tick,
    int FirerId,
    int TargetId,
    Fixed OriginX,
    Fixed OriginY,
    Fixed EndX,
    Fixed EndY,
    ShotOutcome Outcome,
    int HitSoldierId,
    HealthState HitResult,
    int HedgeRolls);

/// <summary>
/// A shot passed within the near-miss radius of a soldier (any side) without hitting him: the input to
/// suppression (item 2.1). <see cref="Distance"/> is the horizontal distance of closest approach.
/// </summary>
public readonly record struct NearMissEvent(
    long Tick,
    int SoldierId,
    int FirerId,
    Fixed OriginX,
    Fixed OriginY,
    CalibreClass Calibre,
    Fixed Distance);
