using PointBlank.Sim.Movement;
using PointBlank.Sim.Org;

namespace PointBlank.Sim.Orders;

/// <summary>
/// The chain of command: every order, from the player or the AI, enters here through
/// <see cref="IssueOrderInput"/> and reaches its unit after a delay.
/// <list type="bullet">
/// <item>Issue (see <see cref="Issue"/>; never throws): an unknown unit, wrong side or unknown kind is
/// refused and recorded; a move to a point off the map, or an order to a unit with no able man, is <see cref="OrderStatus.Rejected"/> at once
/// (<see cref="OrderNote.OffMap"/>, <see cref="OrderNote.NoAbleMen"/>). Otherwise the delay reasons are read (no effective leader,
/// shaken, broken, out of HQ contact) and the delay rolled (one RNG draw); the order is Pending. Reasons
/// and delay are fixed at issue: a leader who falls while the order is Pending does not re-roll it. A new
/// order to a unit replaces its ticket; a move already under way carries on until the new order is
/// delivered.</item>
/// <item>Delivery (when the count reaches 0): an advance-type order may be refused (broken or shaken
/// squads; never a fall-back or neutral order). A shaken squad may then hesitate (never over a fall-back)
/// for the hesitate delay. Otherwise the order executes.</item>
/// <item>Execution: moves start in <see cref="MovementSystem"/> and stay Executing until it finishes;
/// Hold stops the squad; kinds without behaviour are Done with <see cref="OrderNote.NotImplemented"/>.</item>
/// </list>
/// Register after <see cref="OrgSystem"/> and before <see cref="MovementSystem"/>.
/// </summary>
public sealed class CommandSystem(OrgSystem org, MovementSystem move, CommandRules rules) : ISimSystem
{
    private readonly Dictionary<int, Ticket> _tickets = [];
    private readonly List<IssueRejection> _rejections = [];

    public CommandRules Rules { get; } = rules;

    /// <summary>The unit's latest order and its state, for the UI. Status None when it has had none.</summary>
    public OrderView StatusOf(int unitId) =>
        _tickets.TryGetValue(unitId, out var t)
            ? new OrderView(unitId, t.Order, t.Status, t.Remaining, t.Total, t.Reasons, t.Note, t.Issued)
            : new OrderView(unitId, null, OrderStatus.None, 0, 0, DelayReason.None, OrderNote.None, -1);

    /// <summary>The reasons an order issued now would be delayed (a UI preview).</summary>
    /// <exception cref="InvalidOperationException">There is no such unit.</exception>
    public DelayReason ReasonsFor(int unitId) => Reasons(Find(unitId));

