using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim.UnitAi;

/// <summary>
/// What the unit AI may do: ids and values only, on the units of the side it thinks for (the implementation
/// rejects any other). The same internal paths orders use: posture, a short local move, held teams during a squad
/// move, and an AI fire task below the player's Fire order.
/// </summary>
internal interface IAiEffects
{
    /// <summary>
    /// The posture a man is to take; the last call of a tick wins. He takes it after his own reaction delay, or at once
    /// with <paramref name="timed"/> (the brain has already waited it); with <paramref name="wait"/> he does not walk
    /// before he has.
    /// </summary>
    void SetPosture(int soldierId, Posture posture, bool timed = false, bool wait = false);

    /// <summary>A short local move; <paramref name="fast"/> at MoveFast speed (a dash into position), else at Move speed.</summary>
    bool MoveSoldier(int squadId, int soldierId, TilePos goal, bool fast);

    /// <summary>Holds teams of a moving squad; with <paramref name="rush"/> the others move at MoveFast speed (a rush).</summary>
    void SetHeldTeams(int squadId, int teamMask, bool rush);

    void SetFire(int squadId, int targetId, Fixed x, Fixed y, int teamMask);

    void StopFire(int squadId);

    /// <summary>Keeps one of the squad's men from firing on its AI fire task, or lets him fire again (his own decision).</summary>
    void SetSilent(int squadId, int soldierId, bool silent);

    /// <summary>Holds one man of the squad's move where he is, or frees him (per-man rush and drop).</summary>
    void SetHeldSoldier(int squadId, int soldierId, bool held);
}

/// <summary>
/// The unit AI's decisions (item 2.3), the same code for both sides. It reads only a <see cref="SideView"/> (own
/// squads, <see cref="SideView.KnownEnemies"/>, <see cref="SideView.IncomingFire"/>, order status and ROE), public static
/// data (the map's terrain and hedges, movement and fire tables, <see cref="UnitAiRules"/>) and its own memory of each
/// squad; it acts only through <see cref="IAiEffects"/>. A boundary test checks that nothing here holds a live sim
/// object. Per own squad, per tick:
/// <list type="bullet">
/// <item>Under fire (a near miss or hit on one of its men within <c>under_fire_memory</c>), after <c>reaction_time</c>:
/// men not walking take the cover posture of their tile; an idle squad's leader sends men on open tiles to the nearest
/// covered tile within <c>cover_search_radius</c>, one man per tile.</item>
/// <item>Fire (none while a player Fire order runs, nor when Broken or ROE HoldFire): under fire, at the source if it is a
/// known contact, else at a tracked contact in range, else area fire at the fuzzed point (clamped to effective range);
/// not under fire and FireAtWill, at the nearest tracked contact within <c>effective_range_factor</c> × best range.</item>
/// <item>Contact: idle men of a squad that has a tracked contact or is engaging kneel (<c>posture_contact</c>).</item>
/// <item>Firing line (idle, with a leader, engaging): men abreast across the threat bearing, <c>line_spacing</c> apart,
/// in order of their lateral place; re-formed when the bearing turns <c>line_reform_sectors</c>. Tasks: when a contact
/// of a task's kind is known, the team of its role goes to the best covered tile near the flank instead.</item>
/// <item>Fire and movement (a Move or Sneak in contact, a leader, 2+ teams): teams alternate into two groups; the
/// covering group is held and fires, the other bounds; they swap at <c>bound_distance</c> or after <c>stall_time</c>
/// stalled, and all close in near the end. Under fire the moving men rush (<c>rush_time</c>) and go down
/// (<c>down_time</c>): a rush is a MoveFast dash, and between rushes every man is down. Men of a moving squad who
/// are not walking (waiting on the guide) go down under fire too. Never on MoveFast (or FallBack, or routing), which stays a straight dash. Fire and movement needs fire
/// superiority: without support, a squad under equal fire is pinned (item 2.1) and holds its advance; a second unit must
/// suppress the enemy first (fix and assault, the runner's <c>fix-assault</c> scenario). Owner decision, item 2.3.</item>
/// <item>A routing squad never fires (its fire task is cleared); under fire its run is broken into MoveFast dashes
/// and drops, like rushes.</item>
/// </list>
/// </summary>
internal sealed class UnitAiBrain(GameMap map, MovementRules moveRules, FireRules fireRules, UnitAiRules rules)
{
    private readonly SortedDictionary<int, Mind> _minds = [];
    private readonly HashSet<int> _claimed = [];

    /// <summary>
    /// The drill a man is on, read from the squad's memory, for the debug overlay (item 2.6): it changes nothing.
    /// <paramref name="done"/> is the kind of the squad's finished order, if any. A drill that says how a man moves or lies is
    /// named only for a man doing so (item 2.5a): <paramref name="walking"/> is his own change of place, not the squad's;
    /// the firing line needs fire he can bring (<paramref name="shooting"/>). A routing man who is not running reads as
    /// broken, lying or halted.
    /// </summary>
    public IntentKind TaskOf(
        int squadId, int soldierId, int teamIndex, bool prone, bool walking, bool shooting, long tick, bool moving, bool routing,
        OrderKind? done, int heldTeams, bool cowering)
    {
        _minds.TryGetValue(squadId, out var m);
        if (cowering)
        {
            return m is { CowerStart: >= 0 } && prone && !walking && tick >= m.CowerStart + Draw(soldierId, m.CowerStart, 6, rules.RushJitterTicks)
                ? IntentKind.Cowering
                : IntentKind.None;
        }

        if (routing)
        {
            return walking ? IntentKind.Fleeing : prone ? IntentKind.BrokenLying : IntentKind.BrokenStill;
        }

        if (moving)
        {
            var held = (heldTeams & (1 << teamIndex)) != 0;
            return m?.Phase == 2 && prone && !walking ? IntentKind.DownBetweenRushes
                : heldTeams != 0 && held != walking ? (held ? IntentKind.Covering : IntentKind.Bounding)
                : heldTeams == 0 && m?.Phase == 1 && walking ? IntentKind.Rushing
                : IntentKind.None;
        }

        var inLine = m is { Firing: true, LineSector: >= 0 };
        return inLine && m!.TaskTeam == teamIndex && (walking || shooting) ? IntentKind.Flanking
            : done == OrderKind.Defend ? IntentKind.Defending
            : done == OrderKind.Ambush ? IntentKind.Ambushing
            : inLine && shooting ? IntentKind.FiringLine
            : IntentKind.None;
    }

