using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Suppression;

namespace PointBlank.Sim.Artillery;

/// <summary>
/// Off-map artillery (item 2.4). Numbers in <see cref="BatteryRules"/> (table <c>artillery</c>).
/// <list type="bullet">
/// <item>Call for fire: an <see cref="OrderKind.CallFire"/> order travels down the chain of command like any order, and at
/// delivery <see cref="CommandSystem"/> hands it to <see cref="Request"/>. Only an HQ or a squad with a radio can call
/// (<see cref="OrderNote.NotObserver"/>), one of its able men must see the point (<see cref="OrderNote.NoLineOfSight"/>,
/// a standing-height point seen from his own posture), and the side needs a battery with missions left
/// (<see cref="OrderNote.NoMissionsLeft"/>). No forward-observer role exists yet: an HQ or a radio is the observer.</item>
/// <item>A mission: the first round lands <c>call_time</c> + <c>flight_time</c> after the call is delivered; its
/// <c>guns</c> × <c>rounds_per_gun</c> rounds land evenly over <c>duration</c>. The mean point of impact is the called
/// point plus an aim error (one draw per axis, at the call) and each round adds its own scatter. Normal draws are the
/// sum of three uniforms, scaled to unit variance.</item>
/// <item>A burst (at ground level): every man (able or wounded) within <c>casualty_radius</c> may be hit: <c>lethal_chance</c>
/// inside <c>lethal_radius</c>, falling linearly to 0 at <c>casualty_radius</c>; × his posture factor; inside a building
/// the burst is not in, × <c>building_factor</c>; else, beyond <c>direct_hit_radius</c>, × <c>shelter_factor</c> when a hedge,
/// wall or the ground blocks the line from the burst to him, and × (1 − <c>cover_weight</c> × his tile's cover). One draw
/// per man in range, then a casualty is killed, incapacitated or wounded (a second wound incapacitates). Then suppression
/// (<see cref="SuppressionSystem.AddExplosion"/>, both sides) and a morale shock (<see cref="MoraleSystem.AddShock"/>) for
/// every squad with an able man within <c>morale_radius</c>, falling linearly with its nearest man's distance.</item>
/// <item>Seen by both sides: <see cref="Impacts"/> (bursts within <c>impact_memory</c>), and the bursts of the last tick
/// (<see cref="LastImpacts"/>) feed <see cref="SideView.IncomingFire"/> as explosion events.</item>
/// <item>Not yet: smoke rounds (item 5.5), building damage (item 4.5: <see cref="Structures"/> is told of each burst),
/// adjust fire.</item>
/// </list>
/// Construct after <see cref="CommandSystem"/> (it attaches itself) and register after <c>FireSystem</c>, before
/// <see cref="SuppressionSystem"/>.
/// </summary>
public sealed class ArtillerySystem : ISimSystem
{
    private readonly GameMap _map;
    private readonly OrgSystem _org;
    private readonly LineOfSight _los;
    private readonly LineOfSight _blast; // hedges as solid walls: sight through a hedge is not shelter from fragments
    private readonly SuppressionSystem? _suppression;
    private readonly MoraleSystem? _morale;
    private readonly List<Mission> _missions = [];
    private readonly SortedDictionary<string, int> _used = new(StringComparer.Ordinal);
    private readonly List<Impact> _impacts = [];
    private readonly List<Impact> _last = [];

    public ArtillerySystem(
        GameMap map, OrgSystem org, LineOfSight los, ArtilleryRules rules, CommandSystem? command = null,
        SuppressionSystem? suppression = null, MoraleSystem? morale = null)
    {
        _map = map;
        _org = org;
        _los = los;
        _blast = new LineOfSight(map, los.Rules.SolidHedges());
        Rules = rules;
        _suppression = suppression;
        _morale = morale;
        if (command != null)
        {
            command.Artillery = this;
        }
    }

    public ArtilleryRules Rules { get; }

    /// <summary>The hook for building damage (item 4.5); null until then.</summary>
    internal IStructureDamage? Structures { get; set; }

    /// <summary>Bursts within their battery's impact memory, oldest first. Seen by everyone.</summary>
    internal IReadOnlyList<ShellImpactView> Impacts => _impacts.Select(i => new ShellImpactView(i.Tick, i.X, i.Y)).ToArray();

    /// <summary>
    /// The bursts of the last tick, with the radius over which men notice them (the suppression radius) and the height of
    /// their smoke and earth.
    /// </summary>
    internal IReadOnlyList<(Fixed X, Fixed Y, long Tick, Fixed Radius, Fixed Plume)> LastImpacts =>
        _last.Select(i => (i.X, i.Y, i.Tick, i.Radius, i.Plume)).ToArray();

