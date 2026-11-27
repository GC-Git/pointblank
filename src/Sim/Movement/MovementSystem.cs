using PointBlank.Sim.Map;
using PointBlank.Sim.Org;

namespace PointBlank.Sim.Movement;

/// <summary>An order to move a squad to a point (metres from the map's north-west corner).</summary>
public sealed record MoveOrder(int SquadId, Fixed X, Fixed Y, MoveMode Mode);

/// <summary>What <see cref="MovementSystem.Execute"/> did with an order. Values are hashed, so never renumber.</summary>
public enum MoveResult : byte
{
    Started = 0,

    /// <summary>The squad has no man able to move.</summary>
    NoAbleMen = 1,

    /// <summary>The target point is outside the map.</summary>
    OffMap = 2,

    /// <summary>No path leads to the target tile.</summary>
    Unreachable = 3,
}

/// <summary>
/// Moves squads. A squad has one path (A* from the centre of its able men to the target tile) and a
/// "guide" that walks it at the mode's speed, slowed by terrain and hedges exactly as the path cost
/// assumes. Each able man has a slot in a staggered column behind the guide along the path (the
/// leader's slot is the guide); he picks the nearest free tile to his slot, finds his own path
/// there and walks it. The guide waits while a man is further than the leash from his slot. Men do
/// not share a final tile: a tile that another squad's idle man holds, or an earlier man of the same
/// squad has claimed, is skipped. Dead and incapacitated men do not move. Item 2.3 adds, for the unit AI:
/// held teams (<see cref="SetHeldTeams"/>: their men wait where they are, the leash ignores them, and the guide waits
/// while every man is held; while any team is held, or during a rush, the others on a Move go at MoveFast speed) and local moves (<see cref="MoveSoldier"/>: one man walks to a tile while his squad has no
/// move; a squad move cancels them). Register after <see cref="OrgSystem"/>.
/// </summary>
public sealed class MovementSystem : ISimSystem
{
    private const int SnapRadius = 6;

    /// <summary>In <see cref="_held"/>: the squad's free men rush (MoveFast speed) even with no team held.</summary>
    private const int RushFlag = 1 << 30;

    private static readonly Fixed HalfTile = Fixed.FromRatio(1, 2);
    private static readonly Fixed InvSqrt2 = Fixed.One / Fixed.Sqrt(Fixed.FromInt(2));

    private readonly GameMap _map;
    private readonly OrgSystem _org;
    private readonly Suppression.SuppressionSystem? _suppression;
    private readonly Dictionary<int, Guide> _guides = [];
    private readonly Dictionary<int, Member> _members = [];
    private readonly SortedDictionary<int, Local> _local = [];
    private readonly SortedDictionary<int, int> _held = [];
    private readonly SortedSet<int> _heldMen = []; // single men the unit AI holds (item 2.4: each man bolts and drops on his own)

    public MovementSystem(GameMap map, OrgSystem org, MovementRules rules, Suppression.SuppressionSystem? suppression = null)
    {
        _suppression = suppression;
        _map = map;
        _org = org;
        Rules = rules;
        Paths = new PathFinder(map, rules);
    }

    public MovementRules Rules { get; }

    public PathFinder Paths { get; }

    /// <summary>Whether the squad has a move order it has not finished.</summary>
    internal bool IsMoving(int squadId) => _guides.ContainsKey(squadId);

    /// <summary>The mode of the squad's unfinished move order, or null when it is not moving.</summary>
    internal MoveMode? ModeOf(int squadId) => _guides.TryGetValue(squadId, out var g) ? g.Mode : null;

    /// <summary>Whether the soldier is walking now: on his squad's move and not held, or on a local move.</summary>
    internal bool IsSoldierMoving(int soldierId) =>
        _local.ContainsKey(soldierId) || (_members.TryGetValue(soldierId, out var m) && m.Path != null && !m.Held);

    /// <summary>
    /// Holds the teams in <paramref name="teamMask"/> (bit per team index) of a moving squad: they wait where they
    /// stand until released (mask 0). Ignored when the squad is not moving; cleared when its move ends or is replaced.
    /// </summary>
    /// <summary>Holds one man of a moving squad where he is (or frees him): the unit AI's per-man rush and drop (item 2.4).</summary>
    internal void SetHeldSoldier(int soldierId, bool held)
    {
        if (held)
        {
            _heldMen.Add(soldierId);
        }
        else
        {
            _heldMen.Remove(soldierId);
        }
    }

