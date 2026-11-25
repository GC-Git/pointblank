using PointBlank.Sim.Data;

namespace PointBlank.Sim.Map;

/// <summary>Terrain properties by type, read from the <c>terrain</c> data table.</summary>
public sealed class TerrainTable
{
    /// <summary>The data id of each tile type, indexed by <see cref="TerrainType"/>.</summary>
    private static readonly string[] Ids = ["open", "road", "light_woods"];

    private readonly TerrainProperties[] _byType;

    private TerrainTable(TerrainProperties[] byType, TerrainProperties hedge)
    {
        _byType = byType;
        Hedge = hedge;
    }

    /// <summary>The hedge edge feature's properties (cover, concealment, crossing cost, height).</summary>
    public TerrainProperties Hedge { get; }

    public TerrainProperties Get(TerrainType type) => _byType[(int)type];

    public static TerrainTable FromData(DataSet data)
    {
        var table = data.Table("terrain");
        var byType = new TerrainProperties[Ids.Length];
        for (var i = 0; i < Ids.Length; i++)
        {
            byType[i] = Read(table, Ids[i]);
        }

        return new TerrainTable(byType, Read(table, "hedge"));
    }

    private static TerrainProperties Read(DataTable table, string id)
    {
        var entry = table.Entries.FirstOrDefault(e => e.Id == id)
            ?? throw new DataException($"terrain: no entry '{id}'.");

        Fixed Num(string name) =>
            entry.Get(name).Number ?? throw new DataException($"terrain/{id}/{name}: not a number.");

        return new TerrainProperties(
            Num("cover"),
            Num("concealment"),
            Num("move_cost_walk"),
            Num("move_cost_fast"),
            Num("move_cost_sneak"),
            Num("block_height"),
            Num("los_obscurement"));
    }
}
