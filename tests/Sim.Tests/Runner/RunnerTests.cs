using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PointBlank.Runner;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Tests.Determinism;

namespace PointBlank.Sim.Tests.Runner;

/// <summary>
/// Item 0.3: the headless runner gives byte-identical stats, and the replays it saves
/// re-run to the same final hash.
/// </summary>
public sealed class RunnerTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("pointblank-runner-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File_(string name) => Path.Combine(_directory, name);

    private static (int Code, string Output, string Error) Invoke(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        int code = RunnerApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    private byte[] RunStats(string name, string seeds, params string[] extra)
    {
        string path = File_(name);
        var result = Invoke(["--scenario", "wander", "--seeds", seeds, "--out", path, .. extra]);
        Assert.True(result.Code == 0, result.Error);
        return File.ReadAllBytes(path);
    }

    private static void RunProcess(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "runner.dll"));
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Two_runner_processes_write_byte_identical_stats_for_seeds_1_to_100()
    {
        RunProcess("--scenario", "wander", "--seeds", "1..100", "--out", File_("a.json"));
        RunProcess("--scenario", "wander", "--seeds", "1..100", "--out", File_("b.json"));

        var first = File.ReadAllBytes(File_("a.json"));
        Assert.Equal(first, File.ReadAllBytes(File_("b.json")));
        Assert.Equal(first, RunStats("c.json", "1..100"));
    }

    [Fact]
    public void Stats_and_replays_do_not_depend_on_the_machine_culture()
    {
        var invariant = RunStats("a.json", "1..5", "--replays", File_("a"));

        var original = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("de-DE").Clone();
            culture.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = culture;

            Assert.Equal(invariant, RunStats("b.json", "1..5", "--replays", File_("b")));
            Assert.Equal(
                File.ReadAllBytes(File_("a/wander-seed3.replay.json")),
                File.ReadAllBytes(File_("b/wander-seed3.replay.json")));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Stats_have_no_carriage_returns_byte_order_mark_or_extra_fields()
    {
        var bytes = RunStats("a.json", "1..3");

        Assert.Equal((byte)'{', bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);

        // A timestamp, path or timing would have to appear as a new field.
        using var stats = JsonDocument.Parse(bytes);
        Assert.Equal(["version", "scenario", "ticks", "runs", "summary"], Names(stats.RootElement));
        Assert.Equal(["seed", "finalHash", "metrics"], Names(stats.RootElement.GetProperty("runs")[0]));
        Assert.Equal(["travelled", "meanX", "meanY"], Names(stats.RootElement.GetProperty("summary")));
    }

    [Fact]
    public void Stats_list_every_seed_in_order_with_a_summary_of_the_runs()
    {
        using var stats = JsonDocument.Parse(RunStats("a.json", "3..12"));
        var root = stats.RootElement;
        var runs = root.GetProperty("runs").EnumerateArray().ToArray();

        Assert.Equal("wander", root.GetProperty("scenario").GetString());
        Assert.Equal(1000, root.GetProperty("ticks").GetInt32());
        Assert.Equal(
            Enumerable.Range(3, 10).Select(i => (ulong)i),
            runs.Select(r => r.GetProperty("seed").GetUInt64()));
        Assert.Equal(10, runs.Select(r => r.GetProperty("finalHash").GetString()).Distinct().Count());

        foreach (var name in new[] { "travelled", "meanX", "meanY" })
        {
            var values = runs.Select(r => r.GetProperty("metrics").GetProperty(name).GetDecimal()).ToArray();
            var summary = root.GetProperty("summary").GetProperty(name);

            Assert.True(values.Distinct().Count() > 1, $"{name} never varies");
            Assert.Equal(values.Min(), summary.GetProperty("min").GetDecimal());
            Assert.Equal(values.Max(), summary.GetProperty("max").GetDecimal());
            Assert.Equal(values.Sum() / values.Length, summary.GetProperty("mean").GetDecimal());
        }
    }

    [Fact]
    public void Each_run_matches_the_battle_for_its_seed()
    {
        using var stats = JsonDocument.Parse(RunStats("a.json", "1..3"));
        var scenario = ScenarioCatalog.Find("wander")!;

        foreach (var run in stats.RootElement.GetProperty("runs").EnumerateArray())
        {
            var battle = Battle.Run(scenario, run.GetProperty("seed").GetUInt64());

            Assert.Equal(
                battle.Replay.FinalHash.ToString("x16"), run.GetProperty("finalHash").GetString());
        }
    }

    [Fact]
    public void Every_saved_replay_re_runs_to_its_final_hash_for_seeds_1_to_100()
    {
        using var stats = JsonDocument.Parse(RunStats("a.json", "1..100", "--replays", File_("r")));
        var scenario = ScenarioCatalog.Find("wander")!;

        foreach (var run in stats.RootElement.GetProperty("runs").EnumerateArray())
        {
            string path = File_($"r/wander-seed{run.GetProperty("seed").GetUInt64()}.replay.json");
            var replay = ReplayJson.Read(File.ReadAllText(path));
            string hash = Battle.Play(scenario, replay).ComputeStateHash().ToString("x16");

            Assert.Equal(run.GetProperty("finalHash").GetString(), hash);
            Assert.NotEmpty(replay.Orders);
            var verified = Invoke("--replay", path);
            Assert.True(verified.Code == 0, verified.Error);
            Assert.Contains(hash, verified.Output);
        }
    }

    [Theory]
    [InlineData("seed")]
    [InlineData("ticks")]
    [InlineData("hash")]
    public void A_tampered_replay_is_reported_as_a_mismatch(string field)
    {
        RunStats("a.json", "1", "--replays", _directory);
        string path = File_("wander-seed1.replay.json");
        var replay = ReplayJson.Read(File.ReadAllText(path));
        var tampered = field switch
        {
            "seed" => replay with { Seed = 2 },
            "ticks" => replay with { Ticks = replay.Ticks + 1 },
            _ => replay with { FinalHash = replay.FinalHash ^ 1 },
        };
        File.WriteAllText(path, ReplayJson.Write(tampered));

        var result = Invoke("--replay", path);

        Assert.Equal(1, result.Code);
        Assert.Contains("MISMATCH", result.Error);
    }

    [Fact]
    public void An_edited_order_is_reported_as_a_mismatch()
    {
        RunStats("a.json", "1", "--replays", _directory);
        string path = File_("wander-seed1.replay.json");
        var replay = ReplayJson.Read(File.ReadAllText(path));
        var first = replay.Orders[0] with { Args = [replay.Orders[0].Args[0], replay.Orders[0].Args[1] + 1] };
        File.WriteAllText(path, ReplayJson.Write(replay with { Orders = [first, .. replay.Orders.Skip(1)] }));

        Assert.Equal(1, Invoke("--replay", path).Code);
    }

    [Theory]
    [InlineData("\"wander\"", "\"no-such-scenario\"")]
    [InlineData("\"open-field\"", "\"other-map\"")]
    [InlineData("\"nudge\"", "\"dance\"")]
    [InlineData("\"tick\": 0,", "\"tick\": 1000,")]
    [InlineData("\"args\": [", "\"args\": [ 99,")]
    [InlineData("\"version\": 1,", "\"version\": 9,")]
    [InlineData("\"finalHash\"", "\"hash\"")]
    [InlineData("{", "")]
    public void A_replay_that_cannot_be_run_exits_2(string find, string replace)
    {
        RunStats("a.json", "1", "--replays", _directory);
        string path = File_("wander-seed1.replay.json");
        string text = File.ReadAllText(path);
        Assert.Contains(find, text);
        File.WriteAllText(path, text.Replace(find, replace));

        var result = Invoke("--replay", path);

        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Error);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1001)]
    [InlineData(0, -1001)]
    [InlineData(0, long.MaxValue)]
    public void The_wander_scenario_rejects_nudges_out_of_range(long index, long distance)
    {
        var scenario = ScenarioCatalog.Find("wander")!;

        Assert.Throws<FormatException>(
            () => scenario.CreateInput(new ReplayOrder(0, "nudge", [index, distance])));
    }

    [Theory]
    [InlineData("--scenario nope --seeds 1..2 --out OUT")]
    [InlineData("--scenario wander --seeds 5..1 --out OUT")]
    [InlineData("--scenario wander --seeds a..b --out OUT")]
    [InlineData("--scenario wander --seeds 1.. --out OUT")]
    [InlineData("--scenario wander --seeds -1 --out OUT")]
    [InlineData("--scenario wander --seeds 1..100001 --out OUT")]
    [InlineData("--scenario wander --seeds 1..18446744073709551615 --out OUT")]
    [InlineData("--scenario wander --out OUT")]
    [InlineData("--scenario wander --seeds 1")]
    [InlineData("--seeds 1 --out OUT")]
    [InlineData("--scenario wander --seeds 1 --out OUT --verbose yes")]
    [InlineData("--scenario wander --seeds 1 --out OUT --seeds 2")]
    [InlineData("--scenario wander --seeds 1 --out OUT --replays")]
    [InlineData("--scenario wander --seeds 1 --out OUT --replay x.json")]
    [InlineData("--replay no-such-file.json")]
    [InlineData("")]
    public void A_bad_command_line_exits_2_and_writes_nothing(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(a => a == "OUT" ? File_("out.json") : a)
            .ToArray();

        var result = Invoke(args);

        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_directory));
    }

    [Fact]
    public void An_unwritable_output_path_exits_2()
    {
        var result = Invoke("--scenario", "wander", "--seeds", "1", "--out", _directory);

        Assert.Equal(2, result.Code);
        Assert.NotEmpty(result.Error);
    }

    [Theory]
    [InlineData("7", 7UL, 7UL)]
    [InlineData("1..100", 1UL, 100UL)]
    [InlineData("18446744073709551615", ulong.MaxValue, ulong.MaxValue)]
    [InlineData("1..100000", 1UL, 100000UL)]
    public void Seed_ranges_parse(string text, ulong first, ulong last)
    {
        Assert.True(SeedRange.TryParse(text, out var a, out var b));
        Assert.Equal((first, last), (a, b));
    }

    [Fact]
    public void Scenario_code_uses_no_banned_api()
    {
        // Scenarios advance the battle, so they must be as deterministic as Sim.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "PointBlank.sln")))
        {
            root = root.Parent;
        }

        var sources = Directory
            .GetFiles(Path.Combine(root!.FullName, "tools", "Runner", "Scenarios"), "*.cs")
            .ToDictionary(f => Path.GetFileName(f), File.ReadAllText);
        Assert.Contains("WanderScenario.cs", sources.Keys);

        var violations = BannedApiScanner.Scan(sources, new HashSet<string>());

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    private static string[] Names(JsonElement element) =>
        element.EnumerateObject().Select(p => p.Name).ToArray();
}
