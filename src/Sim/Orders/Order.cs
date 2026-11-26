using PointBlank.Sim.Movement;

namespace PointBlank.Sim.Orders;

/// <summary>
/// The one order vocabulary, shared by the player and the AI. Values are hashed and stored in
/// replays, so never renumber. Implemented today: the four moves, <see cref="Fire"/> and <see cref="Hold"/>; the others
/// are delivered through the chain of command and acknowledged as <see cref="OrderNote.NotImplemented"/>.
/// </summary>
public enum OrderKind : byte
{
    Move = 0,
    MoveFast = 1,
    Sneak = 2,

    /// <summary>A move (at MoveFast speed and posture) that a squad never refuses.</summary>
    FallBack = 3,
    Defend = 4,
    Ambush = 5,
    /// <summary>Fire at a known contact (<see cref="Order.TargetId"/>) or at a point (area fire, TargetId -1).</summary>
    Fire = 6,
    Smoke = 7,

    /// <summary>Stop where you are (also "Stop").</summary>
    Hold = 8,
}

/// <summary>How refusal rules treat an order kind.</summary>
public enum OrderClass : byte
{
    /// <summary>May be refused by a shaken or broken squad.</summary>
    Advance = 0,

    /// <summary>Never refused.</summary>
    Neutral = 1,

    /// <summary>Never refused and never hesitated over.</summary>
    FallBack = 2,
}

/// <summary>
/// An order. X and Y are the target point in metres from the map's north-west corner (ignored by Hold).
/// <see cref="TargetId"/> is the enemy soldier a Fire order aims at (a contact of the side), or -1.
/// </summary>
public sealed record Order(OrderKind Kind, Fixed X, Fixed Y, int TargetId = -1);

public static class OrderKinds
{
    /// <summary>
    /// Every move except FallBack counts as an advance for now, even a move away from the enemy:
    /// the sim has no notion of "towards the enemy" yet. A later item may classify moves by direction.
    /// </summary>
    public static OrderClass ClassOf(OrderKind kind) => kind switch
    {
        OrderKind.Move or OrderKind.MoveFast or OrderKind.Sneak => OrderClass.Advance,
        OrderKind.FallBack => OrderClass.FallBack,
        _ => OrderClass.Neutral,
    };

    public static bool IsMove(OrderKind kind) => kind is OrderKind.Move or OrderKind.MoveFast or OrderKind.Sneak or OrderKind.FallBack;

    /// <exception cref="ArgumentException">The kind is not a move.</exception>
    public static MoveMode MoveModeOf(OrderKind kind) => kind switch
    {
        OrderKind.Move => MoveMode.Move,
        OrderKind.MoveFast or OrderKind.FallBack => MoveMode.MoveFast,
        OrderKind.Sneak => MoveMode.Sneak,
        _ => throw new ArgumentException($"{kind} is not a move."),
    };
}

/// <summary>Where an order is in the pipeline. Values are hashed, so never renumber.</summary>
public enum OrderStatus : byte
{
    /// <summary>The unit has had no order.</summary>
    None = 0,

    /// <summary>Travelling down the chain of command; <see cref="OrderView.TicksRemaining"/> counts down.</summary>
    Pending = 1,

    /// <summary>Delivered to a shaken unit that hesitates before obeying; ticks remaining counts down.</summary>
    Hesitating = 2,

    Refused = 3,

    /// <summary>Being carried out (a move under way).</summary>
    Executing = 4,

    /// <summary>Finished, or acknowledged (see <see cref="OrderView.Note"/>).</summary>
    Done = 5,

    /// <summary>Could not be carried out; <see cref="OrderView.Note"/> says why.</summary>
    Rejected = 6,

    /// <summary>
    /// An advance-type move held because the squad is pinned (item 2.1): it is not moving, and the move
    /// starts again, to the same point, when the squad is no longer pinned.
    /// </summary>
    Held = 7,
}