    internal void SetHeldTeams(int squadId, int teamMask, bool rush = false)
    {
        var value = (teamMask & ~RushFlag) | (rush ? RushFlag : 0);
        if (value == 0 || !_guides.ContainsKey(squadId))
        {
            _held.Remove(squadId);
        }
        else
        {
            _held[squadId] = value;
        }
    }

    /// <summary>The pace the squad's free men move at now: MoveFast during a rush or a bound, else its move's mode; null when not moving.</summary>
    internal MoveMode? PaceOf(int squadId) =>
        !_guides.TryGetValue(squadId, out var g) ? null
        : _held.GetValueOrDefault(squadId) != 0 && g.Mode is MoveMode.Move or MoveMode.MoveFast ? MoveMode.MoveFast
        : g.Mode;

    /// <summary>
    /// Sends one able man of a squad that has no move to a tile (a short local move for the unit AI); his posture is
    /// left as it is. False when the squad is moving, the man cannot move, the tile is off the map or unreachable, or
    /// he is already there.
    /// </summary>
    internal bool MoveSoldier(int squadId, int soldierId, TilePos goal, MoveMode mode)
    {
        var squad = _org.Squads.FirstOrDefault(s => s.Id == squadId);
        var soldier = squad?.Soldiers.FirstOrDefault(s => s.Id == soldierId);
        if (soldier == null || !IsAble(soldier) || _guides.ContainsKey(squadId) || !_map.InBounds(goal.X, goal.Y))
        {
            return false;
        }

        var m = new Member { SinceReplan = int.MaxValue / 2 };
        Replan(m, soldier, goal, mode);
        if (m.Path == null)
        {
            _local.Remove(soldierId);
            return false;
        }

        _local[soldierId] = new Local(soldier, m, mode);
        return true;
    }

    /// <summary>Whether a point (metres from the north-west corner) lies on the map.</summary>
    public bool IsOnMap(Fixed x, Fixed y) =>
        x >= Fixed.Zero && y >= Fixed.Zero
        && x < _map.TileSize * Fixed.FromInt(_map.Width) && y < _map.TileSize * Fixed.FromInt(_map.Height);

    /// <summary>
    /// Starts the order: plans the squad's path; every able man takes the mode's posture on his own reaction
    /// (<c>Org.PostureChange</c>) and walks once he has.
    /// Orders reach this through the chain of command (<c>Orders.CommandSystem</c>); tests may call it
    /// directly. Changes nothing unless the result is <see cref="MoveResult.Started"/>: a target off
    /// the map is rejected, never clamped to the edge.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no such squad.</exception>
    internal MoveResult Execute(MoveOrder order)
    {
        var squad = _org.Squads.FirstOrDefault(s => s.Id == order.SquadId)
            ?? throw new InvalidOperationException($"No squad {order.SquadId}.");
        if (!IsOnMap(order.X, order.Y))
        {
            return MoveResult.OffMap;
        }

        var active = squad.Soldiers.Where(IsAble).ToList();
        if (active.Count == 0)
        {
            return MoveResult.NoAbleMen;
        }

        var sumX = Fixed.Zero;
        var sumY = Fixed.Zero;
        foreach (var s in active)
        {
            sumX += s.X;
            sumY += s.Y;
        }

        var count = Fixed.FromInt(active.Count);
        var path = Paths.FindPath(TileOf(sumX / count, sumY / count), TileOf(order.X, order.Y), order.Mode);
        if (path == null)
        {
            return MoveResult.Unreachable;
        }

        _guides[squad.Id] = new Guide(order.Mode, path, TileOf(order.X, order.Y))
        {
            Seg = path.Count == 1 ? 0 : 1,
            Done = path.Count == 1,
        };
        _held.Remove(squad.Id);
        foreach (var s in squad.Soldiers)
        {
            _local.Remove(s.Id);
        }

        foreach (var s in active)
        {
            _members[s.Id] = new Member { SinceReplan = int.MaxValue / 2 };
            PostureChange.Order(s, Rules.PostureOf(order.Mode)); // each man gets up or down on his own reaction
        }

        return MoveResult.Started;
    }

