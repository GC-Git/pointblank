using PointBlank.Sim.Map;

namespace PointBlank.Sim.Movement;

/// <summary>
/// A* on the tile grid. Eight neighbours; a step costs its length (1, or <see cref="Diagonal"/>
/// for a diagonal) times the mean terrain cost of the two tiles, plus the hedge crossing cost when
/// it crosses a hedge edge. The heuristic is octile distance times the cheapest terrain cost, which
/// never overestimates (and is consistent), so paths are cheapest. Ties break on f, then h (nearer
/// the goal first), then tile index, so the same map always gives the same path.
/// Not thread-safe: it reuses its work arrays between searches.
/// </summary>
public sealed class PathFinder
{
    /// <summary>Length of a diagonal step in tile-lengths (a fixed approximation of the square root of 2).</summary>
    public static readonly Fixed Diagonal = Fixed.FromRatio(14142, 10000);

    // Neighbour order: N, E, S, W, NE, SE, SW, NW.
    private static readonly int[] Dx = [0, 1, 0, -1, 1, 1, -1, -1];
    private static readonly int[] Dy = [-1, 0, 1, 0, -1, 1, 1, -1];

    private readonly GameMap _map;
    private readonly MovementRules _rules;
    private readonly Fixed[] _g;
    private readonly int[] _parent;
    private readonly int[] _seen;
    private readonly int[] _closed;
    private readonly List<Node> _heap = [];
    private int _stamp;

    public PathFinder(GameMap map, MovementRules rules)
    {
        _map = map;
        _rules = rules;
        var n = map.Width * map.Height;
        _g = new Fixed[n];
        _parent = new int[n];
        _seen = new int[n];
        _closed = new int[n];
    }

    /// <summary>Tiles taken off the open list by the last search (for tests).</summary>
    public int Expanded { get; private set; }

