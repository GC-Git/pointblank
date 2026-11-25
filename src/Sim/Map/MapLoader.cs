using PointBlank.Sim.Data;

namespace PointBlank.Sim.Map;

/// <summary>
/// The map load entry point for everything outside narrow unit tests: it always checks wall
/// materials against the <c>materials</c> data table, so a typo cannot slip through.
/// </summary>
public static class MapLoader
{
    /// <exception cref="MapException">The text is not a valid map, or names an unknown material.</exception>
    public static GameMap Parse(string json, DataSet data) =>
        MapFile.Parse(json, MaterialIds(data));

    /// <exception cref="MapException">The file is not a valid map, or names an unknown material.</exception>
    public static GameMap Load(string path, DataSet data) =>
        Parse(File.ReadAllText(path), data);

    private static HashSet<string> MaterialIds(DataSet data) =>
        data.Table("materials").Entries.Select(e => e.Id).ToHashSet();
}
