using PointBlank.Sim.Data;

namespace PointBlank.Sim.Morale;

/// <summary>
/// Why a squad's morale is where it is, shown to its own side (anti-goal: the player must be able to
/// tell why a unit is pinned or broken). Values are hashed, so never renumber.
/// </summary>
[Flags]
public enum MoraleReason : ushort
{
    None = 0,

    /// <summary>The squad is suppressed (suppression drains morale and stops recovery).</summary>
    UnderFire = 1,

    /// <summary>The squad is pinned by suppression.</summary>
    Pinned = 2,

    /// <summary>The squad has lost men.</summary>
    Casualties = 4,

    /// <summary>At least <c>heavy_casualty_fraction</c> of the squad is down.</summary>
    HeavyCasualties = 8,

    /// <summary>The squad leader went down within <c>leader_killed_memory</c>.</summary>
    LeaderKilled = 16,

    /// <summary>No effective leader now.</summary>
    Leaderless = 32,

    /// <summary>No friendly squad within <c>support_radius</c>.</summary>
    Isolated = 64,

    /// <summary>Out of contact with every HQ of the side (see <c>CommandSystem.InContact</c>).</summary>
    OutOfContact = 128,

    /// <summary>A friendly squad is within <c>support_radius</c> (softer shocks, faster recovery).</summary>
    Supported = 256,

    /// <summary>An HQ of the side is within <c>hq_rally_radius</c> (faster recovery).</summary>
    NearHq = 512,

    /// <summary>Broken or routing under shellfire (item 2.4): the men cower flat where they are until the shelling lifts.</summary>
    Cowering = 1024,
}

/// <summary>Helpers for <see cref="MoraleReason"/>.</summary>
public static class MoraleReasons
{
    /// <summary>Most important first: the order in which the UI should list reasons.</summary>
    public static readonly MoraleReason[] Priority =
    [
        MoraleReason.Cowering, MoraleReason.LeaderKilled, MoraleReason.HeavyCasualties, MoraleReason.Pinned, MoraleReason.UnderFire,
        MoraleReason.Leaderless, MoraleReason.Isolated, MoraleReason.OutOfContact, MoraleReason.Casualties,
        MoraleReason.NearHq, MoraleReason.Supported,
    ];

    /// <summary>The reasons set in <paramref name="reasons"/>, most important first, at most <paramref name="count"/>.</summary>
    public static IReadOnlyList<MoraleReason> Top(MoraleReason reasons, int count) =>
        Priority.Where(r => (reasons & r) != 0).Take(count).ToArray();
}

/// <summary>
/// The numbers the morale model uses, from the <c>morale</c> table (entry <c>default</c>). A record, so tests can
/// vary one value with <c>with</c>. Levels run from 0 to <see cref="MaxLevel"/>; see <see cref="MoraleSystem"/>.
/// </summary>
public sealed record MoraleRules
{
    public static readonly Fixed MaxLevel = Fixed.FromInt(100);

    private const string EntryId = "default";

    private MoraleRules()
    {
    }

    public Fixed StartLevel { get; init; }

    /// <summary>Points per second at squad suppression 100.</summary>
    public Fixed SuppressionDrain { get; init; }

    public Fixed CasualtyShock { get; init; }

    public Fixed ShockSpreadMin { get; init; }

    public Fixed ShockSpreadMax { get; init; }

    public Fixed LeaderShock { get; init; }

    public Fixed SupportShockReduction { get; init; }

    public Fixed CasualtyCeiling { get; init; }

    public Fixed LeaderlessCeiling { get; init; }

    public Fixed IsolatedCeiling { get; init; }

    public Fixed OutOfContactCeiling { get; init; }

    public Fixed DriftRate { get; init; }

    public Fixed RecoveryRate { get; init; }

    public Fixed LeaderRecoveryWeight { get; init; }

    public Fixed SupportRecoveryBonus { get; init; }

    public Fixed HqRallyBonus { get; init; }

    public Fixed SupportRadius { get; init; }

    public Fixed HqRallyRadius { get; init; }

    public int LossWindowTicks { get; init; }

    public int LeaderKilledMemoryTicks { get; init; }

    public Fixed HeavyCasualtyFraction { get; init; }

