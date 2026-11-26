using PointBlank.Sim.Map;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;

namespace PointBlank.Sim.Fire;

/// <summary>
/// Small-arms fire: fire control for squads with a <see cref="OrderKind.Fire"/> order, and the
/// resolution of every shot as a projectile (see <see cref="Shoot"/> and <see cref="FireRules"/>).
/// <list type="bullet">
/// <item>Ammo: each soldier carries his weapon's <c>carried_rounds</c> (magazine included) and loads
/// <c>magazine_capacity</c>; an empty magazine takes <c>reload_time</c>. A soldier with no rounds stops
/// firing; a squad none of whose able, armed men has a round reads <see cref="AmmoState.OutOfAmmo"/>
/// and its fire order ends with <see cref="OrderNote.OutOfAmmo"/>.</item>
/// <item>Cadence: a shot credit grows at the weapon's rate of fire (its start phase is one RNG draw
/// when the order starts); a weapon with <c>burst_length</c> &gt; 1 pauses <c>burst_pause</c> after each
/// burst. A soldier fires only within his weapon's max range, and holds fire while a friendly stands
/// within <see cref="FireRules.MaskClearance"/> of his line of fire short of the target; while holding, his shot credit
/// still grows to one round, so he fires as soon as the line clears (item 2.3).</item>
/// <item>Aim: a contact target (an enemy soldier his side knows) is aimed at live while the side tracks
/// him; a contact not Tracked is area fire at his last-known point; area fire aims at the point. The order
/// ends when the contact is forgotten or a Tracked target falls (<see cref="OrderNote.TargetGone"/>), or on a new order. Fire does not
/// stop a move under way (the firer then has the moving factor).</item>
/// <item>Suppression (optional <see cref="SuppressionSystem"/>): it scales dispersion and the shot credit,
/// and receives every near miss and hit.</item>
/// <item>Every shot calls <see cref="SpottingSystem.NoteFired"/>.</item>
/// <item>AI fire (item 2.3): <see cref="SetAiFire"/> gives a squad a fire task from the unit AI, with a team mask (only
/// those teams fire). It works like a fire order (same target rules, cadence and hold-fire) but sits below it: while
/// the squad has a player Fire order, the AI task is ignored. New orders do not stop it; the AI does
/// (<see cref="StopAiFire"/>). A forgotten or fallen target ends it. AI fire holds while a friendly is within the near-miss
/// radius of the line (not just the mask clearance), so the AI never suppresses its own men.</item>
/// </list>
/// Register after <see cref="SpottingSystem"/>, and pass it to <see cref="CommandSystem"/>.
/// </summary>
public sealed class FireSystem : ISimSystem
{
    private static readonly Fixed SixtySeconds = Fixed.FromInt(60);
    private static readonly Fixed MinRange = Fixed.FromRatio(1, 10);

    private readonly OrgSystem _org;
    private readonly SightRules _sight;
    private readonly SpottingSystem? _spotting;
    private readonly SuppressionSystem? _suppression;
    private readonly ShotTracer _tracer;
    private readonly List<State> _states = [];
    private readonly Dictionary<int, int> _indexOf = [];
    private readonly SortedDictionary<int, FireOrder> _orders = [];
    private readonly SortedDictionary<int, OrderNote> _ended = [];
    private readonly SortedDictionary<int, FireOrder> _ai = [];
    private readonly SortedSet<int> _aiSilent = []; // men the unit AI keeps from firing (item 2.4 follow-up: fire discipline)
    private readonly List<ShotEvent> _shots = [];
    private readonly List<NearMissEvent> _nearMisses = [];
    private readonly List<(Fixed S, Fixed Lateral, Soldier Soldier)> _candidates = [];

    public FireSystem(GameMap map, OrgSystem org, SightRules sight, FireRules rules, SpottingSystem? spotting = null, SuppressionSystem? suppression = null)
    {
        _suppression = suppression;
        _org = org;
        _sight = sight;
        _spotting = spotting;
        _tracer = new ShotTracer(map, sight);
        Rules = rules;
    }

