using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Org;

/// <summary>
/// A man's pending posture change (item 2.4a): no order, unit-AI decision or pin changes a posture directly. It is
/// requested here, waits the man's own reaction delay (a hash of his id and the tick, so World.Rng's stream is
/// untouched) and is applied by <see cref="Apply"/>, the only place a posture changes after spawning. The latest request
/// wins but keeps the time already running, so a stream of new decisions cannot put a man off for ever.
/// </summary>
internal static class PostureChange
{
    /// <summary>A due time not yet drawn: the next <see cref="Apply"/> draws it from the order delay.</summary>
    private const long Undrawn = -1;

    /// <summary>
    /// A new movement order: replaces anything pending (an older decision never overrides it), and the man waits where
    /// he is until he has taken the order's posture.
    /// </summary>
    public static void Order(Soldier s, Posture posture)
    {
        Cancel(s);
        if (s.Posture != posture)
        {
            (s.PendingPosture, s.PostureDueAt, s.AwaitingPosture) = (posture, Undrawn, true);
        }
    }

    /// <summary>
    /// A decision at <paramref name="tick"/>, taking effect after the man's own draw of 0 to <paramref name="maxDelay"/>
    /// ticks. With <paramref name="wait"/> he does not walk until it is done. A <paramref name="maxDelay"/> of 0 means the
    /// caller has already waited the man's own delay: it takes effect at the next <see cref="Apply"/>, and asking that way
    /// for the posture he is in drops any pending change. A delayed request for the posture he is in changes nothing: a man
    /// finishes the move he has started (dropping it on every passing second thought would put him off for ever).
    /// </summary>
    public static void Request(Soldier s, Posture posture, long tick, int maxDelay, bool wait = false)
    {
        if (s.Posture == posture)
        {
            if (maxDelay == 0)
            {
                Cancel(s);
            }

            return;
        }

        if (s.PendingPosture == null || maxDelay == 0)
        {
            s.PostureDueAt = tick + Draw(s.Id, tick, maxDelay);
        }

        s.PendingPosture = posture;
        s.AwaitingPosture |= wait;
    }

    public static void Cancel(Soldier s) => (s.PendingPosture, s.PostureDueAt, s.AwaitingPosture) = (null, 0, false);

    /// <summary>
    /// Applies every due change. In a squad of 3 or more able men they never all move on one tick: if every able man is
    /// due at once, one (by his own draw) follows on the next tick.
    /// </summary>
    public static void Apply(IEnumerable<Squad> squads, long tick, int orderDelay)
    {
        var due = new List<Soldier>();
        foreach (var q in squads)
        {
            due.Clear();
            var able = 0;
            foreach (var s in q.Soldiers)
            {
                if (!s.CanLead)
                {
                    Cancel(s);
                    continue;
                }

                able++;
                if (s.PendingPosture == null)
                {
                    continue;
                }

                if (s.PostureDueAt == Undrawn)
                {
                    s.PostureDueAt = tick + Draw(s.Id, tick, orderDelay);
                }

                if (s.PostureDueAt <= tick)
                {
                    due.Add(s);
                }
            }

            if (able >= 3 && due.Count == able)
            {
                var late = due.MaxBy(s => (Draw(s.Id, tick, int.MaxValue - 1), s.Id))!;
                late.PostureDueAt = tick + 1;
                due.Remove(late);
            }

            foreach (var s in due)
            {
                s.Posture = s.PendingPosture!.Value;
                Cancel(s);
            }
        }
    }

    /// <summary>A deterministic draw in [0, <paramref name="range"/>] from a man and a tick (SplitMix64 finaliser).</summary>
    private static int Draw(int soldierId, long tick, int range)
    {
        if (range <= 0)
        {
            return 0;
        }

        var z = unchecked(((ulong)soldierId * 0x9E3779B97F4A7C15UL) ^ ((ulong)tick * 0xBF58476D1CE4E5B9UL) ^ 0xD1B54A32D192ED03UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (int)(z % (ulong)(range + 1));
    }
}