    /// <summary>The side's missions, oldest first.</summary>
    internal IReadOnlyList<FireMissionView> MissionsOf(string side, long tick) =>
        _missions.Where(m => m.Side == side).Select(m => new FireMissionView(
            m.Id, m.CallerId, m.TargetX, m.TargetY, m.Status,
            m.Status == MissionStatus.Incoming ? (int)Math.Max(0, m.FirstTick - tick) : 0,
            m.Total - m.Fired, m.Total, m.CalledTick)).ToArray();

    /// <summary>Missions the side may still call (0 without a battery).</summary>
    internal int MissionsLeft(string side) =>
        Rules.For(side) is { } b ? Math.Max(0, b.Missions - (_used.TryGetValue(side, out var n) ? n : 0)) : 0;

    /// <summary>A delivered CallFire order: starts a mission, or says why not (the note).</summary>
    internal OrderNote Request(World world, Squad squad, Order order)
    {
        var b = Rules.For(squad.Side);
        if (b == null)
        {
            return OrderNote.NoMissionsLeft;
        }

        if (!squad.IsHq && !squad.HasRadio)
        {
            return OrderNote.NotObserver;
        }

        var target = new Viewpoint(order.X, order.Y, Posture.Standing);
        if (!squad.Soldiers.Any(s => s.CanLead && _los.Check(new Viewpoint(s.X, s.Y, s.Posture), target).Visible))
        {
            return OrderNote.NoLineOfSight;
        }

        if (MissionsLeft(squad.Side) == 0)
        {
            return OrderNote.NoMissionsLeft;
        }

        _used[squad.Side] = (_used.TryGetValue(squad.Side, out var n) ? n : 0) + 1;
        var ax = order.X + (Normal(world.Rng) * b.AimError);
        var ay = order.Y + (Normal(world.Rng) * b.AimError);
        _missions.Add(new Mission
        {
            Id = _missions.Count,
            Side = squad.Side,
            CallerId = squad.Id,
            TargetX = order.X,
            TargetY = order.Y,
            AimX = ax,
            AimY = ay,
            CalledTick = world.Tick,
            FirstTick = world.Tick + b.CallTicks + b.FlightTicks,
            Total = b.Rounds,
        });
        return OrderNote.None;
    }

    public void Tick(World world)
    {
        _last.Clear();
        _impacts.RemoveAll(i => world.Tick - i.Tick > i.Memory);
        foreach (var m in _missions)
        {
            if (m.Status == MissionStatus.Complete)
            {
                continue;
            }

            var b = Rules.For(m.Side)!;
            while (m.Fired < m.Total && world.Tick >= m.FirstTick + ((long)m.Fired * b.DurationTicks / m.Total))
            {
                m.Status = MissionStatus.Firing;
                Burst(world, b, m.AimX + (Normal(world.Rng) * b.Scatter), m.AimY + (Normal(world.Rng) * b.Scatter));
                m.Fired++;
            }

            if (m.Fired == m.Total)
            {
                m.Status = MissionStatus.Complete;
            }
        }
    }

    public void AppendState(StateHasher hasher)
    {
        hasher.Add(_missions.Count);
        foreach (var m in _missions)
        {
            hasher.Add(m.Id);
            hasher.Add(m.Side);
            hasher.Add(m.CallerId);
            hasher.Add(m.TargetX);
            hasher.Add(m.TargetY);
            hasher.Add(m.AimX);
            hasher.Add(m.AimY);
            hasher.Add((byte)m.Status);
            hasher.Add(m.CalledTick);
            hasher.Add(m.FirstTick);
            hasher.Add(m.Total);
            hasher.Add(m.Fired);
        }

        hasher.Add(_used.Count);
        foreach (var (side, n) in _used)
        {
            hasher.Add(side);
            hasher.Add(n);
        }

        hasher.Add(_impacts.Count);
        foreach (var i in _impacts)
        {
            hasher.Add(i.Tick);
            hasher.Add(i.X);
            hasher.Add(i.Y);
        }

        hasher.Add(_last.Count);
        foreach (var i in _last)
        {
            hasher.Add(i.X);
            hasher.Add(i.Y);
            hasher.Add(i.Radius);
        }
    }

    /// <summary>A draw with mean 0 and variance 1: (u1 + u2 + u3 − 1.5) × 2, from three RNG draws.</summary>
    internal static Fixed Normal(SimRng rng)
    {
        var sum = rng.NextFixed() + rng.NextFixed() + rng.NextFixed();
        return (sum - Fixed.FromRatio(3, 2)) * Fixed.FromInt(2);
    }