    public FireRules Rules { get; }

    /// <summary>This tick's shots, in the order fired. Part of the state hash.</summary>
    internal IReadOnlyList<ShotEvent> Shots => _shots;

    /// <summary>This tick's near misses (item 2.1 reads them). Part of the state hash.</summary>
    internal IReadOnlyList<NearMissEvent> NearMisses => _nearMisses;

    /// <summary>Rounds a soldier has left (magazine included); -1 for an unknown soldier or weapon.</summary>
    internal int RoundsOf(int soldierId)
    {
        Sync();
        return _indexOf.TryGetValue(soldierId, out var i) && _states[i].Weapon != null ? _states[i].Rounds : -1;
    }

    /// <summary>The tick a soldier last fired, or a large negative number.</summary>
    internal long LastFiredTick(int soldierId)
    {
        Sync();
        return _indexOf.TryGetValue(soldierId, out var i) ? _states[i].LastFired : long.MinValue / 2;
    }

    internal AmmoState AmmoOf(Squad squad)
    {
        Sync();
        int armed = 0, empty = 0;
        foreach (var s in squad.Soldiers)
        {
            var st = _states[_indexOf[s.Id]];
            if (s.CanLead && st.Weapon != null)
            {
                armed++;
                empty += st.Rounds == 0 ? 1 : 0;
            }
        }

        return armed > 0 && empty == armed ? AmmoState.OutOfAmmo : empty > 0 ? AmmoState.Partial : AmmoState.Ok;
    }

    internal bool IsFiring(int squadId) => _orders.ContainsKey(squadId);

    /// <summary>Whether the squad has a unit AI fire task (item 2.3).</summary>
    internal bool IsAiFiring(int squadId) => _ai.ContainsKey(squadId);

    /// <summary>
    /// Sets or updates the squad's AI fire task: at a contact its side knows (<paramref name="targetId"/>) or at a
    /// point (-1), by the teams in <paramref name="teamMask"/> (bit per team index). A new task draws a random start
    /// phase for each man (one RNG draw each); an update keeps the cadence. An unknown contact clears the task.
    /// </summary>
    internal void SetAiFire(World world, Squad squad, int targetId, Fixed x, Fixed y, int teamMask)
    {
        Sync();
        if (targetId >= 0)
        {
            if (_spotting?.ContactOf(squad.Side, targetId) is not { } c)
            {
                _ai.Remove(squad.Id);
                return;
            }

            (x, y) = (c.X, c.Y);
        }

        if (!_ai.ContainsKey(squad.Id))
        {
            foreach (var s in squad.Soldiers)
            {
                StateOf(s).Credit = world.Rng.NextFixed();
            }
        }

        _ai[squad.Id] = new FireOrder(targetId, x, y) { Mask = teamMask };
    }

    /// <summary>Ends the squad's AI fire task, if any.</summary>
    internal void StopAiFire(int squadId) => _ai.Remove(squadId);

    /// <summary>Keeps one man from firing on his squad's AI fire task (or lets him fire again): his own decision (item 2.4 follow-up).</summary>
    internal void SetAiSilent(int soldierId, bool silent)
    {
        if (silent)
        {
            _aiSilent.Add(soldierId);
        }
        else
        {
            _aiSilent.Remove(soldierId);
        }
    }

    /// <summary>Why the squad's last fire order ended (<see cref="OrderNote.None"/> if it was replaced or none ended).</summary>
    internal OrderNote EndNoteOf(int squadId) => _ended.TryGetValue(squadId, out var n) ? n : OrderNote.None;