    public void Think(SideView view, IAiEffects fx)
    {
        var tick = view.Tick;
        var squads = view.Squads;
        var known = view.KnownEnemies;
        var men = new Dictionary<int, (int SquadId, SoldierView Soldier)>();
        foreach (var q in squads)
        {
            foreach (var s in q.Soldiers)
            {
                men[s.Id] = (q.Id, s);
            }
        }

        foreach (var e in view.IncomingFire)
        {
            if (men.TryGetValue(e.SoldierId, out var o))
            {
                Note(MindOf(o.SquadId), e, o.Soldier, tick);
            }
        }

        foreach (var q in squads)
        {
            ThinkSquad(view, fx, q, known, tick);
        }
    }

    public void AppendState(StateHasher hasher)
    {
        hasher.Add(_minds.Count);
        foreach (var (id, m) in _minds)
        {
            hasher.Add(id);
            hasher.Add(m.LastUnderFire);
            hasher.Add(m.UnderFireSince);
            hasher.Add(m.LastEnemyFire);
            hasher.Add(m.EnemyFireSince);
            hasher.Add(m.NoteTick);
            hasher.Add(m.SourceId);
            hasher.Add(m.FireX);
            hasher.Add(m.FireY);
            hasher.Add(m.Band);
            hasher.Add(m.LineSector);
            hasher.Add(m.CoverMoved);
            hasher.Add(m.Group);
            hasher.Add(m.Stall);
            hasher.Add(m.BestProgress);
            hasher.Add(m.Phase);
            hasher.Add(m.PhaseLeft);
            hasher.Add(m.TaskTeam);
            hasher.Add(m.Firing);
            hasher.Add(m.TargetId);
            hasher.Add(m.DefendTick);
            hasher.Add(m.LastShelled);
            hasher.Add(m.FireStart);
            hasher.Add(m.FireEnd);
            hasher.Add(m.BoltStart);
            hasher.Add(m.CowerStart);
            hasher.Add(m.StoppedSince);
            hasher.Add(m.BoltAt.Count);
            foreach (var (sid, at) in m.BoltAt)
            {
                hasher.Add(sid);
                hasher.Add(at);
            }

            hasher.Add(m.BurstDist.Count);
            foreach (var (sid, bd) in m.BurstDist)
            {
                hasher.Add(sid);
                hasher.Add(bd);
            }
        }
    }

    private static bool Able(SoldierView s) => s.Health is HealthState.Ok or HealthState.Wounded;

    private static Fixed Len(Fixed x, Fixed y) => Fixed.Sqrt((x * x) + (y * y));

    private Mind MindOf(int squadId)
    {
        if (!_minds.TryGetValue(squadId, out var m))
        {
            _minds[squadId] = m = new Mind();
        }

        return m;
    }

    /// <summary>Records one incoming round: the newest, nearest or known-source one sets the point the squad fires back at.</summary>
    private void Note(Mind m, IncomingFireView e, SoldierView victim, long tick)
    {
        if (e.Friendly)
        {
            return; // a friend's round is not enemy fire: men do not go to ground for it (it put a firing line prone, blind)
        }

        if (tick - m.LastUnderFire > rules.UnderFireMemoryTicks)
        {
            m.UnderFireSince = e.Tick;
            m.CoverMoved = false;
            m.Phase = 0;
        }

        m.LastUnderFire = Math.Max(m.LastUnderFire, e.Tick);
        if (e.Explosion)
        {
            m.LastShelled = Math.Max(m.LastShelled, e.Tick);
            m.BurstDist[victim.Id] = e.Distance;
            return; // a shell burst (item 2.4): men go to ground, but there is no one to fire back at
        }

        if (tick - m.LastEnemyFire > rules.UnderFireMemoryTicks)
        {
            m.EnemyFireSince = e.Tick;
        }

        m.LastEnemyFire = Math.Max(m.LastEnemyFire, e.Tick);
        if (e.SourceId >= 0 || m.NoteTick != e.Tick || (m.SourceId < 0 && e.Distance < m.Band))
        {
            m.NoteTick = e.Tick;
            m.SourceId = e.SourceId;
            m.Band = e.Distance;
            m.FireX = victim.X + (e.DirX * e.Distance);
            m.FireY = victim.Y + (e.DirY * e.Distance);
        }
    }

