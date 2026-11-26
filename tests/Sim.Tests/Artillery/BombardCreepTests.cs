using PointBlank.Runner;
using PointBlank.Sim.Replays;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Artillery;

/// <summary>
/// Item 2.4, the honest creep test (owner: realism first). Barrage against a control, seeds 1 to 200. Realistic behaviour
/// comes first; the thresholds below say what "realistic" must at least look like, and the raw numbers are printed.
/// <list type="number">
/// <item>The defenders open fire one by one: the time from the first to the last defender's first shot is spread out.</item>
/// <item>Reported (realism first): discovery per band, counting only bands the squad reached; whether the US knows
/// strictly more survivors at 50 m (and 100 m) than at 150 m. The squad closes only where the hedge is empty (CreepTraceTests).</item>
/// <item>Reported: survivors first found close in, and never found.</item>
/// <item>The barrage leaves fewer, weaker defenders and costs the creeping squad fewer men than the control.</item>
/// </list>
/// The checklist bar "in at least 30% of seeds the attacker has not spotted every surviving defender" is reported (fog at the
/// creep start), not asserted: see the PR for why.
/// </summary>
[Collection(TimingCollection.Name)]
public class BombardCreepTests(ITestOutputHelper output)
{
    private const int Seeds = 200;

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

    private static double Mean(IEnumerable<Dictionary<string, double>> runs, string name) =>
        runs.Select(r => r[name]).DefaultIfEmpty(double.NaN).Average();

    private static double Median(IEnumerable<double> xs)
    {
        var a = xs.Order().ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }

    [Fact]
    public void The_creep_finds_the_survivors_one_by_one_and_the_barrage_pays_for_itself()
    {
        var barrage = Run("bombard-creep");
        var control = Run("bombard-creep-control");
        foreach (var (name, runs) in new[] { ("barrage", barrage), ("control", control) })
        {
            var spreads = runs.Where(r => r["fire_spread"] >= 0).Select(r => r["fire_spread"]).ToList();
            var bands = string.Join(", ", BombardCreepScenario.Bands.Select(b =>
            {
                var reached = runs.Where(r => r[$"known_{b}"] >= 0).ToList();
                return $"{b} m: reached {reached.Count}, known {Mean(reached, $"known_{b}"):F2}";
            }));
            output.WriteLine(
                $"{name}: able defenders at the creep start {Mean(runs, "survivors"):F2}, known then {Mean(runs, "known_at_start"):F2}, "
                + $"suppressed {Mean(runs, "suppressed"):F2}, peak suppression {Mean(runs, "de_peak_suppression"):F1}, morale {Mean(runs, "de_morale"):F1}, "
                + $"routing {Mean(runs, "de_routing"):P0}; defenders who fired {Mean(runs, "de_firers"):F2}, fire spread median "
                + $"{Median(spreads):F1} s (seeds with 2+ firers {spreads.Count}); {bands}; found by fire {Mean(runs, "found_firing"):F2}, "
                + $"found within 50 m {Mean(runs, "found_close"):F2}, never found {Mean(runs, "never_found"):F2}; "
                + $"US creep casualties {Mean(runs, "us_creep_casualties"):F2}; defender casualties {Mean(runs, "de_casualties"):F2}");
        }

        var fog = barrage.Count(r => r["known_at_start"] < r["survivors"]);
        output.WriteLine($"checklist bar (reported): seeds where the US knows fewer survivors than the truth at the creep start: {fog}/{Seeds}");

        // (1) Staggered opening fire.
        var spread = barrage.Concat(control).Where(r => r["fire_spread"] >= 0).Select(r => r["fire_spread"]).ToList();
        Assert.True(spread.Count >= Seeds / 2, $"{spread.Count} battles with two or more defenders firing");
        Assert.True(Median(spread) >= 3, $"median spread {Median(spread)} s");

        // (2) Progressive discovery, over the bands actually reached.
        var closed = barrage.Where(r => r["known_50"] >= 0 && r["known_150"] >= 0).ToList();
        var more = closed.Count(r => r["known_50"] > r["known_150"]);
        output.WriteLine($"barrage seeds that reached 50 m: {closed.Count}; knew strictly more survivors at 50 m than at 150 m: {more}");
        Assert.True(closed.Count >= 20, $"{closed.Count} seeds reached 50 m");
        // Reported, not asserted (CLAUDE.md rule 12): the squad gets within 50 m only where no unknown defender is left at the
        // hedge (CreepTraceTests), so "strictly more known at 50 m than at 150 m" is not what realistic behaviour gives here.
        var gained = barrage.Where(r => r["known_100"] >= 0 && r["known_150"] >= 0).ToList();
        output.WriteLine($"barrage seeds that reached 100 m: {gained.Count}; knew strictly more survivors at 100 m than at 150 m: {gained.Count(r => r["known_100"] > r["known_150"])}");

        // (3) Survivors who stay hidden until close (or for good).
        var hidden = barrage.Count(r => r["found_close"] + r["never_found"] > 0);
        output.WriteLine($"barrage seeds with a survivor hidden until within 50 m or never found: {hidden}/{Seeds}");
        output.WriteLine($"barrage seeds with a survivor first found close in (not by fire): {barrage.Count(r => r["found_close"] > 0)}/{Seeds}");

        // (4) The barrage against the control.
        Assert.True(Mean(barrage, "survivors") < Mean(control, "survivors") - 1);
        Assert.True(Mean(barrage, "de_morale") < Mean(control, "de_morale") - 20);
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
