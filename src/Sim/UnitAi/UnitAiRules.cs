using PointBlank.Sim.Data;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim.UnitAi;

/// <summary>
/// Coarse bearings and distances for what a side learns from fire it cannot see (item 2.3): 16 sectors of 22.5
/// degrees, measured from east (+x) toward south (+y, map y grows southward), and distances rounded to a band.
/// </summary>
public static class Bearings
{
    public const int Sectors = 16;

    // cos(k × 22.5°) for k = 0..4, to six decimals: geometry, not data.
    private static readonly Fixed[] Quarter =
    [
        Fixed.One, Fixed.FromRatio(923880, 1000000), Fixed.FromRatio(707107, 1000000), Fixed.FromRatio(382683, 1000000), Fixed.Zero,
    ];

    /// <summary>The unit vector of a sector (0 east, 4 south, 8 west, 12 north).</summary>
    public static (Fixed X, Fixed Y) Dir(int sector)
    {
        var k = ((sector % Sectors) + Sectors) % Sectors;
        return (Cos(k), Cos(k - 4));
    }

    /// <summary>The sector nearest the direction (dx, dy); 0 for a zero vector. Ties go to the lower sector.</summary>
    public static int SectorOf(Fixed dx, Fixed dy)
    {
        var best = 0;
        var bestDot = Fixed.MinValue;
        for (var k = 0; k < Sectors; k++)
        {
            var (x, y) = Dir(k);
            var dot = (x * dx) + (y * dy);
            if (dot > bestDot)
            {
                (best, bestDot) = (k, dot);
            }
        }

        return best;
    }

    /// <summary>The smallest number of sectors between two sectors (0 to 8).</summary>
    public static int Between(int a, int b)
    {
        var d = (((a - b) % Sectors) + Sectors) % Sectors;
        return d > Sectors / 2 ? Sectors - d : d;
    }

    /// <summary>A distance rounded to the nearest multiple of <paramref name="band"/>, at least one band.</summary>
    public static Fixed Band(Fixed distance, Fixed band)
    {
        var n = ((distance / band) + Fixed.FromRatio(1, 2)).ToInt();
        return band * Fixed.FromInt(Math.Max(1, n));
    }

    private static Fixed Cos(int k)
    {
        k = ((k % Sectors) + Sectors) % Sectors;
        return k <= 4 ? Quarter[k] : k <= 8 ? -Quarter[8 - k] : k <= 12 ? -Quarter[k - 8] : Quarter[16 - k];
    }
}

/// <summary>
/// A squad leader's task from <c>unit_ai_tasks.json</c>: when a contact of <see cref="TargetKind"/> (null: any) is
/// known, the team of the man with <see cref="Role"/> goes to the best covered tile within <see cref="SearchRadius"/>
/// of a point <see cref="FlankOffset"/> beyond the end of the firing line.
/// </summary>
public sealed record AiTaskRule(string Id, Role Role, ContactKind? TargetKind, Fixed FlankOffset, Fixed SearchRadius);

/// <summary>The unit AI's numbers (<c>unit_ai.json</c>, entry <c>default</c>) and tasks. Times are in ticks.</summary>
public sealed class UnitAiRules
{
    public const string EntryId = "default";

    public int ReactionTicks { get; init; }

    public int UnderFireMemoryTicks { get; init; }

    public Fixed DistanceBand { get; init; }

    public Fixed EffectiveRangeFactor { get; init; }

    public Fixed CoverMin { get; init; }

    public Fixed CoverSearchRadius { get; init; }

    /// <summary>How far (m) a man of a squad ordered to Defend or Ambush looks for a firing position at a hedge (item 2.4 follow-up).</summary>
    public Fixed DefendSearchRadius { get; init; }

    /// <summary>Fire discipline (item 2.4 follow-up): a man at or above this suppression level keeps his head down and does not fire.</summary>
    public Fixed FireHoldSuppression { get; init; }

    /// <summary>The squad leader and MG gunner open fire within this many ticks of the squad's fire task starting.</summary>
    public int OpenDelayTicks { get; init; }

    /// <summary>The other men join between these many ticks after the start (each his own, deterministic draw).</summary>
    public int JoinDelayMinTicks { get; init; }

    public int JoinDelayMaxTicks { get; init; }

    /// <summary>Chance a man stays silent through a fire task; <see cref="ShakenSilentChance"/> when his squad is shaken, pinned or rallying.</summary>
    public Fixed SilentChance { get; init; }

    public Fixed ShakenSilentChance { get; init; }

    /// <summary>A squad on Defend or Ambush opens fire on its own only at enemies within this range (m), unless fired on.</summary>
    public Fixed DefendOpenRange { get; init; }

    /// <summary>Per-man bolt (item 2.4): each man of a routing squad gets up within this many ticks of the flee starting (the leader within a quarter of it).</summary>
    public int BoltDelayTicks { get; init; }

    /// <summary>Extra ticks before a man gets up at suppression 100 (linear).</summary>
    public int BoltSuppressionTicks { get; init; }

    /// <summary>Extra ticks for a man whose last shell burst was at 0 m, falling linearly to 0 at <see cref="BoltBurstRadius"/>.</summary>
    public int BoltBurstTicks { get; init; }

    public Fixed BoltBurstRadius { get; init; }

    /// <summary>Each man's rush and drop cycle is offset by up to this many ticks (his own draw).</summary>
    public int RushJitterTicks { get; init; }

    /// <summary>The longest a man takes to change posture on a unit-AI decision (<c>ai_posture_delay</c>).</summary>
    public int AiPostureDelayTicks { get; init; }

