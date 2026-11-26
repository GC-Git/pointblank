using PointBlank.Runner;
using PointBlank.Sim.Replays;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Suppression;

/// <summary>Item 2.1 "Done when": an MG pins a squad crossing open ground; hedges make it clearly harder.</summary>
public class PinScenarioTests(ITestOutputHelper output)
{
    private const int Seeds = 100;

    private (int Pinned, double MeanSeconds) Run(string id)
    {
        var scenario = ScenarioCatalog.Find(id)!;
        Assert.Equal(PinScenario.RunTicks, scenario.Ticks); // 30 s
        var pinned = 0;
        long ticks = 0;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            var m = Battle.Run(scenario, seed).World;
            var metrics = scenario.Measure(m).ToDictionary(x => x.Name, x => x.Value.ToInt());
            if (metrics["pinned"] == 1)
            {
                pinned++;
                ticks += metrics["ticks_to_pin"];
                Assert.InRange(metrics["ticks_to_pin"], 0, PinScenario.RunTicks);
            }
        }

        var mean = pinned == 0 ? 0 : ticks / (double)pinned / World.TicksPerSecond;
        output.WriteLine($"{id}: pinned within 30 s in {pinned}/{Seeds} seeds, mean time to pin {mean:F1} s");
        return (pinned, mean);
    }

    [Fact]
    public void Sustained_MG_fire_pins_a_squad_in_the_open_and_hedges_make_it_clearly_harder()
    {
        var open = Run("pin-open");
        var hedges = Run("pin-hedges");
        Assert.True(open.Pinned >= 80, $"open: {open.Pinned}%");
        Assert.True(open.Pinned - hedges.Pinned >= 20, $"open {open.Pinned}% vs hedges {hedges.Pinned}%");
    }

    [Theory]
    [InlineData("pin-open")]
    [InlineData("pin-hedges")]
    public void The_scenario_replays_to_the_same_hash(string id)
    {
        var scenario = ScenarioCatalog.Find(id)!;
        var result = Battle.Run(scenario, 7);
        Assert.Equal(result.Replay.FinalHash, Battle.Play(scenario, result.Replay).ComputeStateHash());
    }
}
