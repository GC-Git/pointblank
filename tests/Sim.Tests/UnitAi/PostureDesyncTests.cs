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
/// </summary>
public class PostureDesyncTests(ITestOutputHelper output)
{
    private const int Seeds = 50;
    private const int MaxNearSync = 5;

    [Theory]
    [InlineData("bombard-creep")]
    [InlineData("pin-open")]
    [InlineData("fix-assault")]
    [InlineData("fix-assault-nobase")]
    [InlineData("equal-fire")]
    [InlineData("idle-open")]
    [InlineData("idle-hidden")]
    [InlineData("advance")]
    public void No_squad_changes_posture_together(string id)
    {
        var m = PostureMetrics.Measure(id, Seeds);
        output.WriteLine($"METRIC {id}: {m}");
        var samples = string.Join("; ", m.Samples);
        Assert.True(m.All == 0, $"{m.All} whole-squad same-tick changes; large ones: {samples}");
        Assert.True(m.Most <= MaxNearSync, $"{m.Most} squad-ticks with 75% or more changing; first: {samples}");
    }
}
