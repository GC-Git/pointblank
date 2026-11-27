using PointBlank.Sim.Orders;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.UnitAi;

namespace PointBlank.Sim.Spotting;

/// <summary>How good a side's knowledge of one enemy is. Values are hashed, so never renumber.</summary>
public enum ContactQuality : byte
{
    /// <summary>In sight: seen within the last refresh cycle. The position follows him (one refresh behind at most).</summary>
    Tracked = 0,

    /// <summary>Out of sight: the position is frozen where he was last seen.</summary>
    Stale = 1,

    /// <summary>Unseen for <see cref="SpottingRules.LostAfterTicks"/>; forgotten after <see cref="SpottingRules.ForgetAfterTicks"/>.</summary>
    Lost = 2,
}

/// <summary>What was seen, coarsely. Values are hashed, so never renumber.</summary>
public enum ContactKind : byte
{
    Infantry = 0,

    /// <summary>A machine-gun crewman (gunner, assistant or loader).</summary>
    MachineGun = 1,
}

/// <summary>
/// What a side knows about one enemy soldier: last-known data only. Value fields only, by design:
/// it holds no reference to the live soldier, so nothing reached through it can reveal where he is now.
/// </summary>
public readonly record struct ContactView(
    int EnemyId,
    ContactKind Kind,
    ContactQuality Quality,
    Fixed X,
    Fixed Y,
    long LastSeenTick,
    long FirstSeenTick,
    int SeenBy);

/// <summary>One side's knowledge of enemy contacts, keyed by enemy soldier id.</summary>
internal sealed class SideKnowledge
{
    internal SideKnowledge(string side) => Side = side;

    public string Side { get; }

    public int Count => Contacts.Count;

    internal SortedDictionary<int, Contact> Contacts { get; } = [];

    /// <summary>A copy of every contact, by enemy id.</summary>
    public ContactView[] Snapshot()
    {
        var views = new ContactView[Contacts.Count];
        var i = 0;
        foreach (var c in Contacts.Values)
        {
            views[i++] = new ContactView(c.EnemyId, c.Kind, c.Quality, c.X, c.Y, c.LastSeen, c.FirstSeen, c.SeenBy);
        }

        return views;
    }

    internal sealed class Contact
    {
        public int EnemyId;
        public ContactKind Kind;
        public ContactQuality Quality;
        public Fixed X;
        public Fixed Y;
        public long LastSeen;
        public long FirstSeen;
        public int SeenBy;
    }
}

/// <summary>
/// What a man is doing, as the debug overlay shows it (item 2.6): his movement, what he is doing about fire and the unit
/// AI's drill are separate (<see cref="TruthSoldier"/>), each <see cref="None"/> when there is none.
/// </summary>
public enum IntentKind : byte
{
    None,

    /// <summary>Walking on his squad's move.</summary>
    Move,

    /// <summary>Walking on a move of his own (the unit AI's).</summary>
    LocalMove,

    /// <summary>On his squad's move but held where he is.</summary>
    Held,

    /// <summary>He has fired within the last few seconds.</summary>
    Firing,

    /// <summary>On a fire task with a friendly in the way, so holding fire.</summary>
    Blocked,

    Reloading,

    OutOfRange,

    NoAmmo,

    /// <summary>On a fire task, nothing stopping him, no recent shot.</summary>
    Aiming,

    // The unit AI's drills.
    Bounding,

    Covering,

    Rushing,

    DownBetweenRushes,

    FiringLine,

    Flanking,

    Defending,

    Ambushing,

    Fleeing,

    Cowering,
}

/// <summary>
/// A copy of a soldier as the debug overlay sees him (<see cref="SpottingDebug"/>). The fields after
/// <see cref="Health"/> are filled only by <see cref="PlayerBattle.DebugTruth"/>. <see cref="MoveX"/>, <see cref="MoveY"/>
/// are where he is walking to (when <see cref="Move"/> is not none); <see cref="FireX"/>, <see cref="FireY"/> what his fire
/// task aims at (when <see cref="Aims"/>).
/// </summary>
public readonly record struct TruthSoldier(
    int Id, string Side, Fixed X, Fixed Y, Posture Posture, HealthState Health, int SquadId = -1, Fixed Suppression = default,
    IntentKind Move = IntentKind.None, Fixed MoveX = default, Fixed MoveY = default, IntentKind Fire = IntentKind.None,
    bool Aims = false, Fixed FireX = default, Fixed FireY = default, IntentKind Task = IntentKind.None,
    Posture? PendingPosture = null);

