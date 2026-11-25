using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PointBlank.Sim.Map;

/// <summary>
/// The map file format (JSON, hand-editable). Terrain is one string per row (<c>.</c> open,
/// <c>=</c> road, <c>w</c> light woods); elevation is one string of space-separated whole
/// decimetres per row; hedges are <c>"x,y,E"</c> or <c>"x,y,S"</c> (the east or south edge of
/// that tile); a building's footprint is row runs <c>"x,y,length"</c> and its openings are
/// <c>"x,y,N|E|S|W,door|window,floor"</c>. Saving is canonical: the same map always gives the
/// same text, so a load then save is byte-identical.
/// </summary>
public static class MapFile
{
    /// <summary>Bump when the layout changes.</summary>
    public const int Version = 1;

    private const string TerrainChars = ".=w";
    private const string SideChars = "NESW";

    public static string Write(GameMap map)
    {
        using var stream = new MemoryStream();
        var options = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        using (var w = new Utf8JsonWriter(stream, options))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteString("id", map.Id);
            w.WriteNumber("tile_size_mm", map.TileSizeMm);
            w.WriteNumber("width", map.Width);
            w.WriteNumber("height", map.Height);

            w.WriteStartArray("terrain");
            for (var y = 0; y < map.Height; y++)
            {
                var row = new StringBuilder(map.Width);
                for (var x = 0; x < map.Width; x++)
                {
                    row.Append(TerrainChars[(int)map.GetTerrain(x, y)]);
                }

                w.WriteStringValue(row.ToString());
            }

            w.WriteEndArray();

            w.WriteStartArray("elevation_dm");
            for (var y = 0; y < map.Height; y++)
            {
                var row = new StringBuilder();
                for (var x = 0; x < map.Width; x++)
                {
                    if (x > 0)
                    {
                        row.Append(' ');
                    }

                    row.Append(map.GetElevationDm(x, y).ToString(CultureInfo.InvariantCulture));
                }

                w.WriteStringValue(row.ToString());
            }

            w.WriteEndArray();

            w.WriteStartArray("hedges");
            for (var y = 0; y < map.Height; y++)
            {
                for (var x = 0; x < map.Width; x++)
                {
                    foreach (var side in new[] { EdgeSide.East, EdgeSide.South })
                    {
                        if (map.HasHedge(x, y, side))
                        {
                            w.WriteStringValue($"{x},{y},{(side == EdgeSide.East ? 'E' : 'S')}");
                        }
                    }
                }
            }

            w.WriteEndArray();

            w.WriteStartArray("buildings");
            foreach (var b in map.Buildings)
            {
                w.WriteStartObject();
                w.WriteString("id", b.Id);
                w.WriteString("wall_material", b.WallMaterial);
                w.WriteNumber("floors", b.Floors);
                w.WriteStartArray("tiles");
                foreach (var (x, y, length) in Runs(b.Tiles))
                {
                    w.WriteStringValue($"{x},{y},{length}");
                }

                w.WriteEndArray();
                w.WriteStartArray("openings");
                foreach (var o in b.Openings)
                {
                    var kind = o.Kind == OpeningKind.Door ? "door" : "window";
                    w.WriteStringValue($"{o.Tile.X},{o.Tile.Y},{SideChars[(int)o.Side]},{kind},{o.Floor}");
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        // .NET indents with the platform's newline; the file must not depend on it.
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    /// <param name="materialIds">
    /// When given, every building's wall material must be one of these (the ids in
    /// <c>materials.json</c>).
    /// </param>
    /// <exception cref="MapException">The text is not a valid map of this version.</exception>
    public static GameMap Parse(string json, IReadOnlySet<string>? materialIds = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseRoot(document.RootElement, materialIds);
        }
        catch (JsonException e)
        {
            throw new MapException($"Invalid JSON: {e.Message}");
        }
        catch (ArgumentException e)
        {
            throw new MapException(e.Message);
        }
    }

    private static GameMap ParseRoot(JsonElement root, IReadOnlySet<string>? materialIds)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new MapException("A map file is a JSON object.");
        }

        var version = Int(root, "version");
        if (version != Version)
        {
            throw new MapException($"Unsupported map version {version} (this build reads version {Version}).");
        }

        var tileSize = Int(root, "tile_size_mm");
        var width = Int(root, "width");
        var height = Int(root, "height");
        if (tileSize < 1 || width < 1 || height < 1 || width > GameMap.MaxSide || height > GameMap.MaxSide)
        {
            throw new MapException(
                $"tile_size_mm must be positive and width and height 1 to {GameMap.MaxSide}.");
        }

        var map = new GameMap(Str(root, "id"), width, height, tileSize);

        var terrain = Rows(root, "terrain", height);
        for (var y = 0; y < height; y++)
        {
            if (terrain[y].Length != width)
            {
                throw new MapException(
                    $"terrain row {y} has {terrain[y].Length} characters, expected {width}.");
            }

            for (var x = 0; x < width; x++)
            {
                var kind = TerrainChars.IndexOf(terrain[y][x], StringComparison.Ordinal);
                if (kind < 0)
                {
                    throw new MapException($"unknown terrain character '{terrain[y][x]}' at ({x}, {y}).");
                }

                map.SetTerrain(x, y, (TerrainType)kind);
            }
        }

        var elevation = Rows(root, "elevation_dm", height);
        for (var y = 0; y < height; y++)
        {
            var cells = elevation[y].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length != width)
            {
                throw new MapException($"elevation_dm row {y} has {cells.Length} values, expected {width}.");
            }

            for (var x = 0; x < width; x++)
            {
                if (!int.TryParse(cells[x], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dm)
                    || Math.Abs(dm) > GameMap.MaxElevationDm)
                {
                    throw new MapException($"elevation_dm: bad value '{cells[x]}' at ({x}, {y}).");
                }

                map.SetElevationDm(x, y, dm);
            }
        }

