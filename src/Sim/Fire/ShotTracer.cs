using PointBlank.Sim.Map;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Fire;

/// <summary>
/// The map side of a shot: a walk along the line of fire, tile by tile, that finds the first piece of
/// terrain to stop it. The line is <c>(x0 + ux·s, y0 + uy·s, z0 + slope·s)</c> for horizontal distance
/// s. Rules:
/// <list type="bullet">
/// <item>Ground is a flat step per tile (its elevation). The shot hits the ground where the line drops
/// below it, or on entering a tile whose ground is above the line.</item>
/// <item>A hedge on a crossed edge, below its top (higher ground of the two tiles + hedge height),
/// stops the shot with the hedge block chance: one RNG draw per hedge reached.</item>
/// <item>An outer wall of a building (an edge between one of its tiles and a tile outside it) crossed
/// below its roof stops the shot, unless a door or window is on that side at that floor. Inside a
/// building the shot is free. No penetration yet (item 4.3).</item>
/// <item>The map edge stops the shot (<see cref="ShotOutcome.OffMap"/>), the max range ends it
/// (<see cref="ShotOutcome.Spent"/>). A crossing through an exact tile corner is taken as x, then y.</item>
/// </list>
/// The map's tiles are copied when this is built: build a new one after the map changes.
/// </summary>
internal sealed class ShotTracer
{
    private readonly int _width;
    private readonly int _height;
    private readonly long _tileRaw;
    private readonly Fixed _tile;
    private readonly Fixed _hedgeHeight;
    private readonly Fixed _floorHeight;
    private readonly Fixed[] _ground;
    private readonly TerrainType[] _terrain;
    private readonly bool[] _hedgeEast;
    private readonly bool[] _hedgeSouth;
    private readonly int[] _building; // index, or -1
    private readonly Fixed[] _roof;
    private readonly int[] _openings; // bit (side * MaxFloors + floor)

    public ShotTracer(GameMap map, SightRules sight)
    {
        _width = map.Width;
        _height = map.Height;
        _tileRaw = map.TileSize.Raw;
        _tile = map.TileSize;
        _hedgeHeight = sight.HedgeHeight;
        _floorHeight = sight.FloorHeight;
        var n = _width * _height;
        _ground = new Fixed[n];
        _terrain = new TerrainType[n];
        _hedgeEast = new bool[n];
        _hedgeSouth = new bool[n];
        _building = new int[n];
        _roof = new Fixed[n];
        _openings = new int[n];
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var i = (y * _width) + x;
                _ground[i] = map.Elevation(x, y);
                _terrain[i] = map.GetTerrain(x, y);
                _hedgeEast[i] = x + 1 < _width && map.HasHedge(x, y, EdgeSide.East);
                _hedgeSouth[i] = y + 1 < _height && map.HasHedge(x, y, EdgeSide.South);
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
                _roof[i] = _ground[i] + (_floorHeight * Fixed.FromInt(building.Floors));
            }

