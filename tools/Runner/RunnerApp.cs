using System.Diagnostics;
using System.Globalization;
using PointBlank.Sim.Replays;

namespace PointBlank.Runner;

/// <summary>The command line of the headless battle runner.</summary>
public static class RunnerApp
{
    public const int Success = 0;
    public const int Mismatch = 1;
    public const int Failure = 2;

    private static readonly string[] Options =
        ["--scenario", "--seeds", "--out", "--replays", "--replay"];

    private static string Usage =>
        $"""
        Usage:
          runner --scenario <id> --seeds <A..B | N> --out <stats.json> [--replays <directory>]
          runner --replay <file.replay.json>

        Scenarios: {string.Join(", ", ScenarioCatalog.Ids)}
        At most {SeedRange.MaxSeeds.ToString(CultureInfo.InvariantCulture)} seeds per run.
        Exit code: 0 success, 1 replay hash mismatch, 2 bad arguments, input or file.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var options = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!Options.Contains(args[i])
                || i + 1 >= args.Length
                || args[i + 1].StartsWith("--", StringComparison.Ordinal)
                || !options.TryAdd(args[i], args[i + 1]))
            {
                return BadArguments(error, $"Unknown, repeated or incomplete option '{args[i]}'.");
            }
        }

        try
        {
            if (options.TryGetValue("--replay", out var replayPath))
            {
                return options.Count == 1
                    ? Verify(replayPath, output, error)
                    : BadArguments(error, "--replay takes no other options.");
            }

            return RunSeeds(options, output, error);
        }
        catch (Exception e) when (e is FormatException or IOException
            or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine(e.Message);
            return Failure;
        }
    }

    private static int RunSeeds(
        Dictionary<string, string> options, TextWriter output, TextWriter error)
    {
        if (!options.TryGetValue("--scenario", out var id)
            || !options.TryGetValue("--seeds", out var seeds)
            || !options.TryGetValue("--out", out var statsPath))
        {
            return BadArguments(error, "--scenario, --seeds and --out are all required.");
        }

        if (ScenarioCatalog.Find(id) is not { } scenario)
        {
            return BadArguments(error, $"Unknown scenario '{id}'.");
        }

        if (!SeedRange.TryParse(seeds, out var first, out var last))
        {
            return BadArguments(error, $"Invalid seed range '{seeds}'.");
        }

        var timer = Stopwatch.StartNew();
        var results = new List<BattleResult>();
        for (ulong i = 0; i <= last - first; i++)
        {
            results.Add(Battle.Run(scenario, first + i));
        }

        var stats = StatsJson.Write(scenario, results);
        WriteText(statsPath, stats);

        if (options.TryGetValue("--replays", out var directory))
        {
            foreach (var result in results)
            {
                var name = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{scenario.Id}-seed{result.Replay.Seed}.replay.json");
                WriteText(Path.Combine(directory, name), ReplayJson.Write(result.Replay));
            }
        }

        // Timing goes to the console only; the files must not change from run to run.
        output.WriteLine(
            $"Ran {results.Count} battle(s) of '{scenario.Id}' in {timer.ElapsedMilliseconds} ms.");
        return Success;
    }

    private static int Verify(string path, TextWriter output, TextWriter error)
    {
        var replay = ReplayJson.Read(File.ReadAllText(path));
        if (ScenarioCatalog.Find(replay.Scenario) is not { } scenario)
        {
            error.WriteLine($"Unknown scenario '{replay.Scenario}'.");
            return Failure;
        }

        var hash = Battle.Play(scenario, replay).ComputeStateHash();
        if (hash != replay.FinalHash)
        {
            error.WriteLine(
                $"MISMATCH: the replay recorded {StatsJson.Hex(replay.FinalHash)}, "
                + $"this run ended on {StatsJson.Hex(hash)}.");
            return Mismatch;
        }

        output.WriteLine($"OK: final hash {StatsJson.Hex(hash)} matches.");
        return Success;
    }

    private static void WriteText(string path, string text)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, text); // UTF-8 without a byte order mark
    }

    private static int BadArguments(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine(Usage);
        return Failure;
    }
}
