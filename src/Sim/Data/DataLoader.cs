using System.Text.Json;

namespace PointBlank.Sim.Data;

/// <summary>
/// Reads the JSON data files (<c>data/&lt;table&gt;.json</c>). Rejects any value without a
/// valid source tag. Numbers go from decimal text to <see cref="Fixed"/> with integer
/// arithmetic only.
/// </summary>
public static class DataLoader
{
    /// <summary>The tables, in the fixed order used by reports.</summary>
    public static readonly string[] TableNames =
        ["units", "weapons", "ammo", "materials", "organisation"];

    private const int MaxDigits = 15;

    private static readonly Dictionary<string, string[]> Required = new()
    {
        ["units"] = ["name", "side", "weapon", "crew"],
        ["weapons"] = ["name", "ammo", "muzzle_velocity", "rate_of_fire", "magazine_capacity", "mass"],
        ["ammo"] = ["caliber", "bullet_mass"],
        ["materials"] = ["name", "cover_protection", "penetration_resistance"],
        ["organisation"] = ["name", "unit", "count"],
    };

    public static DataSet LoadDirectory(string directory)
    {
        var tables = new List<DataTable>();
        foreach (var name in TableNames)
        {
            var path = Path.Combine(directory, name + ".json");
            if (!File.Exists(path))
            {
                throw new DataException($"Data file not found: {path}");
            }

            tables.Add(ParseTable(File.ReadAllText(path), name));
        }

        return new DataSet(tables);
    }

    public static DataTable ParseTable(string json, string expectedTable)
    {
        if (!Required.TryGetValue(expectedTable, out var required))
        {
            throw new DataException($"Unknown table '{expectedTable}'.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new DataException($"{expectedTable}: invalid JSON: {e.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("table", out var t)
                || t.ValueKind != JsonValueKind.String
                || t.GetString() != expectedTable)
            {
                throw new DataException($"Expected a file with \"table\": \"{expectedTable}\".");
            }

            if (!root.TryGetProperty("entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                throw new DataException($"{expectedTable}: missing \"entries\" array.");
            }

            var ids = new HashSet<string>();
            var list = new List<DataEntry>();
            foreach (var e in entries.EnumerateArray())
            {
                var entry = ParseEntry(expectedTable, e, required);
                if (!ids.Add(entry.Id))
                {
                    throw new DataException($"{expectedTable}: duplicate id '{entry.Id}'.");
                }

                list.Add(entry);
            }

            return new DataTable(expectedTable, list);
        }
    }

    private static DataEntry ParseEntry(string table, JsonElement e, string[] required)
    {
        if (e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            throw new DataException($"{table}: every entry needs a non-empty string \"id\".");
        }

        var id = idElement.GetString()!;
        if (!e.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            throw new DataException($"{table}/{id}: missing \"values\" object.");
        }

        var parsed = new SortedDictionary<string, DataValue>(StringComparer.Ordinal);
        foreach (var p in values.EnumerateObject())
        {
            parsed[p.Name] = ParseValue($"{table}/{id}/{p.Name}", p.Value);
        }

        foreach (var name in required)
        {
            if (!parsed.ContainsKey(name))
            {
                throw new DataException($"{table}/{id}: missing required value '{name}'.");
            }
        }

        return new DataEntry(id, parsed);
    }

    private static DataValue ParseValue(string where, JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Object)
        {
            throw new DataException($"{where}: a value must be an object with value, unit and source.");
        }

        if (!v.TryGetProperty("source", out var source))
        {
            throw new DataException($"{where}: no source tag.");
        }

        var parsedSource = ParseSource(where, source);

        if (!v.TryGetProperty("value", out var raw)
            || raw.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
        {
            throw new DataException($"{where}: \"value\" must be a string or a number.");
        }

        var text = raw.ValueKind == JsonValueKind.String ? raw.GetString()! : raw.GetRawText();
        if (!v.TryGetProperty("unit", out var unit)
            || unit.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(unit.GetString()))
        {
            throw new DataException($"{where}: \"unit\" is required (use \"text\" for non-numbers).");
        }

        if (raw.ValueKind == JsonValueKind.Number && !TryParseFixed(text, out _))
        {
            throw new DataException($"{where}: number '{text}' is not a plain decimal.");
        }

        Fixed? number = TryParseFixed(text, out var f) ? f : null;
        return new DataValue(text, unit.GetString()!, parsedSource, number);
    }

    private static DataSource ParseSource(string where, JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty("kind", out var kind)
            || kind.ValueKind != JsonValueKind.String)
        {
            throw new DataException($"{where}: source must be an object with a \"kind\".");
        }

        switch (kind.GetString())
        {
            case "reference":
                return new DataSource(DataSourceKind.Reference, Need(where, s, "ref"));
            case "estimated":
                return new DataSource(DataSourceKind.Estimated, Need(where, s, "reasoning"));
            case "placeholder":
                var note = s.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()!
                    : string.Empty;
                return new DataSource(DataSourceKind.Placeholder, note);
            default:
                throw new DataException(
                    $"{where}: unknown source kind '{kind.GetString()}' "
                    + "(use reference, estimated or placeholder).");
        }
    }

    private static string Need(string where, JsonElement s, string name)
    {
        if (s.TryGetProperty(name, out var p)
            && p.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(p.GetString()))
        {
            return p.GetString()!;
        }

        throw new DataException($"{where}: source needs a non-empty \"{name}\".");
    }

    /// <summary>
    /// Parses an optionally signed plain decimal (digits, optionally a point and more
    /// digits) of at most 15 digits into Q31.32 using integers only.
    /// </summary>
    public static bool TryParseFixed(string text, out Fixed value)
    {
        value = default;
        var s = text.AsSpan();
        var negative = s.Length > 0 && s[0] == '-';
        if (negative)
        {
            s = s[1..];
        }

        long scaled = 0;
        long denominator = 1;
        var digits = 0;
        var seenPoint = false;
        var fractionDigits = 0;
        foreach (var c in s)
        {
            if (c == '.' && !seenPoint)
            {
                seenPoint = true;
            }
            else if (c is >= '0' and <= '9')
            {
                digits++;
                if (digits > MaxDigits)
                {
                    return false;
                }

                scaled = (scaled * 10) + (c - '0');
                if (seenPoint)
                {
                    denominator *= 10;
                    fractionDigits++;
                }
            }
            else
            {
                return false;
            }
        }

        if (digits == 0 || s[0] == '.' || s[^1] == '.')
        {
            return false;
        }

        if (seenPoint && fractionDigits == 0)
        {
            return false;
        }

        value = Fixed.FromRatio(negative ? -scaled : scaled, denominator);
        return true;
    }
}
