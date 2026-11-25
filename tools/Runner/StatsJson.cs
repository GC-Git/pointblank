using System.Globalization;
using System.Text;
using System.Text.Json;
using PointBlank.Sim;
using PointBlank.Sim.Replays;

namespace PointBlank.Runner;

/// <summary>
/// The <c>stats.json</c> format. It holds only what the battles determine (no times, paths
/// or machine data), in a fixed key order, so equal runs give equal bytes.
/// </summary>
public static class StatsJson
{
    /// <summary>Bump when the layout changes.</summary>
    public const int Version = 1;

    private const decimal FixedOne = 1L << 32;

    /// <param name="results">At least one battle of the scenario, in seed order.</param>
    public static string Write(IScenario scenario, IReadOnlyList<BattleResult> results)
    {
        var metrics = results.Select(r => scenario.Measure(r.World)).ToArray();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("scenario", scenario.Id);
            writer.WriteNumber("ticks", scenario.Ticks);

            writer.WriteStartArray("runs");
            for (int i = 0; i < results.Count; i++)
            {
                var replay = results[i].Replay;
                writer.WriteStartObject();
                writer.WriteNumber("seed", replay.Seed);
                writer.WriteString("finalHash", Hex(replay.FinalHash));
                writer.WriteStartObject("metrics");
                foreach (var metric in metrics[i])
                {
                    writer.WriteNumber(metric.Name, Number(metric.Value));
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("summary");
            for (int m = 0; m < metrics[0].Count; m++)
            {
                var values = metrics.Select(run => Number(run[m].Value)).ToArray();
                writer.WriteStartObject(metrics[0][m].Name);
                writer.WriteNumber("min", values.Min());
                writer.WriteNumber("max", values.Max());
                writer.WriteNumber("mean", values.Sum() / values.Length);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        // .NET 8 indents with the platform's newline; the file must not depend on it.
        return Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n";
    }

    public static string Hex(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    // Decimal arithmetic is done in software, so it rounds the same on every machine.
    private static decimal Number(Fixed value) => value.Raw / FixedOne;
}