    /// <summary>Ends the squad's move where its men stand now (a Hold order). Does nothing if it is not moving.</summary>
    /// <exception cref="InvalidOperationException">There is no such squad.</exception>
    internal void Stop(int squadId)
    {
        var squad = _org.Squads.FirstOrDefault(s => s.Id == squadId)
            ?? throw new InvalidOperationException($"No squad {squadId}.");
        _guides.Remove(squadId);
        _held.Remove(squadId);
        foreach (var s in squad.Soldiers)
        {
            _members.Remove(s.Id);
            _local.Remove(s.Id);
            PostureChange.Cancel(s); // a later Hold is not overridden by the posture of the move it ends
        }
    }

    public void Tick(World world)
    {
        PostureChange.Apply(_org.Squads, world.Tick, Rules.OrderPostureDelayTicks); // before anyone steps

        if (_local.Count > 0)
        {
            foreach (var id in _local.Keys.ToArray())
            {
                var l = _local[id];
                if (IsAble(l.Soldier) && !l.Soldier.AwaitingPosture)
                {
                    MoveMember(l.Member, l.Soldier, l.Mode);
                }

                if (!IsAble(l.Soldier) || l.Member.Path == null)
                {
                    _local.Remove(id);
                }
            }
        }

        if (_guides.Count == 0)
        {
            return;
        }

        var idle = IdleTiles();
        foreach (var squad in _org.Squads)
        {
            if (_guides.TryGetValue(squad.Id, out var guide))
            {
                Step(squad, guide, idle);
            }
        }
    }

    public void AppendState(StateHasher hasher)
    {
        hasher.Add(_heldMen.Count);
        foreach (var id in _heldMen)
        {
            hasher.Add(id);
        }

        hasher.Add(_org.Squads.Count);
        foreach (var squad in _org.Squads)
        {
            var has = _guides.TryGetValue(squad.Id, out var g);
            hasher.Add(has);
            if (has)
            {
                hasher.Add((byte)g!.Mode);
                hasher.Add(g.Target.X);
                hasher.Add(g.Target.Y);
                hasher.Add(g.Path.Count);
                foreach (var t in g.Path)
                {
                    hasher.Add(t.X);
                    hasher.Add(t.Y);
                }

                hasher.Add(g.Seg);
                hasher.Add(g.P);
                hasher.Add(g.Done);
                hasher.Add(_held.GetValueOrDefault(squad.Id));
            }

            foreach (var soldier in squad.Soldiers)
            {
                var hm = _members.TryGetValue(soldier.Id, out var m);
                hasher.Add(hm);
                if (!hm)
                {
                    continue;
                }

                hasher.Add(m!.HasGoal);
                hasher.Add(m.Held);
                hasher.Add(m.Goal.X);
                hasher.Add(m.Goal.Y);
                hasher.Add(m.Stuck);
                hasher.Add(m.SinceReplan);
                hasher.Add(m.Path?.Count ?? -1);
                if (m.Path != null)
                {
                    foreach (var t in m.Path)
                    {
                        hasher.Add(t.X);
                        hasher.Add(t.Y);
                    }

                    hasher.Add(m.Seg);
                    hasher.Add(m.P);
                    hasher.Add(m.StartX);
                    hasher.Add(m.StartY);
                    hasher.Add(m.FirstFraction);
                }
            }
        }

        hasher.Add(_local.Count);
        foreach (var (id, l) in _local)
        {
            hasher.Add(id);
            hasher.Add((byte)l.Mode);
            HashMember(hasher, l.Member);
        }
    }

    private static void HashMember(StateHasher hasher, Member m)
    {
        hasher.Add(m.Goal.X);
        hasher.Add(m.Goal.Y);
        hasher.Add(m.Path?.Count ?? -1);
        if (m.Path != null)
        {
            foreach (var t in m.Path)
            {
                hasher.Add(t.X);
                hasher.Add(t.Y);
            }

            hasher.Add(m.Seg);
            hasher.Add(m.P);
            hasher.Add(m.StartX);
            hasher.Add(m.StartY);
            hasher.Add(m.FirstFraction);
        }
    }

    private static bool IsAble(Soldier s) => s.Health is HealthState.Ok or HealthState.Wounded;

