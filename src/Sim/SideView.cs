using PointBlank.Sim.Artillery;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.UnitAi;

namespace PointBlank.Sim;

/// <summary>
/// A copy of one of the side's own soldiers. <see cref="Rounds"/> is what he has left (-1 without fire rules or a known weapon).
/// <see cref="Suppression"/> is his suppression level (0 to 100) and <see cref="SuppressionState"/> its state.
/// <see cref="Moving"/>: he is walking now (on his squad's move and not held, or on a local move of the unit AI).
/// <see cref="PendingPosture"/>: the posture he is about to take on his own reaction (item 2.4a), or null.
/// </summary>
public readonly record struct SoldierView(
    int Id, string Key, Role Role, int Rank, string WeaponId, int TeamIndex, Fixed X, Fixed Y, Posture Posture, HealthState Health, int Rounds,
    Fixed Suppression, SuppressionState SuppressionState, bool Moving = false, Posture? PendingPosture = null);

/// <summary>
/// A round that passed close to (or hit) one of the side's own soldiers on the last fire tick, as the side knows it
/// (item 2.3): the bearing from him to the shooter as one of 16 sectors (<see cref="Sector"/>, with its unit vector
/// <see cref="DirX"/>, <see cref="DirY"/>) and the distance rounded to the unit AI's <c>distance_band</c>, never the exact
/// origin. <see cref="SourceId"/> is the shooter's id only when he is already a contact the side knows, else -1.
/// <see cref="Friendly"/>: one of the side's own men fired it (a man ducks a round from either side, but does not fire back).
/// <see cref="Explosion"/> (item 2.4): a shell burst within its suppression radius of him; the bearing and distance are to the
/// burst, never to the guns (they are off the map), and <see cref="SourceId"/> is -1.
/// </summary>
public readonly record struct IncomingFireView(
    int SoldierId, long Tick, int SourceId, int Sector, Fixed DirX, Fixed DirY, Fixed Distance, bool Hit, bool Friendly = false, bool Explosion = false);

/// <summary>
/// A copy of one of the side's own squads. <see cref="LeaderId"/> is -1 while the post is vacant. <see cref="Ammo"/>
/// reads <see cref="AmmoState.OutOfAmmo"/> when none of its able, armed men has a round left. <see cref="SuppressionLevel"/>
/// is the mean suppression of its able men (0 to 100) and <see cref="Suppression"/> the squad's state (Pinned: it holds
/// advance orders). <see cref="Morale"/> is the morale state (Pinned mirrors the suppression state unless Broken or Routing),
/// <see cref="MoraleLevel"/> its level (0 to 100), <see cref="MoraleReasons"/> why, and <see cref="MoraleTopReasons"/> the three
/// most important of those reasons first (item 2.2). Enemy suppression and morale are never shown (fog of war).
/// </summary>
public readonly record struct SquadView(
    int Id,
    string OrgId,
    bool IsHq,
    bool HasRadio,
    MoraleState Morale,
    int LeaderId,
    int TicksToSuccession,
    bool IsMoving,
    AmmoState Ammo,
    IReadOnlyList<SoldierView> Soldiers,
    Fixed SuppressionLevel,
    SuppressionState Suppression,
    Fixed MoraleLevel,
    MoraleReason MoraleReasons,
    IReadOnlyList<MoraleReason> MoraleTopReasons,
    bool IsHalting = false);

/// <summary>
/// What one side may know, and the only API for UI and AI code (project rule, see CLAUDE.md). It
/// shows the side's own units in full, as copies; enemies only as <see cref="ContactView"/> copies of
/// what the side has spotted; and order status and issuing for its own units only. It hands out no
/// live sim object. A unit of the other side is answered exactly like a unit that does not exist.
/// Every call reads the world afresh.
/// </summary>
public sealed class SideView
{
    private readonly World _world;
    private readonly OrgSystem _org;
    private readonly MovementSystem? _move;
    private readonly CommandSystem? _command;
    private readonly SpottingSystem? _spotting;
    private readonly FireSystem? _fire;
    private readonly SuppressionSystem? _suppression;
    private readonly MoraleSystem? _morale;
    private readonly UnitAiSystem? _ai;
    private readonly ArtillerySystem? _artillery;

    internal SideView(World world, string side)
    {
        _world = world;
        Side = side;
        _org = world.FindSystem<OrgSystem>() ?? throw new InvalidOperationException("The world has no OrgSystem.");
        _move = world.FindSystem<MovementSystem>();
        _command = world.FindSystem<CommandSystem>();
        _spotting = world.FindSystem<SpottingSystem>();
        _fire = world.FindSystem<FireSystem>();
        _suppression = world.FindSystem<SuppressionSystem>();
        _morale = world.FindSystem<MoraleSystem>();
        _ai = world.FindSystem<UnitAiSystem>();
        _artillery = world.FindSystem<ArtillerySystem>();
    }