/// <summary>
/// A copy of a squad for the debug overlay: morale, mean suppression and its latest order (<see cref="Order"/> null when it
/// has none; <see cref="OrderX"/>, <see cref="OrderY"/> its point).
/// </summary>
public readonly record struct TruthSquad(
    int Id, string Side, MoraleState Morale, Fixed MoraleLevel, Fixed Suppression, OrderKind? Order, OrderStatus OrderStatus,
    Fixed OrderX, Fixed OrderY);

/// <summary>
/// DEBUG ONLY: the ground truth behind the fog, for overlays (item 2.6) and tests. Gameplay code (UI
/// and AI) must never read it; they read <see cref="SpottingSystem.KnownEnemies"/>.
/// </summary>
public sealed class SpottingDebug
{
    private readonly SpottingSystem _system;

    internal SpottingDebug(SpottingSystem system, IReadOnlyList<TruthSoldier> soldiers, IReadOnlyList<TruthSquad> squads)
    {
        _system = system;
        Soldiers = soldiers;
        Squads = squads;
    }

    /// <summary>Every squad, both sides (empty unless asked through <see cref="PlayerBattle.DebugTruth"/>).</summary>
    public IReadOnlyList<TruthSquad> Squads { get; }

    /// <summary>Every soldier, both sides, with live positions.</summary>
    public IReadOnlyList<TruthSoldier> Soldiers { get; }

    /// <summary>Spotting progress (0 to 1) of an observer on a target.</summary>
    public Fixed Progress(int observerId, int targetId) => _system.ProgressOf(observerId, targetId);
}

/// <summary>
/// Spotting and fog of war. Each side has its own <see cref="SideKnowledge"/>.
/// <list type="bullet">
/// <item>Scheduling: soldiers are listed in spawn order (index i). The enemy pair (i, j), i &lt; j, is
/// refreshed on the ticks t where (i + j) mod <see cref="SpottingRules.RefreshTicks"/> equals t mod that,
/// so every pair is refreshed once per cycle and the load is spread evenly (the LineOfSight stagger).
/// One line-of-sight check serves both directions, as the check is symmetric bit for bit. Pairs past
/// the max range are culled by squared distance before any check.</item>
/// <item>Spotting is deterministic accumulation, no RNG: a pair with visible LOS adds
/// <see cref="SpottingRules.Rate"/> × the cycle's seconds to its progress; at 1 the target is spotted
/// and his contact set (Tracked, position, tick, observer). Losing LOS or a zero rate clears the pair.</item>
/// <item>Tracking: a Tracked contact is also refreshed by any friendly with visible LOS and a positive
/// rate, so the side keeps it while anyone can see it. Otherwise the position freezes: Stale after one
/// cycle unseen, Lost after <see cref="SpottingRules.LostAfterTicks"/>, removed after
/// <see cref="SpottingRules.ForgetAfterTicks"/>.</item>
/// <item>Motion: a soldier whose position changed within the last cycle is moving, at his squad's
/// move mode (Move if none). Firing: <see cref="NoteFired"/> (item 1.7). Observer state:
/// <c>ObserverFactor</c>, from the observer's suppression (item 2.1).</item>
/// <item>Dead and incapacitated soldiers, and soldiers off the map, neither observe nor are seen.</item>
/// </list>
/// Register after <see cref="MovementSystem"/>.
/// </summary>
public sealed class SpottingSystem : ISimSystem
{
    private static readonly SideKnowledge[] NoSides = [];

    private readonly GameMap _map;
    private readonly OrgSystem _org;
    private readonly MovementSystem _move;
    private readonly LineOfSight _los;
    private readonly SuppressionSystem? _suppression;
    private readonly long _tileRaw;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<int, int> _indexOf = [];
    private readonly Dictionary<long, Fixed> _progress = [];
    private readonly SortedDictionary<string, SideKnowledge> _sides = new(StringComparer.Ordinal);

