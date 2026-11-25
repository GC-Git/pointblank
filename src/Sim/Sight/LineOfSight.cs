using PointBlank.Sim.Map;

namespace PointBlank.Sim.Sight;

/// <summary>
/// Line of sight in 3D over a 2D tile map. The rules are in
/// <c>docs/superpowers/plans/2026-11-25-item-1.2-los.md</c>; in short:
/// <list type="bullet">
/// <item>The ray runs eye to eye. Eye height = tile ground + floor × floor height + posture eye height.</item>
/// <item>The walk always starts from the smaller endpoint (by x, y, eye height), so a check is
/// symmetric bit for bit. Grid crossings are ordered exactly (Int128 cross products on raw values).</item>
/// <item>Ground is a flat step per tile (its centre elevation). Every tile but the two endpoint
/// tiles blocks if the ray enters or leaves it below its ground.</item>
/// <item>Terrain with obscurement (woods) adds per metre of ray inside the tile when the ray is
/// below ground + canopy height there; endpoint tiles count too. Building tiles ignore terrain.</item>
/// <item>A hedge is a thin wall on its edge, top = higher ground of the two tiles + hedge
/// height; crossing below the top adds the hedge obscurement. No special rule for soldiers
/// next to a hedge.</item>
/// <item>A building that holds neither endpoint blocks below its roof. One that holds an
/// endpoint is open inside; the ray crosses its outer wall only through a door or window on
/// that side and floor, or above the roof.</item>
/// <item>Through an exact tile corner both L-shaped edge paths are tried and the clearer one
/// is used, so a continuous hedge line never leaks.</item>
/// </list>
/// The map's tiles are copied when this is built: build a new one after the map changes.
/// </summary>
public sealed class LineOfSight
{
    private readonly GameMap _map;
    private readonly SightRules _rules;
    private readonly int _width;
    private readonly long _tileRaw;
    private readonly Fixed[] _ground;
    private readonly Fixed[] _canopyTop; // ground + canopy height
    private readonly Fixed[] _perMetre;
    private readonly int[] _building; // index into _floors, or -1
    private readonly int[] _floors;
    private readonly Fixed[] _roof;
    private readonly int[] _openings; // bit (side * MaxFloors + floor)
    private readonly bool[] _hedgeEast;
    private readonly bool[] _hedgeSouth;

