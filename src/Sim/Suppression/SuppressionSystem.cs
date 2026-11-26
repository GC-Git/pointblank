using PointBlank.Sim.Fire;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Suppression;

/// <summary>
/// Suppression: what incoming fire does to a man's will to act, apart from casualties (see
/// <see cref="SuppressionRules"/> for the numbers).
/// <list type="bullet">
/// <item>Per soldier: a level (0 to 100) built by <see cref="OnNearMiss"/> and <see cref="OnHit"/>
/// (called by <see cref="FireSystem"/>) and <see cref="AddExplosion"/> (the hook for item 2.4), and
/// a state (<see cref="SuppressionState"/>). A man who becomes Pinned goes prone.</item>
/// <item>Per squad: the mean level of its able men, with the same thresholds. When a squad becomes
/// Pinned all its able men go prone, and <c>CommandSystem</c> holds its advance-type orders
/// (<c>OrderStatus.Held</c>) until it drops below the release threshold.</item>
/// <item>Effects (read by the other systems): <see cref="DispersionFactor"/> (FireSystem accuracy),
/// <see cref="RateFactor"/> (FireSystem cadence), <see cref="ObserverFactor"/> (spotting) and
/// <see cref="SpeedFactor"/> (movement).</item>
/// <item>Morale (item 2.2) is separate: this system never changes <see cref="Squad.Morale"/>, and a
/// shaken or broken squad's refusals are decided at delivery, before holding.</item>
/// </list>
/// Construct it first and pass it to the systems it affects; register it after <see cref="FireSystem"/>.
/// </summary>
public sealed class SuppressionSystem(OrgSystem org, SuppressionRules rules) : ISimSystem
{
    private readonly List<Entry> _states = [];
    private readonly Dictionary<int, int> _indexOf = [];
    private readonly SortedDictionary<int, SquadState> _squads = [];

    public SuppressionRules Rules { get; } = rules;

    internal Fixed LevelOf(int soldierId) => Find(soldierId)?.Level ?? Fixed.Zero;

    internal SuppressionState StateOf(int soldierId) => Find(soldierId)?.State ?? SuppressionState.None;

    internal Fixed SquadLevelOf(int squadId) => _squads.TryGetValue(squadId, out var q) ? q.Level : Fixed.Zero;

    internal SuppressionState SquadStateOf(int squadId) => _squads.TryGetValue(squadId, out var q) ? q.State : SuppressionState.None;

    internal bool IsPinned(int squadId) => SquadStateOf(squadId) == SuppressionState.Pinned;

    /// <summary>The soldier's dispersion multiplier: 1 at level 0, <c>dispersion_max</c> at 100.</summary>
    internal Fixed DispersionFactor(int soldierId) => Fixed.One + ((Rules.DispersionMax - Fixed.One) * Unit(soldierId));

    /// <summary>The soldier's rate-of-fire multiplier: <c>rate_pinned</c> while Pinned, else 1 falling to <c>rate_min</c> at 100.</summary>
    internal Fixed RateFactor(int soldierId) =>
        StateOf(soldierId) == SuppressionState.Pinned ? Rules.RatePinned : Fixed.One - ((Fixed.One - Rules.RateMin) * Unit(soldierId));

    /// <summary>The observer's spotting-rate multiplier: 1 falling to <c>observer_min</c> at 100.</summary>
    internal Fixed ObserverFactor(int soldierId) => Fixed.One - ((Fixed.One - Rules.ObserverMin) * Unit(soldierId));

    /// <summary>The soldier's movement-speed multiplier by state.</summary>
    internal Fixed SpeedFactor(int soldierId) => StateOf(soldierId) switch
    {
        SuppressionState.Pinned => Rules.SpeedPinned,
        SuppressionState.Suppressed => Rules.SpeedSuppressed,
        _ => Fixed.One,
    };

    /// <summary>Sets a soldier's level (clamped to 0..100) without marking him under fire: for tests and scenario setup.</summary>
    internal void SetLevel(int soldierId, Fixed level)
    {
        var st = Find(soldierId) ?? throw new ArgumentException($"No soldier {soldierId}.");
        st.Level = level < Fixed.Zero ? Fixed.Zero : level > SuppressionRules.MaxLevel ? SuppressionRules.MaxLevel : level;
    }

    /// <summary>A shot passed close to a soldier: suppression for him and, shared, his squad-mates nearby.</summary>
    internal void OnNearMiss(World world, NearMissEvent e, Fixed nearMissRadius)
    {
        var s = org.FindSoldier(e.SoldierId);
        if (s == null)
        {
            return;
        }

        var ratio = nearMissRadius > Fixed.Zero ? e.Distance / nearMissRadius : Fixed.Zero;
        ratio = ratio > Fixed.One ? Fixed.One : ratio;
        var amount = Rules.NearMiss * Rules.CalibreWeight(e.Calibre) * (Fixed.One - (Rules.DistanceFalloff * ratio));
        Add(world, s, amount);
        var share = amount * Rules.ShareFactor;
        if (share <= Fixed.Zero)
        {
            return;
        }

        var r2 = Rules.ShareRadius * Rules.ShareRadius;
        foreach (var m in org.FindSquadOf(s.Id)!.Soldiers)
        {
            if (m != s && m.CanLead && Dist2(m, s.X, s.Y) <= r2)
            {
                Add(world, m, share);
            }
        }
    }

