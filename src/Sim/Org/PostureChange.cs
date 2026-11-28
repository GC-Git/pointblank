using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Org;

/// <summary>
/// A man's pending posture change (item 2.4a): no order, unit-AI decision or pin changes a posture directly. It is
/// requested here, waits the man's own reaction delay (a hash of his id and the tick, so World.Rng's stream is
/// untouched) and is applied by <see cref="Apply"/>, the only place a posture changes after spawning. The newest decision
/// wins: a different posture replaces the pending one but keeps the time already running (a stream of new decisions cannot
/// put a man off for ever), and the posture he is already in cancels it. A pin drop (<see cref="Drop"/>) is a reflex, not
/// a decision: it is held apart, and no order or decision cancels it.
/// </summary>
internal static class PostureChange
{
    /// <summary>A due time not yet drawn: the next <see cref="Apply"/> draws it from the order delay.</summary>
    private const long Undrawn = -1;

    /// <summary>
    /// A new movement order: replaces anything pending (an older decision never overrides it), and the man waits where
    /// he is until he has taken the order's posture. One exception (item 2.4d): a man <paramref name="underFire"/>
    /// (suppressed or pinned) who is on his way lower than the order wants finishes getting down, in his own time; he
    /// waits until he has, and the unit AI decides when he comes up again.
    /// </summary>
    public static void Order(Soldier s, Posture posture, bool underFire = false)
    {
        if (underFire && s.PendingPosture > s.Posture && s.PendingPosture > posture) // lower is higher in the enum
        {
            s.AwaitingPosture = true;
            return;
        }

        Cancel(s);
        if (s.Posture != posture)
        {
            (s.PendingPosture, s.PostureDueAt, s.AwaitingPosture) = (posture, Undrawn, true);
        }
    }

    /// <summary>
    /// A decision at <paramref name="tick"/>, taking effect after the man's own draw of 0 to <paramref name="maxDelay"/>
    /// ticks. With <paramref name="wait"/> he does not walk until it is done. A <paramref name="maxDelay"/> of 0 means the
    /// caller has already waited the man's own delay: it takes effect at the next <see cref="Apply"/>. A request for the
    /// posture he is in drops any pending change.
    /// </summary>
    public static void Request(Soldier s, Posture posture, long tick, int maxDelay, bool wait = false)
    {
        if (s.Posture == posture)
        {
            Cancel(s);
            return;
        }

        if (s.PendingPosture == null || maxDelay == 0)
        {
            s.PostureDueAt = tick + Draw(s.Id, tick, maxDelay);
        }

        s.PendingPosture = posture;
        s.AwaitingPosture |= wait;
    }

    /// <summary>
    /// A pinned man goes flat at the next <see cref="Apply"/> (SuppressionSystem has already waited his own reaction).
    /// </summary>
    public static void Drop(Soldier s) => s.Dropping = s.Posture != Posture.Prone;

    /// <summary>
    /// The squad's move is stopped: drops any pending rise, whoever asked for it. A man on his way lower (the move's
    /// posture or a decision of the unit AI) still gets there, with no move left to wait for: a Hold is no reason to
    /// stay up.
    /// </summary>
    public static void Halt(Soldier s)
    {
        if (s.PendingPosture < s.Posture) // a rise (Standing is lowest in the enum)
        {
            Cancel(s);
        }

        s.AwaitingPosture = false;
    }

    /// <summary>
    /// A halt (item 2.9) as it is delivered: drops a pending rise at once. A man who was waiting for it before setting
    /// off stays where he is. A pending change lower is kept.
    /// </summary>
    public static void CancelRise(Soldier s)
    {
        if (s.PendingPosture < s.Posture) // a rise (Standing is lowest in the enum)
        {
            (s.PendingPosture, s.PostureDueAt) = (null, 0);
        }
    }

    /// <summary>Drops a pending decision (not a pin drop).</summary>
    public static void Cancel(Soldier s) => (s.PendingPosture, s.PostureDueAt, s.AwaitingPosture) = (null, 0, false);

    /// <summary>
    /// Applies every due change. A man who is <paramref name="heldDown"/> by fire (see
    /// <c>MovementSystem.HeldDown</c>) does not get up: a rise that falls due then is dropped, whoever decided it. In a squad of 3 or more able men they never all move on one tick: if every able man is
    /// due at once, one (by his own draw) follows on the next tick.
    /// </summary>
    public static void Apply(IEnumerable<Squad> squads, long tick, int orderDelay, Func<Squad, Soldier, bool> heldDown)
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
                    s.Dropping = false;
                    continue;
                }

                able++;
                if (s.Dropping)
                {
                    due.Add(s);
                    continue;
                }

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
                    if (s.PendingPosture < s.Posture && heldDown(q, s)) // a rise (Standing is lowest in the enum)
                    {
                        Cancel(s); // he stays down (and, on a move, crawls); going lower is still allowed
                    }
                    else
                    {
                        due.Add(s);
                    }
                }
            }

            if (able >= 3 && due.Count == able)
            {
                var late = due.MaxBy(s => (Draw(s.Id, tick, int.MaxValue - 1), s.Id))!;
                if (!late.Dropping)
                {
                    late.PostureDueAt = tick + 1; // a drop stays due by itself
                }

                due.Remove(late);
            }

            foreach (var s in due)
            {
                s.Posture = s.Dropping ? Posture.Prone : s.PendingPosture!.Value;
                s.Dropping = false;
                Cancel(s);
            }
        }
    }

    /// <summary>A deterministic draw in [0, <paramref name="range"/>] from a man and a tick (SplitMix64 finaliser).</summary>
    internal static int Draw(int soldierId, long tick, int range)
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
