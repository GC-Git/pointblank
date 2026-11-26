using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using Xunit.Abstractions;
using static PointBlank.Sim.Tests.Morale.MoraleRig;

namespace PointBlank.Sim.Tests.Morale;

/// <summary>Item 2.2 "Done when", over seeds 1–100.</summary>
public class MoraleScenarioTests(ITestOutputHelper output)
{
    private const int Seeds = 100;

    private static Fixed Jitter(SimRng rng, int metres) => Fixed.FromRatio(rng.NextInt(-metres * 1000, (metres * 1000) + 1), 1000);

    /// <summary>
    /// A US squad with its HQ 30 m away. At 1 s its leader is killed (or, in the control, nobody); at 2 s it is
    /// ordered to move. Returns the morale level when the order is issued and the order's rolled delay.
    /// </summary>
    private static (double Level, int Delay) LeaderRun(ulong seed, bool killLeader)
    {
        var rig = Create(seed);
        var rng = new SimRng((seed * 7919UL) + 3UL);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(300) + Jitter(rng, 10), M(200) + Jitter(rng, 10));
        rig.Org.SpawnSquad("us_platoon_hq", M(330), M(200));
        if (killLeader)
        {
            rig.World.Schedule(World.TicksPerSecond, new SetHealthInput(q.Leader!.Id, HealthState.Dead));
        }

        rig.Run(2 * World.TicksPerSecond);
        var level = q.MoraleLevel;
        rig.World.Schedule(rig.World.Tick, new IssueOrderInput("us", q.Id, new Order(OrderKind.Move, M(350), M(200))));
        rig.Run(1);
        return ((double)level.Raw / (1L << 32), rig.Command.StatusOf(q.Id).TotalDelayTicks);
    }

    [Fact]
    public void After_its_leader_is_killed_a_squad_has_lower_morale_and_obeys_more_slowly()
    {
        var killed = Enumerable.Range(1, Seeds).Select(s => LeaderRun((ulong)s, true)).ToList();
        var control = Enumerable.Range(1, Seeds).Select(s => LeaderRun((ulong)s, false)).ToList();
        var kLevel = killed.Average(r => r.Level);
        var cLevel = control.Average(r => r.Level);
        var kDelay = killed.Average(r => r.Delay) / World.TicksPerSecond;
        var cDelay = control.Average(r => r.Delay) / World.TicksPerSecond;
        output.WriteLine($"leader killed: mean morale {kLevel:F1}, mean order delay {kDelay:F2} s");
        output.WriteLine($"control:       mean morale {cLevel:F1}, mean order delay {cDelay:F2} s");
        Assert.True(killed.All(r => r.Level < 100));
        Assert.True(kLevel < cLevel - 10, $"{kLevel} vs {cLevel}");
        Assert.True(kDelay > cDelay + 1, $"{kDelay} vs {cDelay}");
    }

    /// <summary>
    /// A US squad (alone, or with its HQ and a second squad near) loses half its men, one at a time at random
    /// moments in the first 15 s. Returns its morale state 5 s after the last loss.
    /// </summary>
    private static MoraleState CasualtyRun(ulong seed, bool supported)
    {
        var rig = Create(seed);
        var rng = new SimRng((seed * 104729UL) + 11UL);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(300) + Jitter(rng, 10), M(200) + Jitter(rng, 10));
        if (supported)
        {
            rig.Org.SpawnSquad("us_platoon_hq", M(330), M(200));
            rig.Org.SpawnSquad("us_rifle_squad", M(300), M(230));
        }

        var losses = (q.Soldiers.Count + 1) / 2;
        var victims = q.Soldiers.OrderBy(_ => rng.NextInt(0, 1_000_000)).Take(losses).ToList();
        var last = 0;
        foreach (var v in victims)
        {
            var tick = rng.NextInt(1, (15 * World.TicksPerSecond) + 1);
            last = Math.Max(last, tick);
            rig.World.Schedule(tick, new SetHealthInput(v.Id, HealthState.Dead));
        }

        rig.Run(last + (5 * World.TicksPerSecond));
        return q.Morale;
    }

    [Fact]
    public void An_unsupported_squad_that_takes_half_casualties_breaks_in_most_seeds()
    {
        static bool IsBroken(MoraleState m) => m is MoraleState.Broken or MoraleState.Routing;
        var alone = Enumerable.Range(1, Seeds).Count(s => IsBroken(CasualtyRun((ulong)s, false)));
        var supported = Enumerable.Range(1, Seeds).Count(s => IsBroken(CasualtyRun((ulong)s, true)));
        output.WriteLine($"50% casualties: broken or routing in {alone}/{Seeds} seeds unsupported, {supported}/{Seeds} supported");
        Assert.True(alone >= 60, $"unsupported {alone}%");
        Assert.True(supported < alone, $"supported {supported}% vs unsupported {alone}%");
    }

    /// <summary>A broken US squad, not under fire, with its HQ 30 m or 500 m away. Returns the seconds until it is Steady (or 600).</summary>
    private static double RallyRun(ulong seed, bool nearHq)
    {
        var rig = Create(seed);
        var rng = new SimRng((seed * 31UL) + 5UL);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(50) + Jitter(rng, 10), M(200) + Jitter(rng, 10));
        rig.Org.SpawnSquad("us_platoon_hq", M(nearHq ? 80 : 550) + Jitter(rng, 10), M(200));
        rig.World.Schedule(0, new SetMoraleInput(q.Id, MoraleState.Broken));
        rig.Run(1);
        var sawRallying = false;
        for (var t = 1; t < 600 * World.TicksPerSecond; t++)
        {
            rig.Run(1);
            sawRallying |= q.Morale == MoraleState.Rallying;
            if (q.Morale == MoraleState.Steady)
            {
                Assert.True(sawRallying);
                return t / (double)World.TicksPerSecond;
            }
        }

        return 600;
    }

    [Fact]
    public void A_squad_near_its_HQ_rallies_faster()
    {
        var near = Enumerable.Range(1, Seeds).Select(s => RallyRun((ulong)s, true)).Average();
        var far = Enumerable.Range(1, Seeds).Select(s => RallyRun((ulong)s, false)).Average();
        output.WriteLine($"broken to steady: mean {near:F1} s near the HQ, {far:F1} s far from it");
        Assert.True(near < 600 && far < 600);
        Assert.True(near < far * 0.75, $"near {near} s vs far {far} s");
    }
}