    /// <summary>A soldier was hit: suppression for every able man of his side within the hit radius (himself included).</summary>
    internal void OnHit(World world, Soldier hit)
    {
        var side = org.FindSquadOf(hit.Id)!.Side;
        var r2 = Rules.HitRadius * Rules.HitRadius;
        foreach (var q in org.Squads)
        {
            if (q.Side != side)
            {
                continue;
            }

            foreach (var m in q.Soldiers)
            {
                if ((m == hit || m.CanLead) && Dist2(m, hit.X, hit.Y) <= r2)
                {
                    Add(world, m, Rules.HitNearby);
                }
            }
        }
    }

    /// <summary>
    /// Suppression from a blast (the hook for item 2.4): <paramref name="amount"/> at the centre,
    /// falling linearly to 0 at <paramref name="radius"/>, for every able soldier of either side.
    /// </summary>
    internal void AddExplosion(World world, Fixed x, Fixed y, Fixed amount, Fixed radius)
    {
        if (radius <= Fixed.Zero)
        {
            return;
        }

        foreach (var q in org.Squads)
        {
            foreach (var m in q.Soldiers)
            {
                var d2 = Dist2(m, x, y);
                if (m.CanLead && d2 <= radius * radius)
                {
                    Add(world, m, amount * (Fixed.One - (Fixed.Sqrt(d2) / radius)));
                }
            }
        }
    }

    public void Tick(World world)
    {
        Sync();
        var perTick = Rules.DecayRate * World.TickDuration;
        foreach (var st in _states)
        {
            var underFire = world.Tick - st.LastUnderFire <= Rules.UnderFireWindowTicks;
            var decay = underFire ? perTick * Rules.UnderFireDecayFactor : perTick;
            st.Level = st.Level > decay ? st.Level - decay : Fixed.Zero;
            var before = st.State;
            st.State = Classify(st.Level, before);
            if (st.State == SuppressionState.Pinned && before != SuppressionState.Pinned && st.Soldier.CanLead)
            {
                st.Soldier.Posture = Posture.Prone;
            }
        }

        foreach (var squad in org.Squads)
        {
            var q = _squads[squad.Id];
            var sum = Fixed.Zero;
            var n = 0;
            foreach (var s in squad.Soldiers)
            {
                if (s.CanLead)
                {
                    sum += _states[_indexOf[s.Id]].Level;
                    n++;
                }
            }

            q.Level = n == 0 ? Fixed.Zero : sum / Fixed.FromInt(n);
            var before = q.State;
            q.State = n == 0 ? SuppressionState.None : Classify(q.Level, before);
            if (q.State == SuppressionState.Pinned && before != SuppressionState.Pinned)
            {
                foreach (var s in squad.Soldiers.Where(s => s.CanLead))
                {
                    s.Posture = Posture.Prone;
                }
            }
        }
    }

    public void AppendState(StateHasher hasher)
    {
        Sync();
        hasher.Add(_states.Count);
        foreach (var st in _states)
        {
            hasher.Add(st.Soldier.Id);
            hasher.Add(st.Level);
            hasher.Add((byte)st.State);
            hasher.Add(st.LastUnderFire);
        }

        hasher.Add(_squads.Count);
        foreach (var (id, q) in _squads)
        {
            hasher.Add(id);
            hasher.Add(q.Level);
            hasher.Add((byte)q.State);
        }
    }

    private static Fixed Dist2(Soldier s, Fixed x, Fixed y)
    {
        var dx = s.X - x;
        var dy = s.Y - y;
        return (dx * dx) + (dy * dy);
    }

    private SuppressionState Classify(Fixed level, SuppressionState before) =>
        level >= Rules.PinnedThreshold || (before == SuppressionState.Pinned && level >= Rules.PinnedRelease) ? SuppressionState.Pinned
        : level >= Rules.SuppressedThreshold ? SuppressionState.Suppressed
        : SuppressionState.None;

    private Fixed Unit(int soldierId) => LevelOf(soldierId) / SuppressionRules.MaxLevel;

    private void Add(World world, Soldier s, Fixed amount)
    {
        Sync();
        var st = _states[_indexOf[s.Id]];
        var level = st.Level + amount;
        st.Level = level > SuppressionRules.MaxLevel ? SuppressionRules.MaxLevel : level;
        st.LastUnderFire = world.Tick;
    }

    private Entry? Find(int soldierId)
    {
        Sync();
        return _indexOf.TryGetValue(soldierId, out var i) ? _states[i] : null;
    }

    /// <summary>Adds a zero state for each soldier and squad spawned since the last call (spawn order).</summary>
    private void Sync()
    {
        var total = 0;
        foreach (var squad in org.Squads)
        {
            total += squad.Soldiers.Count;
        }

        if (total == _states.Count && org.Squads.Count == _squads.Count)
        {
            return; // nothing spawned since the last call (soldiers and squads are never removed)
        }

        foreach (var squad in org.Squads)
        {
            if (!_squads.ContainsKey(squad.Id))
            {
                _squads[squad.Id] = new SquadState();
            }

            foreach (var s in squad.Soldiers)
            {
                if (!_indexOf.ContainsKey(s.Id))
                {
                    _indexOf[s.Id] = _states.Count;
                    _states.Add(new Entry(s));
                }
            }
        }
    }

    private sealed class Entry(Soldier soldier)
    {
        public Soldier Soldier { get; } = soldier;

        public Fixed Level { get; set; }

        public SuppressionState State { get; set; }

        public long LastUnderFire { get; set; } = long.MinValue / 2;
    }

    private sealed class SquadState
    {
        public Fixed Level { get; set; }

        public SuppressionState State { get; set; }
    }
}