    public SpottingSystem(GameMap map, OrgSystem org, MovementSystem move, LineOfSight los, SpottingRules rules, SuppressionSystem? suppression = null)
    {
        _suppression = suppression;
        _map = map;
        _org = org;
        _move = move;
        _los = los;
        _tileRaw = map.TileSize.Raw;
        Rules = rules;
    }

    public SpottingRules Rules { get; }

    /// <summary>Diagnostics only (not state): line-of-sight checks run in the last tick.</summary>
    public int LosChecksLastTick { get; private set; }

    /// <summary>
    /// The only way the UI and the AI read enemies: what <paramref name="side"/> knows, last-known
    /// data only, as a copy ordered by enemy id. Empty for a side with no knowledge.
    /// </summary>
    internal IReadOnlyList<ContactView> KnownEnemies(string side) =>
        _sides.TryGetValue(side, out var k) ? k.Snapshot() : [];

    /// <summary>Every side's knowledge, ordered by side name.</summary>
    internal IReadOnlyCollection<SideKnowledge> Sides => _sides.Count == 0 ? NoSides : _sides.Values;

    /// <summary>What <paramref name="side"/> knows of one enemy soldier, or null (fire control, item 1.7).</summary>
    internal ContactView? ContactOf(string side, int enemyId) =>
        _sides.TryGetValue(side, out var k) && k.Contacts.TryGetValue(enemyId, out var c)
            ? new ContactView(c.EnemyId, c.Kind, c.Quality, c.X, c.Y, c.LastSeen, c.FirstSeen, c.SeenBy)
            : null;

    /// <summary>When the soldier last moved (for tests and traces).</summary>
    internal long LastMovedTick(int soldierId)
    {
        Sync();
        return _indexOf.TryGetValue(soldierId, out var i) ? _entries[i].LastMoved : long.MinValue / 2;
    }

    /// <summary>The tick a soldier was last noted firing, or a large negative number.</summary>
    internal long LastFiredTick(int soldierId)
    {
        Sync();
        return _indexOf.TryGetValue(soldierId, out var i) ? _entries[i].LastFired : long.MinValue / 2;
    }

    /// <summary>Records that a soldier fired (item 1.7 calls this); it is easier to spot for the firing window.</summary>
    internal void NoteFired(int soldierId, long tick)
    {
        Sync();
        if (_indexOf.TryGetValue(soldierId, out var i))
        {
            _entries[i].LastFired = tick;
        }
    }

    /// <summary>DEBUG ONLY: live positions for overlays; see <see cref="SpottingDebug"/>.</summary>
    public SpottingDebug DebugTruth() => new(
        this,
        Array.AsReadOnly(_org.Squads.SelectMany(q => q.Soldiers.Select(s => new TruthSoldier(s.Id, q.Side, s.X, s.Y, s.Posture, s.Health))).ToArray()),
        []);

    /// <summary>
    /// <see cref="DebugTruth()"/> with each man's suppression and intent, and every squad (item 2.6). It only reads: the
    /// battle is the same whether or not it is asked.
    /// </summary>
    internal SpottingDebug DebugTruth(World world)
    {
        var suppression = world.FindSystem<SuppressionSystem>();
        var command = world.FindSystem<CommandSystem>();
        var fire = world.FindSystem<FireSystem>();
        var ai = world.FindSystem<UnitAiSystem>();
        var soldiers = new List<TruthSoldier>();
        var squads = new List<TruthSquad>();
        foreach (var q in _org.Squads)
        {
            var order = command?.StatusOf(q.Id);
            squads.Add(new TruthSquad(
                q.Id, q.Side, q.Morale, q.MoraleLevel, suppression?.SquadLevelOf(q.Id) ?? Fixed.Zero, order?.Order?.Kind,
                order?.Status ?? OrderStatus.None, order?.Order?.X ?? Fixed.Zero, order?.Order?.Y ?? Fixed.Zero));
            var done = order is { Status: OrderStatus.Done } ? order.Order?.Kind : null;
            foreach (var s in q.Soldiers)
            {
                var man = new TruthSoldier(
                    s.Id, q.Side, s.X, s.Y, s.Posture, s.Health, q.Id, suppression?.LevelOf(s.Id) ?? Fixed.Zero,
                    PendingPosture: s.PendingPosture);
                if (s.CanLead)
                {
                    if (_move.GoalOf(s.Id) is { } goal)
                    {
                        var move = goal.Local ? IntentKind.LocalMove : goal.Held ? IntentKind.Held : IntentKind.Move;
                        man = man with { Move = move, MoveX = goal.X, MoveY = goal.Y };
                    }

                    if (fire?.ActivityOf(q, s, world.Tick) is { } shooting)
                    {
                        man = man with
                        {
                            Fire = shooting.Activity, Aims = shooting.Target != null,
                            FireX = shooting.Target?.X ?? Fixed.Zero, FireY = shooting.Target?.Y ?? Fixed.Zero,
                        };
                    }

                    man = man with { Task = ai?.TaskOf(q, s, done) ?? IntentKind.None };
                }

                soldiers.Add(man);
            }
        }

        return new SpottingDebug(this, soldiers.AsReadOnly(), squads.AsReadOnly());
    }

