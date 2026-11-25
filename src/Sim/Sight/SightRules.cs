using PointBlank.Sim.Data;
using PointBlank.Sim.Map;

namespace PointBlank.Sim.Sight;

/// <summary>
/// The numbers line of sight uses, read from the <c>postures</c>, <c>sight</c> and
/// <c>terrain</c> data tables. A record, so tests can vary one value with <c>with</c>.
/// </summary>
public sealed record SightRules
{
    private static readonly string[] PostureIds = ["standing", "kneeling", "prone"];

    private readonly Fixed[] _eye;
    private readonly Fixed[] _canopy;
    private readonly Fixed[] _perMetre;

    /// <param name="eyeHeights">Eye height above the floor, indexed by <see cref="Posture"/>.</param>
    /// <param name="canopyHeights">Height up to which a terrain obscures, indexed by <see cref="TerrainType"/>.</param>
    /// <param name="obscurementPerMetre">Obscurement per metre of ray below that height, by terrain.</param>
    public SightRules(
        IReadOnlyList<Fixed> eyeHeights,
        IReadOnlyList<Fixed> canopyHeights,
        IReadOnlyList<Fixed> obscurementPerMetre,
        Fixed floorHeight,
        Fixed blockThreshold,
        Fixed hedgeHeight,
        Fixed hedgeObscurement)
    {
        var terrains = Enum.GetValues<TerrainType>().Length;
        if (eyeHeights.Count != PostureIds.Length || canopyHeights.Count != terrains || obscurementPerMetre.Count != terrains)
        {
            throw new ArgumentException("One eye height per posture and one value per terrain type are needed.");
        }

        if (floorHeight <= Fixed.Zero || blockThreshold <= Fixed.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(floorHeight), "Floor height and block threshold must be positive.");
        }

        _eye = [.. eyeHeights];
        _canopy = [.. canopyHeights];
        _perMetre = [.. obscurementPerMetre];
        FloorHeight = floorHeight;
        BlockThreshold = blockThreshold;
        HedgeHeight = hedgeHeight;
        HedgeObscurement = hedgeObscurement;
    }

    /// <summary>Height of one storey; a building's walls are floors times this.</summary>
    public Fixed FloorHeight { get; init; }

    /// <summary>Accumulated obscurement at which a ray is blocked.</summary>
    public Fixed BlockThreshold { get; init; }

    public Fixed HedgeHeight { get; init; }

    /// <summary>Obscurement added each time a ray crosses a hedge below its top.</summary>
    public Fixed HedgeObscurement { get; init; }

    public Fixed EyeHeight(Posture posture) => _eye[(int)posture];

    public Fixed CanopyHeight(TerrainType type) => _canopy[(int)type];

    public Fixed ObscurementPerMetre(TerrainType type) => _perMetre[(int)type];

    public static SightRules FromData(DataSet data)
    {
        var terrain = TerrainTable.FromData(data);
        var types = Enum.GetValues<TerrainType>();
        var postures = data.Table("postures");
        var eyes = PostureIds.Select(id => Number(postures, id, "eye_height")).ToArray();
        var sight = data.Table("sight");

        return new SightRules(
            eyes,
            types.Select(t => terrain.Get(t).BlockHeight).ToArray(),
            types.Select(t => terrain.Get(t).LosObscurement).ToArray(),
            Number(sight, "default", "floor_height"),
            Number(sight, "default", "block_threshold"),
            terrain.Hedge.BlockHeight,
            terrain.Hedge.LosObscurement);
    }

    private static Fixed Number(DataTable table, string id, string name)
    {
        var entry = table.Entries.FirstOrDefault(e => e.Id == id)
            ?? throw new DataException($"{table.Name}: no entry '{id}'.");
        return entry.Get(name).Number ?? throw new DataException($"{table.Name}/{id}/{name}: not a number.");
    }
}