    /// <summary>
    /// Starts a delivered fire order (called by <see cref="CommandSystem"/>). Returns
    /// <see cref="OrderNote.None"/> when the squad starts firing, else why it cannot: the target is not
    /// a contact its side knows (<see cref="OrderNote.UnknownTarget"/>), no able man has a round
    /// (<see cref="OrderNote.OutOfAmmo"/>), or the aim point is past every armed man's range
    /// (<see cref="OrderNote.OutOfRange"/>).
    /// </summary>
    internal OrderNote Start(World world, Squad squad, Order order)
    {
        Sync();
        _orders.Remove(squad.Id);
        _ended.Remove(squad.Id);
        var fo = new FireOrder(order.TargetId, order.X, order.Y);
        if (fo.TargetId >= 0)
        {
            if (_spotting?.ContactOf(squad.Side, fo.TargetId) is not { } c)
            {
                return OrderNote.UnknownTarget;
            }

            (fo.X, fo.Y) = (c.X, c.Y);
        }

        var armed = squad.Soldiers.Where(s => s.CanLead && StateOf(s).Weapon != null && StateOf(s).Rounds > 0).ToList();
        if (armed.Count == 0)
        {
            return OrderNote.OutOfAmmo;
        }

        if (!armed.Any(s => InRange(s, StateOf(s).Weapon!, fo.X, fo.Y)))
        {
            return OrderNote.OutOfRange;
        }

        _orders[squad.Id] = fo;
        foreach (var s in squad.Soldiers)
        {
            StateOf(s).Credit = world.Rng.NextFixed(); // a random start phase, so a squad does not fire in volleys
        }

        return OrderNote.None;
    }

    /// <summary>Stops the squad's fire (a new order was delivered).</summary>
    internal void Stop(int squadId)
    {
        _orders.Remove(squadId);
        _ended.Remove(squadId);
    }

    public void Tick(World world)
    {
        Sync();
        _shots.Clear();
        _nearMisses.Clear();
        foreach (var st in _states)
        {
            var s = st.Soldier;
            st.Moved = s.X != st.PrevX || s.Y != st.PrevY;
            (st.PrevX, st.PrevY) = (s.X, s.Y);
            if (st.Pause > 0)
            {
                st.Pause--;
            }

            if (st.Reload > 0 && --st.Reload == 0)
            {
                st.Magazine = Math.Min(st.Weapon!.MagazineCapacity, st.Rounds);
            }
        }

        foreach (var squad in _org.Squads)
        {
            if (_orders.TryGetValue(squad.Id, out var fo))
            {
                FireSquad(world, squad, fo, false);
            }
            else if (_ai.TryGetValue(squad.Id, out var ai))
            {
                FireSquad(world, squad, ai, true);
            }
        }
    }