    /// <summary>
    /// An HQ is always in contact. A squad is in contact when an HQ of its side that has an able man
    /// is within the command radius (between the centres of their able men), or when both the squad
    /// and that HQ have a radio. With no such HQ on the map it is out of contact.
    /// </summary>
    public bool InContact(Squad squad)
    {
        if (squad.IsHq)
        {
            return true;
        }

        var centre = Centre(squad);
        if (centre == null)
        {
            return false;
        }

        var r2 = Rules.CommandRadius * Rules.CommandRadius;
        foreach (var hq in org.Squads)
        {
            if (!hq.IsHq || hq.Side != squad.Side || Centre(hq) is not { } c)
            {
                continue;
            }

            var dx = c.X - centre.Value.X;
            var dy = c.Y - centre.Value.Y;
            if ((squad.HasRadio && hq.HasRadio) || (dx * dx) + (dy * dy) <= r2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Orders rejected before reaching a ticket (unknown unit, wrong side, unknown kind), oldest first.
    /// Part of the state hash.
    /// </summary>
    public IReadOnlyList<IssueRejection> Rejections => _rejections;

    /// <summary>
    /// Puts an order into the chain of command (called by <see cref="IssueOrderInput"/>). Never throws
    /// for a bad order, so an AI or UI caller cannot crash the sim: an unknown unit, a unit of the other
    /// side or an unknown kind is refused without touching any ticket and recorded in
    /// <see cref="Rejections"/>; a move off the map, or any order to a unit with no able man, replaces the
    /// unit's ticket with a Rejected one carrying the note. The delay reasons and duration are fixed
    /// here, at issue: if the leader falls (or morale changes) while the order is Pending it is not
    /// re-rolled.
    /// </summary>
    public IssueResult Issue(World world, string side, int unitId, Order order)
    {
        var squad = org.Squads.FirstOrDefault(s => s.Id == unitId);
        var refused = squad == null ? IssueResult.UnknownUnit
            : squad.Side != side ? IssueResult.WrongSide
            : !Enum.IsDefined(order.Kind) ? IssueResult.UnknownKind
            : IssueResult.Accepted;
        if (refused != IssueResult.Accepted)
        {
            _rejections.Add(new IssueRejection(world.Tick, side, unitId, refused));
            return refused;
        }

        var t = new Ticket(order, world.Tick);
        _tickets[unitId] = t;
        if (OrderKinds.IsMove(order.Kind) && !move.IsOnMap(order.X, order.Y))
        {
            t.Status = OrderStatus.Rejected;
            t.Note = OrderNote.OffMap;
            return IssueResult.OffMap;
        }

        if (!squad!.Soldiers.Any(s => s.CanLead))
        {
            t.Status = OrderStatus.Rejected;
            t.Note = OrderNote.NoAbleMen;
            return IssueResult.NoAbleMen;
        }

        t.Reasons = Reasons(squad);
        t.Total = t.Remaining = Rules.RollDelayTicks(t.Reasons, world.Rng);
        t.Status = OrderStatus.Pending;
        return IssueResult.Accepted;
    }

    public void Tick(World world)
    {
        foreach (var squad in org.Squads)
        {
            if (!_tickets.TryGetValue(squad.Id, out var t))
            {
                continue;
            }

            switch (t.Status)
            {
                case OrderStatus.Executing when !move.IsMoving(squad.Id):
                    t.Status = OrderStatus.Done;
                    break;
                case OrderStatus.Pending when --t.Remaining <= 0:
                    t.Remaining = 0;
                    Deliver(world.Rng, squad, t);
                    break;
                case OrderStatus.Hesitating when --t.Remaining <= 0:
                    t.Remaining = 0;
                    Execute(squad, t);
                    break;
            }
        }
    }

    public void AppendState(StateHasher hasher)
    {
        hasher.Add(org.Squads.Count);
        foreach (var squad in org.Squads)
        {
            var has = _tickets.TryGetValue(squad.Id, out var t);
            hasher.Add(has);
            if (!has)
            {
                continue;
            }

            hasher.Add((byte)t!.Order.Kind);
            hasher.Add(t.Order.X);
            hasher.Add(t.Order.Y);
            hasher.Add(t.Issued);
            hasher.Add((byte)t.Status);
            hasher.Add(t.Remaining);
            hasher.Add(t.Total);
            hasher.Add((byte)t.Reasons);
            hasher.Add((byte)t.Note);
        }

        hasher.Add(_rejections.Count);
        foreach (var r in _rejections)
        {
            hasher.Add(r.Tick);
            hasher.Add(r.Side);
            hasher.Add(r.UnitId);
            hasher.Add((byte)r.Reason);
        }
    }

    private static bool Roll(SimRng rng, Fixed chance) => rng.NextFixed() < chance;

    private static (Fixed X, Fixed Y)? Centre(Squad squad)
    {
        var x = Fixed.Zero;
        var y = Fixed.Zero;
        var n = 0;
        foreach (var s in squad.Soldiers)
        {
            if (s.CanLead)
            {
                x += s.X;
                y += s.Y;
                n++;
            }
        }

        return n == 0 ? null : (x / Fixed.FromInt(n), y / Fixed.FromInt(n));
    }

    private Squad Find(int unitId) =>
        org.Squads.FirstOrDefault(s => s.Id == unitId) ?? throw new InvalidOperationException($"No unit {unitId}.");

    private DelayReason Reasons(Squad squad)
    {
        var r = DelayReason.None;
        r |= squad.HasEffectiveLeader ? DelayReason.None : DelayReason.NoLeader;
        r |= squad.Morale switch
        {
            MoraleState.Shaken => DelayReason.Shaken,
            MoraleState.Broken => DelayReason.Broken,
            _ => DelayReason.None,
        };
        r |= InContact(squad) ? DelayReason.None : DelayReason.OutOfContact;
        return r;
    }

    private void Deliver(SimRng rng, Squad squad, Ticket t)
    {
        var cls = OrderKinds.ClassOf(t.Order.Kind);
        var refuse = cls != OrderClass.Advance
            ? Fixed.Zero
            : squad.Morale switch
            {
                MoraleState.Broken => Rules.BrokenRefuseChance,
                MoraleState.Shaken => Rules.ShakenRefuseChance,
                _ => Fixed.Zero,
            };
        if (refuse > Fixed.Zero && Roll(rng, refuse))
        {
            t.Status = OrderStatus.Refused;
            return;
        }

        if (squad.Morale == MoraleState.Shaken && cls != OrderClass.FallBack && Rules.HesitateChance > Fixed.Zero
            && Roll(rng, Rules.HesitateChance) && Rules.HesitateTicks > 0)
        {
            t.Status = OrderStatus.Hesitating;
            t.Remaining = Rules.HesitateTicks;
            return;
        }

        Execute(squad, t);
    }

    private void Execute(Squad squad, Ticket t)
    {
        var kind = t.Order.Kind;
        if (OrderKinds.IsMove(kind))
        {
            var result = move.Execute(new MoveOrder(squad.Id, t.Order.X, t.Order.Y, OrderKinds.MoveModeOf(kind)));
            t.Status = result == MoveResult.Started ? OrderStatus.Executing : OrderStatus.Rejected;
            t.Note = result switch
            {
                MoveResult.NoAbleMen => OrderNote.NoAbleMen,
                MoveResult.OffMap => OrderNote.OffMap,
                MoveResult.Unreachable => OrderNote.Unreachable,
                _ => OrderNote.None,
            };
            return;
        }

        if (kind == OrderKind.Hold)
        {
            move.Stop(squad.Id);
            t.Status = OrderStatus.Done;
            return;
        }

        t.Status = OrderStatus.Done;
        t.Note = OrderNote.NotImplemented;
    }

    private sealed class Ticket(Order order, long issued)
    {
        public Order Order { get; } = order;

        public long Issued { get; } = issued;

        public OrderStatus Status { get; set; }

        public int Remaining { get; set; }

        public int Total { get; set; }

        public DelayReason Reasons { get; set; }

        public OrderNote Note { get; set; }
    }
}