    private void ThinkSquad(SideView view, IAiEffects fx, SquadView q, IReadOnlyList<ContactView> known, long tick)
    {
        var m = MindOf(q.Id);
        var able = q.Soldiers.Where(Able).ToList();
        if ((q.MoraleReasons & Morale.MoraleReason.Cowering) != 0)
        {
            // Broken or routing under shellfire (item 2.4): flat where they are, no fire and no moves of the AI's own.
            if (m.Firing)
            {
                fx.StopFire(q.Id);
                (m.Firing, m.FireEnd) = (false, tick);
            }

            // Each man drops on his own reaction (no squad-wide flip), within rush_jitter of the squad starting to cower.
            if (m.CowerStart < 0)
            {
                m.CowerStart = tick;
            }

            ClearBolt(fx, q, m);
            m.StoppedSince = -1;
            foreach (var s in able)
            {
                if (tick >= m.CowerStart + Draw(s.Id, m.CowerStart, 6, rules.RushJitterTicks))
                {
                    fx.SetPosture(s.Id, Posture.Prone, timed: true);
                }
            }

            return;
        }

        m.CowerStart = -1;
        if (able.Count == 0 || q.Morale == MoraleState.Routing)
        {
            if (m.Firing)
            {
                fx.StopFire(q.Id);
                m.Firing = false;
            }

            if (able.Count > 0 && q.IsMoving)
            {
                m.StoppedSince = -1;
                Flee(fx, q, able, m, tick);
            }
            else
            {
                m.Phase = 0;
                ClearBolt(fx, q, m);
                if (m.StoppedSince < 0)
                {
                    m.StoppedSince = tick;
                }

                var from = Math.Max(m.UnderFireSince, m.StoppedSince);
                foreach (var s in able)
                {
                    // Each man on his own reaction (no squad-wide flip): within rush_jitter of the fire spell starting, or
                    // of the squad stopping in one already running.
                    if (tick - m.LastUnderFire <= rules.UnderFireMemoryTicks
                        && tick >= from + Draw(s.Id, from, 7, rules.RushJitterTicks))
                    {
                        fx.SetPosture(s.Id, rules.PostureOpen, timed: true); // routed and stopped under fire: down
                    }
                }
            }

            return;
        }

        ClearBolt(fx, q, m);
        m.StoppedSince = -1;
        var cx = Fixed.Zero;
        var cy = Fixed.Zero;
        var range = Fixed.Zero;
        foreach (var s in able)
        {
            cx += s.X;
            cy += s.Y;
            var w = fireRules.TryWeapon(s.WeaponId);
            range = w != null && w.MaxRange > range ? w.MaxRange : range;
        }

        cx /= Fixed.FromInt(able.Count);
        cy /= Fixed.FromInt(able.Count);
        var engage = range * rules.EffectiveRangeFactor;

        var status = view.StatusOf(q.Id);
        var order = status?.Order;
        var playerFire = status is { Status: OrderStatus.Executing } && order?.Kind == OrderKind.Fire;
        var moving = q.IsMoving;
        var mode = !moving ? (MoveMode?)null
            : order != null && OrderKinds.IsMove(order.Kind) && status!.Status == OrderStatus.Executing ? OrderKinds.MoveModeOf(order.Kind)
            : MoveMode.MoveFast; // a flee or other move the AI did not order: leave it alone
        // Enemy rounds close by put men down, start rushes and are fired back at (friendly rounds are ignored).
        var underFire = tick - m.LastUnderFire <= rules.UnderFireMemoryTicks;
        var reacting = underFire && tick - m.UnderFireSince >= rules.ReactionTicks;
        var enemyFire = tick - m.LastEnemyFire <= rules.UnderFireMemoryTicks;
        var returning = enemyFire && tick - m.EnemyFireSince >= rules.ReactionTicks;

        ContactView? tracked = null;
        var trackedD2 = Fixed.MaxValue;
        ContactView? source = null;
        foreach (var c in known)
        {
            var d2 = ((c.X - cx) * (c.X - cx)) + ((c.Y - cy) * (c.Y - cy));
            if (c.Quality == ContactQuality.Tracked && d2 < trackedD2)
            {
                (tracked, trackedD2) = (c, d2);
            }

            if (enemyFire && c.EnemyId == m.SourceId)
            {
                source = c;
            }
        }

        var inRange = tracked != null && trackedD2 <= engage * engage;

        // Fire discipline (item 2.4 follow-up): on Defend or Ambush the squad opens fire on its own only at close range.
        var defending = status is { Status: OrderStatus.Done } && order is { Kind: OrderKind.Defend or OrderKind.Ambush };
        var open = defending && rules.DefendOpenRange < engage ? rules.DefendOpenRange : engage;
        var openRange = tracked != null && trackedD2 <= open * open;

        // The fire task.
        var roe = view.RoeOf(q.Id) ?? RoeMode.FireAtWill;
        var fireId = -2;
        Fixed fireX = Fixed.Zero, fireY = Fixed.Zero;
        if (!playerFire && q.Morale != MoraleState.Broken && roe != RoeMode.HoldFire)
        {
            if (returning && source is { } src)
            {
                (fireId, fireX, fireY) = (src.EnemyId, src.X, src.Y);
            }
            else if (returning && inRange)
            {
                (fireId, fireX, fireY) = (tracked!.Value.EnemyId, tracked.Value.X, tracked.Value.Y);
            }
            else if (returning)
            {
                var dx = m.FireX - cx;
                var dy = m.FireY - cy;
                var d = Len(dx, dy);
                if (d > Fixed.One)
                {
                    var r = d > engage ? engage : d;
                    (fireId, fireX, fireY) = (-1, cx + (dx * r / d), cy + (dy * r / d));
                }
            }
            else if (roe == RoeMode.FireAtWill && openRange)
            {
                (fireId, fireX, fireY) = (tracked!.Value.EnemyId, tracked.Value.X, tracked.Value.Y);
            }
            else if (roe == RoeMode.FireAtWill && m.Firing && known.FirstOrDefault(c => c.EnemyId == m.TargetId) is { Quality: ContactQuality.Stale } last
                && ((last.X - cx) * (last.X - cx)) + ((last.Y - cy) * (last.Y - cy)) <= engage * engage)
            {
                (fireId, fireX, fireY) = (last.EnemyId, last.X, last.Y); // keep fire on a target that just went out of sight
            }
        }

        // The threat the squad faces.
        var hasThreat = true;
        Fixed tx, ty;
        if (fireId >= -1)
        {
            (tx, ty) = (fireX, fireY);
        }
        else if (source is { } s0)
        {
            (tx, ty) = (s0.X, s0.Y);
        }
        else if (enemyFire)
        {
            (tx, ty) = (m.FireX, m.FireY);
        }
        else if (tracked is { } t0)
        {
            (tx, ty) = (t0.X, t0.Y);
        }
        else
        {
            (tx, ty, hasThreat) = (cx, cy, false);
        }

        var tl = Len(tx - cx, ty - cy);
        hasThreat &= tl > Fixed.One;
        var (ux, uy) = hasThreat ? ((tx - cx) / tl, (ty - cy) / tl) : (Fixed.Zero, Fixed.Zero);

        // Fire and movement, and rushes under fire.
        var teams = able.Select(s => s.TeamIndex).Distinct().Order().ToList();
        var all = 0;
        foreach (var t in teams)
        {
            all |= 1 << t;
        }

        var held = 0;
        var fireMask = -1;
        if (moving && mode != MoveMode.MoveFast && (underFire || inRange))
        {
            if (q.LeaderId >= 0 && teams.Count >= 2 && order != null)
            {
                int Group(int team) => teams.IndexOf(team) % 2;
                int CoverMask()
                {
                    var mask = 0;
                    foreach (var t in teams)
                    {
                        mask |= Group(t) != m.Group ? 1 << t : 0;
                    }

                    return mask;
                }

                var bound = able.Where(s => Group(s.TeamIndex) == m.Group).ToList();
                var cover = able.Where(s => Group(s.TeamIndex) != m.Group).ToList();
                var ox = order.X - cx;
                var oy = order.Y - cy;
                var ol = Len(ox, oy);
                if (ol <= rules.BoundDistance || bound.Count == 0 || cover.Count == 0)
                {
                    m.Stall = 0; // close in: everyone moves to the end
                }
                else
                {
                    // Stalled: the bounding group's progress toward the end point (not the men's steps) stopped.
                    var progress = ((Mean(bound, true) * ox) + (Mean(bound, false) * oy)) / ol;
                    if (m.Phase != 2)
                    {
                        if (m.Stall == 0 || progress > m.BestProgress + Fixed.One)
                        {
                            (m.BestProgress, m.Stall) = (progress, 1);
                        }
                        else if (!bound.Any(s => s.Moving))
                        {
                            m.Stall++; // still and not gaining: blocked, or waiting on nobody
                        }
                    }

                    var along = (((Mean(bound, true) - Mean(cover, true)) * ox) + ((Mean(bound, false) - Mean(cover, false)) * oy)) / ol;
                    if (along >= rules.BoundDistance || m.Stall > rules.StallTicks)
                    {
                        m.Group ^= 1;
                        m.Stall = 0;
                    }

                    held = CoverMask();
                    fireMask = held;
                }
            }

            if (underFire)
            {
                if (m.Phase == 0)
                {
                    (m.Phase, m.PhaseLeft) = (1, rules.RushTicks);
                }
                else if (--m.PhaseLeft <= 0)
                {
                    (m.Phase, m.PhaseLeft) = m.Phase == 1 ? (2, rules.DownTicks) : (1, rules.RushTicks);
                }

                held = m.Phase == 2 ? all : held;
            }
            else
            {
                m.Phase = 0;
            }
        }
        else
        {
            (m.Phase, m.Stall) = (0, 0);
        }

        if (moving)
        {
            // A rush is a MoveFast dash; between rushes everyone is down.
            fx.SetHeldTeams(q.Id, held, underFire && m.Phase == 1);
        }

        // Posture.
        var engaging = fireId >= -1 || playerFire;
        foreach (var s in able)
        {
            var isHeld = moving && (held & (1 << s.TeamIndex)) != 0;
            Posture want;
            if (moving && !isHeld && (s.Moving || !underFire))
            {
                want = moveRules.PostureOf(mode!.Value); // walking (or rushing) on the squad's move
            }
            else if (moving && !isHeld)
            {
                want = CoverPosture(s, ux, uy, hasThreat); // under fire and waiting on the squad's move: down
            }
            else if (s.Moving)
            {
                if (!underFire || s.Posture != Posture.Standing)
                {
                    continue; // a local move: keep the posture he set off in
                }

                want = rules.PostureContact; // under fire on a local move: crouch
            }
            else if (reacting || (isHeld && underFire))
            {
                want = CoverPosture(s, ux, uy, hasThreat);
            }
            else if ((isHeld || engaging || tracked != null) && (s.PendingPosture ?? s.Posture) == Posture.Standing)
            {
                want = rules.PostureContact;
            }
            else
            {
                continue;
            }

            if (want == rules.PostureCover && !moving && tick - m.LastShelled <= rules.UnderFireMemoryTicks)
            {
                want = rules.PostureOpen; // under shellfire a man lies flat, even in cover
            }

            fx.SetPosture(s.Id, want);
        }

        // The leader's drills, for an idle squad.
        if (!moving && q.LeaderId >= 0 && hasThreat)
        {
            var sector = Bearings.SectorOf(ux, uy);
            if (engaging && !playerFire && (m.LineSector < 0 || Bearings.Between(sector, m.LineSector) >= rules.LineReformSectors))
            {
                FormLine(fx, q, able, known, cx, cy, ux, uy, m);
                (m.LineSector, m.CoverMoved) = (sector, true);
            }
            else if (reacting && !m.CoverMoved)
            {
                CoverMoves(fx, q, able, ux, uy);
                m.CoverMoved = true;
            }
        }

        // Defend or Ambush (item 2.4 follow-up): once per order, the men take firing positions at a hedge facing the order
        // point, where they can see and fire through it (LineOfSight: a man at a hedge), instead of lying blind behind it.
        if (!moving && status is { Status: OrderStatus.Done } && order is { Kind: OrderKind.Defend or OrderKind.Ambush }
            && m.DefendTick != status.IssuedTick)
        {
            m.DefendTick = status.IssuedTick;
            TakeFiringPositions(fx, q, able, order.X, order.Y);
        }

        if (fireId >= -1)
        {
            if (!m.Firing && tick - m.FireEnd > rules.UnderFireMemoryTicks)
            {
                m.FireStart = tick;
            }

            fx.SetFire(q.Id, fireId, fireX, fireY, fireMask);
            (m.Firing, m.TargetId) = (true, fireId);
            Discipline(fx, q, able, m, tick);
        }
        else if (m.Firing)
        {
            fx.StopFire(q.Id);
            (m.Firing, m.FireEnd) = (false, tick);
            foreach (var s in able)
            {
                fx.SetSilent(q.Id, s.Id, false);
            }
        }

        // A badly suppressed man keeps low (item 2.4 follow-up), whatever the squad is doing, unless he is on the move.
        foreach (var s in able)
        {
            if (!s.Moving && s.Suppression >= rules.FireHoldSuppression)
            {
                fx.SetPosture(s.Id, rules.PostureOpen);
            }
        }
    }

