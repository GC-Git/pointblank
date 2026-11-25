using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PointBlank.Sim.Replays;

/// <summary>The replay file format. The same replay always gives the same text.</summary>
public static class ReplayJson
{
    /// <summary>Bump when the layout changes.</summary>
    public const int Version = 2;

    private const int HashDigits = 16;

    public static string Write(Replay replay)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("scenario", replay.Scenario);
            writer.WriteNumber("seed", replay.Seed);
            writer.WritePropertyName("map");
            WriteRef(writer, replay.Map);
            writer.WriteStartArray("forces");
            foreach (var force in replay.Forces)
            {
                WriteRef(writer, force);
            }

            writer.WriteEndArray();
            writer.WriteNumber("ticks", replay.Ticks);
            writer.WriteStartArray("orders");
            foreach (var order in replay.Orders)
            {
                writer.WriteStartObject();
                writer.WriteNumber("tick", order.Tick);
                writer.WriteString("type", order.Type);
                writer.WriteStartArray("args");
                foreach (var arg in order.Args)
                {
                    writer.WriteNumberValue(arg);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("finalHash", replay.FinalHash.ToString("x16", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        // .NET 8 indents with the platform's newline; the file must not depend on it.
        // Newlines inside strings are escaped, so only layout is affected.
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    /// <exception cref="FormatException">The text is not a valid replay of this version.</exception>
    public static Replay Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            int version = root.GetProperty("version").GetInt32();
            if (version != Version)
            {
                throw new FormatException(
                    $"Replay version {version} is not supported; expected {Version}.");
            }

            return new Replay(
                Text(root, "scenario"),
                root.GetProperty("seed").GetUInt64(),
                ReadRef(root.GetProperty("map")),
                root.GetProperty("forces").EnumerateArray().Select(ReadRef).ToArray(),
                root.GetProperty("ticks").GetInt32(),
                root.GetProperty("orders").EnumerateArray().Select(ReadOrder).ToArray(),
                ReadHash(Text(root, "finalHash")));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // What JsonDocument throws for bad syntax, a wrong type and a missing field.
            throw new FormatException($"Not a valid replay: {e.Message}", e);
        }
    }

    private static void WriteRef(Utf8JsonWriter writer, ContentRef content)
    {
        writer.WriteStartObject();
        writer.WriteString("id", content.Id);
        writer.WriteNumber("version", content.Version);
        writer.WriteString("hash", content.Hash.ToString("x16", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static ContentRef ReadRef(JsonElement element) =>
        new(Text(element, "id"), element.GetProperty("version").GetInt32(), ReadHash(Text(element, "hash")));

    private static ReplayOrder ReadOrder(JsonElement element) =>
        new(
            element.GetProperty("tick").GetInt64(),
            Text(element, "type"),
            element.GetProperty("args").EnumerateArray().Select(a => a.GetInt64()).ToArray());

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString()
        ?? throw new FormatException($"Not a valid replay: '{name}' is null.");

    private static ulong ReadHash(string text) =>
        text.Length == HashDigits
        && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hash)
            ? hash
            : throw new FormatException($"Not a valid replay: '{text}' is not a state hash.");
}
