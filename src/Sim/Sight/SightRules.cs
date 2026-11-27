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

    /// <summary>Rays at least this long (m) take the hedge's full obscurement (item 2.4 follow-up). Default 0: always full.</summary>
    public Fixed HedgeFarRange { get; init; }

    /// <summary>Rays at most this long (m) take only <see cref="HedgeNearObscurement"/>; linear up to <see cref="HedgeFarRange"/>.</summary>
    public Fixed HedgeNearRange { get; init; }

    /// <summary>A hedge's obscurement on a short ray (capped at <see cref="HedgeObscurement"/>).</summary>
    public Fixed HedgeNearObscurement { get; init; } = Fixed.FromInt(1000);

    /// <summary>A crossing closer than this (m) to either end of the ray is a man at the hedge, looking through it.</summary>
    public Fixed HedgeAdjacentRange { get; init; }

    /// <summary>The most a hedge obscures at a crossing next to one end of the ray.</summary>
    public Fixed HedgeAdjacentObscurement { get; init; } = Fixed.FromInt(1000);

    /// <summary>A copy in which every hedge is its full obscurement at any range: the physical hedge, for blast (item 2.4).</summary>
    public SightRules SolidHedges() => this with
    {
        HedgeFarRange = Fixed.Zero, HedgeNearRange = Fixed.Zero, HedgeNearObscurement = Fixed.FromInt(1000),
        HedgeAdjacentRange = Fixed.Zero, HedgeAdjacentObscurement = Fixed.FromInt(1000),
    };

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
            terrain.Hedge.LosObscurement)
        {
            HedgeFarRange = Number(sight, "default", "hedge_far_range"),
            HedgeNearRange = Number(sight, "default", "hedge_near_range"),
            HedgeNearObscurement = Number(sight, "default", "hedge_near_obscurement"),
            HedgeAdjacentRange = Number(sight, "default", "hedge_adjacent_range"),
            HedgeAdjacentObscurement = Number(sight, "default", "hedge_adjacent_obscurement"),
        };
    }

    private static Fixed Number(DataTable table, string id, string name)
    {
        var entry = table.Entries.FirstOrDefault(e => e.Id == id)
            ?? throw new DataException($"{table.Name}: no entry '{id}'.");
        return entry.Get(name).Number ?? throw new DataException($"{table.Name}/{id}/{name}: not a number.");
    }
}