    private static Fixed Square(Fixed v) => v * v;

    private static Fixed Lerp(Fixed a, Fixed b, Fixed t) => a + ((b - a) * t);

    private void Step(Squad squad, Guide guide, Dictionary<int, int> idle)
    {
        var active = new List<Soldier>();
        foreach (var s in squad.Soldiers)
        {
            if (IsAble(s))
            {
                active.Add(s);
            }
            else
            {
                _members.Remove(s.Id);
            }
        }

        if (active.Count == 0)
        {
            _guides.Remove(squad.Id);
            return;
        }

        var targetBuilding = _map.BuildingAt(guide.Target.X, guide.Target.Y);
        var claimed = new HashSet<int>();
        var slots = new (Fixed X, Fixed Y)[active.Count];
        var goals = new TilePos[active.Count];
        for (var i = 0; i < active.Count; i++)
        {
            slots[i] = SlotPoint(guide, i);
            goals[i] = PickGoal(slots[i], targetBuilding, claimed, idle, squad.Id);
            claimed.Add((goals[i].Y * _map.Width) + goals[i].X);
        }

        var heldMask = _held.GetValueOrDefault(squad.Id) & ~RushFlag;
        var anyFree = false;
        foreach (var s in active)
        {
            anyFree |= (heldMask & (1 << s.TeamIndex)) == 0 && !_heldMen.Contains(s.Id) && !s.AwaitingPosture;
        }

        // While some teams are held (fire and movement), the others bound: they rush at MoveFast speed on the same path.
        var pace = PaceOf(squad.Id)!.Value;
        if (!guide.Done && anyFree && !AnyBeyondLeash(active, slots, heldMask))
        {
            AdvanceGuide(guide, pace);
        }

        var finished = guide.Done;
        for (var i = 0; i < active.Count; i++)
        {
            var soldier = active[i];
            if (!_members.TryGetValue(soldier.Id, out var m))
            {
                _members[soldier.Id] = m = new Member { SinceReplan = int.MaxValue / 2 };
            }

            m.SinceReplan = Math.Min(m.SinceReplan + 1, int.MaxValue / 2);
            m.Held = (heldMask & (1 << soldier.TeamIndex)) != 0 || _heldMen.Contains(soldier.Id) || soldier.AwaitingPosture;
            if (m.Held)
            {
                finished = false;
                continue;
            }

            if (!m.HasGoal || (m.Goal != goals[i] && m.SinceReplan >= Rules.ReplanTicks))
            {
                Replan(m, soldier, goals[i], guide.Mode);
            }

            MoveMember(m, soldier, pace);
            finished &= m.Path == null && m.HasGoal && m.Goal == goals[i];
        }

        if (finished)
        {
            _guides.Remove(squad.Id);
            _held.Remove(squad.Id);
            foreach (var s in active)
            {
                _members.Remove(s.Id);
            }
        }
    }

    private bool AnyBeyondLeash(List<Soldier> active, (Fixed X, Fixed Y)[] slots, int heldMask)
    {
        var leash2 = Square(Rules.Leash);
        for (var i = 0; i < active.Count; i++)
        {
            if ((heldMask & (1 << active[i].TeamIndex)) != 0)
            {
                continue;
            }

            if (_members.TryGetValue(active[i].Id, out var m) && m.Stuck)
            {
                continue;
            }

            if (Square(active[i].X - slots[i].X) + Square(active[i].Y - slots[i].Y) > leash2)
            {
                return true;
            }
        }

        return false;
    }

    private void AdvanceGuide(Guide g, MoveMode mode)
    {
        var tile = _map.TileSize;
        var budget = Rules.Speed(mode) * World.TickDuration;
        while (budget > Fixed.Zero && !g.Done)
        {
            var cost = Paths.StepCost(g.Path[g.Seg - 1], g.Path[g.Seg], mode) * tile;
            var need = cost * (Fixed.One - g.P);
            if (budget >= need)
            {
                budget -= need;
                if (g.Seg == g.Path.Count - 1)
                {
                    g.P = Fixed.One;
                    g.Done = true;
                }
                else
                {
                    g.Seg++;
                    g.P = Fixed.Zero;
                }
            }
            else
            {
                g.P += budget / cost;
                budget = Fixed.Zero;
            }
        }
    }