    /// <summary>
    /// Per-man fire decisions (item 2.4 follow-up) on the squad's AI fire task. A man fires only when all hold: his
    /// suppression is below <c>fire_hold_suppression</c> (else he keeps his head down); he is not one of the men who stay
    /// silent through this task (<c>silent_chance</c>, <c>shaken_silent_chance</c> when the squad is shaken, pinned or rallying);
    /// and his own delay since the task started has run: the leader and the MG gunner within <c>open_delay</c>, the others
    /// between <c>join_delay_min</c> and <c>join_delay_max</c> (doubled with no leader). The draws are a deterministic hash
    /// of the man and the task's start tick, so they need no state and no RNG; both sides use the same rules.
    /// </summary>
    private void Discipline(IAiEffects fx, SquadView q, List<SoldierView> able, Mind m, long tick)
    {
        var shaken = q.Morale is MoraleState.Shaken or MoraleState.Pinned or MoraleState.Rallying;
        var silentChance = shaken ? rules.ShakenSilentChance : rules.SilentChance;
        foreach (var s in able)
        {
            var first = s.Id == q.LeaderId || s.Role == Role.MgGunner;
            var delay = first
                ? Draw(s.Id, m.FireStart, 1, rules.OpenDelayTicks)
                : rules.JoinDelayMinTicks + Draw(s.Id, m.FireStart, 2, rules.JoinDelayMaxTicks - rules.JoinDelayMinTicks);
            delay *= q.LeaderId < 0 ? 2 : 1;
            var silent = !first && Fixed.FromRatio(Draw(s.Id, m.FireStart, 3, 1_000_000), 1_000_000) < silentChance;
            var hold = s.Suppression >= rules.FireHoldSuppression || silent || tick < m.FireStart + delay;
            fx.SetSilent(q.Id, s.Id, hold);
        }
    }