    /// <summary>
    /// The cheapest path from <paramref name="start"/> to <paramref name="goal"/>, both included, or
    /// null when there is none.
    /// </summary>
    public List<TilePos>? FindPath(TilePos start, TilePos goal, MoveMode mode)
    {
        if (!_map.InBounds(start.X, start.Y) || !_map.InBounds(goal.X, goal.Y))
        {
            return null;
        }

        _stamp++;
        Expanded = 0;
        _heap.Clear();
        var w = _map.Width;
        var minCost = _rules.MinTerrainCost(mode);
        var s = (start.Y * w) + start.X;
        var t = (goal.Y * w) + goal.X;
        _g[s] = Fixed.Zero;
        _parent[s] = -1;
        _seen[s] = _stamp;
        var h0 = Heuristic(start, goal, minCost);
        Push(new Node(h0, h0, s));

        while (_heap.Count > 0)
        {
            var node = Pop();
            var idx = node.Index;
            if (_closed[idx] == _stamp)
            {
                continue;
            }

            _closed[idx] = _stamp;
            Expanded++;
            if (idx == t)
            {
                return Rebuild(idx);
            }

            var a = new TilePos(idx % w, idx / w);
            for (var d = 0; d < 8; d++)
            {
                var b = new TilePos(a.X + Dx[d], a.Y + Dy[d]);
                if (!CanStep(a, b))
                {
                    continue;
                }

                var bi = (b.Y * w) + b.X;
                if (_closed[bi] == _stamp)
                {
                    continue;
                }

                var g = _g[idx] + StepCost(a, b, mode);
                if (_seen[bi] == _stamp && g >= _g[bi])
                {
                    continue;
                }

                _seen[bi] = _stamp;
                _g[bi] = g;
                _parent[bi] = idx;
                var h = Heuristic(b, goal, minCost);
                Push(new Node(g + h, h, bi));
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a unit can step from <paramref name="a"/> to the neighbouring tile <paramref name="b"/>
    /// (eight directions). Walls: moving between tiles of one building is free, but entering or
    /// leaving a building needs a ground-floor door on the wall crossed. A diagonal step may not cut
    /// a corner: a building tile, or a hedge on any of the four edges around the corner, stops it.
    /// </summary>
    public bool CanStep(TilePos a, TilePos b)
    {
        if (!_map.InBounds(b.X, b.Y))
        {
            return false;
        }

        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (dx == 0 && dy == 0)
        {
            return true;
        }

        if (dx != 0 && dy != 0)
        {
            var m1 = new TilePos(a.X + dx, a.Y);
            var m2 = new TilePos(a.X, a.Y + dy);
            return _map.BuildingAt(a.X, a.Y) == null && _map.BuildingAt(b.X, b.Y) == null
                && _map.BuildingAt(m1.X, m1.Y) == null && _map.BuildingAt(m2.X, m2.Y) == null
                && !_map.HasHedgeBetween(a, m1) && !_map.HasHedgeBetween(m1, b)
                && !_map.HasHedgeBetween(a, m2) && !_map.HasHedgeBetween(m2, b);
        }

        var ba = _map.BuildingAt(a.X, a.Y);
        var bb = _map.BuildingAt(b.X, b.Y);
        if (ba != null && ba != bb && !HasDoor(ba, a, Facing(dx, dy)))
        {
            return false;
        }

        return bb == null || bb == ba || HasDoor(bb, b, Facing(-dx, -dy));
    }

    /// <summary>The cost of a legal step, in tile-lengths of open ground (see <see cref="CanStep"/>).</summary>
    public Fixed StepCost(TilePos a, TilePos b, MoveMode mode)
    {
        var diagonal = a.X != b.X && a.Y != b.Y;
        var mean = (_rules.TerrainCost(mode, _map.GetTerrain(a.X, a.Y))
            + _rules.TerrainCost(mode, _map.GetTerrain(b.X, b.Y))) / Fixed.FromInt(2);
        var cost = (diagonal ? Diagonal : Fixed.One) * mean;
        if (!diagonal && (a.X != b.X || a.Y != b.Y) && _map.HasHedgeBetween(a, b))
        {
            cost += _rules.HedgeCost(mode);
        }

        return cost;
    }

    private static Fixed Heuristic(TilePos a, TilePos b, Fixed minCost)
    {
        var dx = Math.Abs(a.X - b.X);
        var dy = Math.Abs(a.Y - b.Y);
        var lo = Math.Min(dx, dy);
        var hi = Math.Max(dx, dy);
        return minCost * ((Diagonal * Fixed.FromInt(lo)) + Fixed.FromInt(hi - lo));
    }

    private static WallSide Facing(int dx, int dy) =>
        dx > 0 ? WallSide.East : dx < 0 ? WallSide.West : dy > 0 ? WallSide.South : WallSide.North;

    private static bool HasDoor(Building building, TilePos tile, WallSide side) =>
        building.Openings.Any(o => o.Tile == tile && o.Side == side && o.Kind == OpeningKind.Door && o.Floor == 0);

    private List<TilePos> Rebuild(int idx)
    {
        var path = new List<TilePos>();
        for (var i = idx; i >= 0; i = _parent[i])
        {
            path.Add(new TilePos(i % _map.Width, i / _map.Width));
        }

        path.Reverse();
        return path;
    }

    private readonly record struct Node(Fixed F, Fixed H, int Index);

    private static bool Less(Node a, Node b)
    {
        var c = a.F.CompareTo(b.F);
        if (c != 0)
        {
            return c < 0;
        }

        c = a.H.CompareTo(b.H);
        return c != 0 ? c < 0 : a.Index < b.Index;
    }

    private void Push(Node node)
    {
        _heap.Add(node);
        var i = _heap.Count - 1;
        while (i > 0)
        {
            var p = (i - 1) / 2;
            if (!Less(_heap[i], _heap[p]))
            {
                break;
            }

            (_heap[i], _heap[p]) = (_heap[p], _heap[i]);
            i = p;
        }
    }

    private Node Pop()
    {
        var top = _heap[0];
        var last = _heap[^1];
        _heap.RemoveAt(_heap.Count - 1);
        if (_heap.Count > 0)
        {
            _heap[0] = last;
            var i = 0;
            while (true)
            {
                var l = (2 * i) + 1;
                var r = l + 1;
                var m = i;
                if (l < _heap.Count && Less(_heap[l], _heap[m]))
                {
                    m = l;
                }

                if (r < _heap.Count && Less(_heap[r], _heap[m]))
                {
                    m = r;
                }

                if (m == i)
                {
                    break;
                }

                (_heap[i], _heap[m]) = (_heap[m], _heap[i]);
                i = m;
            }
        }

        return top;
    }
}
