using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Suppression;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Posture-change counts over seeds of one scenario (items 2.4a). A squad-tick counts only with 3 or more able men.
/// <see cref="All"/>: every able man changed on that tick. <see cref="Most"/>: at least 75% did (includes All).
/// <see cref="AllButOne"/>: all but one did. <see cref="PinnedStandUps"/>: a pinned man got to his feet.
/// <see cref="QuickReversals"/>: a man went back to the posture he had left, within 1 s of leaving it.
/// </summary>
internal sealed class PostureMetrics
{
    public int All, Most, AllButOne, PinnedStandUps, QuickReversals, Changes;

    public List<string> Samples { get; } = [];

    public override string ToString() =>
        $"all {All}, >=75% {Most}, all-but-one {AllButOne}, stand-ups while pinned {PinnedStandUps}, quick reversals {QuickReversals}, changes {Changes}";

    public static PostureMetrics Measure(string id, int seeds)
    {
        var r = new PostureMetrics();
        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var scripted = id is "bombard-creep" or "pin-open" || id.StartsWith("fix-", StringComparison.Ordinal);
            var world = scripted ? StandingScanTests.Runner(id, seed, out var ticks) : StandingScanTests.Rig(id, seed, out ticks);
            var org = world.GetSystem<OrgSystem>();
            var suppression = world.GetSystem<SuppressionSystem>();
            var last = new Dictionary<int, Posture>();
            var left = new Dictionary<int, (Posture From, long Tick)>();
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
                        var had = last.TryGetValue(s.Id, out var was);
                        last[s.Id] = s.Posture;
                        if (!s.CanLead)
                        {
                            continue;
                        }

                        able++;
                        if (!had || was == s.Posture)
                        {
                            continue;
                        }

                        changed++;
                        r.Changes++;
                        if (s.Posture == Posture.Standing && suppression.StateOf(s.Id) == SuppressionState.Pinned)
                        {
                            r.PinnedStandUps++;
                        }

                        if (left.TryGetValue(s.Id, out var l) && l.From == s.Posture && world.Tick - l.Tick <= World.TicksPerSecond)
                        {
                            r.QuickReversals++;
                        }

                        left[s.Id] = (was, world.Tick);
                    }

                    if (able < 3)
                    {
                        continue;
                    }

                    r.All += changed == able ? 1 : 0;
                    r.AllButOne += changed == able - 1 ? 1 : 0;
                    if (changed * 4 >= able * 3)
                    {
                        r.Most++;
                        if (r.Samples.Count < 12)
                        {
                            r.Samples.Add($"seed {seed} tick {world.Tick} squad {q.Id}: {changed}/{able} changed, now " + string.Join(" ",
                                q.Soldiers.Where(s => s.CanLead).Select(s => $"{s.Posture}{(suppression.StateOf(s.Id) == SuppressionState.Pinned ? "!" : "")}")));
                        }
                    }
                }
            }
        }

        return r;
    }
}
