using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Fire;

/// <summary>
/// The numbers small-arms fire uses: the <c>fire</c> table (entry <c>default</c>), every weapon in
/// <c>weapons.json</c> with its round's calibre class, and terrain cover. A record, so tests can vary
/// one value with <c>with</c> (and a weapon with <see cref="WithWeapon"/>).
/// <para>
/// Angular error (one standard deviation, radians) of a shot at range d:
/// <c>dispersion × posture × (moving_factor if the firer moved) × (1 + dispersion_per_100m × d / 100)
/// × suppression × morale</c>, the last two 1 until items 2.1 and 2.2. Cover: the bottom
/// <c>cover × body height</c> of a soldier is hidden by his tile's terrain; a shot reaching that part
/// is stopped.
/// </para>
/// </summary>
public sealed record FireRules
{
    private const string EntryId = "default";

    private Fixed[] _bodyHeight = new Fixed[3];
    private Fixed[] _postureDispersion = new Fixed[3];
    private Fixed[] _cover = [];
    private Dictionary<string, WeaponStats> _weapons = new(StringComparer.Ordinal);

    private FireRules()
    {
    }

    public Fixed NearMissRadius { get; init; }

    /// <summary>The chance a hedge stops a shot that reaches it below its top.</summary>
    public Fixed HedgeBlockChance { get; init; }

    public Fixed BodyWidth { get; init; }

    public Fixed MovingFactor { get; init; }

    public Fixed DispersionPer100M { get; init; }

    public Fixed AreaAimHeight { get; init; }

    /// <summary>A soldier holds fire while a friendly is this close to his line of fire, short of the target.</summary>
    public Fixed MaskClearance { get; init; }

    public Fixed KillChance { get; init; }

    public Fixed IncapacitateChance { get; init; }

    public Fixed BodyHeight(Posture posture) => _bodyHeight[(int)posture];

    public Fixed PostureDispersion(Posture posture) => _postureDispersion[(int)posture];

    /// <summary>Terrain cover of a tile type (0 to 1), from the <c>terrain</c> table.</summary>
    public Fixed Cover(TerrainType type) => _cover[(int)type];

    /// <exception cref="KeyNotFoundException">No such weapon.</exception>
    public WeaponStats Weapon(string id) => _weapons[id];

    public WeaponStats? TryWeapon(string id) => _weapons.TryGetValue(id, out var w) ? w : null;

    /// <summary>A copy with one weapon changed.</summary>
    public FireRules WithWeapon(string id, Func<WeaponStats, WeaponStats> change) =>
        this with { _weapons = new Dictionary<string, WeaponStats>(_weapons, StringComparer.Ordinal) { [id] = change(_weapons[id]) } };

    /// <summary>The angular error (radians, one standard deviation) of a shot; see the type's summary.</summary>
    public Fixed Sigma(WeaponStats weapon, Posture firerPosture, bool moving, Fixed range, Fixed suppression, Fixed morale)
    {
        var s = weapon.Dispersion * PostureDispersion(firerPosture) * (Fixed.One + (DispersionPer100M * range / Fixed.FromInt(100)));
        return (moving ? s * MovingFactor : s) * suppression * morale;
    }

    /// <exception cref="DataException">An entry is missing or a value is out of range.</exception>
    public static FireRules FromData(DataSet data)
    {
        var e = data.Table("fire").Entries.FirstOrDefault(x => x.Id == EntryId)
            ?? throw new DataException($"fire: no '{EntryId}' entry.");
        var big = Fixed.FromInt(100_000);
        Fixed Num(DataEntry entry, string table, string name, Fixed min, Fixed max)
        {
            var v = entry.Get(name).Number ?? throw new DataException($"{table}/{entry.Id}/{name}: not a number.");
            return v < min || v > max ? throw new DataException($"{table}/{entry.Id}/{name}: {v} is out of range.") : v;
        }

        Fixed F(string name, Fixed max) => Num(e, "fire", name, Fixed.Zero, max);
        Fixed Chance(string name) => F(name, Fixed.One);

        var ammo = data.Table("ammo");
        var weapons = new Dictionary<string, WeaponStats>(StringComparer.Ordinal);
        foreach (var w in data.Table("weapons").Entries)
        {
            Fixed W(string name, int min, int max) => Num(w, "weapons", name, Fixed.FromInt(min), Fixed.FromInt(max));
            int Ticks(string name) => (W(name, 0, 3600) * Fixed.FromInt(World.TicksPerSecond)).ToInt();
            var ammoId = w.Get("ammo").Text;
            var round = ammo.Entries.FirstOrDefault(a => a.Id == ammoId)
                ?? throw new DataException($"weapons/{w.Id}/ammo: '{ammoId}' is not in ammo.json.");
            var calibre = round.Get("calibre_class").Text switch
            {
                "pistol" => CalibreClass.Pistol,
                "rifle" => CalibreClass.Rifle,
                "heavy" => CalibreClass.Heavy,
                var other => throw new DataException($"ammo/{ammoId}/calibre_class: unknown class '{other}'."),
            };
            weapons[w.Id] = new WeaponStats(
                w.Id,
                W("dispersion", 0, 1000) / Fixed.FromInt(1000),
                W("max_range", 1, 5000),
                W("rate_of_fire", 1, 3000),
                W("magazine_capacity", 1, 10_000).ToInt(),
                W("burst_length", 1, 10_000).ToInt(),
                Ticks("burst_pause"),
                Ticks("reload_time"),
                W("carried_rounds", 0, 100_000).ToInt(),
                calibre);
        }

        var terrain = TerrainTable.FromData(data);
        return new FireRules
        {
            NearMissRadius = F("near_miss_radius", Fixed.FromInt(100)),
            HedgeBlockChance = Chance("hedge_block_chance"),
            BodyWidth = F("body_width", Fixed.FromInt(10)),
            MovingFactor = F("moving_factor", Fixed.FromInt(100)),
            DispersionPer100M = F("dispersion_per_100m", Fixed.FromInt(100)),
            AreaAimHeight = F("area_aim_height", Fixed.FromInt(100)),
            MaskClearance = F("mask_clearance", Fixed.FromInt(10)),
            KillChance = Chance("kill_chance"),
            IncapacitateChance = Chance("incapacitate_chance"),
            _bodyHeight = [F("body_height_standing", big), F("body_height_kneeling", big), F("body_height_prone", big)],
            _postureDispersion = [F("dispersion_standing", big), F("dispersion_kneeling", big), F("dispersion_prone", big)],
            _cover = Enum.GetValues<TerrainType>().Select(t => terrain.Get(t).Cover).ToArray(),
            _weapons = weapons,
        };
    }
}