    /// <summary>
    /// Resolves one shot from <paramref name="firer"/> at a point (and at <paramref name="target"/>'s
    /// body, if given): rolls the angular error, traces the line through the map and past every
    /// soldier, applies the hit (wounding) and records the shot and its near misses. No ammo is used
    /// here; fire control does that. RNG: 4 draws for the error, one per hedge reached, one per hit.
    /// </summary>
    internal ShotEvent Shoot(World world, Soldier firer, Fixed aimX, Fixed aimY, Soldier? target)
    {
        Sync();
        var st = StateOf(firer);
        var weapon = st.Weapon ?? throw new InvalidOperationException($"Soldier {firer.Id} has no known weapon.");
        var rng = world.Rng;
        var originTile = _tracer.TileOf(firer.X, firer.Y);
        var z0 = (originTile < 0 ? Fixed.Zero : _tracer.Ground(originTile)) + _sight.EyeHeight(firer.Posture);

        var aimTile = _tracer.TileOf(aimX, aimY);
        var aimGround = aimTile < 0 ? Fixed.Zero : _tracer.Ground(aimTile);
        Fixed aimZ;
        if (target != null)
        {
            var h = Rules.BodyHeight(target.Posture);
            var cover = aimTile < 0 ? Fixed.Zero : Rules.Cover(_tracer.Terrain(aimTile));
            aimZ = aimGround + (h * (Fixed.One + cover) / Fixed.FromInt(2)); // centre of the exposed body
        }
        else
        {
            aimZ = aimGround + Rules.AreaAimHeight;
        }

        var dx = aimX - firer.X;
        var dy = aimY - firer.Y;
        var d = Fixed.Sqrt((dx * dx) + (dy * dy));
        Fixed ux, uy;
        if (d < MinRange)
        {
            (ux, uy, d) = (Fixed.One, Fixed.Zero, MinRange);
        }
        else
        {
            (ux, uy) = (dx / d, dy / d);
        }

        var sigma = Rules.Sigma(weapon, firer.Posture, st.Moved, d, SuppressionFactor(firer), MoraleFactor(firer));
        var yaw = sigma * Normal(rng);
        var pitch = sigma * Normal(rng);
        (ux, uy) = (ux - (uy * yaw), uy + (ux * yaw)); // small-angle rotation; |u| = 1 + yaw²/2, negligible
        var slope = ((aimZ - z0) / d) + pitch;

        var walk = _tracer.Start(firer.X, firer.Y, z0, ux, uy, slope, weapon.MaxRange, Rules.HedgeBlockChance, rng);
        var half = Rules.BodyWidth / Fixed.FromInt(2);
        var reach = Rules.NearMissRadius > half ? Rules.NearMissRadius : half;
        _candidates.Clear();
        foreach (var squad in _org.Squads)
        {
            foreach (var s in squad.Soldiers)
            {
                if (s == firer || !s.CanLead)
                {
                    continue;
                }

                var rx = s.X - firer.X;
                var ry = s.Y - firer.Y;
                var along = (rx * ux) + (ry * uy);
                if (along <= Fixed.Zero || along > weapon.MaxRange)
                {
                    continue;
                }

                var lateral = (rx * uy) - (ry * ux);
                lateral = lateral < Fixed.Zero ? -lateral : lateral;
                if (lateral <= reach)
                {
                    _candidates.Add((along, lateral, s));
                }
            }
        }

        _candidates.Sort((a, b) => a.S != b.S ? a.S.CompareTo(b.S) : a.Soldier.Id.CompareTo(b.Soldier.Id));
        var outcome = ShotOutcome.Spent;
        var stopS = weapon.MaxRange;
        var hitId = -1;
        var hitResult = HealthState.Ok;
        var stopped = false;
        foreach (var (along, lateral, s) in _candidates)
        {
            if (!walk.Advance(along))
            {
                break;
            }

            var tile = _tracer.TileOf(s.X, s.Y);
            var g = tile < 0 ? Fixed.Zero : _tracer.Ground(tile);
            var h = Rules.BodyHeight(s.Posture);
            var coverTop = g + (h * (tile < 0 ? Fixed.Zero : Rules.Cover(_tracer.Terrain(tile))));
            var z = walk.HeightAt(along);
            if (lateral <= half && z >= g && z <= g + h && Rules.HitsAsNearMisses && z >= coverTop)
            {
                NearMiss(world, s, firer, weapon, lateral);
                continue;
            }

            if (lateral <= half && z >= g && z <= g + h)
            {
                stopS = along;
                stopped = true;
                hitId = s.Id;
                if (z < coverTop)
                {
                    outcome = ShotOutcome.Cover;
                    NearMiss(world, s, firer, weapon, lateral);
                }
                else
                {
                    outcome = ShotOutcome.Hit;
                    hitResult = Wound(rng, s);
                    _suppression?.OnHit(world, s);
                }

                break;
            }

            if (lateral <= Rules.NearMissRadius)
            {
                NearMiss(world, s, firer, weapon, lateral);
            }
        }

        if (!stopped)
        {
            walk.Advance(weapon.MaxRange + Fixed.One);
            outcome = walk.Stopped ?? ShotOutcome.Spent;
            stopS = walk.Stopped != null ? walk.StopS : weapon.MaxRange;
        }

        var shot = new ShotEvent(
            world.Tick, firer.Id, target?.Id ?? -1, firer.X, firer.Y, firer.X + (ux * stopS), firer.Y + (uy * stopS),
            outcome, hitId, hitResult, walk.HedgeRolls);
        _shots.Add(shot);
        return shot;
    }

