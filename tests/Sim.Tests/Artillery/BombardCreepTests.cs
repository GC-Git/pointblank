using PointBlank.Runner;
using PointBlank.Sim.Replays;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Artillery;

/// <summary>
/// Item 2.4 "Done when", with the evaluator's creep test: after a 2-minute barrage the defenders are measurably degraded
/// against a no-barrage control; when the creep (Sneak to the hedge) begins the US side knows fewer surviving defenders
/// than the truth in at least 30% of seeds; survivors are discovered progressively as it closes, some when they fire.
/// </summary>
public class BombardCreepTests(ITestOutputHelper output)
{
    private const int Seeds = 100;

    private static Dictionary<string, double>[] Run(string id)
    {
        var scenario = ScenarioCatalog.Find(id)!;
        var results = new Dictionary<string, double>[Seeds];
        Parallel.For(0, Seeds, i =>
        {
            var world = Battle.Run(scenario, (ulong)(i + 1)).World;
            results[i] = scenario.Measure(world).ToDictionary(m => m.Name, m => m.Value.Raw / 4294967296.0);
        });
        return results;
    }

    private static double Mean(Dictionary<string, double>[] runs, string name) => runs.Average(r => r[name]);

    [Fact]
    public void A_barrage_degrades_the_defenders_and_the_creep_discovers_the_survivors_progressively()
    {
        var barrage = Run("bombard-creep");
        var control = Run("bombard-creep-control");
        foreach (var (name, runs) in new[] { ("barrage", barrage), ("control", control) })
        {
            var bands = string.Join(", ", BombardCreepScenario.Bands.Select(b => $"{b} m {Mean(runs, $"known_{b}"):F2}"));
            output.WriteLine(
                $"{name}: at the creep start, able defenders {Mean(runs, "survivors"):F2}, known {Mean(runs, "known_at_start"):F2}, "
                + $"suppressed {Mean(runs, "suppressed"):F2}, squad suppression {Mean(runs, "de_suppression"):F1} (peak {Mean(runs, "de_peak_suppression"):F1}), "
                + $"morale {Mean(runs, "de_morale"):F1}, routing {Mean(runs, "de_routing"):P0}; discovered share {bands}; "
                + $"reached 20 m {Mean(runs, "reached_20"):P0}; found when firing {Mean(runs, "found_firing"):F2}; "
                + $"US casualties in the creep {Mean(runs, "us_creep_casualties"):F2}; defender casualties at the end {Mean(runs, "de_casualties"):F2}");
        }

        // (i) Fog at the start of the creep.
        var unknown = barrage.Count(r => r["known_at_start"] < r["survivors"]);
        output.WriteLine($"seeds where the US knows fewer surviving defenders than the truth at the creep start: {unknown}/{Seeds}");
        Assert.True(unknown >= 30, $"{unknown}%");

        // (ii) Progressive discovery as the squad closes.
        var shares = BombardCreepScenario.Bands.Select(b => Mean(barrage, $"known_{b}")).ToArray();
        for (var i = 1; i < shares.Length; i++)
        {
            Assert.True(shares[i] >= shares[i - 1], string.Join(", ", shares));
        }

        Assert.True(shares[^1] >= 0.8, $"{shares[^1]}");
        Assert.True(Mean(barrage, "known_at_start") / Math.Max(0.01, Mean(barrage, "survivors")) < shares[^1]);

        // (iii) Some survivors are first discovered when they fire.
        Assert.True(barrage.Count(r => r["found_firing"] > 0) >= 10);

        // (iv) Against the control: fewer, weaker defenders and fewer US casualties in the creep.
        Assert.True(Mean(barrage, "survivors") <= Mean(control, "survivors") - 1);
        Assert.True(Mean(barrage, "de_peak_suppression") >= Mean(control, "de_peak_suppression") + 30);
        Assert.True(Mean(barrage, "de_morale") <= Mean(control, "de_morale") - 20);
        Assert.True(Mean(barrage, "us_creep_casualties") < Mean(control, "us_creep_casualties"));
    }

    [Theory]
    [InlineData("bombard-creep")]
    [InlineData("bombard-creep-control")]
    public void The_scenario_replays_to_the_same_hash(string id)
    {
        var scenario = ScenarioCatalog.Find(id)!;
        var result = Battle.Run(scenario, 11);
        Assert.Equal(result.Replay.FinalHash, Battle.Play(scenario, result.Replay).ComputeStateHash());
    }
}
