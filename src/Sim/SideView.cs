using PointBlank.Sim.Fire;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim;

/// <summary>A copy of one of the side's own soldiers. <see cref="Rounds"/> is what he has left (-1 without fire rules or a known weapon).</summary>
public readonly record struct SoldierView(
    int Id, string Key, Role Role, int Rank, string WeaponId, int TeamIndex, Fixed X, Fixed Y, Posture Posture, HealthState Health, int Rounds);

/// <summary>
/// A copy of one of the side's own squads. <see cref="LeaderId"/> is -1 while the post is vacant. <see cref="Ammo"/>
/// reads <see cref="AmmoState.OutOfAmmo"/> when none of its able, armed men has a round left.
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
    IReadOnlyList<SoldierView> Soldiers);

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

    internal SideView(World world, string side)
    {
        _world = world;
        Side = side;
        _org = world.FindSystem<OrgSystem>() ?? throw new InvalidOperationException("The world has no OrgSystem.");
        _move = world.FindSystem<MovementSystem>();
        _command = world.FindSystem<CommandSystem>();
        _spotting = world.FindSystem<SpottingSystem>();
        _fire = world.FindSystem<FireSystem>();
    }

    public string Side { get; }

    public long Tick => _world.Tick;

    /// <summary>The side's own squads, in spawn order.</summary>
    public IReadOnlyList<SquadView> Squads => _org.Squads.Where(q => q.Side == Side).Select(ViewOf).ToArray();

    /// <summary>One of the side's own squads, or null (also for an enemy squad).</summary>
    public SquadView? Squad(int unitId) => Own(unitId) is { } q ? ViewOf(q) : null;

    /// <summary>The enemies the side has spotted, last-known data only (<see cref="SpottingSystem"/>).</summary>
    public IReadOnlyList<ContactView> KnownEnemies => _spotting?.KnownEnemies(Side) ?? [];

    /// <summary>The order state of one of the side's own units, or null (also for an enemy unit).</summary>
    public OrderView? StatusOf(int unitId) => _command != null && Own(unitId) != null ? _command.StatusOf(unitId) : null;

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
            s.Id, s.Key, s.Role, s.Rank, s.WeaponId, s.TeamIndex, s.X, s.Y, s.Posture, s.Health, _fire?.RoundsOf(s.Id) ?? -1)).ToArray());
}