    private void Replan(Member m, Soldier s, TilePos goal, MoveMode mode)
    {
        m.Goal = goal;
        m.HasGoal = true;
        m.SinceReplan = 0;
        m.Stuck = false;
        m.Path = null;
        var start = TileOf(s.X, s.Y);
        var path = Paths.FindPath(start, goal, mode);
        if (path == null)
        {
            m.Stuck = true;
            return;
        }

        if (path.Count == 1)
        {
            path.Add(start);
        }

        var target = Centre(path[1]);
        var dx = target.X - s.X;
        var dy = target.Y - s.Y;
        if (dx == Fixed.Zero && dy == Fixed.Zero && path.Count == 2)
        {
            return;
        }

        var full = StepLength(path[0], path[1]) * _map.TileSize;
        m.Path = path;
        m.Seg = 1;
        m.P = Fixed.Zero;
        m.StartX = s.X;
        m.StartY = s.Y;
        m.FirstFraction = Fixed.Sqrt(Square(dx) + Square(dy)) / full;
    }

    private void MoveMember(Member m, Soldier s, MoveMode mode)
    {
        if (m.Path == null)
        {
            return;
        }

        var tile = _map.TileSize;
        var budget = Rules.Speed(mode) * World.TickDuration * (_suppression?.SpeedFactor(s.Id) ?? Fixed.One); // a suppressed man slows, a pinned one crawls
        while (budget > Fixed.Zero && m.Path != null)
        {
            var b = m.Path[m.Seg];
            var cost = Paths.StepCost(m.Path[m.Seg - 1], b, mode) * tile * (m.Seg == 1 ? m.FirstFraction : Fixed.One);
            var need = cost * (Fixed.One - m.P);
            if (cost <= Fixed.Zero || budget >= need)
            {
                budget -= need;
                var c = Centre(b);
                s.X = c.X;
                s.Y = c.Y;
                m.Seg++;
                m.P = Fixed.Zero;
                if (m.Seg >= m.Path.Count)
                {
                    m.Path = null;
                }
            }
            else
            {
                m.P += budget / cost;
                budget = Fixed.Zero;
                var from = m.Seg == 1 ? (m.StartX, m.StartY) : Centre(m.Path[m.Seg - 1]);
                var to = Centre(b);
                s.X = Lerp(from.Item1, to.X, m.P);
                s.Y = Lerp(from.Item2, to.Y, m.P);
            }
        }
    }

    /// <summary>Tiles held by idle men, mapped to the squad whose man is there (the lowest squad id wins).</summary>
    private Dictionary<int, int> IdleTiles()
    {
        var idle = new Dictionary<int, int>();
        foreach (var squad in _org.Squads)
        {
            foreach (var s in squad.Soldiers)
            {
                if (IsAble(s) && (!_members.TryGetValue(s.Id, out var m) || m.Path == null))
                {
                    var t = TileOf(s.X, s.Y);
                    idle.TryAdd((t.Y * _map.Width) + t.X, squad.Id);
                }
            }
        }

        return idle;
    }

    private TilePos PickGoal(
        (Fixed X, Fixed Y) point, Building? targetBuilding, HashSet<int> claimed, Dictionary<int, int> idle, int squadId)
    {
        var centre = TileOf(point.X, point.Y);
        for (var r = 0; r <= SnapRadius; r++)
        {
            TilePos? best = null;
            var bestDist = Fixed.MaxValue;
            for (var y = centre.Y - r; y <= centre.Y + r; y++)
            {
                for (var x = centre.X - r; x <= centre.X + r; x++)
                {
                    if (Math.Max(Math.Abs(x - centre.X), Math.Abs(y - centre.Y)) != r || !_map.InBounds(x, y))
                    {
                        continue;
                    }

                    var b = _map.BuildingAt(x, y);
                    var index = (y * _map.Width) + x;
                    if ((b != null && b != targetBuilding) || claimed.Contains(index)
                        || (idle.TryGetValue(index, out var owner) && owner != squadId))
                    {
                        continue;
                    }

                    var c = Centre(new TilePos(x, y));
                    var d = Square(c.X - point.X) + Square(c.Y - point.Y);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = new TilePos(x, y);
                    }
                }
            }

            if (best != null)
            {
                return best.Value;
            }
        }