/// <summary>Why an order's delay is longer than the base delay. Values are hashed, so never renumber.</summary>
[Flags]
public enum DelayReason : byte
{
    None = 0,
    NoLeader = 1,
    Shaken = 2,
    Broken = 4,
    OutOfContact = 8,

    /// <summary>Morale below full (item 2.2): <c>low_morale_delay</c> × (100 − level) / 100 extra.</summary>
    LowMorale = 16,
}

/// <summary>Extra detail on a finished or rejected order. Values are hashed, so never renumber.</summary>
public enum OrderNote : byte
{
    None = 0,

    /// <summary>The order kind was delivered and acknowledged but has no behaviour yet.</summary>
    NotImplemented = 1,

    /// <summary>The target point is outside the map.</summary>
    OffMap = 2,

    /// <summary>No path leads to the target.</summary>
    Unreachable = 3,

    /// <summary>No man in the unit can carry the order out.</summary>
    NoAbleMen = 4,

    /// <summary>A Fire order's target is not a contact the unit's side knows.</summary>
    UnknownTarget = 5,

    /// <summary>No able, armed man in the unit has a round left.</summary>
    OutOfAmmo = 6,

    /// <summary>A Fire order's target fell or was forgotten.</summary>
    TargetGone = 7,

    /// <summary>A Fire order's target is beyond every armed man's range.</summary>
    OutOfRange = 8,

    /// <summary>Refused because the squad is broken (or, on a hesitation or refusal, shaken: <see cref="MoraleShaken"/>).</summary>
    MoraleBroken = 9,

    /// <summary>Refused or hesitated over because the squad is shaken (or pinned or rallying, which count as shaken).</summary>
    MoraleShaken = 10,

    /// <summary>Refused or cancelled because the squad is routing: it obeys only FallBack.</summary>
    Routing = 11,

    /// <summary>On a <see cref="OrderStatus.Held"/> order: the squad is pinned by suppression.</summary>
    Pinned = 12,
}

/// <summary>What <see cref="CommandSystem.Issue"/> did with an order. Values are hashed, so never renumber.</summary>
public enum IssueResult : byte
{
    /// <summary>The order entered the chain of command (Pending).</summary>
    Accepted = 0,

    /// <summary>No unit has that id; nothing changed.</summary>
    UnknownUnit = 1,

    /// <summary>The unit belongs to the other side; its orders were not touched.</summary>
    WrongSide = 2,

    /// <summary>The order kind is not in the vocabulary; nothing changed.</summary>
    UnknownKind = 3,

    /// <summary>The move's target is off the map; the unit's ticket is Rejected (<see cref="OrderNote.OffMap"/>).</summary>
    OffMap = 4,

    /// <summary>The unit has no able man; its ticket is Rejected (<see cref="OrderNote.NoAbleMen"/>).</summary>
    NoAbleMen = 5,
}

/// <summary>An order rejected before it reached any unit's ticket (unknown unit, wrong side, unknown kind).</summary>
public readonly record struct IssueRejection(long Tick, string Side, int UnitId, IssueResult Reason);

/// <summary>
/// What the UI shows for a unit: its latest order, where it is, and why it is slow. TicksRemaining is
/// the number of further steps until the order is delivered (Pending) or obeyed (Hesitating), else 0.
/// TotalDelayTicks is the chain-of-command delay rolled when the order was issued.
/// </summary>
public sealed record OrderView(
    int UnitId, Order? Order, OrderStatus Status, int TicksRemaining, int TotalDelayTicks, DelayReason Reasons,
    OrderNote Note, long IssuedTick);

/// <summary>An order from the player or the AI to one of its side's units, entering the chain of command when it applies.</summary>
public sealed class IssueOrderInput(string side, int unitId, Order order) : ISimInput
{
    public string Side { get; } = side;

    public int UnitId { get; } = unitId;

    public Order Order { get; } = order;

    public void Apply(World world) => world.GetSystem<CommandSystem>().Issue(world, Side, UnitId, Order);
}
