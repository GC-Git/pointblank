using PointBlank.Runner;
using PointBlank.Sim.Replays;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Item 2.3, owner decision ("Yes, needs fire support"): a lone squad advancing across open ground against an equal enemy
/// that is firing is pinned (see <see cref="UnitAiScenarioTests"/>); with a base of fire suppressing the enemy it gets on.
/// </summary>
public class FixAssaultTests(ITestOutputHelper output)
{
    private const int Seeds = 100;

    private static List<Dictionary<string, double>> Run(string id)
    {
        var scenario = ScenarioCatalog.Find(id)!;
        var runs = new List<Dictionary<string, double>>();
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            var world = Battle.Run(scenario, seed).World;
            runs.Add(scenario.Measure(world).ToDictionary(m => m.Name, m => m.Value.ToInt() + ((m.Value - Fixed.FromInt(m.Value.ToInt())).Raw / 4294967296.0)));
        }

        return runs;
    }

    [Fact]
    public void A_base_of_fire_suppresses_the_enemy_and_lets_the_assault_squad_get_on()
    {
        var free = Run("fix-assault-unopposed").Average(r => r["advance"]);
        var lone = Run("fix-assault-nobase");
        var fixedRuns = Run("fix-assault");
        string Line(string name, List<Dictionary<string, double>> runs) =>
            $"{name}: mean advance {runs.Average(r => r["advance"]):F0} m ({runs.Average(r => r["advance"]) / free:P0} of {free:F0} m unopposed); "
            + $">= 60% or reached in {runs.Count(r => r["reached"] == 1 || r["advance"] >= 0.6 * free)}/{Seeds}; "
            + $"German squad suppressed in {runs.Count(r => r["de_suppressed"] == 1)}/{Seeds} (pinned {runs.Average(r => r["de_pinned_ticks"]) / 10:F0} s mean); "
            + $"assault held by pinning {runs.Average(r => r["us_held_ticks"]) / 10:F0} s mean; casualties {runs.Average(r => r["us_casualties"]):F1}";
        output.WriteLine(Line("with a base of fire", fixedRuns));
        output.WriteLine(Line("without", lone));

        Assert.True(fixedRuns.Count(r => r["de_suppressed"] == 1) >= 90);
        Assert.True(fixedRuns.Count(r => r["reached"] == 1 || r["advance"] >= 0.6 * free) >= 70);
        Assert.True(fixedRuns.Average(r => r["advance"]) >= 2 * lone.Average(r => r["advance"]) + 20);
    }

    [Fact]
    public void The_fix_assault_scenario_replays_to_the_same_hash()
    {
        var scenario = ScenarioCatalog.Find("fix-assault")!;
        var result = Battle.Run(scenario, 7);
        Assert.Equal(result.Replay.FinalHash, Battle.Play(scenario, result.Replay).ComputeStateHash());
    }
}