            foreach (var o in building.Openings)
            {
                _openings[(o.Tile.Y * _width) + o.Tile.X] |= 1 << (((int)o.Side * Building.MaxFloors) + o.Floor);
            }
        }
    }

    /// <summary>The tile index of a point, or -1 off the map.</summary>
    public int TileOf(Fixed x, Fixed y)
    {
        if (x.Raw < 0 || y.Raw < 0)
        {
            return -1;
        }

        var tx = x.Raw / _tileRaw;
        var ty = y.Raw / _tileRaw;
        return tx >= _width || ty >= _height ? -1 : (int)((ty * _width) + tx);
    }

    public Fixed Ground(int tile) => _ground[tile];

    public TerrainType Terrain(int tile) => _terrain[tile];

    /// <summary>A walk along one line of fire; advance it with <see cref="Walk.Advance"/>.</summary>
    public Walk Start(Fixed x0, Fixed y0, Fixed z0, Fixed ux, Fixed uy, Fixed slope, Fixed maxRange, Fixed hedgeBlockChance, SimRng rng) =>
        new(this, x0, y0, z0, ux, uy, slope, maxRange, hedgeBlockChance, rng);

    internal sealed class Walk
    {
        // Below this a direction component never reaches a tile edge within any weapon range.
        private static readonly Fixed Tiny = Fixed.FromRatio(1, 1 << 16);

        private readonly ShotTracer _t;
        private readonly Fixed _x0;
        private readonly Fixed _y0;
        private readonly Fixed _z0;
        private readonly Fixed _slope;
        private readonly Fixed _max;
        private readonly Fixed _chance;
        private readonly SimRng _rng;
        private readonly int _stepX;
        private readonly int _stepY;
        private readonly Fixed _deltaX;
        private readonly Fixed _deltaY;
        private Fixed _nextX;
        private Fixed _nextY;
        private int _tx;
        private int _ty;
        private Fixed _s;
        private bool _entered;

        internal Walk(ShotTracer t, Fixed x0, Fixed y0, Fixed z0, Fixed ux, Fixed uy, Fixed slope, Fixed max, Fixed chance, SimRng rng)
        {
            _t = t;
            _x0 = x0;
            _y0 = y0;
            _z0 = z0;
            _slope = slope;
            _max = max;
            _chance = chance;
            _rng = rng;
            _tx = (int)(x0.Raw / t._tileRaw);
            _ty = (int)(y0.Raw / t._tileRaw);
            (_stepX, _deltaX, _nextX) = Axis(ux, x0, _tx);
            (_stepY, _deltaY, _nextY) = Axis(uy, y0, _ty);
            Ux = ux;
            Uy = uy;
        }

        public Fixed Ux { get; }

        public Fixed Uy { get; }

        /// <summary>Set once the walk stopped: what stopped it, and where (s).</summary>
        public ShotOutcome? Stopped { get; private set; }

        public Fixed StopS { get; private set; }

        public int HedgeRolls { get; private set; }

        public Fixed HeightAt(Fixed s) => _z0 + (_slope * s);

        /// <summary>Walks on to distance <paramref name="until"/>; false (and <see cref="Stopped"/> set) if the terrain stopped the shot first.</summary>
        public bool Advance(Fixed until)
        {
            while (Stopped == null)
            {
                var i = (_ty * _t._width) + _tx;
                var g = _t._ground[i];
                if (_entered && HeightAt(_s) < g)
                {
                    return Stop(ShotOutcome.Ground, _s);
                }

                _entered = false;
                var sOut = Min(Min(_nextX, _nextY), _max);
                var end = Min(sOut, until);
                if (_slope < Fixed.Zero && HeightAt(end) < g)
                {
                    return Stop(ShotOutcome.Ground, Max(_s, (g - _z0) / _slope));
                }

                if (until <= sOut)
                {
                    _s = until;
                    return true;
                }

                if (sOut >= _max)
                {
                    return Stop(ShotOutcome.Spent, _max);
                }

                // Cross one edge.
                var (fx, fy) = (_tx, _ty);
                if (_nextX <= _nextY)
                {
                    _tx += _stepX;
                    _nextX = _nextX == Fixed.MaxValue ? _nextX : _nextX + _deltaX;
                }
                else
                {
                    _ty += _stepY;
                    _nextY = _nextY == Fixed.MaxValue ? _nextY : _nextY + _deltaY;
                }

                _s = sOut;
                _entered = true;
                if (_tx < 0 || _ty < 0 || _tx >= _t._width || _ty >= _t._height)
                {
                    return Stop(ShotOutcome.OffMap, sOut);
                }

                var j = (_ty * _t._width) + _tx;
                var z = HeightAt(sOut);
                if (Hedge(fx, fy, _tx, _ty) && z < Max(g, _t._ground[j]) + _t._hedgeHeight)
                {
                    HedgeRolls++;
                    if (_rng.NextFixed() < _chance)
                    {
                        return Stop(ShotOutcome.Hedge, sOut);
                    }
                }

                if (_t._building[i] != _t._building[j] && (WallStops(i, Side(fx, fy, _tx, _ty), z) || WallStops(j, Side(_tx, _ty, fx, fy), z)))
                {
                    return Stop(ShotOutcome.Wall, sOut);
                }
            }

            return false;
        }

        /// <summary>Per axis: the step direction, the distance s between edge crossings and the s of the first one.</summary>
        private (int Step, Fixed Delta, Fixed Next) Axis(Fixed u, Fixed origin, int tile)
        {
            if (u > -Tiny && u < Tiny)
            {
                return (0, Fixed.MaxValue, Fixed.MaxValue);
            }

            var size = _t._tile;
            return u > Fixed.Zero
                ? (1, size / u, (Fixed.FromRaw((tile + 1) * _t._tileRaw) - origin) / u)
                : (-1, size / -u, (Fixed.FromRaw(tile * _t._tileRaw) - origin) / u);
        }

        private static Fixed Min(Fixed a, Fixed b) => a < b ? a : b;

        private static Fixed Max(Fixed a, Fixed b) => a > b ? a : b;

        /// <summary>The wall side of tile (ax, ay) that faces its neighbour (bx, by).</summary>
        private static WallSide Side(int ax, int ay, int bx, int by) =>
            bx > ax ? WallSide.East : bx < ax ? WallSide.West : by > ay ? WallSide.South : WallSide.North;

        private bool Stop(ShotOutcome outcome, Fixed s)
        {
            Stopped = outcome;
            StopS = s;
            return false;
        }

        private bool Hedge(int ax, int ay, int bx, int by)
        {
            var (ox, oy) = bx + by > ax + ay ? (ax, ay) : (bx, by);
            var o = (oy * _t._width) + ox;
            return ax != bx ? _t._hedgeEast[o] : _t._hedgeSouth[o];
        }

        private bool WallStops(int tile, WallSide side, Fixed z)
        {
            if (_t._building[tile] < 0 || z >= _t._roof[tile])
            {
                return false;
            }

            var floor = ((z - _t._ground[tile]) / _t._floorHeight).ToInt();
            return floor < 0 || floor >= Building.MaxFloors || (_t._openings[tile] & (1 << (((int)side * Building.MaxFloors) + floor))) == 0;
        }
    }
}
