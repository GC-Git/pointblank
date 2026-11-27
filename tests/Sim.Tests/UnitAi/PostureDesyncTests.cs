using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Item 2.4a "Done when": no squad with 3 or more able men ever has every able man change posture on the same tick,
/// whatever drives the change (an order, the unit AI, a pin, morale). Every squad, every tick, seeds 1 to 50. There is
/// no justified exception, so that count must be zero.
///
/// Zero alone proves little: <c>PostureChange.Apply</c> holds one man back a tick, so "all but one together" would pass.
/// So near-synchrony is bounded too: squad-ticks on which at least 75% of the able men change (a 3-man squad needs all
/// three). It cannot be zero: independent men whose delays fall in a 0.8 s window coincide now and then, mostly in
/// squads of 3 or 4. The bound is <see cref="MaxNearSync"/> in 50 battles, one in ten battles at worst: measured values
/// are at most 1, and before item 2.4a every scenario had 48 or more (bombard-creep 324). With
/// <c>order_posture_delay</c> and <c>ai_posture_delay</c> set to 0 every scenario here exceeds it.
///
/// Item 2.4b, on the same battles. No man gets up while his squad is pinned (as it stood when the tick began), with one
/// documented exception: a routing squad's men who are not pinned themselves may get up to run in rushes (owner decision:
/// an accepted realistic reason, each man judged by his own state, so not bounded here). Quick stand/drop reversals (back
/// to the posture left, within 1 s) are no higher than the baseline: the same count measured with the same code on commit
/// c7f461b, before item 2.4a.
/// </summary>
public class PostureDesyncTests(ITestOutputHelper output)
{
    private const int Seeds = 50;
    private const int MaxNearSync = 5;

    [Theory]
    [InlineData("bombard-creep", 3062)]
    [InlineData("pin-open", 249)]
    [InlineData("fix-assault", 741)]
    [InlineData("fix-assault-nobase", 946)]
    [InlineData("equal-fire", 946)]
    [InlineData("idle-open", 61)]
    [InlineData("idle-hidden", 607)]
    [InlineData("advance", 347)]
    public void Squads_change_posture_realistically(string id, int baselineReversals)
    {
        var m = PostureMetrics.Measure(id, Seeds);
        output.WriteLine($"METRIC {id}: {m}");
        var samples = string.Join("; ", m.Samples);
        Assert.True(m.All == 0, $"{m.All} whole-squad same-tick changes; large ones: {samples}");
        Assert.True(m.Most <= MaxNearSync, $"{m.Most} squad-ticks with 75% or more changing; first: {samples}");
        Assert.True(m.UnexcusedRises == 0, $"{m.UnexcusedRises} rises in pinned squads that are not routing: {m}");
        Assert.True(m.QuickReversals <= baselineReversals, $"{m.QuickReversals} quick reversals, baseline {baselineReversals}: {m}");
    }
}
