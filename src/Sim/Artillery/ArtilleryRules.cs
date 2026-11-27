using PointBlank.Sim.Data;
using PointBlank.Sim.Map;

namespace PointBlank.Sim.Artillery;

/// <summary>
/// One side's off-map battery and its HE shell, from an entry of the <c>artillery</c> table. A record, so tests can
/// vary one value with <c>with</c>. Times are in ticks; distances in metres.
/// </summary>
public sealed record BatteryRules
{
    public string Id { get; init; } = "";

    public string Side { get; init; } = "";

    /// <summary>Fire missions the side may call per battle.</summary>
    public int Missions { get; init; }

    public int CallTicks { get; init; }

    public int FlightTicks { get; init; }

    public int Guns { get; init; }

    public int RoundsPerGun { get; init; }

    public int DurationTicks { get; init; }

    /// <summary>Standard deviation per axis of the mission's mean point of impact.</summary>
    public Fixed AimError { get; init; }

    /// <summary>Standard deviation per axis of each round around the mean point of impact.</summary>
    public Fixed Scatter { get; init; }

    public Fixed LethalRadius { get; init; }

    public Fixed CasualtyRadius { get; init; }

    public Fixed LethalChance { get; init; }

    public Fixed DirectHitRadius { get; init; }

    public Fixed KneelingFactor { get; init; }

    public Fixed ProneFactor { get; init; }

    public Fixed ShelterFactor { get; init; }

    public Fixed BuildingFactor { get; init; }

    public Fixed CoverWeight { get; init; }

    public Fixed KillChance { get; init; }

    public Fixed IncapacitateChance { get; init; }

    public Fixed Suppression { get; init; }

    public Fixed SuppressionRadius { get; init; }

    public Fixed MoraleShock { get; init; }

    public Fixed MoraleRadius { get; init; }

    public int ImpactMemoryTicks { get; init; }

    /// <summary>Rounds per mission: guns × rounds per gun.</summary>
    public int Rounds => Guns * RoundsPerGun;
}

/// <summary>The batteries of every side (table <c>artillery</c>) and the terrain cover the blast rules read.</summary>
public sealed record ArtilleryRules
{
    private Fixed[] _cover = [];

    public IReadOnlyList<BatteryRules> Batteries { get; init; } = [];

    /// <summary>Terrain cover of a tile type (0 to 1), from the <c>terrain</c> table.</summary>
    public Fixed Cover(TerrainType type) => _cover[(int)type];

    /// <summary>The side's battery, or null (it has no artillery).</summary>
    public BatteryRules? For(string side) => Batteries.FirstOrDefault(b => b.Side == side);

    /// <summary>A copy with one side's battery replaced.</summary>
    public ArtilleryRules With(BatteryRules battery) =>
        this with { Batteries = Batteries.Select(b => b.Side == battery.Side ? battery : b).ToArray() };

    /// <exception cref="DataException">A value is out of range, or two entries are for the same side.</exception>
    public static ArtilleryRules FromData(DataSet data)
    {
        var big = Fixed.FromInt(100000);
        var list = new List<BatteryRules>();
        foreach (var e in data.Table("artillery").Entries)
        {
            Fixed F(string name, Fixed max)
            {
                var v = e.Get(name).Number ?? throw new DataException($"artillery/{e.Id}/{name}: not a number.");
                return v < Fixed.Zero || v > max ? throw new DataException($"artillery/{e.Id}/{name}: {v} is out of range.") : v;
            }

            int Count(string name) => F(name, Fixed.FromInt(1000)).ToInt();
            int Ticks(string name) => (F(name, Fixed.FromInt(3600)) * Fixed.FromInt(World.TicksPerSecond)).ToInt();
            Fixed Ratio(string name) => F(name, Fixed.One);
            var b = new BatteryRules
            {
                Id = e.Id,
                Side = e.Get("side").Text,
                Missions = Count("missions"),
                CallTicks = Ticks("call_time"),
                FlightTicks = Ticks("flight_time"),
                Guns = Count("guns"),
                RoundsPerGun = Count("rounds_per_gun"),
                DurationTicks = Ticks("duration"),
                AimError = F("aim_error", big),
                Scatter = F("scatter", big),
                LethalRadius = F("lethal_radius", big),
                CasualtyRadius = F("casualty_radius", big),
                LethalChance = Ratio("lethal_chance"),
                DirectHitRadius = F("direct_hit_radius", big),
                KneelingFactor = Ratio("kneeling_factor"),
                ProneFactor = Ratio("prone_factor"),
                ShelterFactor = Ratio("shelter_factor"),
                BuildingFactor = Ratio("building_factor"),
                CoverWeight = Ratio("cover_weight"),
                KillChance = Ratio("kill_chance"),
                IncapacitateChance = Ratio("incapacitate_chance"),
                Suppression = F("suppression", Fixed.FromInt(100)),
                SuppressionRadius = F("suppression_radius", big),
                MoraleShock = F("morale_shock", Fixed.FromInt(100)),
                MoraleRadius = F("morale_radius", big),
                ImpactMemoryTicks = Ticks("impact_memory"),
            };
            if (b.LethalRadius > b.CasualtyRadius || b.KillChance + b.IncapacitateChance > Fixed.One || b.Rounds == 0)
            {
                throw new DataException($"artillery/{e.Id}: needs lethal_radius <= casualty_radius, kill + incapacitate <= 1 and at least one round.");
            }

            if (list.Any(x => x.Side == b.Side))
            {
                throw new DataException($"artillery/{e.Id}: a second battery for side '{b.Side}'.");
            }

            list.Add(b);
        }

        var terrain = TerrainTable.FromData(data);
        return new ArtilleryRules
        {
            Batteries = list,
            _cover = Enum.GetValues<TerrainType>().Select(t => terrain.Get(t).Cover).ToArray(),
        };
    }
}

/// <summary>Where a fire mission is. Values are hashed, so never renumber.</summary>
public enum MissionStatus : byte
{
    /// <summary>Called: the battery is laying and the first rounds are in the air (<see cref="FireMissionView.EtaTicks"/>).</summary>
    Incoming = 0,

    /// <summary>Rounds are landing.</summary>
    Firing = 1,

    /// <summary>Every round has landed.</summary>
    Complete = 2,
}

/// <summary>
/// One of the side's own fire missions, for the UI: the called point (not where the rounds really fall), its state,
/// the ticks until the first round lands (0 once firing) and the rounds still to land.
/// </summary>
public readonly record struct FireMissionView(
    int Id, int CallerId, Fixed X, Fixed Y, MissionStatus Status, int EtaTicks, int RoundsLeft, int RoundsTotal, long CalledTick);

/// <summary>A shell burst, seen and heard by both sides (loud): where and when it landed.</summary>
public readonly record struct ShellImpactView(long Tick, Fixed X, Fixed Y);

/// <summary>The hook for building damage (item 4.5): told of every burst, with the building it landed in or null.</summary>
public interface IStructureDamage
{
    void OnImpact(World world, Fixed x, Fixed y, Building? building);
}