    /// <summary>The line of sight spotting uses, for a side's view of the ground (item 2.6).</summary>
    internal bool Sees(Soldier observer, Fixed x, Fixed y, Posture target)
    {
        var dx = x - observer.X;
        var dy = y - observer.Y;
        return observer.CanLead && _move.IsOnMap(observer.X, observer.Y) && _move.IsOnMap(x, y)
            && (dx * dx) + (dy * dy) <= Rules.MaxRange * Rules.MaxRange
            && _los.Check(new Viewpoint(observer.X, observer.Y, observer.Posture), new Viewpoint(x, y, target)).Visible;
    }

    public void Tick(World world)
    {
        Sync();
        var tick = world.Tick;
        foreach (var e in _entries)
        {
            var s = e.Soldier;
            if (s.X != e.PrevX || s.Y != e.PrevY)
            {
                e.LastMoved = tick;
                e.PrevX = s.X;
                e.PrevY = s.Y;
            }
        }

        var n = Rules.RefreshTicks;
        var elapsed = World.TickDuration * Fixed.FromInt(n);
        var max2 = Rules.MaxRange * Rules.MaxRange;
        var slot = (int)(tick % n);
        var checks = 0;
        var count = _entries.Count;
        for (var i = 0; i < count; i++)
        {
            var a = _entries[i];
            var aOk = a.Soldier.CanLead && _move.IsOnMap(a.Soldier.X, a.Soldier.Y);

            // The unordered pair (i, j), i < j, is refreshed on the ticks where (i + j) mod n == slot.
            var j = i + 1;
            j += (int)((((long)slot - i - j) % n) + n) % n;
            for (; j < count; j += n)
            {
                var b = _entries[j];
                if (a.Side == b.Side)
                {
                    continue;
                }

                var dx = b.Soldier.X - a.Soldier.X;
                var dy = b.Soldier.Y - a.Soldier.Y;
                var d2 = (dx * dx) + (dy * dy);
                if (!aOk || !b.Soldier.CanLead || d2 > max2 || !_move.IsOnMap(b.Soldier.X, b.Soldier.Y))
                {
                    _progress.Remove(Key(a.Soldier.Id, b.Soldier.Id));
                    _progress.Remove(Key(b.Soldier.Id, a.Soldier.Id));
                    continue;
                }

                // One check serves both directions: LineOfSight is symmetric bit for bit.
                checks++;
                var los = _los.Check(
                    new Viewpoint(a.Soldier.X, a.Soldier.Y, a.Soldier.Posture), new Viewpoint(b.Soldier.X, b.Soldier.Y, b.Soldier.Posture));
                var distance = los.Visible ? Fixed.Sqrt(d2) : Fixed.Zero;
                Observe(a, b, los, distance, elapsed, tick);
                Observe(b, a, los, distance, elapsed, tick);
            }
        }

        LosChecksLastTick = checks;
        Age(tick);
    }

