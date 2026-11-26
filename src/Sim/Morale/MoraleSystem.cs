using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;

namespace PointBlank.Sim.Morale;

/// <summary>
/// Squad morale (item 2.2): a level from 0 to 100 (also on <see cref="Squad.MoraleLevel"/>), a state
/// (<see cref="Squad.Morale"/>) and the reasons behind them (<see cref="ReasonsOf"/>). Numbers in <see cref="MoraleRules"/>.
/// <list type="bullet">
/// <item>Shocks: each man who goes down (dead or incapacitated) costs <c>casualty_shock</c> × a random spread (one
/// RNG draw per man) × (1 + share of the squad down); the leader going down costs <c>leader_shock</c> more. A squad
/// with friendly support takes (1 − <c>support_shock_reduction</c>) of each.</item>
/// <item>Drain: <c>suppression_drain</c> × squad suppression / 100 per second.</item>
/// <item>Ceiling: 100 − share down × <c>casualty_ceiling</c> − <c>leaderless_ceiling</c> (no effective leader)
/// − <c>isolated_ceiling</c> (no friendly squad within <c>support_radius</c>) − <c>out_of_contact_ceiling</c>
/// (no HQ contact, from <see cref="CommandSystem.InContact"/>). Above it the level drifts down at <c>drift_rate</c>.</item>
/// <item>Recovery, only while the squad is not suppressed and has lost no man for <c>loss_window</c>: toward the
/// ceiling at <c>recovery_rate</c> × (1 + <see cref="Squad.LeaderBonus"/> × <c>leader_recovery_weight</c>
/// + <c>support_recovery_bonus</c> if supported + <c>hq_rally_bonus</c> if an HQ is within <c>hq_rally_radius</c>).</item>
/// <item>States, with hysteresis: Steady → Shaken below <c>shaken_below</c>, back at <c>shaken_release</c>; any → Broken
/// below <c>broken_below</c>; Broken → Routing below <c>rout_below</c>; Broken or Routing → Rallying at <c>broken_release</c>
/// while recovering; Rallying → Steady (Shaken if below <c>shaken_release</c>) at the lower of <c>steady_above</c> and the
/// ceiling, or back to Broken below <c>broken_below</c>.</item>
/// <item>Pinned: there is one notion of pinned, the squad's suppression state. The shown state is
/// <see cref="MoraleState.Pinned"/> while suppression is Pinned and the state above is Steady, Shaken or Rallying;
/// Broken and Routing take precedence. Holding advances stays the suppression rule in <see cref="CommandSystem"/>.</item>
/// <item>Known limit: a squad with very heavy casualties and no support nearby cannot recover from Broken. Its ceiling can sit
/// below <c>broken_release</c>, so it never reaches Rallying and stays Broken (or routs) until a friendly squad or an HQ comes
/// near. By design for now.</item>
/// <item>Routing (item 2.3 follow-ups in <see cref="Flee"/>: mean bearing of the known enemies within <c>rout_threat_radius</c>,
/// nearest reachable fallback point): on entering it the squad runs (MoveFast) <c>rout_distance</c> straight away from the nearest enemy its
/// side knows (<see cref="SpottingSystem.KnownEnemies"/>, so fog is respected), or to its nearest HQ if it knows none,
/// pulled in until the point is on the map. <see cref="CommandSystem"/> refuses it every order but FallBack.</item>
/// </list>
/// Shell bursts (item 2.4) also shock morale directly through <see cref="AddShock"/>.
/// Register after <see cref="SuppressionSystem"/>.
/// </summary>
public sealed class MoraleSystem(
    OrgSystem org, MoraleRules rules, SuppressionSystem? suppression = null, CommandSystem? command = null,
    MovementSystem? move = null, SpottingSystem? spotting = null) : ISimSystem
{
    private readonly SortedDictionary<int, Entry> _entries = [];

    public MoraleRules Rules { get; } = rules;

    internal MoraleReason ReasonsOf(int squadId) => _entries.TryGetValue(squadId, out var e) ? e.Reasons : MoraleReason.None;

    /// <summary>Sets a squad's level (clamped to 0..100): for tests and scenario setup.</summary>
    internal void SetLevel(int squadId, Fixed level)
    {
        var e = Get(squadId);
        e.Level = Clamp(level);
        e.Squad.SetMoraleLevel(e.Level);
    }

    /// <summary>A morale shock that is not a casualty (item 2.4: a shell burst nearby): the level drops by <paramref name="amount"/>.</summary>
    /// <summary>A shell burst fell within <c>cower_radius</c> of one of the squad's able men (item 2.4).</summary>
    internal void NoteShelled(int squadId, long tick) => Get(squadId).LastShelled = tick;

    internal void AddShock(int squadId, Fixed amount)
    {
        var e = Get(squadId);
        e.Level = Clamp(e.Level - amount);
        e.Squad.SetMoraleLevel(e.Level);
    }

    /// <summary>Sets a squad's state (before the pinned overlay): for tests and scenario setup.</summary>
    internal void SetState(int squadId, MoraleState state)
    {
        var e = Get(squadId);
        e.Core = state == MoraleState.Pinned ? MoraleState.Shaken : state;
        e.Squad.SetMorale(state);
    }

    /// <summary>Forces a state and a level inside its band (<see cref="SetMoraleInput"/>).</summary>
    internal void Force(int squadId, MoraleState state)
    {
        var two = Fixed.FromInt(2);
        SetLevel(squadId, state switch
        {
            MoraleState.Steady => Rules.StartLevel,
            MoraleState.Shaken or MoraleState.Pinned => (Rules.BrokenRelease + Rules.ShakenBelow) / two,
            MoraleState.Rallying => (Rules.BrokenRelease + Rules.SteadyAbove) / two,
            MoraleState.Routing => Rules.RoutBelow / two,
            _ => (Rules.RoutBelow + Rules.BrokenBelow) / two,
        });
        SetState(squadId, state);
    }

    public void Tick(World world)
    {
        Sync();
        var dt = World.TickDuration;
        foreach (var e in _entries.Values)
        {
            var squad = e.Squad;
            var able = squad.Soldiers.Count(s => s.CanLead);
            if (able == 0)
            {
                e.Able = 0;
                continue;
            }

            var centre = Centre(squad)!.Value;
            var supported = Near(squad, centre, Rules.SupportRadius, hqOnly: false);
            var nearHq = Near(squad, centre, Rules.HqRallyRadius, hqOnly: true);
            var inContact = command?.InContact(squad) ?? true;
            var supLevel = suppression?.SquadLevelOf(squad.Id) ?? Fixed.Zero;
            var supState = suppression?.SquadStateOf(squad.Id) ?? SuppressionState.None;

            // Shocks.
            var level = e.Level;
            var shockFactor = supported ? Fixed.One - Rules.SupportShockReduction : Fixed.One;
            for (var down = e.Able - able; down > 0; down--)
            {
                var lostAfter = Fixed.FromRatio(e.Initial - (able + down - 1), e.Initial);
                var spread = Rules.ShockSpreadMin + ((Rules.ShockSpreadMax - Rules.ShockSpreadMin) * world.Rng.NextFixed());
                level -= Rules.CasualtyShock * spread * (Fixed.One + lostAfter) * shockFactor;
                e.LastLoss = world.Tick;
            }

            e.Able = Math.Min(e.Able, able);
            if (e.LeaderId >= 0 && squad.Soldiers.First(s => s.Id == e.LeaderId) is { CanLead: false })
            {
                level -= Rules.LeaderShock * shockFactor;
                e.LeaderKilled = world.Tick;
            }

            e.LeaderId = squad.Leader?.Id ?? -1;

            // Drain, ceiling and recovery.
            level -= Rules.SuppressionDrain * supLevel / MoraleRules.MaxLevel * dt;
            var lost = Fixed.FromRatio(e.Initial - able, e.Initial);
            var ceiling = MoraleRules.MaxLevel - (lost * Rules.CasualtyCeiling);
            ceiling -= squad.HasEffectiveLeader ? Fixed.Zero : Rules.LeaderlessCeiling;
            ceiling -= supported ? Fixed.Zero : Rules.IsolatedCeiling;
            ceiling -= inContact ? Fixed.Zero : Rules.OutOfContactCeiling;
            ceiling = Clamp(ceiling);
            var recovering = supState == SuppressionState.None && world.Tick - e.LastLoss > Rules.LossWindowTicks;
            if (level > ceiling)
            {
                var drift = Rules.DriftRate * dt;
                level = level - ceiling > drift ? level - drift : ceiling;
            }
            else if (recovering)
            {
                var mult = Fixed.One + (squad.LeaderBonus * Rules.LeaderRecoveryWeight)
                    + (supported ? Rules.SupportRecoveryBonus : Fixed.Zero) + (nearHq ? Rules.HqRallyBonus : Fixed.Zero);
                var gain = Rules.RecoveryRate * mult * dt;
                level = ceiling - level > gain ? level + gain : ceiling;
            }

            e.Level = Clamp(level);
            squad.SetMoraleLevel(e.Level);

            // State.
            var before = e.Core;
            e.Core = Next(e.Core, e.Level, ceiling, recovering);
            // Cower first (item 2.4, owner decision): a broken or routing squad under shellfire lies flat where it is, not
            // moving (and, through the unit AI, not firing), until no burst has fallen within cower_radius for cower_lift.
            // A squad that routs under shellfire bolts only then.
            if (e.Core == MoraleState.Routing && before != MoraleState.Routing)
            {
                e.FleePending = true;
            }

            e.FleePending &= e.Core == MoraleState.Routing;
            var cowering = e.Core is MoraleState.Broken or MoraleState.Routing && world.Tick - e.LastShelled <= Rules.CowerLiftTicks;
            if (cowering)
            {
                if (move?.IsMoving(squad.Id) == true)
                {
                    move.Stop(squad.Id);
                    e.FleePending |= e.Core == MoraleState.Routing; // caught by shellfire on the run: down, then run again
                }

                foreach (var s in squad.Soldiers)
                {
                    if (s.CanLead)
                    {
                        s.Posture = Sight.Posture.Prone;
                    }
                }
            }
            else if (e.FleePending)
            {
                e.FleePending = false;
                Flee(squad, centre);
            }

            e.Cowering = cowering;

            var pinned = supState == SuppressionState.Pinned;
            squad.SetMorale(pinned && e.Core is MoraleState.Steady or MoraleState.Shaken or MoraleState.Rallying ? MoraleState.Pinned : e.Core);

            // Reasons.
            var r = MoraleReason.None;
            r |= supState != SuppressionState.None ? MoraleReason.UnderFire : MoraleReason.None;
            r |= pinned ? MoraleReason.Pinned : MoraleReason.None;
            r |= able < e.Initial ? MoraleReason.Casualties : MoraleReason.None;
            r |= able < e.Initial && lost >= Rules.HeavyCasualtyFraction ? MoraleReason.HeavyCasualties : MoraleReason.None;
            r |= e.LeaderKilled >= 0 && world.Tick - e.LeaderKilled <= Rules.LeaderKilledMemoryTicks ? MoraleReason.LeaderKilled : MoraleReason.None;
            r |= squad.HasEffectiveLeader ? MoraleReason.None : MoraleReason.Leaderless;
            r |= supported ? MoraleReason.Supported : MoraleReason.Isolated;
            r |= inContact ? MoraleReason.None : MoraleReason.OutOfContact;
            r |= nearHq ? MoraleReason.NearHq : MoraleReason.None;
            r |= e.Cowering ? MoraleReason.Cowering : MoraleReason.None;
            e.Reasons = r;
        }
    }

    public void AppendState(StateHasher hasher)
    {
        Sync();
        hasher.Add(_entries.Count);
        foreach (var (id, e) in _entries)
        {
            hasher.Add(id);
            hasher.Add(e.Level);
            hasher.Add((byte)e.Core);
            hasher.Add(e.LeaderId);
            hasher.Add(e.Able);
            hasher.Add(e.Initial);
            hasher.Add(e.LastLoss);
            hasher.Add(e.LeaderKilled);
            hasher.Add(e.LastShelled);
            hasher.Add(e.FleePending);
            hasher.Add(e.Cowering);
            hasher.Add((ushort)e.Reasons);
        }
    }

    private static Fixed Clamp(Fixed v) => v < Fixed.Zero ? Fixed.Zero : v > MoraleRules.MaxLevel ? MoraleRules.MaxLevel : v;

    private static (Fixed X, Fixed Y)? Centre(Squad squad)
    {
        var x = Fixed.Zero;
        var y = Fixed.Zero;
        var n = 0;
        foreach (var s in squad.Soldiers)
        {
            if (s.CanLead)
            {
                x += s.X;
                y += s.Y;
                n++;
            }
        }

        return n == 0 ? null : (x / Fixed.FromInt(n), y / Fixed.FromInt(n));
    }

    private static Fixed Dist2((Fixed X, Fixed Y) a, (Fixed X, Fixed Y) b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }

    private MoraleState Next(MoraleState core, Fixed level, Fixed ceiling, bool recovering)
    {
        var rallied = ceiling < Rules.SteadyAbove ? ceiling : Rules.SteadyAbove;
        return core switch
        {
            MoraleState.Routing => recovering && level >= Rules.BrokenRelease ? MoraleState.Rallying : MoraleState.Routing,
            MoraleState.Broken => level < Rules.RoutBelow ? MoraleState.Routing
                : recovering && level >= Rules.BrokenRelease ? MoraleState.Rallying
                : MoraleState.Broken,
            MoraleState.Rallying => level < Rules.BrokenBelow ? MoraleState.Broken
                : level >= rallied ? (level >= Rules.ShakenRelease ? MoraleState.Steady : MoraleState.Shaken)
                : MoraleState.Rallying,
            _ when level < Rules.BrokenBelow => MoraleState.Broken,
            MoraleState.Shaken => level >= Rules.ShakenRelease ? MoraleState.Steady : MoraleState.Shaken,
            _ => level < Rules.ShakenBelow ? MoraleState.Shaken : MoraleState.Steady,
        };
    }

    /// <summary>Whether a squad of the side (an HQ, if <paramref name="hqOnly"/>) other than this one has its able men's centre within the radius.</summary>
    private bool Near(Squad squad, (Fixed X, Fixed Y) centre, Fixed radius, bool hqOnly)
    {
        var r2 = radius * radius;
        foreach (var q in org.Squads)
        {
            if (q != squad && q.Side == squad.Side && (!hqOnly || q.IsHq) && Centre(q) is { } c && Dist2(c, centre) <= r2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Starts the rout: away from the mean bearing of the known enemies within <c>rout_threat_radius</c> (the nearest
    /// known one if none is that close or their bearings cancel out), or to the nearest own HQ when none is known. It
    /// takes the nearest reachable of a set of fallback points: the ideal one and points turned 45 and 90 degrees either
    /// side, each at full, half, quarter ... distance, on the map, tried by distance from the ideal point.
    /// </summary>
    private void Flee(Squad squad, (Fixed X, Fixed Y) centre)
    {
        if (move == null)
        {
            return;
        }

        (Fixed X, Fixed Y)? from = null;
        var best = Fixed.MaxValue;
        var r2 = Rules.RoutThreatRadius * Rules.RoutThreatRadius;
        var sx = Fixed.Zero;
        var sy = Fixed.Zero;
        foreach (var c in spotting?.KnownEnemies(squad.Side) ?? [])
        {
            var d = Dist2((c.X, c.Y), centre);
            if (d < best)
            {
                (from, best) = ((c.X, c.Y), d);
            }

            if (d <= r2 && d > Fixed.Zero)
            {
                var l = Fixed.Sqrt(d);
                sx += (centre.X - c.X) / l;
                sy += (centre.Y - c.Y) / l;
            }
        }

        (Fixed X, Fixed Y) dir;
        var dist = Rules.RoutDistance;
        var sum = Fixed.Sqrt((sx * sx) + (sy * sy));
        if (sum > Fixed.FromRatio(1, 100))
        {
            dir = (sx / sum, sy / sum);
        }
        else if (from is { } f)
        {
            var len = Fixed.Sqrt(best);
            dir = len > Fixed.Zero ? ((centre.X - f.X) / len, (centre.Y - f.Y) / len) : (Fixed.One, Fixed.Zero);
        }
        else
        {
            (Fixed X, Fixed Y)? hq = null;
            best = Fixed.MaxValue;
            foreach (var q in org.Squads)
            {
                if (q != squad && q.IsHq && q.Side == squad.Side && Centre(q) is { } c && Dist2(c, centre) < best)
                {
                    (hq, best) = (c, Dist2(c, centre));
                }
            }

            if (hq is not { } h || best == Fixed.Zero)
            {
                return;
            }

            var len = Fixed.Sqrt(best);
            dir = ((h.X - centre.X) / len, (h.Y - centre.Y) / len);
            dist = len;
        }

        var ideal = (centre.X + (dir.X * dist), centre.Y + (dir.Y * dist));
        var c45 = Fixed.FromRatio(707107, 1000000);
        (Fixed X, Fixed Y)[] turns =
        [
            dir, ((dir.X - dir.Y) * c45, (dir.X + dir.Y) * c45), ((dir.X + dir.Y) * c45, (dir.Y - dir.X) * c45),
            (-dir.Y, dir.X), (dir.Y, -dir.X),
        ];
        var candidates = new List<(Fixed D2, int Order, Fixed X, Fixed Y)>();
        for (var i = 0; i < 8; i++, dist /= Fixed.FromInt(2))
        {
            foreach (var t in turns)
            {
                var x = centre.X + (t.X * dist);
                var y = centre.Y + (t.Y * dist);
                if (move.IsOnMap(x, y))
                {
                    candidates.Add((Dist2((x, y), ideal), candidates.Count, x, y));
                }
            }
        }

        candidates.Sort((a, b) => a.D2 != b.D2 ? a.D2.CompareTo(b.D2) : a.Order.CompareTo(b.Order));
        foreach (var c in candidates)
        {
            if (move.Execute(new MoveOrder(squad.Id, c.X, c.Y, MoveMode.MoveFast)) == MoveResult.Started)
            {
                return;
            }
        }
    }

    private Entry Get(int squadId)
    {
        Sync();
        return _entries.TryGetValue(squadId, out var e) ? e : throw new ArgumentException($"No squad {squadId}.");
    }

    /// <summary>Adds a fresh entry for each squad spawned since the last call.</summary>
    private void Sync()
    {
        foreach (var squad in org.Squads)
        {
            if (!_entries.ContainsKey(squad.Id))
            {
                var able = squad.Soldiers.Count(s => s.CanLead);
                var e = new Entry(squad)
                {
                    Level = Rules.StartLevel,
                    Initial = Math.Max(1, squad.Soldiers.Count),
                    Able = able,
                    LeaderId = squad.Leader?.Id ?? -1,
                };
                _entries[squad.Id] = e;
                squad.SetMoraleLevel(e.Level);
            }
        }
    }

    private sealed class Entry(Squad squad)
    {
        public Squad Squad { get; } = squad;

        public Fixed Level { get; set; }

        public MoraleState Core { get; set; }

        public int LeaderId { get; set; }

        public int Able { get; set; }

        public int Initial { get; set; }

        public long LastLoss { get; set; } = long.MinValue / 2;

        public long LeaderKilled { get; set; } = -1;

        public long LastShelled { get; set; } = long.MinValue / 2;

        public bool FleePending { get; set; }

        public bool Cowering { get; set; }

        public MoraleReason Reasons { get; set; }
    }
}