    /// <summary>A deterministic draw in [0, <paramref name="range"/>] from a man, a tick and a salt (SplitMix64 finaliser).</summary>
    private static int Draw(int soldierId, long tick, int salt, int range)
    {
        if (range <= 0)
        {
            return 0;
        }

        var z = unchecked(((ulong)soldierId * 0x9E3779B97F4A7C15UL) ^ ((ulong)tick * 0xBF58476D1CE4E5B9UL) ^ ((ulong)salt * 0x94D049BB133111EBUL));
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (int)(z % (ulong)(range + 1));
    }

    /// <summary>
    /// A routing squad's run (MoraleSystem chose where): it never fires, but under fire its men still dash and drop:
    /// rushes at MoveFast for <c>rush_time</c>, then everyone down (<c>posture_open</c>) for <c>down_time</c>.
    /// </summary>
    private void Flee(IAiEffects fx, SquadView q, List<SoldierView> able, Mind m, long tick)
    {
        // Item 2.4 review: each man on his own. He gets up after his own delay from the start of the flee (BoltDelay), and
        // under fire his rush and drop cycle is offset by his own draw, so the squad never stands or drops on one tick.
        // The squad still runs one way: the move (MoraleSystem chose where) is shared; a held man just waits.
        if (m.BoltStart < 0)
        {
            m.BoltStart = tick;
        }

        var period = Math.Max(1, rules.RushTicks + rules.DownTicks);
        foreach (var s in able)
        {
            if (!m.BoltAt.TryGetValue(s.Id, out var start))
            {
                m.BoltAt[s.Id] = start = m.BoltStart + BoltDelay(q, s, m, tick); // fixed once: his rush cycle keeps its length
            }

            if (s.SuppressionState == Suppression.SuppressionState.Pinned)
            {
                // A pinned man does not get up for the next rush: he stays where he is, and SuppressionSystem lays him flat
                // on his own reaction. (Re-standing every pinned man on the tick after a pin was the last squad-wide flip.)
                // Once he is no longer pinned he still takes his own moment (a draw within rush_jitter) before he gets up.
                var after = tick + 1 + Draw(s.Id, m.BoltStart, 8, rules.RushJitterTicks);
                m.BoltAt[s.Id] = m.BoltAt.TryGetValue(s.Id, out var was) ? Math.Max(was, after) : Math.Max(m.BoltStart + BoltDelay(q, s, m, tick), after);
                fx.SetHeldSoldier(q.Id, s.Id, true);
                continue;
            }

            // Each man takes up the rush and drop cycle on his own reaction when the fire starts, and keeps it a moment
            // longer when it lifts (both his own draw), so the men who are down do not all drop or get up together.
            var underFire = tick >= m.UnderFireSince + Draw(s.Id, m.UnderFireSince, 10, rules.RushJitterTicks)
                && tick - m.LastUnderFire <= rules.UnderFireMemoryTicks + Draw(s.Id, m.LastUnderFire, 9, rules.RushJitterTicks);
            // His offset delays the moment he gets up, and his cycle starts there (item 2.4b): a man who has just got up
            // to run does not drop again at once because of where the cycle happens to be.
            var rise = start + Draw(s.Id, m.BoltStart, 5, rules.RushJitterTicks);
            var down = tick < rise || (underFire && (tick - rise) % period >= rules.RushTicks);
            fx.SetHeldSoldier(q.Id, s.Id, down);
            var want = down ? rules.PostureOpen : moveRules.PostureOf(MoveMode.MoveFast);
            // Dropping out of a rush is already offset man by man. Getting up (a new rush, or the fire lifting) and the drop
            // of a man still waiting to bolt take his own reaction, as every other decision does: men whose timers happen
            // to end together do not get up together.
            fx.SetPosture(s.Id, want, timed: down && tick >= rise);
        }

        fx.SetHeldTeams(q.Id, 0, tick - m.LastUnderFire <= rules.UnderFireMemoryTicks);
    }

