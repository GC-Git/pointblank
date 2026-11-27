using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Spotting;

/// <summary>How a target was moving during the last refresh window. Values are hashed, so never renumber.</summary>
public enum TargetMotion : byte
{
    Still = 0,
    Sneak = 1,
    Move = 2,
    MoveFast = 3,
}

/// <summary>
/// The numbers spotting uses, from the <c>spotting</c> data table (entry <c>default</c>). A record,
/// so tests can vary one value with <c>with</c>.
/// <para>
/// Spotting progress per second for one observer–target pair with a visible line of sight is
/// <c>base_rate × distance × (1 − concealment) × (1 − obscurement) × posture × motion × firing ×
/// observer</c>; the target is spotted when the progress reaches 1. Distance is 1 up to
/// <see cref="NearRange"/>, <c>near_range / d</c> beyond it and 0 past <see cref="MaxRange"/>.
/// Concealment is the terrain value of the target's tile; obscurement is what the ray's woods and
/// hedges take away (<see cref="LosResult.Obscurement"/>).
/// </para>
/// </summary>
public sealed record SpottingRules
{
    private const string EntryId = "default";

    /// <summary>The highest allowed max range, so that its square fits in <see cref="Fixed"/>.</summary>
    public const int MaxRangeLimit = 40_000;

    private Fixed[] _posture = new Fixed[3];
    private Fixed[] _motion = new Fixed[4];
    private Fixed[] _concealment = [];

    private SpottingRules()
    {
    }

    /// <summary>Each observer re-checks every enemy once per this many ticks.</summary>
    public int RefreshTicks { get; init; }

    /// <summary>Progress per second in the best case (see the type's summary).</summary>
    public Fixed BaseRate { get; init; }

    public Fixed NearRange { get; init; }

    public Fixed MaxRange { get; init; }

    /// <summary>Rate factor for a target that fired within <see cref="FiringWindowTicks"/>.</summary>
    public Fixed FiringFactor { get; init; }

    public int FiringWindowTicks { get; init; }

    /// <summary>
    /// A man within this distance (m) of a hedge that lies between him and the observer is at a firing position behind it
    /// (item 2.4 follow-up). 0 turns the rule off. Still and not firing, he can be spotted only within <see cref="HedgeStillRange"/>;
    /// moving, only within <see cref="HedgeMovingRange"/>; firing, at any range (the usual rate and firing factor).
    /// </summary>
    public Fixed HedgePositionDistance { get; init; }

    public Fixed HedgeStillRange { get; init; }

    public Fixed HedgeMovingRange { get; init; }

    /// <summary>
    /// Close-range spotting (item 2.4 follow-up): within this distance (m) a man in sight is noticed at no less than
    /// <see cref="CloseRate"/> × (1 − d / range) × (1 − obscurement) × the observer factor, whatever his posture or cover. 0: off.
    /// </summary>
    public Fixed CloseRange { get; init; }

    /// <summary>The close-range spotting rate (progress per second) at zero distance.</summary>
    public Fixed CloseRate { get; init; }

    /// <summary>An unseen contact becomes Lost this many ticks after it was last seen.</summary>
    public int LostAfterTicks { get; init; }

    /// <summary>An unseen contact is forgotten this many ticks after it was last seen.</summary>
    public int ForgetAfterTicks { get; init; }

    public Fixed PostureFactor(Posture posture) => _posture[(int)posture];

    public Fixed MotionFactor(TargetMotion motion) => _motion[(int)motion];

    /// <summary>The concealment of a terrain type (from the <c>terrain</c> table), 0 to 1.</summary>
    public Fixed Concealment(TerrainType type) => _concealment[(int)type];

    /// <summary>Spotting progress per second (see the type's summary). Zero past the max range.</summary>
    public Fixed Rate(
        Fixed distance, Fixed concealment, Fixed obscurement, Posture posture, TargetMotion motion, bool firing, Fixed observerFactor)
    {
        if (distance > MaxRange)
        {
            return Fixed.Zero;
        }

        var rate = BaseRate * PostureFactor(posture) * MotionFactor(motion) * observerFactor
            * Clamp01(Fixed.One - concealment) * Clamp01(Fixed.One - obscurement);
        if (firing)
        {
            rate *= FiringFactor;
        }

        return distance > NearRange ? rate * NearRange / distance : rate;
    }

    /// <exception cref="DataException">The entry is missing or a value is out of range.</exception>
    public static SpottingRules FromData(DataSet data)
    {
        var e = data.Table("spotting").Entries.FirstOrDefault(x => x.Id == EntryId)
            ?? throw new DataException($"spotting: no '{EntryId}' entry.");
        Fixed Num(string name, Fixed min, Fixed max)
        {
            var v = e.Get(name).Number ?? throw new DataException($"spotting/{EntryId}/{name}: not a number.");
            return v < min || v > max ? throw new DataException($"spotting/{EntryId}/{name}: {v} is out of range.") : v;
        }

        var big = Fixed.FromInt(100_000);
        var terrain = TerrainTable.FromData(data);
        Fixed Factor(string name) => Num(name, Fixed.Zero, Fixed.FromInt(100));
        int Ticks(string name) => (Num(name, Fixed.Zero, big) * Fixed.FromInt(World.TicksPerSecond)).ToInt();

        var rules = new SpottingRules
        {
            RefreshTicks = Num("refresh_ticks", Fixed.One, Fixed.FromInt(100)).ToInt(),
            BaseRate = Factor("base_rate"),
            NearRange = Num("near_range", Fixed.One, big),
            MaxRange = Num("max_range", Fixed.One, Fixed.FromInt(MaxRangeLimit)),
            FiringFactor = Factor("firing_factor"),
            FiringWindowTicks = Ticks("firing_window"),
            HedgePositionDistance = Num("hedge_position_distance", Fixed.Zero, big),
            HedgeStillRange = Num("hedge_still_range", Fixed.Zero, big),
            HedgeMovingRange = Num("hedge_moving_range", Fixed.Zero, big),
            CloseRange = Num("close_range", Fixed.Zero, big),
            CloseRate = Num("close_rate", Fixed.Zero, Fixed.FromInt(100)),
            LostAfterTicks = Ticks("lost_after"),
            ForgetAfterTicks = Ticks("forget_after"),
            _posture = [Factor("posture_standing"), Factor("posture_kneeling"), Factor("posture_prone")],
            _concealment = Enum.GetValues<TerrainType>().Select(t => terrain.Get(t).Concealment).ToArray(),
            _motion = [Factor("motion_still"), Factor("motion_sneak"), Factor("motion_move"), Factor("motion_move_fast")],
        };
        if (rules.LostAfterTicks > rules.ForgetAfterTicks)
        {
            throw new DataException($"spotting/{EntryId}: lost_after cannot exceed forget_after.");
        }

        return rules;
    }

    private static Fixed Clamp01(Fixed v) => v < Fixed.Zero ? Fixed.Zero : v > Fixed.One ? Fixed.One : v;
}