        return centre;
    }

    /// <summary>
    /// A man's slot: slot 0 is the guide; slot n sits (n+1)/2 ranks behind it along the path, left
    /// for odd n and right for even n, offset sideways from the path's direction there. Before the
    /// start of the path a slot stays at the start tile (the column unwinds as the guide moves).
    /// </summary>
    private (Fixed X, Fixed Y) SlotPoint(Guide g, int slot)
    {
        var row = (slot + 1) / 2;
        var back = Rules.Spacing * Fixed.FromInt(row);
        var lateral = slot == 0 ? Fixed.Zero : (slot % 2 == 1 ? -Rules.Lateral : Rules.Lateral);
        var path = g.Path;
        Fixed x;
        Fixed y;
        (Fixed X, Fixed Y) dir = (Fixed.One, Fixed.Zero);
        if (path.Count == 1)
        {
            var c = Centre(path[0]);
            (x, y) = (c.X, c.Y);
        }
        else
        {
            var k = g.Seg;
            var travelled = g.P * (StepLength(path[k - 1], path[k]) * _map.TileSize);
            var rest = back;
            while (true)
            {
                dir = Unit(path[k - 1], path[k]);
                if (rest <= travelled)
                {
                    var a = Centre(path[k - 1]);
                    var d = travelled - rest;
                    (x, y) = (a.X + (dir.X * d), a.Y + (dir.Y * d));
                    break;
                }

                rest -= travelled;
                k--;
                if (k < 1)
                {
                    var a = Centre(path[0]);
                    (x, y) = (a.X, a.Y);
                    break;
                }

                travelled = StepLength(path[k - 1], path[k]) * _map.TileSize;
            }
        }

        return (x + (-dir.Y * lateral), y + (dir.X * lateral));
    }

    private static Fixed StepLength(TilePos a, TilePos b) =>
        a.X != b.X && a.Y != b.Y ? PathFinder.Diagonal : Fixed.One;

    private static (Fixed X, Fixed Y) Unit(TilePos a, TilePos b)
    {
        var dx = Math.Sign(b.X - a.X);
        var dy = Math.Sign(b.Y - a.Y);
        var scale = dx != 0 && dy != 0 ? InvSqrt2 : Fixed.One;
        return (Fixed.FromInt(dx) * scale, Fixed.FromInt(dy) * scale);
    }

    private (Fixed X, Fixed Y) Centre(TilePos t) =>
        (_map.TileSize * (Fixed.FromInt(t.X) + HalfTile), _map.TileSize * (Fixed.FromInt(t.Y) + HalfTile));

    private TilePos TileOf(Fixed x, Fixed y)
    {
        var tx = x < Fixed.Zero ? 0 : (x / _map.TileSize).ToInt();
        var ty = y < Fixed.Zero ? 0 : (y / _map.TileSize).ToInt();
        return new TilePos(Math.Min(tx, _map.Width - 1), Math.Min(ty, _map.Height - 1));
    }

    private sealed class Guide(MoveMode mode, List<TilePos> path, TilePos target)
    {
        public MoveMode Mode { get; } = mode;

        public List<TilePos> Path { get; } = path;

        public TilePos Target { get; } = target;

        /// <summary>The guide is on the segment from <c>Path[Seg - 1]</c> to <c>Path[Seg]</c>, P of the way along.</summary>
        public int Seg { get; set; }

        public Fixed P { get; set; }

        public bool Done { get; set; }
    }

    private sealed class Member
    {
        public bool HasGoal { get; set; }

        public TilePos Goal { get; set; }

        public bool Stuck { get; set; }

        public int SinceReplan { get; set; }

        /// <summary>His team is held this tick (item 2.3).</summary>
        public bool Held { get; set; }

        /// <summary>The man's own path, or null when he is idle. He walks from his start point to <c>Path[Seg]</c>.</summary>
        public List<TilePos>? Path { get; set; }

        public int Seg { get; set; }

        public Fixed P { get; set; }

        public Fixed StartX { get; set; }

        public Fixed StartY { get; set; }

        /// <summary>How much of a full first step the walk from the start point to <c>Path[1]</c> is.</summary>
        public Fixed FirstFraction { get; set; }
    }

    private sealed record Local(Soldier Soldier, Member Member, MoveMode Mode);
}