    /// <summary>
    /// The ticks a man of a routing squad waits before getting up: his own draw within <c>bolt_delay</c> (the leader within a
    /// quarter of it), plus <c>bolt_suppression_delay</c> × his suppression / 100, plus <c>bolt_burst_delay</c> if the last
    /// burst he noticed (within the under-fire memory) was close, falling to 0 at <c>bolt_burst_radius</c>.
    /// </summary>
    private int BoltDelay(SquadView q, SoldierView s, Mind m, long tick)
    {
        var delay = Draw(s.Id, m.BoltStart, 4, s.Id == q.LeaderId ? rules.BoltDelayTicks / 4 : rules.BoltDelayTicks);
        delay += (Fixed.FromInt(rules.BoltSuppressionTicks) * s.Suppression / Fixed.FromInt(100)).ToInt();
        if (tick - m.LastShelled <= rules.UnderFireMemoryTicks + rules.BoltDelayTicks && m.BurstDist.TryGetValue(s.Id, out var d)
            && rules.BoltBurstRadius > Fixed.Zero && d < rules.BoltBurstRadius)
        {
            delay += (Fixed.FromInt(rules.BoltBurstTicks) * (Fixed.One - (d / rules.BoltBurstRadius))).ToInt();
        }

        return delay;
    }

    /// <summary>Frees every man the per-man flee held and forgets its start (the squad is no longer fleeing).</summary>
    private void ClearBolt(IAiEffects fx, SquadView q, Mind m)
    {
        if (m.BoltStart < 0)
        {
            return;
        }

        m.BoltStart = -1;
        m.BoltAt.Clear();
        foreach (var s in q.Soldiers)
        {
            fx.SetHeldSoldier(q.Id, s.Id, false);
        }
    }

    private static Fixed Mean(List<SoldierView> men, bool x)
    {
        var sum = Fixed.Zero;
        foreach (var s in men)
        {
            sum += x ? s.X : s.Y;
        }

        return sum / Fixed.FromInt(men.Count);
    }

    private TilePos TileOf(Fixed x, Fixed y)
    {
        var tx = (x / map.TileSize).ToInt();
        var ty = (y / map.TileSize).ToInt();
        return new TilePos(Math.Clamp(tx, 0, map.Width - 1), Math.Clamp(ty, 0, map.Height - 1));
    }

    private (Fixed X, Fixed Y) Centre(TilePos t) =>
        ((Fixed.FromInt(t.X) + Fixed.FromRatio(1, 2)) * map.TileSize, (Fixed.FromInt(t.Y) + Fixed.FromRatio(1, 2)) * map.TileSize);

    /// <summary>
    /// How well a tile covers a man from the threat direction: 2 inside a building or behind a hedge on the tile edge
    /// facing the threat (the dominant axis; with no threat, any hedge edge), 1 in terrain whose cover is at least
    /// <c>cover_min</c>, else 0.
    /// </summary>
    private int CoverScore(TilePos t, Fixed ux, Fixed uy, bool hasThreat)
    {
        if (map.BuildingAt(t.X, t.Y) != null)
        {
            return 2;
        }

        bool East() => map.HasHedge(t.X, t.Y, EdgeSide.East);
        bool West() => t.X > 0 && map.HasHedge(t.X - 1, t.Y, EdgeSide.East);
        bool South() => map.HasHedge(t.X, t.Y, EdgeSide.South);
        bool North() => t.Y > 0 && map.HasHedge(t.X, t.Y - 1, EdgeSide.South);
        var ax = ux < Fixed.Zero ? -ux : ux;
        var ay = uy < Fixed.Zero ? -uy : uy;
        var hedge = !hasThreat ? East() || West() || South() || North()
            : ax >= ay ? (ux > Fixed.Zero ? East() : West())
            : (uy > Fixed.Zero ? South() : North());
        return hedge ? 2 : fireRules.Cover(map.GetTerrain(t.X, t.Y)) >= rules.CoverMin ? 1 : 0;
    }

    private Posture CoverPosture(SoldierView s, Fixed ux, Fixed uy, bool hasThreat) =>
        s.SuppressionState == Suppression.SuppressionState.Pinned ? rules.PostureOpen
        : CoverScore(TileOf(s.X, s.Y), ux, uy, hasThreat) > 0 ? rules.PostureCover
        : rules.PostureOpen;