    public LineOfSight(GameMap map, SightRules rules)
    {
        _map = map;
        _rules = rules;
        _width = map.Width;
        _tileRaw = map.TileSize.Raw;
        var n = map.Width * map.Height;
        _ground = new Fixed[n];
        _canopyTop = new Fixed[n];
        _perMetre = new Fixed[n];
        _building = new int[n];
        _roof = new Fixed[n];
        _openings = new int[n];
        _hedgeEast = new bool[n];
        _hedgeSouth = new bool[n];
        _floors = map.Buildings.Select(b => b.Floors).ToArray();

        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                var i = (y * _width) + x;
                var terrain = map.GetTerrain(x, y);
                _ground[i] = map.Elevation(x, y);
                _canopyTop[i] = _ground[i] + rules.CanopyHeight(terrain);
                _perMetre[i] = rules.ObscurementPerMetre(terrain);
                _hedgeEast[i] = x + 1 < map.Width && map.HasHedge(x, y, EdgeSide.East);
                _hedgeSouth[i] = y + 1 < map.Height && map.HasHedge(x, y, EdgeSide.South);
                _building[i] = -1;
            }
        }

        for (var b = 0; b < map.Buildings.Count; b++)
        {
            var building = map.Buildings[b];
            foreach (var t in building.Tiles)
            {
                var i = (t.Y * _width) + t.X;
                _building[i] = b;
                _roof[i] = _ground[i] + (rules.FloorHeight * Fixed.FromInt(building.Floors));
            }

            foreach (var o in building.Openings)
            {
                _openings[(o.Tile.Y * _width) + o.Tile.X] |= 1 << (((int)o.Side * Building.MaxFloors) + o.Floor);
            }
        }
    }

    /// <summary>Absolute eye height of a viewpoint, in metres.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The point is off the map.</exception>
    /// <exception cref="ArgumentException">The floor does not exist there.</exception>
    public Fixed EyeHeight(Viewpoint v)
    {
        var i = TileIndex(v);
        if (v.Floor < 0 || (v.Floor > 0 && (_building[i] < 0 || v.Floor >= _floors[_building[i]])))
        {
            throw new ArgumentException($"Floor {v.Floor} does not exist at ({v.X}, {v.Y}).", nameof(v));
        }

        return _ground[i] + (_rules.FloorHeight * Fixed.FromInt(v.Floor)) + _rules.EyeHeight(v.Posture);
    }

    /// <summary>Whether the eyes of <paramref name="a"/> and <paramref name="b"/> see each other.</summary>
    public LosResult Check(Viewpoint a, Viewpoint b) => Run(a, b, null);

    /// <summary>Debug helper: every tile the ray visits, from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public IReadOnlyList<LosStep> Trace(Viewpoint a, Viewpoint b)
    {
        var steps = new List<LosStep>();
        Run(a, b, steps);
        return steps;
    }

    /// <summary>
    /// Debug helper for overlays: whether a target with <paramref name="targetPosture"/> at the
    /// centre of each tile (ground floor) is visible from <paramref name="observer"/>, row-major.
    /// </summary>
    public bool[] VisibleTiles(Viewpoint observer, Posture targetPosture)
    {
        var grid = new bool[_map.Width * _map.Height];
        var half = Fixed.FromRaw(_tileRaw / 2);
        for (var y = 0; y < _map.Height; y++)
        {
            for (var x = 0; x < _map.Width; x++)
            {
                var target = new Viewpoint(
                    Fixed.FromRaw(x * _tileRaw) + half, Fixed.FromRaw(y * _tileRaw) + half, targetPosture);
                grid[(y * _width) + x] = Check(observer, target).Visible;
            }
        }

        return grid;
    }

    private LosResult Run(Viewpoint a, Viewpoint b, List<LosStep>? trace)
    {
        var ea = EyeHeight(a);
        var eb = EyeHeight(b);
        var swap = Compare(a, ea, b, eb) > 0;
        var result = swap ? new Walk(this, b, eb, a, ea, trace).Run() : new Walk(this, a, ea, b, eb, trace).Run();
        if (swap && trace != null)
        {
            trace.Reverse();
            for (var i = 0; i < trace.Count; i++)
            {
                var s = trace[i];
                trace[i] = s with
                {
                    EnterT = Fixed.One - s.ExitT,
                    ExitT = Fixed.One - s.EnterT,
                    EnterHeight = s.ExitHeight,
                    ExitHeight = s.EnterHeight,
                };
            }
        }

        return result;
    }

    private static int Compare(Viewpoint a, Fixed ea, Viewpoint b, Fixed eb)
    {
        var c = a.X.CompareTo(b.X);
        if (c == 0)
        {
            c = a.Y.CompareTo(b.Y);
        }

        return c != 0 ? c : ea.CompareTo(eb);
    }

    private int TileIndex(Viewpoint v)
    {
        if (v.X.Raw < 0 || v.Y.Raw < 0 || v.X.Raw / _tileRaw >= _map.Width || v.Y.Raw / _tileRaw >= _map.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(v), $"({v.X}, {v.Y}) is off the map.");
        }

        return (int)(v.Y.Raw / _tileRaw * _width) + (int)(v.X.Raw / _tileRaw);
    }

    private static Fixed Min(Fixed a, Fixed b) => a < b ? a : b;

    private static Fixed Max(Fixed a, Fixed b) => a > b ? a : b;

    /// <summary>What crossing one tile edge costs: a hard block, or hedge obscurement.</summary>
    private struct EdgeCost
    {
        public LosBlocker Block;
        public int BlockTile;
        public Fixed Hedge;
        public int HedgeTile;
    }

    /// <summary>One walk along a ray, from the canonical first endpoint to the second.</summary>
    private struct Walk
    {
        private readonly LineOfSight _los;
        private readonly List<LosStep>? _trace;
        private readonly long _x0;
        private readonly long _y0;
        private readonly long _dx;
        private readonly long _dy;
        private readonly Fixed _eyeA;
        private readonly Fixed _dh;
        private readonly int _startTile;
        private readonly int _endTile;
        private readonly int _buildingA;
        private readonly int _buildingB;
        private Fixed _length;
        private Fixed _obscurement;

        public Walk(LineOfSight los, Viewpoint a, Fixed ea, Viewpoint b, Fixed eb, List<LosStep>? trace)
        {
            _los = los;
            _trace = trace;
            _x0 = a.X.Raw;
            _y0 = a.Y.Raw;
            _dx = b.X.Raw - a.X.Raw;
            _dy = b.Y.Raw - a.Y.Raw;
            _eyeA = ea;
            _dh = eb - ea;
            _startTile = los.TileIndex(a);
            _endTile = los.TileIndex(b);
            _buildingA = los._building[_startTile];
            _buildingB = los._building[_endTile];
            _length = Fixed.Zero;
            _obscurement = Fixed.Zero;
        }

        public LosResult Run()
        {
            var los = _los;
            var tile = los._tileRaw;
            var w = los._width;
            var i = (int)(_x0 / tile);
            var j = (int)(_y0 / tile);
            var sx = Math.Sign(_dx);
            var sy = Math.Sign(_dy);
            var nx = Math.Abs((_endTile % w) - i);
            var ny = Math.Abs((_endTile / w) - j);
            var adx = Math.Abs(_dx);
            var ady = Math.Abs(_dy);

            // Raw distance from the start to the next vertical / horizontal grid line along each
            // axis (non-negative). The next crossing is the smaller of numX/adx and numY/ady,
            // ordered exactly by the cross products numX*ady and numY*adx, kept incrementally.
            var numX = Math.Abs((sx > 0 ? (i + 1) * tile : i * tile) - _x0);
            var numY = Math.Abs((sy > 0 ? (j + 1) * tile : j * tile) - _y0);
            var crossX = (Int128)numX * ady;
            var crossY = (Int128)numY * adx;
            var stepCrossX = (Int128)tile * ady;
            var stepCrossY = (Int128)tile * adx;

            // Ray parameter and height at those crossings, stepped by constants. The rounding
            // drift is at most a few 2^-32 per tile and the walk is canonical, so it is
            // deterministic and symmetric; only heights use these, never the crossing order.
            Fixed tX = default, dtX = default, hX = default, dhX = default;
            Fixed tY = default, dtY = default, hY = default, dhY = default;
            if (nx > 0)
            {
                tX = Fixed.FromRatio(numX, adx);
                dtX = Fixed.FromRatio(tile, adx);
                hX = _eyeA + (_dh * tX);
                dhX = _dh * dtX;
            }

            if (ny > 0)
            {
                tY = Fixed.FromRatio(numY, ady);
                dtY = Fixed.FromRatio(tile, ady);
                hY = _eyeA + (_dh * tY);
                dhY = _dh * dtY;
            }

            var tIn = Fixed.Zero;
            var hIn = _eyeA;
            while (true)
            {
                var index = (j * w) + i;
                if (nx == 0 && ny == 0)
                {
                    return Tile(index, tIn, Fixed.One, hIn, _eyeA + _dh, out var end) ?? end;
                }

                int order; // < 0: x line first, > 0: y line first, 0: both (a corner)
                if (ny == 0)
                {
                    order = -1;
                }
                else if (nx == 0)
                {
                    order = 1;
                }
                else
                {
                    order = crossX.CompareTo(crossY);
                }

                var tOut = order <= 0 ? tX : tY;
                var hOut = order <= 0 ? hX : hY;
                var blocked = Tile(index, tIn, tOut, hIn, hOut, out _);
                if (blocked != null)
                {
                    return blocked.Value;
                }

                var ni = order <= 0 ? i + sx : i;
                var nj = order >= 0 ? j + sy : j;
                var next = (nj * w) + ni;
                EdgeCost cost;
                if (order != 0)
                {
                    cost = los.Edge(index, next, order < 0, hOut, _buildingA, _buildingB);
                }
                else
                {
                    var viaX = (j * w) + ni;
                    var viaY = (nj * w) + i;
                    var pathX = Combine(los.Edge(index, viaX, true, hOut, _buildingA, _buildingB), los.Edge(viaX, next, false, hOut, _buildingA, _buildingB));
                    var pathY = Combine(los.Edge(index, viaY, false, hOut, _buildingA, _buildingB), los.Edge(viaY, next, true, hOut, _buildingA, _buildingB));
                    cost = Better(pathX, pathY);
                }

                var edgeBlock = ApplyEdge(cost);
                if (edgeBlock != null)
                {
                    MarkLast(edgeBlock.Value.Blocker);
                    return edgeBlock.Value;
                }

                if (order <= 0)
                {
                    i = ni;
                    nx--;
                    crossX += stepCrossX;
                    tX += dtX;
                    hX += dhX;
                }

                if (order >= 0)
                {
                    j = nj;
                    ny--;
                    crossY += stepCrossY;
                    tY += dtY;
                    hY += dhY;
                }

                tIn = tOut;
                hIn = hOut;
            }
        }

        private static EdgeCost Combine(EdgeCost first, EdgeCost second)
        {
            if (first.Block != LosBlocker.None)
            {
                return first;
            }

            if (second.Block != LosBlocker.None)
            {
                return second;
            }

            return new EdgeCost
            {
                Hedge = first.Hedge + second.Hedge,
                HedgeTile = second.Hedge > Fixed.Zero ? second.HedgeTile : first.HedgeTile,
            };
        }

        /// <summary>The clearer of two corner paths; ties go to the x-first path.</summary>
        private static EdgeCost Better(EdgeCost x, EdgeCost y)
        {
            var xBlocked = x.Block != LosBlocker.None;
            var yBlocked = y.Block != LosBlocker.None;
            if (xBlocked != yBlocked)
            {
                return xBlocked ? y : x;
            }

            return xBlocked || x.Hedge <= y.Hedge ? x : y;
        }

        private LosResult? ApplyEdge(EdgeCost cost)
        {
            if (cost.Block != LosBlocker.None)
            {
                return Blocked(cost.Block, cost.BlockTile);
            }

            if (cost.Hedge > Fixed.Zero)
            {
                _obscurement += cost.Hedge;
                if (_obscurement >= _los._rules.BlockThreshold)
                {
                    return Blocked(LosBlocker.Hedge, cost.HedgeTile);
                }
            }

            return null;
        }

        /// <summary>The ray inside one tile. Returns a block, or null (and the result if this is the last tile).</summary>
        private LosResult? Tile(int index, Fixed tIn, Fixed tOut, Fixed hIn, Fixed hOut, out LosResult clear)
        {
            var los = _los;
            var low = Min(hIn, hOut);
            var blocker = LosBlocker.None;
            var building = los._building[index];
            if (index != _startTile && index != _endTile && low < los._ground[index])
            {
                blocker = LosBlocker.Ground;
            }
            else if (building >= 0)
            {
                if (building != _buildingA && building != _buildingB && low < los._roof[index])
                {
                    blocker = LosBlocker.Building;
                }
            }
            else if (los._perMetre[index] > Fixed.Zero && low < los._canopyTop[index])
            {
                if (_length == Fixed.Zero)
                {
                    var dx = Fixed.FromRaw(_dx);
                    var dy = Fixed.FromRaw(_dy);
                    _length = Fixed.Sqrt((dx * dx) + (dy * dy));
                }

                _obscurement += los._perMetre[index] * (_length * (tOut - tIn));
                if (_obscurement >= los._rules.BlockThreshold)
                {
                    blocker = LosBlocker.Woods;
                }
            }

            var obscured = blocker != LosBlocker.None ? Fixed.One : Min(_obscurement, Fixed.One);
            _trace?.Add(new LosStep(los.Pos(index), tIn, tOut, hIn, hOut, obscured, blocker));
            clear = new LosResult(true, obscured, LosBlocker.None, default);
            return blocker != LosBlocker.None ? Blocked(blocker, index) : null;
        }

        private readonly LosResult Blocked(LosBlocker blocker, int index) =>
            new(false, Fixed.One, blocker, _los.Pos(index));

        private readonly void MarkLast(LosBlocker blocker)
        {
            if (_trace is { Count: > 0 })
            {
                _trace[^1] = _trace[^1] with { Blocker = blocker, Obscurement = Fixed.One };
            }
        }
    }

    private TilePos Pos(int index) => new(index % _width, index / _width);

    /// <summary>The cost of crossing from tile <paramref name="from"/> to its neighbour <paramref name="to"/> at height <paramref name="h"/>.</summary>
    private EdgeCost Edge(int from, int to, bool horizontal, Fixed h, int buildingA, int buildingB)
    {
        var cost = default(EdgeCost);
        var owner = Math.Min(from, to); // the tile west or north of the edge
        if ((horizontal ? _hedgeEast : _hedgeSouth)[owner]
            && h < Max(_ground[from], _ground[to]) + _rules.HedgeHeight)
        {
            cost.Hedge = _rules.HedgeObscurement;
            cost.HedgeTile = owner;
        }

        var bf = _building[from];
        var bt = _building[to];
        if (bf != bt)
        {
            var sideFrom = horizontal ? (to > from ? WallSide.East : WallSide.West) : (to > from ? WallSide.South : WallSide.North);
            var sideTo = (WallSide)(((int)sideFrom + 2) % 4);
            if (bf >= 0 && !WallPasses(from, sideFrom, bf, h, buildingA, buildingB))
            {
                cost.Block = LosBlocker.Building;
                cost.BlockTile = from;
            }
            else if (bt >= 0 && !WallPasses(to, sideTo, bt, h, buildingA, buildingB))
            {
                cost.Block = LosBlocker.Building;
                cost.BlockTile = to;
            }
        }

        return cost;
    }

    private bool WallPasses(int index, WallSide side, int building, Fixed h, int buildingA, int buildingB)
    {
        if (h >= _roof[index])
        {
            return true;
        }

        if (building != buildingA && building != buildingB)
        {
            return false;
        }

        var above = h - _ground[index];
        var floor = above > Fixed.Zero ? (above / _rules.FloorHeight).ToInt() : 0;
        floor = Math.Min(floor, _floors[building] - 1);
        return (_openings[index] & (1 << (((int)side * Building.MaxFloors) + floor))) != 0;
    }
}
