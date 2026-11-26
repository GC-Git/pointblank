using PointBlank.Runner;
using PointBlank.Sim.Replays;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Artillery;

/// <summary>
/// Item 2.4 "Done when": after a 2-minute barrage on a defended position, defender morale and suppression are measurably
/// degraded (against a no-barrage control), and in at least 30% of seeds the attacker has not spotted every surviving
/// defender when the advance begins (the side's knowledge against the truth, read by the scenario's watch).
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
    public void A_two_minute_barrage_degrades_the_defenders_and_leaves_survivors_unspotted()
    {
        var barrage = Run("bombard-creep");
        var control = Run("bombard-creep-control");
        foreach (var (name, runs) in new[] { ("barrage", barrage), ("control", control) })
        {
            output.WriteLine(
                $"{name}: at the advance, suppression {Mean(runs, "de_suppression"):F1} (peak {Mean(runs, "de_peak_suppression"):F1}), "
                + $"morale {Mean(runs, "de_morale"):F1}, casualties {Mean(runs, "de_casualties"):F2}, able survivors {Mean(runs, "de_survivors"):F2}, "
                + $"unspotted {Mean(runs, "de_unspotted"):F2}; after 30 s of advance: unspotted {Mean(runs, "de_unspotted_end"):F2}, "
                + $"US casualties {Mean(runs, "us_casualties"):F2}");
        }

        var unspotted = barrage.Count(r => r["de_survivors"] > 0 && r["de_unspotted"] > 0);
        output.WriteLine($"seeds with a surviving defender the US side has not spotted when the advance begins: {unspotted}/{Seeds}");

        Assert.True(Mean(barrage, "de_suppression") >= Mean(control, "de_suppression") + 20);
        Assert.True(Mean(barrage, "de_morale") <= Mean(control, "de_morale") - 20);
        Assert.True(Mean(barrage, "de_casualties") > 0);
        Assert.True(barrage.Count(r => r["de_survivors"] > 0) >= 90, "a barrage on dug-in men leaves survivors");
        Assert.True(unspotted >= 30, $"{unspotted}%");
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