    /// <summary>The unclaimed tile within <paramref name="radius"/> of a point with the best cover, then the nearest; null if none.</summary>
    private TilePos? BestTile(
        Fixed x, Fixed y, Fixed radius, Fixed ux, Fixed uy, int minScore,
        Fixed cx = default, Fixed cy = default, Fixed px = default, Fixed py = default, List<Fixed>? laterals = null)
    {
        var gap = Clearance() + Fixed.FromRatio(1, 4);
        var size = map.TileSize;
        var r = (radius / size).ToInt() + 1;
        var c = TileOf(x, y);
        TilePos? best = null;
        var bestScore = -1;
        var bestD = Fixed.MaxValue;
        var r2 = radius * radius;
        for (var ty = c.Y - r; ty <= c.Y + r; ty++)
        {
            for (var tx = c.X - r; tx <= c.X + r; tx++)
            {
                if (!map.InBounds(tx, ty) || _claimed.Contains((ty * map.Width) + tx))
                {
                    continue;
                }

                var t = new TilePos(tx, ty);
                var (tcx, tcy) = Centre(t);
                var d = ((tcx - x) * (tcx - x)) + ((tcy - y) * (tcy - y));
                if (d > r2 && !(tx == c.X && ty == c.Y))
                {
                    continue;
                }

                var lat = ((tcx - cx) * px) + ((tcy - cy) * py);
                if (laterals != null && laterals.Any(l => (lat - l < Fixed.Zero ? l - lat : lat - l) <= gap))
                {
                    continue; // the task team keeps clear of the line's lines of fire
                }

                var score = CoverScore(t, ux, uy, true);
                if (score >= minScore && (score > bestScore || (score == bestScore && d < bestD)))
                {
                    (best, bestScore, bestD) = (t, score, d);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// A firing-line tile near a slot (within two tiles): unclaimed, and laterally (across the threat) further than the AI
    /// fire clearance from every tile already given to the line, so no man in the line masks another once it is formed.
    /// Tiles snap slots to the grid, which could otherwise bring two men within the clearance. Best cover first, then nearest.
    /// </summary>
    private TilePos? LineTile(Fixed x, Fixed y, Fixed cx, Fixed cy, Fixed px, Fixed py, Fixed ux, Fixed uy, List<Fixed> laterals)
    {
        var gap = Clearance() + Fixed.FromRatio(1, 4);
        var c = TileOf(x, y);
        TilePos? best = null;
        var bestScore = -1;
        var bestD = Fixed.MaxValue;
        for (var ty = c.Y - 2; ty <= c.Y + 2; ty++)
        {
            for (var tx = c.X - 2; tx <= c.X + 2; tx++)
            {
                if (!map.InBounds(tx, ty) || _claimed.Contains((ty * map.Width) + tx))
                {
                    continue;
                }

                var t = new TilePos(tx, ty);
                var (tcx, tcy) = Centre(t);
                var lat = ((tcx - cx) * px) + ((tcy - cy) * py);
                if (laterals.Any(l => (lat - l < Fixed.Zero ? l - lat : lat - l) <= gap))
                {
                    continue;
                }

                var d = ((tcx - x) * (tcx - x)) + ((tcy - y) * (tcy - y));
                var score = CoverScore(t, ux, uy, true);
                if (score > bestScore || (score == bestScore && d < bestD))
                {
                    (best, bestScore, bestD) = (t, score, d);
                }
            }
        }

        return best;
    }

    /// <summary>The AI fire clearance (see <see cref="FireSystem"/>): the near-miss radius, or the mask clearance if larger.</summary>
    private Fixed Clearance() => fireRules.NearMissRadius > fireRules.MaskClearance ? fireRules.NearMissRadius : fireRules.MaskClearance;

    private void Claim(TilePos t) => _claimed.Add((t.Y * map.Width) + t.X);

    private void Send(IAiEffects fx, SquadView q, SoldierView s, TilePos goal, bool fast = false)
    {
        Claim(goal);
        if (goal != TileOf(s.X, s.Y))
        {
            fx.MoveSoldier(q.Id, s.Id, goal, fast);
        }
    }

    /// <summary>
    /// Each man not already at a hedge (or in a building) facing the point goes to the nearest such tile within
    /// <c>defend_search_radius</c> (one man per tile) and lies down there (reactions under fire may then kneel him).
    /// </summary>
    private void TakeFiringPositions(IAiEffects fx, SquadView q, List<SoldierView> able, Fixed px, Fixed py)
    {
        _claimed.Clear();
        var cx = Fixed.Zero;
        var cy = Fixed.Zero;
        foreach (var s in able)
        {
            cx += s.X;
            cy += s.Y;
        }

        cx /= Fixed.FromInt(able.Count);
        cy /= Fixed.FromInt(able.Count);
        var l = Len(px - cx, py - cy);
        if (l <= Fixed.One)
        {
            return;
        }

        var (ux, uy) = ((px - cx) / l, (py - cy) / l);
        var moving = new List<SoldierView>();
        foreach (var s in able)
        {
            if (CoverScore(TileOf(s.X, s.Y), ux, uy, true) >= 2)
            {
                Claim(TileOf(s.X, s.Y));
            }
            else
            {
                moving.Add(s);
            }
        }

        foreach (var s in moving)
        {
            if (BestTile(s.X, s.Y, rules.DefendSearchRadius, ux, uy, 2) is { } goal)
            {
                Send(fx, q, s, goal);
            }
        }

        foreach (var s in able)
        {
            // Lying at the hedge: he sees through it (LineOfSight) and shows little. He is down before he moves up.
            fx.SetPosture(s.Id, Posture.Prone, wait: true);
        }
    }

    /// <summary>The leader sends each man in the open to the best covered tile near him (one man per tile).</summary>
    private void CoverMoves(IAiEffects fx, SquadView q, List<SoldierView> able, Fixed ux, Fixed uy)
    {
        _claimed.Clear();
        var open = new List<SoldierView>();
        foreach (var s in able)
        {
            var t = TileOf(s.X, s.Y);
            if (CoverScore(t, ux, uy, true) > 0 || s.Moving)
            {
                Claim(t);
            }
            else
            {
                open.Add(s);
            }
        }

        foreach (var s in open)
        {
            if (BestTile(s.X, s.Y, rules.CoverSearchRadius, ux, uy, 1) is { } goal)
            {
                Send(fx, q, s, goal);
            }
        }
    }

    /// <summary>The firing line across the threat bearing, and the specialist task if one applies.</summary>
    private void FormLine(
        IAiEffects fx, SquadView q, List<SoldierView> able, IReadOnlyList<ContactView> known, Fixed cx, Fixed cy, Fixed ux, Fixed uy, Mind m)
    {
        _claimed.Clear();
        var (px, py) = (-uy, ux);
        AiTaskRule? task = null;
        var taskTeam = -1;
        foreach (var rule in rules.Tasks)
        {
            var man = able.FirstOrDefault(s => s.Role == rule.Role);
            if (man.Key != null && known.Any(c => rule.TargetKind == null || c.Kind == rule.TargetKind))
            {
                (task, taskTeam) = (rule, man.TeamIndex);
                break;
            }
        }

        var line = able.Where(s => s.TeamIndex != taskTeam).ToList();
        if (line.Count == 0)
        {
            (line, task, taskTeam) = (able, null, -1);
        }

        line.Sort((a, b) =>
        {
            var la = ((a.X - cx) * px) + ((a.Y - cy) * py);
            var lb = ((b.X - cx) * px) + ((b.Y - cy) * py);
            return la != lb ? la.CompareTo(lb) : a.Id.CompareTo(b.Id);
        });
        // Slots snap to tiles, which can shift a man up to half a tile across the line: the spacing allows for that on
        // top of the AI fire clearance, so neighbours stay clear of each other's line of fire.
        var spacing = Clearance() + map.TileSize;
        spacing = spacing > rules.LineSpacing ? spacing : rules.LineSpacing;
        var half = spacing * Fixed.FromInt(line.Count - 1) / Fixed.FromInt(2);
        var laterals = new List<Fixed>();

        // The line forms one tile behind the squad's rearmost man (away from the threat): the men fall back into it, so
        // nobody has to walk forward through the others' lines of fire (measured: forming it ahead of the front man
        // leaves the rear men masked for most of their run and fails the 10 s firing criterion in 20 seeds in 100).
        var depth = Fixed.MaxValue;
        foreach (var s in able)
        {
            var d = ((s.X - cx) * ux) + ((s.Y - cy) * uy);
            depth = d < depth ? d : depth; // u points at the threat: the rearmost man is least far along it
        }

        depth -= map.TileSize;
        for (var i = 0; i < line.Count; i++)
        {
            var off = (spacing * Fixed.FromInt(i)) - half;
            var sx = cx + (px * off) + (ux * depth);
            var sy = cy + (py * off) + (uy * depth);
            if (LineTile(sx, sy, cx, cy, px, py, ux, uy, laterals) is { } goal)
            {
                var (gx, gy) = Centre(goal);
                laterals.Add(((gx - cx) * px) + ((gy - cy) * py));
                Send(fx, q, line[i], goal, true);
            }
        }

        m.TaskTeam = taskTeam;
        if (task == null)
        {
            return;
        }

        // The task: the flank whose search finds the better cover (the right-hand flank on a tie).
        TilePos? spot = null;
        var spotScore = -1;
        foreach (var side in new[] { 1, -1 })
        {
            var reach = half + task.FlankOffset;
            var bx = cx + (px * reach * Fixed.FromInt(side));
            var by = cy + (py * reach * Fixed.FromInt(side));
            if (BestTile(bx, by, task.SearchRadius, ux, uy, 0, cx, cy, px, py, laterals) is { } t && CoverScore(t, ux, uy, true) > spotScore)
            {
                (spot, spotScore) = (t, CoverScore(t, ux, uy, true));
            }
        }

        if (spot is not { } first)
        {
            return;
        }

        var team = able.Where(s => s.TeamIndex == taskTeam).OrderBy(s => s.Role == task.Role ? 0 : 1).ThenBy(s => s.Id).ToList();
        Send(fx, q, team[0], first, true);
        var (fx0, fy0) = Centre(first);
        for (var i = 1; i < team.Count; i++)
        {
            if (BestTile(fx0, fy0, map.TileSize * Fixed.FromInt(3), ux, uy, 0, cx, cy, px, py, laterals) is { } t)
            {
                Send(fx, q, team[i], t, true);
            }
        }
    }

    /// <summary>The AI's memory of one own squad. Every field is hashed.</summary>
    private sealed class Mind
    {
        public long LastUnderFire { get; set; } = long.MinValue / 2;

        public long UnderFireSince { get; set; } = long.MinValue / 2;

        /// <summary>The last enemy round (not a friend's) near one of its men, and when that spell began.</summary>
        public long LastEnemyFire { get; set; } = long.MinValue / 2;

        public long EnemyFireSince { get; set; } = long.MinValue / 2;

        public long NoteTick { get; set; } = -1;

        /// <summary>The firer of the noted round, if he is a known contact; else -1.</summary>
        public int SourceId { get; set; } = -1;

        /// <summary>The fuzzed point the noted round came from (bearing sector and distance band from the man it passed).</summary>
        public Fixed FireX { get; set; }

        public Fixed FireY { get; set; }

        public Fixed Band { get; set; }

        public int LineSector { get; set; } = -1;

        public bool CoverMoved { get; set; }

        /// <summary>Fire and movement: the bounding group (0 or 1).</summary>
        public int Group { get; set; }

        /// <summary>Ticks since the bounding group last gained a metre toward the end point (0: not counting).</summary>
        public int Stall { get; set; }

        public Fixed BestProgress { get; set; }


        /// <summary>Rushes: 0 none, 1 rushing, 2 down.</summary>
        public int Phase { get; set; }

        public int PhaseLeft { get; set; }

        public int TaskTeam { get; set; } = -1;

        /// <summary>The issue tick of the Defend or Ambush order whose firing positions were taken (item 2.4 follow-up), or -1.</summary>
        public long DefendTick { get; set; } = -1;

        /// <summary>The last shell burst near one of its men (item 2.4 follow-up): under shellfire men lie flat.</summary>
        public long LastShelled { get; set; } = long.MinValue / 2;

        /// <summary>When the squad's current AI fire task began (item 2.4 follow-up: per-man delays count from it).</summary>
        public long FireStart { get; set; } = -1;

        /// <summary>When its last AI fire task ended: a task that resumes within the under-fire memory keeps its start (and delays).</summary>
        public long FireEnd { get; set; } = long.MinValue / 2;

        /// <summary>When the squad's current flee began (per-man bolt delays count from it), or -1.</summary>
        public long BoltStart { get; set; } = -1;

        /// <summary>When the squad began to cower, or -1.</summary>
        public long CowerStart { get; set; } = -1;

        /// <summary>When the routing squad stopped (-1 while it is not a stopped, routing squad).</summary>
        public long StoppedSince { get; set; } = -1;

        /// <summary>Each man's distance (band) to the last shell burst he noticed.</summary>
        public SortedDictionary<int, Fixed> BurstDist { get; } = [];

        /// <summary>The tick each man of a fleeing squad gets up (drawn once, when the flee starts).</summary>
        public SortedDictionary<int, long> BoltAt { get; } = [];

        public bool Firing { get; set; }

        /// <summary>The contact the AI fire task aims at, or -1 for area fire.</summary>
        public int TargetId { get; set; } = -1;
    }
}