    public string Side { get; }

    public long Tick => _world.Tick;

    /// <summary>
    /// A fresh copy of the ground (item 2.5), or null for a world without a map. Both sides know the map. It copies every
    /// tile, so a caller keeps it rather than asking every frame.
    /// </summary>
    public MapView? Map => _world.FindSystem<MapSystem>() is { } m ? MapView.Of(m.Map) : null;

    /// <summary>The side's own squads, in spawn order.</summary>
    public IReadOnlyList<SquadView> Squads => _org.Squads.Where(q => q.Side == Side).Select(ViewOf).ToArray();

    /// <summary>One of the side's own squads, or null (also for an enemy squad).</summary>
    public SquadView? Squad(int unitId) => Own(unitId) is { } q ? ViewOf(q) : null;

    /// <summary>The enemies the side has spotted, last-known data only (<see cref="SpottingSystem"/>).</summary>
    public IReadOnlyList<ContactView> KnownEnemies => _spotting?.KnownEnemies(Side) ?? [];

    /// <summary>
    /// Whether one of the side's own able men could see a target with <paramref name="target"/> posture at the point, by the
    /// line of sight and maximum range spotting uses (item 2.6). It says nothing about who is there. False for any other
    /// man, off the map, or without spotting.
    /// </summary>
    public bool CanSee(int soldierId, Fixed x, Fixed y, Posture target) =>
        _spotting != null
        && _org.Squads.Where(q => q.Side == Side).SelectMany(q => q.Soldiers).FirstOrDefault(s => s.Id == soldierId) is { } s
        && _spotting.Sees(s, x, y, target);

    /// <summary>The rules of engagement of one of the side's own units (item 2.3), or null (also for an enemy unit).</summary>
    public RoeMode? RoeOf(int unitId) => _command != null && Own(unitId) != null ? _command.RoeOf(unitId) : null;

    /// <summary>
    /// The rounds that passed close to or hit the side's own men on the last fire tick, fuzzed (see
    /// <see cref="IncomingFireView"/>). Empty without a <see cref="FireSystem"/> and a <see cref="UnitAiSystem"/> (whose
    /// rules give the distance band). The leak, by design: the side learns that someone fired and roughly from where,
    /// as a soldier hears it; many men under fire could triangulate a little, as real troops do.
    /// </summary>
    public IReadOnlyList<IncomingFireView> IncomingFire
    {
        get
        {
            if (_fire == null || _ai == null)
            {
                return [];
            }

            var list = new List<IncomingFireView>();
            var bursts = _artillery?.LastImpacts ?? [];
            if (_fire.NearMisses.Count == 0 && _fire.Shots.Count == 0 && bursts.Count == 0)
            {
                return list;
            }

            var sides = new Dictionary<int, (bool Own, Soldier Soldier)>();
            foreach (var q in _org.Squads)
            {
                foreach (var m in q.Soldiers)
                {
                    sides[m.Id] = (q.Side == Side, m);
                }
            }

            void Add(int soldierId, int firerId, Fixed ox, Fixed oy, long tick, bool hit)
            {
                // Only rounds at the side's own men; its own men's rounds are marked friendly (known to be friendly).
                if (!sides.TryGetValue(soldierId, out var v) || !v.Own)
                {
                    return;
                }

                var friendly = sides.TryGetValue(firerId, out var f) && f.Own;

                var s = v.Soldier;
                var dx = ox - s.X;
                var dy = oy - s.Y;
                var sector = Bearings.SectorOf(dx, dy);
                var (ux, uy) = Bearings.Dir(sector);
                var band = Bearings.Band(Fixed.Sqrt((dx * dx) + (dy * dy)), _ai.Rules.DistanceBand);
                var source = !friendly && _spotting?.ContactOf(Side, firerId) != null ? firerId : -1;
                list.Add(new IncomingFireView(soldierId, tick, source, sector, ux, uy, band, hit, friendly));
            }

            foreach (var n in _fire.NearMisses)
            {
                Add(n.SoldierId, n.FirerId, n.OriginX, n.OriginY, n.Tick, false);
            }

            foreach (var shot in _fire.Shots)
            {
                if (shot.Outcome == ShotOutcome.Hit)
                {
                    Add(shot.HitSoldierId, shot.FirerId, shot.OriginX, shot.OriginY, shot.Tick, true);
                }
            }

            // Shell bursts (item 2.4): every able man of the side within the burst's radius notices it.
            foreach (var b in bursts)
            {
                foreach (var q in _org.Squads)
                {
                    if (q.Side != Side)
                    {
                        continue;
                    }

                    foreach (var s in q.Soldiers)
                    {
                        var dx = b.X - s.X;
                        var dy = b.Y - s.Y;
                        var d2 = (dx * dx) + (dy * dy);
                        if (!s.CanLead || d2 > b.Radius * b.Radius)
                        {
                            continue;
                        }

                        var sector = Bearings.SectorOf(dx, dy);
                        var (ux, uy) = Bearings.Dir(sector);
                        var band = Bearings.Band(Fixed.Sqrt(d2), _ai.Rules.DistanceBand);
                        list.Add(new IncomingFireView(s.Id, b.Tick, -1, sector, ux, uy, band, false, false, true));
                    }
                }
            }

            return list;
        }
    }