    public void AppendState(StateHasher hasher)
    {
        hasher.Add(_states.Count);
        foreach (var st in _states)
        {
            hasher.Add(st.Soldier.Id);
            hasher.Add(st.Rounds);
            hasher.Add(st.Magazine);
            hasher.Add(st.Credit);
            hasher.Add(st.BurstFired);
            hasher.Add(st.Pause);
            hasher.Add(st.Reload);
            hasher.Add(st.PrevX);
            hasher.Add(st.PrevY);
            hasher.Add(st.Moved);
            hasher.Add(st.LastFired);
        }

        hasher.Add(_orders.Count);
        foreach (var (id, fo) in _orders)
        {
            hasher.Add(id);
            hasher.Add(fo.TargetId);
            hasher.Add(fo.X);
            hasher.Add(fo.Y);
        }

        hasher.Add(_ai.Count);
        foreach (var (id, fo) in _ai)
        {
            hasher.Add(id);
            hasher.Add(fo.TargetId);
            hasher.Add(fo.X);
            hasher.Add(fo.Y);
            hasher.Add(fo.Mask);
        }

        hasher.Add(_aiSilent.Count);
        foreach (var id in _aiSilent)
        {
            hasher.Add(id);
        }

        hasher.Add(_ended.Count);
        foreach (var (id, note) in _ended)
        {
            hasher.Add(id);
            hasher.Add((byte)note);
        }

        hasher.Add(_shots.Count);
        foreach (var s in _shots)
        {
            hasher.Add(s.Tick);
            hasher.Add(s.FirerId);
            hasher.Add(s.TargetId);
            hasher.Add(s.OriginX);
            hasher.Add(s.OriginY);
            hasher.Add(s.EndX);
            hasher.Add(s.EndY);
            hasher.Add((byte)s.Outcome);
            hasher.Add(s.HitSoldierId);
            hasher.Add((byte)s.HitResult);
            hasher.Add(s.HedgeRolls);
        }

        hasher.Add(_nearMisses.Count);
        foreach (var n in _nearMisses)
        {
            hasher.Add(n.Tick);
            hasher.Add(n.SoldierId);
            hasher.Add(n.FirerId);
            hasher.Add(n.OriginX);
            hasher.Add(n.OriginY);
            hasher.Add((byte)n.Calibre);
            hasher.Add(n.Distance);
        }
    }

    /// <summary>An approximate standard normal: the Irwin–Hall sum of 3 uniforms, centred and scaled (3 draws, bounded at ±3).</summary>
    private static Fixed Normal(SimRng rng) =>
        (rng.NextFixed() + rng.NextFixed() + rng.NextFixed() - Fixed.FromRatio(3, 2)) * Fixed.FromInt(2);

    /// <summary>The firer's suppression as a dispersion factor (1 without a <see cref="SuppressionSystem"/>).</summary>
    private Fixed SuppressionFactor(Soldier firer) => _suppression?.DispersionFactor(firer.Id) ?? Fixed.One;

    /// <summary>The firer's morale as a dispersion factor. 1 for now; item 2.2 sets it.</summary>
    private static Fixed MoraleFactor(Soldier firer) => Fixed.One;

    private static bool InRange(Soldier s, WeaponStats w, Fixed x, Fixed y)
    {
        var dx = x - s.X;
        var dy = y - s.Y;
        return Fixed.Sqrt((dx * dx) + (dy * dy)) <= w.MaxRange;
    }

