using PointBlank.Sim.Scenarios;
using PointBlank.Runner;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Org;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Artillery;

/// <summary>
/// Item 2.4: a tick-by-tick trace of the barrage battles whose creeping squad gets within 50 m of the hedge (seeds 1 to 30).
/// Every first sighting of a defender is printed with why it was possible: distance to the nearest able US man, posture,
/// whether he fired or moved lately, and the observer's suppression; and, when the squad gets within 50 m, how many able
/// defenders are still at the hedge unknown. A diagnostic: it prints, and asserts only that some battle closes that far.
/// See the PR for the findings.
/// </summary>
public class CreepTraceTests(ITestOutputHelper output)
{
    private const int HedgeBand = 3; // a man within 3 m behind the hedge line is at it

    [Trait(Speed.Name, Speed.Slow)]
    [Fact]
    public void Trace_of_the_barrage_battles_that_close_to_50m()
    {
        var scenario = ScenarioCatalog.Find("bombard-creep")!;
        var traced = false;
        var closing = 0;
        var hiddenTotal = 0;
        var traces = SeedRuns.Map(30, seed =>
        {
            var w = scenario.CreateWorld(seed);
            foreach (var o in scenario.ScriptedOrders)
            {
                w.Schedule(o.Tick, scenario.CreateInput(o));
            }

            var org = w.GetSystem<OrgSystem>();
            var spot = w.GetSystem<SpottingSystem>();
            var fire = w.GetSystem<FireSystem>();
            var sup = w.GetSystem<SuppressionSystem>();
            var known = new HashSet<int>();
            var lines = new List<string>();
            int? hiddenAtHedge = null;
            for (var t = 0; t < scenario.Ticks && hiddenAtHedge == null; t++)
            {
                w.Step();
                var us = org.Squads.Where(q => q.Side == "us").SelectMany(q => q.Soldiers).Where(s => s.CanLead).ToList();
                foreach (var c in spot.KnownEnemies("us"))
                {
                    var d = org.Squads[3].Soldiers.FirstOrDefault(s => s.Id == c.EnemyId);
                    if (d == null || !known.Add(d.Id) || us.Count == 0)
                    {
                        continue;
                    }

                    var near = us.MinBy(u => ((u.X - d.X) * (u.X - d.X)) + ((u.Y - d.Y) * (u.Y - d.Y)))!;
                    var dist = Fixed.Sqrt(((near.X - d.X) * (near.X - d.X)) + ((near.Y - d.Y) * (near.Y - d.Y))).ToInt();
                    lines.Add($"t={w.Tick / 10.0:F1}s defender {d.Id} at ({d.X.ToInt()},{d.Y.ToInt()}) {d.Posture} {d.Health} {org.Squads[3].Morale}: "
                        + $"nearest US {dist} m (suppression {sup.LevelOf(near.Id).ToInt()}), fired {w.Tick - fire.LastFiredTick(d.Id) <= 30}, "
                        + $"moved {w.Tick - spot.LastMovedTick(d.Id) <= 30}");
                }

                var squad = org.Squads[0].Soldiers.Where(s => s.CanLead).ToList();
                if (w.Tick >= BombardCreepScenario.AdvanceTick && squad.Count > 0
                    && squad.Average(s => s.X.Raw / 4294967296.0) - BombardCreepScenario.HedgeX <= 50)
                {
                    hiddenAtHedge = org.Squads[3].Soldiers.Count(s => s.CanLead && !known.Contains(s.Id)
                        && s.X <= Fixed.FromInt(BombardCreepScenario.HedgeX) && s.X >= Fixed.FromInt(BombardCreepScenario.HedgeX - HedgeBand));
                    lines.Add($"t={w.Tick / 10.0:F1}s the squad is within 50 m; able defenders unknown at the hedge: {hiddenAtHedge}; "
                        + $"able defenders {org.Squads[3].Soldiers.Count(s => s.CanLead)}, known {known.Count}");
                }
            }

            return (Lines: lines, HiddenAtHedge: hiddenAtHedge);
        });

        for (var seed = 1; seed <= traces.Count; seed++)
        {
            var (lines, hiddenAtHedge) = traces[seed - 1];
            if (hiddenAtHedge == null)
            {
                continue;
            }

            closing++;
            if (!traced)
            {
                output.WriteLine($"trace of seed {seed}:");
                lines.ForEach(output.WriteLine);
                traced = true;
            }

            output.WriteLine($"seed {seed}: closed to 50 m with {hiddenAtHedge} unknown defenders at the hedge");
            hiddenTotal += hiddenAtHedge.Value;
        }

        output.WriteLine($"{closing} seeds closed to 50 m; unknown defenders left at the hedge then: {hiddenTotal} in all");
        Assert.True(closing > 0, "no barrage battle in seeds 1 to 30 closed to 50 m");
    }
}