    public Fixed ShakenBelow { get; init; }

    public Fixed ShakenRelease { get; init; }

    public Fixed BrokenBelow { get; init; }

    public Fixed BrokenRelease { get; init; }

    public Fixed RoutBelow { get; init; }

    public Fixed SteadyAbove { get; init; }

    public Fixed RoutDistance { get; init; }

    /// <summary>A routing squad runs from the mean bearing of the known enemies within this distance (item 2.3).</summary>
    public Fixed RoutThreatRadius { get; init; }

    /// <summary>A broken or routing squad with an able man within this distance (m) of a shell burst cowers (item 2.4).</summary>
    public Fixed CowerRadius { get; init; }

    /// <summary>It bolts (or recovers its feet) only after this many ticks with no burst within <see cref="CowerRadius"/>.</summary>
    public int CowerLiftTicks { get; init; }

    /// <exception cref="DataException">The entry is missing or a value is out of range.</exception>
    public static MoraleRules FromData(DataSet data)
    {
        var e = data.Table("morale").Entries.FirstOrDefault(x => x.Id == EntryId)
            ?? throw new DataException($"morale: no '{EntryId}' entry.");
        Fixed F(string name, Fixed max)
        {
            var v = e.Get(name).Number ?? throw new DataException($"morale/{e.Id}/{name}: not a number.");
            return v < Fixed.Zero || v > max ? throw new DataException($"morale/{e.Id}/{name}: {v} is out of range.") : v;
        }

        var big = Fixed.FromInt(10_000);
        Fixed Level(string name) => F(name, MaxLevel);
        Fixed Ratio(string name) => F(name, Fixed.One);
        int Ticks(string name) => (F(name, big) * Fixed.FromInt(World.TicksPerSecond)).ToInt();
        var r = new MoraleRules
        {
            StartLevel = Level("start_level"),
            SuppressionDrain = F("suppression_drain", big),
            CasualtyShock = Level("casualty_shock"),
            ShockSpreadMin = F("shock_spread_min", Fixed.FromInt(10)),
            ShockSpreadMax = F("shock_spread_max", Fixed.FromInt(10)),
            LeaderShock = Level("leader_shock"),
            SupportShockReduction = Ratio("support_shock_reduction"),
            CasualtyCeiling = Level("casualty_ceiling"),
            LeaderlessCeiling = Level("leaderless_ceiling"),
            IsolatedCeiling = Level("isolated_ceiling"),
            OutOfContactCeiling = Level("out_of_contact_ceiling"),
            DriftRate = F("drift_rate", big),
            RecoveryRate = F("recovery_rate", big),
            LeaderRecoveryWeight = F("leader_recovery_weight", big),
            SupportRecoveryBonus = F("support_recovery_bonus", big),
            HqRallyBonus = F("hq_rally_bonus", big),
            SupportRadius = F("support_radius", big),
            HqRallyRadius = F("hq_rally_radius", big),
            LossWindowTicks = Ticks("loss_window"),
            LeaderKilledMemoryTicks = Ticks("leader_killed_memory"),
            HeavyCasualtyFraction = Ratio("heavy_casualty_fraction"),
            ShakenBelow = Level("shaken_below"),
            ShakenRelease = Level("shaken_release"),
            BrokenBelow = Level("broken_below"),
            BrokenRelease = Level("broken_release"),
            RoutBelow = Level("rout_below"),
            SteadyAbove = Level("steady_above"),
            RoutDistance = F("rout_distance", big),
            RoutThreatRadius = F("rout_threat_radius", big),
            CowerRadius = F("cower_radius", big),
            CowerLiftTicks = (F("cower_lift", big) * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
        };
        if (r.ShockSpreadMin > r.ShockSpreadMax || !(r.RoutBelow <= r.BrokenBelow && r.BrokenBelow < r.BrokenRelease
            && r.BrokenRelease <= r.ShakenBelow && r.ShakenBelow <= r.ShakenRelease && r.BrokenRelease <= r.SteadyAbove))
        {
            throw new DataException(
                "morale/default: needs shock_spread_min <= shock_spread_max and rout_below <= broken_below < broken_release <= shaken_below <= shaken_release, broken_release <= steady_above.");
        }

        return r;
    }
}
