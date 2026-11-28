using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Item 2.4c: a man of a stationary squad with a fire task who is masked by friendlies steps to a clear line, on his own
/// reaction, without advancing, without leaving a move order or a task-less squad, and without shuffling to and fro.
/// These tests are of the drill in a calm squad, so they hold suppression at zero: in this block the squad's own rounds
/// pass its men closely enough to pin them, and suppressed men do not shift (<see cref="MaskedGateTests"/>).
/// </summary>
public class MaskedSquadTests
{
    private const int AimX = 100;
    private const int Run = 900;

    private sealed record Trace(AiRig Rig, Squad Squad, Dictionary<int, (Fixed X, Fixed Y)> Start, Dictionary<int, List<long>> Starts, int MaskedEarly);

    /// <summary>A US rifle squad in its spawn block (ranks along the line of fire), kneeling, with area fire ordered 150 m west.</summary>
    private static Trace Play(ulong seed, OrderKind? order)
    {
        var rig = AiRig.Create(seed, new GameMap("open", 200, 60), morale: false); // alone for 90 s it would lose heart
        var q = rig.Org.SpawnSquad("us_rifle_squad", AiRig.M(250), AiRig.M(60), Posture.Kneeling);
        if (order is { } kind)
        {
            rig.Order(q, kind, AimX, 60);
        }

        var start = q.Soldiers.ToDictionary(s => s.Id, s => (s.X, s.Y));
        var starts = q.Soldiers.ToDictionary(s => s.Id, _ => new List<long>());
        var walking = new HashSet<int>();
        var early = 0;
        for (var t = 0; t < Run; t++)
        {
            rig.Calm(q);
            rig.World.Step();
            foreach (var s in q.Soldiers)
            {
                if (!rig.Move.IsSoldierMoving(s.Id))
                {
                    walking.Remove(s.Id);
                }
                else if (walking.Add(s.Id))
                {
                    starts[s.Id].Add(rig.World.Tick);
                }
            }

            if (early == 0 && rig.Fire.FireTaskOf(q.Id) != null)
            {
                early = Masked(rig, q);
            }
        }

        return new Trace(rig, q, start, starts, early);
    }

    private static int Masked(AiRig rig, Squad q) =>
        q.Soldiers.Count(s => rig.Fire.ActivityOf(q, s, rig.World.Tick).Activity == IntentKind.Blocked);

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void Masked_men_under_a_fire_order_step_to_a_clear_line(ulong seed)
    {
        var t = Play(seed, OrderKind.Fire);

        Assert.True(t.MaskedEarly >= 6, $"the block starts masked: {t.MaskedEarly}");
        // Not every man: lanes 2.25 m apart within the search radius do not hold all twelve.
        Assert.True(Masked(t.Rig, t.Squad) <= t.MaskedEarly / 2, $"still masked: {Masked(t.Rig, t.Squad)} of {t.MaskedEarly}");
        var fired = t.Squad.Soldiers.Count(s => t.Rig.Fire.LastFiredTick(s.Id) > 0);
        Assert.True(fired >= 8, $"{fired} of {t.Squad.Soldiers.Count} fired");
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(2UL)]
    [InlineData(3UL)]
    public void They_go_one_by_one_sideways_not_forward_and_stay_put_afterwards(ulong seed)
    {
        var t = Play(seed, OrderKind.Fire);
        var shifted = t.Starts.Where(p => p.Value.Count > 0).ToList();
        var firsts = shifted.Select(p => p.Value[0]).Order().ToList();
        var rules = t.Rig.Ai!.Rules;

        Assert.True(shifted.Count >= 4);
        Assert.True(firsts[0] >= rules.UnmaskDelayMinTicks, $"first shift at tick {firsts[0]}");
        Assert.True(firsts.Distinct().Count() >= 4 && firsts[^1] - firsts[0] >= World.TicksPerSecond, string.Join(",", firsts));
        Assert.All(shifted, p => Assert.True(p.Value.Count <= 2, $"man {p.Key} shifted {p.Value.Count} times"));
        Assert.All(shifted, p => Assert.True(p.Value[^1] < Run / 2, $"man {p.Key} still shifting at {p.Value[^1]}"));
        foreach (var s in t.Squad.Soldiers)
        {
            var (x, y) = t.Start[s.Id];
            Assert.True(s.X >= x - AiRig.M(2), $"man {s.Id} advanced {x - s.X} m toward the target");
            var d2 = ((s.X - x) * (s.X - x)) + ((s.Y - y) * (s.Y - y));
            var reach = rules.UnmaskSearchRadius * Fixed.FromInt(2) + AiRig.M(2);
            Assert.True(d2 <= reach * reach, $"man {s.Id} wandered");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OrderKind.Defend)]
    public void A_squad_with_no_fire_task_does_not_shuffle(OrderKind? order)
    {
        var rig = Play(1, order);
        Assert.Null(rig.Rig.Fire.FireTaskOf(rig.Squad.Id));
        Assert.All(rig.Starts, p => Assert.Empty(p.Value)); // open ground: Defend has no hedge to send anyone to
    }

    [Fact]
    public void A_move_order_still_takes_every_man_after_he_has_shifted()
    {
        var rig = AiRig.Create(1, new GameMap("open", 200, 60), morale: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", AiRig.M(250), AiRig.M(60), Posture.Kneeling);
        rig.Order(q, OrderKind.Fire, AimX, 60);
        for (var t = 0; t < 600 && !q.Soldiers.Any(s => rig.Move.IsSoldierMoving(s.Id)); t++)
        {
            rig.Calm(q);
            rig.World.Step();
        }

        rig.Order(q, OrderKind.Move, 300, 60); // while a man is still stepping aside
        var midShift = false;
        for (var t = 0; t < 1500; t++)
        {
            var walking = q.Soldiers.Any(s => rig.Move.IsSoldierMoving(s.Id));
            rig.World.Step();
            midShift |= walking && rig.Move.IsMoving(q.Id) && t < 100;
        }

        Assert.True(midShift, "the order arrived while someone was shifting");
        Assert.All(q.Soldiers, s => Assert.True(s.X > AiRig.M(265), $"man {s.Id} at {s.X},{s.Y}"));
    }
}
