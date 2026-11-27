using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Item 2.4a "Done when": no squad with 3 or more able men ever has every able man change posture on the same tick,
/// whatever drives the change (an order, the unit AI, a pin, morale). Every squad, every tick, seeds 1 to 50. There is
/// no justified exception, so the expected count is zero. "Able" is judged after the tick; a change is any difference
/// from the man's posture after the previous tick.
/// </summary>
public class PostureDesyncTests(ITestOutputHelper output)
{
    private const int Seeds = 50;

    [Theory]
    [InlineData("bombard-creep")]
    [InlineData("pin-open")]
    [InlineData("fix-assault")]
    [InlineData("fix-assault-nobase")]
    [InlineData("equal-fire")]
    [InlineData("idle-open")]
    [InlineData("idle-hidden")]
    [InlineData("advance")]
    public void No_squad_changes_posture_all_on_one_tick(string id)
    {
        var count = 0;
        string? first = null;
        for (ulong seed = 1; seed <= Seeds; seed++)
        {
            var scripted = id is "bombard-creep" or "pin-open" || id.StartsWith("fix-", StringComparison.Ordinal);
            var world = scripted ? StandingScanTests.Runner(id, seed, out var ticks) : StandingScanTests.Rig(id, seed, out ticks);
            var org = world.GetSystem<OrgSystem>();
            var last = new Dictionary<int, Posture>();
            for (var i = 0; i <= ticks; i++)
            {
                if (i > 0)
                {
                    world.Step();
                }

                foreach (var q in org.Squads)
                {
                    var able = 0;
                    var changed = 0;
                    foreach (var s in q.Soldiers)
                    {
                        if (s.CanLead)
                        {
                            able++;
                            changed += last.TryGetValue(s.Id, out var was) && was != s.Posture ? 1 : 0;
                        }

                        last[s.Id] = s.Posture;
                    }

                    if (able >= 3 && changed == able)
                    {
                        count++;
                        first ??= $"seed {seed}: all {able} able men of squad {q.Id} changed posture at tick {world.Tick}";
                    }
                }
            }
        }

        output.WriteLine($"{id}: {count} whole-squad same-tick posture changes over {Seeds} seeds");
        Assert.True(count == 0, $"{count} cases; first: {first}");
    }
}