    /// <summary>The side's own fire missions (item 2.4), oldest first: status, ETA and rounds left. Empty without artillery.</summary>
    public IReadOnlyList<FireMissionView> FireMissions => _artillery?.MissionsOf(Side, _world.Tick) ?? [];

    /// <summary>Fire missions the side may still call (0 without artillery or a battery).</summary>
    public int MissionsLeft => _artillery?.MissionsLeft(Side) ?? 0;

    /// <summary>Recent shell bursts of either side, oldest first: loud, so everyone knows where they fell.</summary>
    public IReadOnlyList<ShellImpactView> ShellImpacts => _artillery?.Impacts ?? [];

    /// <summary>The order state of one of the side's own units, or null (also for an enemy unit).</summary>
    public OrderView? StatusOf(int unitId) => _command != null && Own(unitId) != null ? _command.StatusOf(unitId) : null;

    /// <summary>
    /// The latest rules-of-engagement change of one of the side's own units (item 2.5a), which travels beside its other
    /// order, or null (also for an enemy unit).
    /// </summary>
    public OrderView? RoeStatusOf(int unitId) => _command != null && Own(unitId) != null ? _command.RoeStatusOf(unitId) : null;

    /// <summary>Why an order to one of the side's own units would be delayed now, or null (also for an enemy unit).</summary>
    public DelayReason? ReasonsFor(int unitId) => _command != null && Own(unitId) is { } q ? _command.ReasonsFor(q.Id) : null;

    /// <summary>
    /// Queues an order for one of the side's own units; it enters the chain of command at the earliest
    /// tick the world accepts (an <see cref="IssueOrderInput"/>, so replays record it). Accepted means
    /// queued: the result of the issue itself shows in <see cref="StatusOf"/>. A unit that is not the
    /// side's own gives <see cref="IssueResult.UnknownUnit"/> and nothing is queued.
    /// </summary>
    public IssueResult Issue(int unitId, Order order)
    {
        if (Own(unitId) == null || _command == null)
        {
            return IssueResult.UnknownUnit;
        }

        if (!Enum.IsDefined(order.Kind))
        {
            return IssueResult.UnknownKind;
        }

        _world.Schedule(_world.EarliestInputTick, new IssueOrderInput(Side, unitId, order));
        return IssueResult.Accepted;
    }

    /// <summary>
    /// The side's own orders refused before reaching a unit, oldest first. An order to an enemy unit
    /// reads <see cref="IssueResult.UnknownUnit"/>, as for a missing one.
    /// </summary>
    public IReadOnlyList<IssueRejection> Rejections =>
        _command == null
            ? []
            : _command.Rejections.Where(r => r.Side == Side)
                .Select(r => r.Reason == IssueResult.WrongSide ? r with { Reason = IssueResult.UnknownUnit } : r).ToArray();

    private Squad? Own(int unitId) => _org.Squads.FirstOrDefault(q => q.Id == unitId && q.Side == Side);

    private SquadView ViewOf(Squad q) => new(
        q.Id,
        q.OrgId,
        q.IsHq,
        q.HasRadio,
        q.Morale,
        q.Leader?.Id ?? -1,
        q.TicksToSuccession,
        _move?.IsMoving(q.Id) ?? false,
        _fire?.AmmoOf(q) ?? AmmoState.Ok,
        q.Soldiers.Select(s => new SoldierView(
            s.Id, s.Key, s.Role, s.Rank, s.WeaponId, s.TeamIndex, s.X, s.Y, s.Posture, s.Health, _fire?.RoundsOf(s.Id) ?? -1,
            _suppression?.LevelOf(s.Id) ?? Fixed.Zero, _suppression?.StateOf(s.Id) ?? SuppressionState.None,
            _move?.IsSoldierMoving(s.Id) ?? false, s.PendingPosture)).ToArray(),
        _suppression?.SquadLevelOf(q.Id) ?? Fixed.Zero,
        _suppression?.SquadStateOf(q.Id) ?? SuppressionState.None,
        q.MoraleLevel,
        _morale?.ReasonsOf(q.Id) ?? MoraleReason.None,
        MoraleReasons.Top(_morale?.ReasonsOf(q.Id) ?? MoraleReason.None, 3),
        _move?.IsHalting(q.Id) ?? false);
}