    /// <summary>One direction of a refreshed pair: the observer's progress on the target, and the side's contact.</summary>
    private void Observe(Entry o, Entry t, LosResult los, Fixed distance, Fixed elapsed, long tick)
    {
        var key = Key(o.Soldier.Id, t.Soldier.Id);
        var rate = los.Visible
            ? Rules.Rate(distance, Concealment(t.Soldier), los.Obscurement, t.Soldier.Posture, Motion(t, tick), Firing(t, tick), ObserverFactor(o.Soldier))
            : Fixed.Zero;
        // Only a man whose eyes are below the hedge top (prone or kneeling) is hidden by it; a standing man shows over it.
        if (rate > Fixed.Zero && !Firing(t, tick) && _los.Rules.EyeHeight(t.Soldier.Posture) < _los.Rules.HedgeHeight && AtHedge(t.Soldier, o.Soldier))
        {
            // At a hedge and silent: picked out only close in, closer still when he keeps still.
            var limit = Motion(t, tick) == TargetMotion.Still ? Rules.HedgeStillRange : Rules.HedgeMovingRange;
            rate = distance > limit ? Fixed.Zero : rate;
        }

        // Close range (item 2.4 follow-up): a man in sight this close is noticed whatever his posture, stillness or cover,
        // at no less than close_rate × (1 − d / close_range) × (1 − obscurement) × the observer's suppression factor (a
        // pinned man still looks around, only less).
        if (los.Visible && distance < Rules.CloseRange)
        {
            var close = Rules.CloseRate * (Fixed.One - (distance / Rules.CloseRange)) * (Fixed.One - Min1(los.Obscurement)) * ObserverFactor(o.Soldier);
            rate = close > rate ? close : rate;
        }

        if (rate <= Fixed.Zero)
        {
            _progress.Remove(key);
            return;
        }

        _progress.TryGetValue(key, out var p);
        p += rate * elapsed;
        var spotted = p >= Fixed.One;
        _progress[key] = spotted ? Fixed.One : p;
        var knowledge = _sides[o.Side];
        knowledge.Contacts.TryGetValue(t.Soldier.Id, out var contact);
        if (spotted || contact is { Quality: ContactQuality.Tracked })
        {
            See(knowledge, contact, t.Soldier, o.Soldier.Id, tick);
        }
    }

    public void AppendState(StateHasher hasher)
    {
        hasher.Add(_entries.Count);
        foreach (var e in _entries)
        {
            hasher.Add(e.Soldier.Id);
            hasher.Add(e.PrevX);
            hasher.Add(e.PrevY);
            hasher.Add(e.LastMoved);
            hasher.Add(e.LastFired);
        }

        var keys = _progress.Keys.ToArray();
        Array.Sort(keys);
        hasher.Add(keys.Length);
        foreach (var k in keys)
        {
            hasher.Add(k);
            hasher.Add(_progress[k]);
        }

        hasher.Add(_sides.Count);
        foreach (var k in _sides.Values)
        {
            hasher.Add(k.Side);
            hasher.Add(k.Contacts.Count);
            foreach (var c in k.Contacts.Values)
            {
                hasher.Add(c.EnemyId);
                hasher.Add((byte)c.Kind);
                hasher.Add((byte)c.Quality);
                hasher.Add(c.X);
                hasher.Add(c.Y);
                hasher.Add(c.LastSeen);
                hasher.Add(c.FirstSeen);
                hasher.Add(c.SeenBy);
            }
        }
    }

    internal Fixed ProgressOf(int observerId, int targetId) =>
        _progress.TryGetValue(Key(observerId, targetId), out var p) ? p : Fixed.Zero;

    private static long Key(int observer, int target) => ((long)observer << 32) | (uint)target;

    /// <summary>The observer's own state as a rate factor: lowered by his suppression (1 without a <see cref="SuppressionSystem"/>).</summary>
    private Fixed ObserverFactor(Soldier observer) => _suppression?.ObserverFactor(observer.Id) ?? Fixed.One;

    private static Fixed Min1(Fixed x) => x > Fixed.One ? Fixed.One : x;

    private static ContactKind KindOf(Soldier s) =>
        s.Role is Role.MgGunner or Role.MgAssistant or Role.MgLoader ? ContactKind.MachineGun : ContactKind.Infantry;

