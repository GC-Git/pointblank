using PointBlank.Sim.Morale;
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
    private const int MaxSamples = 12;

    public int All, Most, AllButOne, PinnedStandUps, QuickReversals, Changes;

    /// <summary>Rises (item 2.4b): a man took a higher posture while his squad was pinned by fire. Seen: it was pinned
    /// when the tick began (what the decision could know). After: it was pinned when the tick ended.</summary>
    public int RisesSeenPinned, RisesAfterPinned, RisesMoralePinned;

    /// <summary>Of <see cref="RisesSeenPinned"/>, those of squads that are not routing: the ones nothing excuses.</summary>
    public int UnexcusedRises;

    public SortedDictionary<string, int> RiseKinds { get; } = [];

    public SortedDictionary<string, int> ReversalKinds { get; } = [];

    public List<string> Samples { get; } = [];

    public override string ToString() =>
        $"all {All}, >=75% {Most}, all-but-one {AllButOne}, stand-ups while pinned {PinnedStandUps}, quick reversals {QuickReversals}, changes {Changes}, rises in pinned squad {RisesSeenPinned} (at tick end {RisesAfterPinned}, morale Pinned {RisesMoralePinned})"
        + " [" + string.Join(", ", RiseKinds.Select(k => $"{k.Key}={k.Value}")) + "] reversals ["
        + string.Join(", ", ReversalKinds.Select(k => $"{k.Key}={k.Value}")) + "]";

    public static PostureMetrics Measure(string id, int seeds)
    {
        var total = new PostureMetrics();
        foreach (var one in SeedRuns.Map(seeds, seed => MeasureSeed(id, seed)))
        {
            total.Add(one);
        }

        return total;
    }

    /// <summary>Adds the next seed's counts; call in seed order, so the samples are the first seeds'.</summary>
    private void Add(PostureMetrics o)
    {
        All += o.All;
        Most += o.Most;
        AllButOne += o.AllButOne;
        PinnedStandUps += o.PinnedStandUps;
        QuickReversals += o.QuickReversals;
        Changes += o.Changes;
        RisesSeenPinned += o.RisesSeenPinned;
        RisesAfterPinned += o.RisesAfterPinned;
        RisesMoralePinned += o.RisesMoralePinned;
        UnexcusedRises += o.UnexcusedRises;
        foreach (var (key, n) in o.RiseKinds)
        {
            RiseKinds[key] = RiseKinds.GetValueOrDefault(key) + n;
        }

        foreach (var (key, n) in o.ReversalKinds)
        {
            ReversalKinds[key] = ReversalKinds.GetValueOrDefault(key) + n;
        }

        Samples.AddRange(o.Samples.Take(MaxSamples - Samples.Count));
    }

    private static PostureMetrics MeasureSeed(string id, ulong seed)
    {
        var r = new PostureMetrics();
        var scripted = id is "bombard-creep" or "pin-open" || id.StartsWith("fix-", StringComparison.Ordinal);
        var world = scripted ? StandingScanTests.Runner(id, seed, out var ticks) : StandingScanTests.Rig(id, seed, out ticks);
        var org = world.GetSystem<OrgSystem>();
        var suppression = world.GetSystem<SuppressionSystem>();
        var last = new Dictionary<int, Posture>();
        var left = new Dictionary<int, (Posture From, long Tick)>();
        var pinnedBefore = new HashSet<int>();
        for (var i = 0; i <= ticks; i++)
        {
            if (i > 0)
            {
                pinnedBefore = org.Squads.Where(q => suppression.IsPinned(q.Id)).Select(q => q.Id).ToHashSet();
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
                    if (s.Posture < was) // a rise (Standing is lowest in the enum)
                    {
                        var seen = pinnedBefore.Contains(q.Id);
                        var after = suppression.IsPinned(q.Id);
                        r.RisesSeenPinned += seen ? 1 : 0;
                        r.RisesAfterPinned += after ? 1 : 0;
                        r.RisesMoralePinned += q.Morale == MoraleState.Pinned ? 1 : 0;
                        if (seen)
                        {
                            r.UnexcusedRises += q.Morale != MoraleState.Routing ? 1 : 0;
                            var key = $"{q.Morale}{(suppression.StateOf(s.Id) == SuppressionState.Pinned ? "/manPinned" : "")}/{was}>{s.Posture}";
                            r.RiseKinds[key] = r.RiseKinds.GetValueOrDefault(key) + 1;
                        }
                    }

                    if (s.Posture == Posture.Standing && suppression.StateOf(s.Id) == SuppressionState.Pinned)
                    {
                        r.PinnedStandUps++;
                    }

                    if (left.TryGetValue(s.Id, out var l) && l.From == s.Posture && world.Tick - l.Tick <= World.TicksPerSecond)
                    {
                        r.QuickReversals++;
                        var key = $"{q.Morale}/{s.Posture}>{was}>{s.Posture}";
                        r.ReversalKinds[key] = r.ReversalKinds.GetValueOrDefault(key) + 1;
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
                    if (r.Samples.Count < MaxSamples)
                    {
                        r.Samples.Add($"seed {seed} tick {world.Tick} squad {q.Id}: {changed}/{able} changed, now " + string.Join(" ",
                            q.Soldiers.Where(s => s.CanLead).Select(s => $"{s.Posture}{(suppression.StateOf(s.Id) == SuppressionState.Pinned ? "!" : "")}")));
                    }
                }
            }
        }

        return r;
    }
}