    /// <summary>Item 2.4c: a man masked this long (his own draw between min and max) steps aside for a clear line.</summary>
    public int UnmaskDelayMinTicks { get; init; }

    public int UnmaskDelayMaxTicks { get; init; }

    /// <summary>After looking for a clear line (found or not), a man does not look again for this long.</summary>
    public int UnmaskRetryTicks { get; init; }

    /// <summary>How far from where he is a masked man looks for a clear line.</summary>
    public Fixed UnmaskSearchRadius { get; init; }

    public Fixed LineSpacing { get; init; }

    public int LineReformSectors { get; init; }

    public Fixed BoundDistance { get; init; }

    public int StallTicks { get; init; }

    public int RushTicks { get; init; }

    public int DownTicks { get; init; }

    public Posture PostureOpen { get; init; }

    public Posture PostureCover { get; init; }

    public Posture PostureContact { get; init; }

    public IReadOnlyList<AiTaskRule> Tasks { get; init; } = [];

    /// <exception cref="DataException">An entry is missing or a value is out of range.</exception>
    public static UnitAiRules FromData(DataSet data)
    {
        var e = data.Table("unit_ai").Entries.FirstOrDefault(x => x.Id == EntryId)
            ?? throw new DataException($"unit_ai: no '{EntryId}' entry.");
        var big = Fixed.FromInt(10_000);
        Fixed F(DataEntry entry, string table, string name, Fixed min, Fixed max)
        {
            var v = entry.Get(name).Number ?? throw new DataException($"{table}/{entry.Id}/{name}: not a number.");
            return v < min || v > max ? throw new DataException($"{table}/{entry.Id}/{name}: {v} is out of range.") : v;
        }

        Fixed N(string name) => F(e, "unit_ai", name, Fixed.Zero, big);
        Fixed Pos(string name) => F(e, "unit_ai", name, Fixed.FromRatio(1, 1000), big);
        int Ticks(string name) => (N(name) * Fixed.FromInt(World.TicksPerSecond)).ToInt();
        Posture Posture(string name) => e.Get(name).Text switch
        {
            "standing" => Sight.Posture.Standing,
            "kneeling" => Sight.Posture.Kneeling,
            "prone" => Sight.Posture.Prone,
            var t => throw new DataException($"unit_ai/{e.Id}/{name}: '{t}' is not a posture."),
        };

        var tasks = new List<AiTaskRule>();
        foreach (var t in data.Table("unit_ai_tasks").Entries)
        {
            var role = OrgTables.RoleOf(t.Get("role").Text ?? "") ?? throw new DataException($"unit_ai_tasks/{t.Id}/role: unknown role.");
            ContactKind? kind = t.Get("target_kind").Text switch
            {
                "any" => null,
                "infantry" => ContactKind.Infantry,
                "machine_gun" => ContactKind.MachineGun,
                var k => throw new DataException($"unit_ai_tasks/{t.Id}/target_kind: '{k}' is not infantry, machine_gun or any."),
            };
            tasks.Add(new AiTaskRule(
                t.Id, role, kind, F(t, "unit_ai_tasks", "flank_offset", Fixed.Zero, big), F(t, "unit_ai_tasks", "search_radius", Fixed.Zero, big)));
        }

        return new UnitAiRules
        {
            ReactionTicks = Ticks("reaction_time"),
            UnderFireMemoryTicks = Ticks("under_fire_memory"),
            DistanceBand = Pos("distance_band"),
            EffectiveRangeFactor = F(e, "unit_ai", "effective_range_factor", Fixed.Zero, Fixed.One),
            CoverMin = F(e, "unit_ai", "cover_min", Fixed.Zero, Fixed.One),
            CoverSearchRadius = N("cover_search_radius"),
            DefendSearchRadius = N("defend_search_radius"),
            FireHoldSuppression = N("fire_hold_suppression"),
            OpenDelayTicks = (N("open_delay") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            JoinDelayMinTicks = (N("join_delay_min") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            JoinDelayMaxTicks = (N("join_delay_max") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            SilentChance = N("silent_chance"),
            ShakenSilentChance = N("shaken_silent_chance"),
            DefendOpenRange = N("defend_open_range"),
            BoltDelayTicks = (N("bolt_delay") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            BoltSuppressionTicks = (N("bolt_suppression_delay") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            BoltBurstTicks = (N("bolt_burst_delay") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            BoltBurstRadius = N("bolt_burst_radius"),
            AiPostureDelayTicks = (N("ai_posture_delay") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            RushJitterTicks = (N("rush_jitter") * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            UnmaskDelayMinTicks = Ticks("unmask_delay_min"),
            UnmaskDelayMaxTicks = Math.Max(Ticks("unmask_delay_min"), Ticks("unmask_delay_max")),
            UnmaskRetryTicks = Math.Max(1, Ticks("unmask_retry_time")),
            UnmaskSearchRadius = N("unmask_search_radius"),
            LineSpacing = Pos("line_spacing"),
            LineReformSectors = Math.Max(1, N("line_reform_sectors").ToInt()),
            BoundDistance = N("bound_distance"),
            StallTicks = Math.Max(1, Ticks("stall_time")),
            RushTicks = Math.Max(1, Ticks("rush_time")),
            DownTicks = Math.Max(1, Ticks("down_time")),
            PostureOpen = Posture("posture_open"),
            PostureCover = Posture("posture_cover"),
            PostureContact = Posture("posture_contact"),
            Tasks = tasks,
        };
    }
}