    private static void See(SideKnowledge knowledge, SideKnowledge.Contact? contact, Soldier target, int observerId, long tick)
    {
        if (contact == null)
        {
            contact = new SideKnowledge.Contact { EnemyId = target.Id, Kind = KindOf(target), FirstSeen = tick };
            knowledge.Contacts[target.Id] = contact;
        }

        contact.Quality = ContactQuality.Tracked;
        contact.X = target.X;
        contact.Y = target.Y;
        contact.LastSeen = tick;
        contact.SeenBy = observerId;
    }

    /// <summary>Adds entries for soldiers spawned since the last tick (spawn order) and a knowledge record per side.</summary>
    private void Sync()
    {
        foreach (var squad in _org.Squads)
        {
            if (!_sides.ContainsKey(squad.Side))
            {
                _sides[squad.Side] = new SideKnowledge(squad.Side);
            }

            foreach (var s in squad.Soldiers)
            {
                if (!_indexOf.ContainsKey(s.Id))
                {
                    _indexOf[s.Id] = _entries.Count;
                    _entries.Add(new Entry(s, squad) { PrevX = s.X, PrevY = s.Y });
                }
            }
        }
    }

    private void Age(long tick)
    {
        foreach (var k in _sides.Values)
        {
            List<int>? forget = null;
            foreach (var c in k.Contacts.Values)
            {
                var age = tick - c.LastSeen;
                if (age >= Rules.ForgetAfterTicks)
                {
                    (forget ??= []).Add(c.EnemyId);
                }
                else if (age >= Rules.LostAfterTicks)
                {
                    c.Quality = ContactQuality.Lost;
                }
                else if (age >= Rules.RefreshTicks)
                {
                    c.Quality = ContactQuality.Stale;
                }
            }

            foreach (var id in forget ?? [])
            {
                k.Contacts.Remove(id);
            }
        }
    }

    private Fixed Concealment(Soldier s)
    {
        var x = (int)(s.X.Raw / _tileRaw);
        var y = (int)(s.Y.Raw / _tileRaw);
        return _map.InBounds(x, y) ? Rules.Concealment(_map.GetTerrain(x, y)) : Fixed.Zero;
    }

    private TargetMotion Motion(Entry e, long tick)
    {
        if (tick - e.LastMoved > Rules.RefreshTicks)
        {
            return TargetMotion.Still;
        }

        return _move.ModeOf(e.Squad.Id) switch
        {
            MoveMode.Sneak => TargetMotion.Sneak,
            MoveMode.MoveFast => TargetMotion.MoveFast,
            _ => TargetMotion.Move,
        };
    }

    private bool Firing(Entry e, long tick) => tick - e.LastFired <= Rules.FiringWindowTicks;
    /// <summary>
    /// Whether the target is at a firing position behind a hedge from this observer (item 2.4 follow-up): a hedge on an edge
    /// of his tile, within <c>hedge_position_distance</c> of him, with the observer beyond that edge's line.
    /// </summary>
    private bool AtHedge(Soldier t, Soldier o)
    {
        var reach = Rules.HedgePositionDistance;
        if (reach <= Fixed.Zero)
        {
            return false;
        }

        var size = Fixed.FromRaw(_tileRaw);
        var x = (int)(t.X.Raw / _tileRaw);
        var y = (int)(t.Y.Raw / _tileRaw);
        if (!_map.InBounds(x, y))
        {
            return false;
        }

        var west = size * Fixed.FromInt(x);
        var east = west + size;
        var north = size * Fixed.FromInt(y);
        var south = north + size;
        return (_map.HasHedge(x, y, EdgeSide.East) && east - t.X <= reach && o.X > east)
            || (x > 0 && _map.HasHedge(x - 1, y, EdgeSide.East) && t.X - west <= reach && o.X < west)
            || (_map.HasHedge(x, y, EdgeSide.South) && south - t.Y <= reach && o.Y > south)
            || (y > 0 && _map.HasHedge(x, y - 1, EdgeSide.South) && t.Y - north <= reach && o.Y < north);
    }


    private sealed class Entry(Soldier soldier, Squad squad)
    {
        public Soldier Soldier { get; } = soldier;

        public Squad Squad { get; } = squad;

        public string Side { get; } = squad.Side;

        public Fixed PrevX { get; set; }

        public Fixed PrevY { get; set; }

        public long LastMoved { get; set; } = long.MinValue / 2;

        public long LastFired { get; set; } = long.MinValue / 2;
    }
}