        foreach (var text in Strings(root, "hedges"))
        {
            var f = text.Split(',');
            if (f.Length != 3 || f[2] is not ("E" or "S"))
            {
                throw new MapException($"hedge '{text}' is not \"x,y,E\" or \"x,y,S\".");
            }

            var x = Number($"hedge '{text}'", f[0]);
            var y = Number($"hedge '{text}'", f[1]);
            if (!map.InBounds(x, y))
            {
                throw new MapException($"hedge '{text}' is off the map.");
            }

            map.SetHedge(x, y, f[2] == "E" ? EdgeSide.East : EdgeSide.South, true);
        }

        if (!root.TryGetProperty("buildings", out var buildings) || buildings.ValueKind != JsonValueKind.Array)
        {
            throw new MapException("Missing \"buildings\" array.");
        }

        foreach (var b in buildings.EnumerateArray())
        {
            map.AddBuilding(ParseBuilding(b, materialIds));
        }

        return map;
    }

    private static Building ParseBuilding(JsonElement b, IReadOnlySet<string>? materialIds)
    {
        if (b.ValueKind != JsonValueKind.Object)
        {
            throw new MapException("Every building is an object.");
        }

        var id = Str(b, "id");
        var material = Str(b, "wall_material");
        if (materialIds != null && !materialIds.Contains(material))
        {
            throw new MapException($"building '{id}': wall material '{material}' is not in materials.json.");
        }

        var tiles = new List<TilePos>();
        foreach (var text in Strings(b, "tiles"))
        {
            var f = text.Split(',');
            var where = $"building '{id}' tiles '{text}'";
            if (f.Length != 3)
            {
                throw new MapException($"{where} is not \"x,y,length\".");
            }

            var x = Number(where, f[0]);
            var y = Number(where, f[1]);
            var length = Number(where, f[2]);
            if (length < 1 || length > GameMap.MaxSide)
            {
                throw new MapException($"{where}: bad length.");
            }

            for (var i = 0; i < length; i++)
            {
                tiles.Add(new TilePos(x + i, y));
            }
        }

        var openings = new List<Opening>();
        foreach (var text in Strings(b, "openings"))
        {
            var f = text.Split(',');
            var where = $"building '{id}' opening '{text}'";
            var side = f.Length == 5 && f[2].Length == 1 ? SideChars.IndexOf(f[2][0], StringComparison.Ordinal) : -1;
            if (side < 0 || f[3] is not ("door" or "window"))
            {
                throw new MapException($"{where} is not \"x,y,N|E|S|W,door|window,floor\".");
            }

            openings.Add(new Opening(
                new TilePos(Number(where, f[0]), Number(where, f[1])),
                (WallSide)side,
                f[3] == "door" ? OpeningKind.Door : OpeningKind.Window,
                Number(where, f[4])));
        }

        try
        {
            return new Building(id, material, Int(b, "floors"), tiles, openings);
        }
        catch (ArgumentException e)
        {
            throw new MapException($"building '{id}': {e.Message}");
        }
    }

    /// <summary>The footprint as maximal runs along each row, in row-major order.</summary>
    private static IEnumerable<(int X, int Y, int Length)> Runs(IReadOnlyList<TilePos> sortedTiles)
    {
        var i = 0;
        while (i < sortedTiles.Count)
        {
            var start = sortedTiles[i];
            var length = 1;
            while (i + length < sortedTiles.Count
                && sortedTiles[i + length].Y == start.Y
                && sortedTiles[i + length].X == start.X + length)
            {
                length++;
            }

            yield return (start.X, start.Y, length);
            i += length;
        }
    }

    private static int Number(string where, string text) =>
        int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)
            ? n
            : throw new MapException($"{where}: '{text}' is not a whole number.");

    private static int Int(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v)
            ? v
            : throw new MapException($"\"{name}\" must be a whole number.");

    private static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.GetString())
            ? p.GetString()!
            : throw new MapException($"\"{name}\" must be a non-empty string.");

    private static List<string> Strings(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
        {
            throw new MapException($"\"{name}\" must be an array of strings.");
        }

        var list = new List<string>();
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new MapException($"\"{name}\" must be an array of strings.");
            }

            list.Add(item.GetString()!);
        }

        return list;
    }

    private static List<string> Rows(JsonElement o, string name, int height)
    {
        var rows = Strings(o, name);
        if (rows.Count != height)
        {
            throw new MapException($"{name} has {rows.Count} rows, expected {height}.");
        }

        return rows;
    }
}