    private HealthState Wound(SimRng rng, Soldier s)
    {
        var r = rng.NextFixed();
        var health = r < Rules.KillChance ? HealthState.Dead
            : r < Rules.KillChance + Rules.IncapacitateChance ? HealthState.Incapacitated
            : s.Health == HealthState.Wounded ? HealthState.Incapacitated
            : HealthState.Wounded;
        _org.FindSquadOf(s.Id)!.SetHealth(s.Id, health);
        return health;
    }

    private void NearMiss(World world, Soldier s, Soldier firer, WeaponStats weapon, Fixed distance)
    {
        var e = new NearMissEvent(world.Tick, s.Id, firer.Id, firer.X, firer.Y, weapon.Calibre, distance);
        _nearMisses.Add(e);
        _suppression?.OnNearMiss(world, e, Rules.NearMissRadius);
    }

    private void FireSquad(World world, Squad squad, FireOrder fo, bool ai)
    {
        void End(int squadId, OrderNote note)
        {
            if (ai)
            {
                _ai.Remove(squadId);
            }
            else
            {
                EndOrder(squadId, note);
            }
        }

        Soldier? target = null;
        if (fo.TargetId >= 0)
        {
            var contact = _spotting?.ContactOf(squad.Side, fo.TargetId);
            target = _org.FindSoldier(fo.TargetId);
            if (contact is not { } c)
            {
                End(squad.Id, OrderNote.TargetGone); // forgotten
                return;
            }

            if (c.Quality != ContactQuality.Tracked)
            {
                // Out of sight: area fire at the last-known point. Whether the unseen man still stands is
                // not the side's knowledge, so it neither ends the order nor steers the shots.
                (fo.X, fo.Y) = (c.X, c.Y);
                target = null;
            }
            else if (target is not { CanLead: true })
            {
                End(squad.Id, OrderNote.TargetGone); // seen to fall
                return;
            }
            else
            {
                (fo.X, fo.Y) = (target.X, target.Y);
            }
        }

        var armed = 0;
        var perTick = World.TickDuration / SixtySeconds;
        foreach (var s in squad.Soldiers)
        {
            var st = StateOf(s);
            var w = st.Weapon;
            if (!s.CanLead || w == null || st.Rounds == 0)
            {
                continue;
            }

            armed++;
            if ((fo.Mask & (1 << s.TeamIndex)) == 0 || (ai && _aiSilent.Contains(s.Id)))
            {
                continue;
            }

            if (st.Reload > 0 || st.Pause > 0 || !InRange(s, w, fo.X, fo.Y))
            {
                continue;
            }

            var gain = w.RoundsPerMinute * perTick * (_suppression?.RateFactor(s.Id) ?? Fixed.One);
            if (Masked(s, squad.Side, fo.X, fo.Y, ai ? AiClearance : Rules.MaskClearance))
            {
                // Holding fire for a friend in the way, he stays aimed and ready: the cadence runs on to one round, which he
                // fires the moment the line clears (item 2.3).
                st.Credit = st.Credit + gain > Fixed.One ? (st.Credit > Fixed.One ? st.Credit : Fixed.One) : st.Credit + gain;
                continue;
            }

            st.Credit += gain;
            while (st.Credit >= Fixed.One && st.Magazine > 0)
            {
                Shoot(world, s, fo.X, fo.Y, target);
                st.Credit -= Fixed.One;
                st.Magazine--;
                st.Rounds--;
                st.LastFired = world.Tick;
                _spotting?.NoteFired(s.Id, world.Tick);
                if (st.Magazine == 0)
                {
                    (st.Credit, st.BurstFired) = (Fixed.Zero, 0);
                    st.Reload = st.Rounds > 0 ? Math.Max(1, w.ReloadTicks) : 0;
                    break;
                }

                if (w.BurstLength > 1 && ++st.BurstFired >= w.BurstLength)
                {
                    (st.Credit, st.BurstFired, st.Pause) = (Fixed.Zero, 0, w.BurstPauseTicks);
                    break;
                }
            }
        }

        if (armed == 0)
        {
            End(squad.Id, squad.Soldiers.Any(s => s.CanLead && StateOf(s).Weapon != null) ? OrderNote.OutOfAmmo : OrderNote.NoAbleMen);
        }
    }