    /// <summary>One HE burst at (x, y), pulled onto the map.</summary>
    internal void Burst(World world, BatteryRules b, Fixed x, Fixed y)
    {
        var w = _map.TileSize * Fixed.FromInt(_map.Width);
        var h = _map.TileSize * Fixed.FromInt(_map.Height);
        var eps = Fixed.FromRatio(1, 1000);
        x = x < Fixed.Zero ? Fixed.Zero : x >= w ? w - eps : x;
        y = y < Fixed.Zero ? Fixed.Zero : y >= h ? h - eps : y;
        var impact = new Impact(world.Tick, x, y, b.SuppressionRadius, b.ImpactMemoryTicks, b.PlumeHeight);
        _impacts.Add(impact);
        _last.Add(impact);

        var burstBuilding = BuildingAt(x, y);
        var burst = new Viewpoint(x, y, Posture.Prone);
        var cas2 = b.CasualtyRadius * b.CasualtyRadius;
        foreach (var q in _org.Squads)
        {
            foreach (var s in q.Soldiers)
            {
                if (!s.CanLead)
                {
                    continue;
                }

                var d2 = Dist2(s.X, s.Y, x, y);
                if (d2 > cas2)
                {
                    continue;
                }

                var d = Fixed.Sqrt(d2);
                var p = d <= b.LethalRadius || b.CasualtyRadius == b.LethalRadius
                    ? b.LethalChance
                    : b.LethalChance * (b.CasualtyRadius - d) / (b.CasualtyRadius - b.LethalRadius);
                p *= s.Posture switch
                {
                    Posture.Prone => b.ProneFactor,
                    Posture.Kneeling => b.KneelingFactor,
                    _ => Fixed.One,
                };
                var inside = BuildingAt(s.X, s.Y);
                if (inside != null)
                {
                    p *= inside == burstBuilding ? Fixed.One : b.BuildingFactor;
                }
                else if (d > b.DirectHitRadius)
                {
                    if (!_blast.Check(burst, new Viewpoint(s.X, s.Y, s.Posture)).Visible)
                    {
                        p *= b.ShelterFactor;
                    }

                    p *= Fixed.One - (b.CoverWeight * Rules.Cover(_map.GetTerrain(TileX(s.X), TileY(s.Y))));
                }

                if (world.Rng.NextFixed() < p)
                {
                    Wound(world.Rng, b, q, s);
                }
            }
        }

        _suppression?.AddExplosion(world, x, y, b.Suppression, b.SuppressionRadius);
        if (_morale != null)
        {
            foreach (var q in _org.Squads)
            {
                var best = Fixed.MaxValue;
                foreach (var s in q.Soldiers)
                {
                    var d2 = Dist2(s.X, s.Y, x, y);
                    if (s.CanLead && d2 < best)
                    {
                        best = d2;
                    }
                }

                var cower = _morale.Rules.CowerRadius;
                if (best <= cower * cower)
                {
                    _morale.NoteShelled(q.Id, world.Tick);
                }

                if (b.MoraleRadius > Fixed.Zero && best <= b.MoraleRadius * b.MoraleRadius)
                {
                    _morale.AddShock(q.Id, b.MoraleShock * (Fixed.One - (Fixed.Sqrt(best) / b.MoraleRadius)));
                }
            }
        }

        Structures?.OnImpact(world, x, y, burstBuilding);
    }

    private static void Wound(SimRng rng, BatteryRules b, Squad q, Soldier s)
    {
        var r = rng.NextFixed();
        var health = r < b.KillChance ? HealthState.Dead
            : r < b.KillChance + b.IncapacitateChance ? HealthState.Incapacitated
            : s.Health == HealthState.Wounded ? HealthState.Incapacitated
            : HealthState.Wounded;
        q.SetHealth(s.Id, health);
    }

    private static Fixed Dist2(Fixed ax, Fixed ay, Fixed bx, Fixed by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    private int TileX(Fixed x) => Math.Clamp((x / _map.TileSize).ToInt(), 0, _map.Width - 1);

    private int TileY(Fixed y) => Math.Clamp((y / _map.TileSize).ToInt(), 0, _map.Height - 1);

    private Building? BuildingAt(Fixed x, Fixed y) => _map.BuildingAt(TileX(x), TileY(y));

    private sealed record Impact(long Tick, Fixed X, Fixed Y, Fixed Radius, int Memory, Fixed Plume);

    private sealed class Mission
    {
        public int Id { get; init; }

        public string Side { get; init; } = "";

        public int CallerId { get; init; }

        public Fixed TargetX { get; init; }

        public Fixed TargetY { get; init; }

        public Fixed AimX { get; init; }

        public Fixed AimY { get; init; }

        public long CalledTick { get; init; }

        public long FirstTick { get; init; }

        public int Total { get; init; }

        public int Fired { get; set; }

        public MissionStatus Status { get; set; }
    }
}
