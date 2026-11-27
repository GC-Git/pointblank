using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Movement;

/// <summary>How a squad travels. Values are hashed, so never renumber.</summary>
public enum MoveMode : byte
{
    Move = 0,
    MoveFast = 1,
    Sneak = 2,
}

/// <summary>
/// The numbers movement uses, from the <c>movement</c>, <c>formation</c>, <c>terrain</c> and
/// <c>postures</c> tables. A mode has a speed on open ground (m/s) and a posture; its
/// <c>terrain_cost</c> names the terrain column (walk, fast or sneak) that slows it down. Terrain
/// costs are stored relative to open ground in the same column, so speed is always "speed on
/// open ground" and a road (0.8 / 1) is quicker, woods (2 / 1) slower. The hedge crossing cost is
/// likewise in tile-lengths of open ground.
/// </summary>
public sealed class MovementRules
{
    private static readonly string[] ModeIds = ["move", "move_fast", "sneak"];
    private static readonly string[] PostureIds = ["standing", "kneeling", "prone"];

    private readonly Fixed[] _speed = new Fixed[3];
    private readonly Posture[] _posture = new Posture[3];
    private readonly Fixed[][] _terrain = new Fixed[3][];
    private readonly Fixed[] _hedge = new Fixed[3];
    private readonly Fixed[] _minTerrain = new Fixed[3];

    private MovementRules()
    {
    }

    /// <summary>Distance between ranks along the line of march, in metres.</summary>
    public Fixed Spacing { get; private init; }

    /// <summary>Sideways offset of the men behind the leader, in metres.</summary>
    public Fixed Lateral { get; private init; }

    /// <summary>The guide waits while a man is further than this from his slot, in metres.</summary>
    public Fixed Leash { get; private init; }

    /// <summary>Fewest ticks between replans of one man's path.</summary>
    public int ReplanTicks { get; private init; }

    /// <summary>The longest a man takes to react to a move order with a posture change (<c>unit_ai/order_posture_delay</c>).</summary>
    public int OrderPostureDelayTicks { get; private init; }

    public Fixed Speed(MoveMode mode) => _speed[(int)mode];

    public Posture PostureOf(MoveMode mode) => _posture[(int)mode];

    /// <summary>Cost of crossing a tile relative to open ground (1 = open).</summary>
    public Fixed TerrainCost(MoveMode mode, TerrainType type) => _terrain[(int)mode][(int)type];

    /// <summary>Extra cost of crossing a hedge edge, in tile-lengths of open ground.</summary>
    public Fixed HedgeCost(MoveMode mode) => _hedge[(int)mode];

    /// <summary>The smallest <see cref="TerrainCost"/> over all terrain: what makes the A* heuristic admissible.</summary>
    public Fixed MinTerrainCost(MoveMode mode) => _minTerrain[(int)mode];

    /// <exception cref="DataException">A value is missing or malformed.</exception>
    public static MovementRules FromData(DataSet data)
    {
        var terrain = TerrainTable.FromData(data);
        var modes = data.Table("movement");
        var formation = data.Table("formation").Entries.FirstOrDefault(e => e.Id == "staggered_column")
            ?? throw new DataException("formation: no entry 'staggered_column'.");

        Fixed Num(DataEntry e, string name) =>
            e.Get(name).Number ?? throw new DataException($"{e.Id}/{name}: not a number.");

        var ai = data.Table("unit_ai").Entries.FirstOrDefault(e => e.Id == "default")
            ?? throw new DataException("unit_ai: no entry 'default'.");
        var orderDelay = Num(ai, "order_posture_delay");
        if (orderDelay < Fixed.Zero)
        {
            throw new DataException("unit_ai/default/order_posture_delay: must not be negative.");
        }

        var types = Enum.GetValues<TerrainType>();
        var rules = new MovementRules
        {
            OrderPostureDelayTicks = (orderDelay * Fixed.FromInt(World.TicksPerSecond)).ToInt(),
            Spacing = Num(formation, "spacing"),
            Lateral = Num(formation, "lateral"),
            Leash = Num(formation, "leash"),
            ReplanTicks = Math.Max(1, Num(formation, "replan_ticks").ToInt()),
        };
        if (rules.Spacing <= Fixed.Zero || rules.Lateral < Fixed.Zero || rules.Leash <= Fixed.Zero)
        {
            throw new DataException("formation: spacing and leash must be positive.");
        }

        for (var m = 0; m < ModeIds.Length; m++)
        {
            var entry = modes.Entries.FirstOrDefault(e => e.Id == ModeIds[m])
                ?? throw new DataException($"movement: no entry '{ModeIds[m]}'.");
            var speed = Num(entry, "speed");
            if (speed <= Fixed.Zero)
            {
                throw new DataException($"movement/{entry.Id}/speed: must be positive.");
            }

            var postureId = entry.Get("posture").Text;
            var postureIndex = Array.IndexOf(PostureIds, postureId);
            if (postureIndex < 0)
            {
                throw new DataException($"movement/{entry.Id}/posture: '{postureId}' is not a posture.");
            }

            Func<TerrainProperties, Fixed> column = entry.Get("terrain_cost").Text switch
            {
                "walk" => p => p.MoveCostWalk,
                "fast" => p => p.MoveCostFast,
                "sneak" => p => p.MoveCostSneak,
                var other => throw new DataException($"movement/{entry.Id}/terrain_cost: '{other}' is not walk, fast or sneak."),
            };

            var open = column(terrain.Get(TerrainType.Open));
            if (open <= Fixed.Zero)
            {
                throw new DataException("terrain/open: the move cost must be positive.");
            }

            rules._speed[m] = speed;
            rules._posture[m] = (Posture)postureIndex;
            rules._terrain[m] = types.Select(t => column(terrain.Get(t)) / open).ToArray();
            rules._hedge[m] = column(terrain.Hedge) / open;
            rules._minTerrain[m] = rules._terrain[m].Min();
            if (rules._minTerrain[m] <= Fixed.Zero || rules._hedge[m] < Fixed.Zero)
            {
                throw new DataException($"terrain: move costs for '{ModeIds[m]}' must be positive.");
            }
        }

        return rules;
    }
}