    /// <summary>Whether a friendly stands within the mask clearance of the line from the soldier to the aim point, short of it.</summary>
    /// <summary>
    /// The hold-fire clearance of AI fire (item 2.3): the near-miss radius (or the mask clearance if larger), so the unit AI
    /// never fires a round that would pass close enough to suppress one of its own men. Player orders keep the mask clearance.
    /// </summary>
    private Fixed AiClearance => Rules.NearMissRadius > Rules.MaskClearance ? Rules.NearMissRadius : Rules.MaskClearance;

    private bool Masked(Soldier firer, string side, Fixed x, Fixed y, Fixed clearance)
    {
        var dx = x - firer.X;
        var dy = y - firer.Y;
        var d2 = (dx * dx) + (dy * dy);
        if (d2 <= Fixed.Zero)
        {
            return false;
        }

        var d = Fixed.Sqrt(d2);

        foreach (var squad in _org.Squads)
        {
            if (squad.Side != side)
            {
                continue;
            }

            foreach (var f in squad.Soldiers)
            {
                if (f == firer || !f.CanLead)
                {
                    continue;
                }

                var rx = f.X - firer.X;
                var ry = f.Y - firer.Y;
                var along = (rx * dx) + (ry * dy); // along × d
                if (along <= Fixed.Zero || along >= d2)
                {
                    continue;
                }

                var lateral = ((rx * dy) - (ry * dx)) / d;
                if (lateral <= clearance && -lateral <= clearance)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void EndOrder(int squadId, OrderNote note)
    {
        _orders.Remove(squadId);
        _ended[squadId] = note;
    }

    private State StateOf(Soldier s) => _states[_indexOf[s.Id]];

    /// <summary>Adds a state, with full ammunition, for each soldier spawned since the last call (spawn order).</summary>
    private void Sync()
    {
        var total = 0;
        foreach (var squad in _org.Squads)
        {
            total += squad.Soldiers.Count;
        }

        if (total == _states.Count)
        {
            return; // soldiers are never removed, so every one already has a state
        }

        foreach (var squad in _org.Squads)
        {
            foreach (var s in squad.Soldiers)
            {
                if (!_indexOf.ContainsKey(s.Id))
                {
                    var w = Rules.TryWeapon(s.WeaponId);
                    var rounds = w?.CarriedRounds ?? 0;
                    _indexOf[s.Id] = _states.Count;
                    _states.Add(new State(s, w)
                    {
                        Rounds = rounds,
                        Magazine = Math.Min(w?.MagazineCapacity ?? 0, rounds),
                        PrevX = s.X,
                        PrevY = s.Y,
                    });
                }
            }
        }
    }

    private sealed class FireOrder(int targetId, Fixed x, Fixed y)
    {
        public int TargetId { get; } = targetId;

        public Fixed X { get; set; } = x;

        public Fixed Y { get; set; } = y;

        /// <summary>The teams that fire (bit per team index); every team for a player order.</summary>
        public int Mask { get; init; } = -1;
    }

    private sealed class State(Soldier soldier, WeaponStats? weapon)
    {
        public Soldier Soldier { get; } = soldier;

        public WeaponStats? Weapon { get; } = weapon;

        public int Rounds { get; set; }

        public int Magazine { get; set; }

        public Fixed Credit { get; set; }

        public int BurstFired { get; set; }

        public int Pause { get; set; }

        public int Reload { get; set; }

        public Fixed PrevX { get; set; }

        public Fixed PrevY { get; set; }

        public bool Moved { get; set; }

        public long LastFired { get; set; } = long.MinValue / 2;
    }
}
