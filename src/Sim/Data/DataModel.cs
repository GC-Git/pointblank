namespace PointBlank.Sim.Data;

/// <summary>A data file is malformed or breaks the source-tag rule.</summary>
public sealed class DataException(string message) : FormatException(message);

public enum DataSourceKind
{
    Reference,
    Estimated,
    Placeholder,
}

/// <summary>Where a value comes from. Detail is the ref, the reasoning or the note.</summary>
public sealed record DataSource(DataSourceKind Kind, string Detail);

/// <summary>One sourced value. Number is set when Text is a plain decimal number.</summary>
public sealed record DataValue(string Text, string Unit, DataSource Source, Fixed? Number);

public sealed class DataEntry(string id, IReadOnlyDictionary<string, DataValue> values)
{
    public string Id { get; } = id;

    /// <summary>Values by name, in file order is not guaranteed; use Names for order.</summary>
    public IReadOnlyDictionary<string, DataValue> Values { get; } = values;

    public DataValue Get(string name) =>
        Values.TryGetValue(name, out var v)
            ? v
            : throw new DataException($"Entry '{Id}' has no value '{name}'.");
}

public sealed class DataTable(string name, IReadOnlyList<DataEntry> entries)
{
    public string Name { get; } = name;

    public IReadOnlyList<DataEntry> Entries { get; } = entries;
}

public sealed class DataSet(IReadOnlyList<DataTable> tables)
{
    public IReadOnlyList<DataTable> Tables { get; } = tables;

    public DataTable Table(string name) =>
        Tables.FirstOrDefault(t => t.Name == name)
        ?? throw new DataException($"No table '{name}'.");
}
