using System.Globalization;
using System.Text;

namespace PointBlank.Sim.Data;

public sealed record TableCounts(string Table, int Values, int Placeholders, int Estimated, int Referenced);

/// <summary>Counts of values by source kind. Output is deterministic (fixed table order).</summary>
public sealed class PlaceholderReport
{
    private PlaceholderReport(IReadOnlyList<TableCounts> tables, IReadOnlyList<string> placeholders)
    {
        Tables = tables;
        PlaceholderPaths = placeholders;
    }

    public IReadOnlyList<TableCounts> Tables { get; }

    /// <summary>Each placeholder as table/entry/value.</summary>
    public IReadOnlyList<string> PlaceholderPaths { get; }

    public int TotalPlaceholders => Tables.Sum(t => t.Placeholders);

    public static PlaceholderReport Build(DataSet set)
    {
        var counts = new List<TableCounts>();
        var paths = new List<string>();
        foreach (var table in set.Tables)
        {
            int values = 0, placeholders = 0, estimated = 0, referenced = 0;
            foreach (var entry in table.Entries)
            {
                foreach (var (name, v) in entry.Values) // sorted by name
                {
                    values++;
                    switch (v.Source.Kind)
                    {
                        case DataSourceKind.Placeholder:
                            placeholders++;
                            paths.Add($"{table.Name}/{entry.Id}/{name}");
                            break;
                        case DataSourceKind.Estimated:
                            estimated++;
                            break;
                        default:
                            referenced++;
                            break;
                    }
                }
            }

            counts.Add(new TableCounts(table.Name, values, placeholders, estimated, referenced));
        }

        return new PlaceholderReport(counts, paths);
    }

    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("Data placeholder report\n");
        sb.Append("table | values | placeholder | estimated | reference\n");
        foreach (var t in Tables)
        {
            sb.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{t.Table} | {t.Values} | {t.Placeholders} | {t.Estimated} | {t.Referenced}\n"));
        }

        sb.Append(string.Create(
            CultureInfo.InvariantCulture, $"Total placeholders: {TotalPlaceholders}\n"));
        foreach (var p in PlaceholderPaths)
        {
            sb.Append("  ").Append(p).Append('\n');
        }

        return sb.ToString();
    }
}
