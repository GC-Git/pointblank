using PointBlank.Sim.Fire;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Org;
using PointBlank.Sim.Suppression;

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
/// Hold stops the squad; Fire starts in <see cref="FireSystem"/> (Rejected with its note if it cannot) and stays
/// Executing until fire ends, then Done with the end note; any other delivered order stops the squad's fire;
/// kinds without behaviour (and Fire without a FireSystem) are Done with <see cref="OrderNote.NotImplemented"/>. SetRoe (item 2.3)
/// sets the squad's <see cref="RoeMode"/> (<see cref="RoeOf"/>, hashed) and touches nothing else; a bad mode is Rejected
/// (<see cref="OrderNote.InvalidRoe"/>). It travels on a ticket of its own (<see cref="RoeStatusOf"/>, item 2.5a), with
/// the same delay and morale rules, so it never replaces a pending or running order, and no order replaces it. A delivered order stops only the player's fire order, never the unit AI's fire.</item>
/// <item>Suppression (item 2.1, optional <see cref="SuppressionSystem"/>): an advance-type move (Move, MoveFast,
/// Sneak) delivered to a pinned squad, or under way when it becomes pinned, is <see cref="OrderStatus.Held"/>: the
/// move stops, and starts again to the same point once the squad is no longer pinned. Fall-back, Hold and Fire
/// are never held. Morale and suppression stay separate: refusal and hesitation (morale) are decided at delivery,
/// and only an order that gets past them can be held (note <see cref="OrderNote.Pinned"/>).</item>
/// <item>Morale (item 2.2): the rules read <see cref="Squad.Morale"/> through <see cref="Band"/> (Pinned and
/// Rallying count as Shaken, Routing as Broken) and add <see cref="DelayReason.LowMorale"/> from
/// <see cref="Squad.MoraleLevel"/>. Refusals and hesitations carry a morale note. A Routing squad refuses
/// every order but FallBack (<see cref="OrderNote.Routing"/>), and an order under way when it routs is
/// cancelled with that note.</item>
/// </list>
/// Register after <see cref="OrgSystem"/> and before <see cref="MovementSystem"/>.
/// </summary>
public sealed class CommandSystem(
    OrgSystem org, MovementSystem move, CommandRules rules, FireSystem? fire = null, SuppressionSystem? suppression = null) : ISimSystem
{
    private readonly Dictionary<int, Ticket> _tickets = [];
    private readonly List<IssueRejection> _rejections = [];
    private readonly SortedDictionary<int, Ticket> _roeTickets = [];
    private readonly SortedDictionary<int, RoeMode> _roe = [];

    public CommandRules Rules { get; } = rules;

    /// <summary>The off-map artillery that CallFire orders go to (item 2.4); set by <c>ArtillerySystem</c>.</summary>
    internal global::PointBlank.Sim.Artillery.ArtillerySystem? Artillery { get; set; }

    /// <summary>The unit's latest order and its state, for the UI. Status None when it has had none.</summary>
    internal OrderView StatusOf(int unitId) => View(_tickets, unitId);

    /// <summary>The unit's latest rules-of-engagement change and its state (item 2.5a). Status None when it has had none.</summary>
    internal OrderView RoeStatusOf(int unitId) => View(_roeTickets, unitId);

    private static OrderView View(IReadOnlyDictionary<int, Ticket> bySquad, int unitId) => Find(bySquad, unitId) is { } t
        ? new OrderView(unitId, t.Order, t.Status, t.Remaining, t.Total, t.Reasons, t.Note, t.Issued)
        : new OrderView(unitId, null, OrderStatus.None, 0, 0, DelayReason.None, OrderNote.None, -1);

    private static Ticket? Find(IReadOnlyDictionary<int, Ticket> bySquad, int unitId) => bySquad.GetValueOrDefault(unitId);
    /// <summary>A squad's rules of engagement (item 2.3); <see cref="RoeMode.FireAtWill"/> until a SetRoe order says otherwise.</summary>
    internal RoeMode RoeOf(int unitId) => _roe.TryGetValue(unitId, out var r) ? r : RoeMode.FireAtWill;

    /// <summary>Sets a squad's rules of engagement at once: for tests and scenario setup (orders use <see cref="OrderKind.SetRoe"/>).</summary>
    internal void SetRoe(int unitId, RoeMode roe) => _roe[unitId] = roe;

    /// <summary>The reasons an order issued now would be delayed (a UI preview).</summary>
    /// <exception cref="InvalidOperationException">There is no such unit.</exception>
    internal DelayReason ReasonsFor(int unitId) => Reasons(FindSquad(unitId));

    /// <summary>
    /// An HQ is always in contact. A squad is in contact when an HQ of its side that has an able man
    /// is within the command radius (between the centres of their able men), or when both the squad
    /// and that HQ have a radio. With no such HQ on the map it is out of contact.
    /// </summary>
    internal bool InContact(Squad squad)
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
    internal IReadOnlyList<IssueRejection> Rejections => _rejections;

    /// <summary>
    /// Puts an order into the chain of command (called by <see cref="IssueOrderInput"/>). Never throws
    /// for a bad order, so an AI or UI caller cannot crash the sim: an unknown unit, a unit of the other
    /// side or an unknown kind is refused without touching any ticket and recorded in
    /// <see cref="Rejections"/>; a move off the map, or any order to a unit with no able man, replaces the
    /// unit's ticket with a Rejected one carrying the note. The delay reasons and duration are fixed
    /// here, at issue: if the leader falls (or morale changes) while the order is Pending it is not
    /// re-rolled.
    /// </summary>
    internal IssueResult Issue(World world, string side, int unitId, Order order)
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
        if (order.Kind == OrderKind.SetRoe)
        {
            _roeTickets[unitId] = t;
        }
        else
        {
            _tickets[unitId] = t;
        }

        if ((OrderKinds.IsMove(order.Kind) || order.Kind == OrderKind.CallFire || (order.Kind == OrderKind.Fire && order.TargetId < 0)) && !move.IsOnMap(order.X, order.Y))
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
        t.Total = t.Remaining = Rules.RollDelayTicks(t.Reasons, world.Rng, squad.MoraleLevel);
        t.Status = OrderStatus.Pending;
        return IssueResult.Accepted;
    }

    public void Tick(World world)
    {
        foreach (var squad in org.Squads)
        {
            TickRoe(world, squad);
            if (!_tickets.TryGetValue(squad.Id, out var t))
            {
                continue;
            }

            if (squad.Morale == MoraleState.Routing && t.Order.Kind != OrderKind.FallBack
                && t.Status is OrderStatus.Executing or OrderStatus.Held or OrderStatus.Hesitating)
            {
                // A routing squad drops what it was doing; MoraleSystem moves it.
                fire?.Stop(squad.Id);
                t.Status = OrderStatus.Refused;
                t.Note = OrderNote.Routing;
                t.Remaining = 0;
                continue;
            }

            switch (t.Status)
            {
                case OrderStatus.Executing when t.Order.Kind == OrderKind.Fire:
                    if (fire?.IsFiring(squad.Id) != true)
                    {
                        t.Status = OrderStatus.Done;
                        t.Note = fire?.EndNoteOf(squad.Id) ?? OrderNote.None;
                    }

                    break;
                case OrderStatus.Executing when IsHeldKind(t.Order.Kind) && Pinned(squad):
                    move.Stop(squad.Id);
                    t.Status = OrderStatus.Held;
                    t.Note = OrderNote.Pinned;
                    break;
                case OrderStatus.Held when !Pinned(squad):
                    Execute(world, squad, t);
                    break;
                case OrderStatus.Executing when !move.IsMoving(squad.Id):
                    t.Status = OrderStatus.Done;
                    break;
                case OrderStatus.Pending when --t.Remaining <= 0:
                    t.Remaining = 0;
                    Deliver(world, squad, t);
                    break;
                case OrderStatus.Hesitating when --t.Remaining <= 0:
                    t.Remaining = 0;
                    Execute(world, squad, t);
                    break;
            }
        }
    }

    /// <summary>Counts down a rules-of-engagement change; a routing squad drops one it hesitates over, as any order.</summary>
    private void TickRoe(World world, Squad squad)
    {
        if (Find(_roeTickets, squad.Id) is not { Status: OrderStatus.Pending or OrderStatus.Hesitating } t)
        {
            return;
        }

        if (squad.Morale == MoraleState.Routing && t.Status == OrderStatus.Hesitating)
        {
            (t.Status, t.Note, t.Remaining) = (OrderStatus.Refused, OrderNote.Routing, 0);
        }
        else if (--t.Remaining <= 0)
        {
            t.Remaining = 0;
            if (t.Status == OrderStatus.Pending)
            {
                Deliver(world, squad, t);
            }
            else
            {
                Execute(world, squad, t);
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

            Append(hasher, t!);
        }

        hasher.Add(_roeTickets.Count);
        foreach (var (id, t) in _roeTickets)
        {
            hasher.Add(id);
            Append(hasher, t);
        }

        hasher.Add(_roe.Count);
        foreach (var (id, roe) in _roe)
        {
            hasher.Add(id);
            hasher.Add((byte)roe);
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

    private static void Append(StateHasher hasher, Ticket t)
    {
        hasher.Add((byte)t.Order.Kind);
        hasher.Add(t.Order.X);
        hasher.Add(t.Order.Y);
        hasher.Add(t.Order.TargetId);
        hasher.Add(t.Issued);
        hasher.Add((byte)t.Status);
        hasher.Add(t.Remaining);
        hasher.Add(t.Total);
        hasher.Add((byte)t.Reasons);
        hasher.Add((byte)t.Note);
    }

    private static bool Roll(SimRng rng, Fixed chance) => rng.NextFixed() < chance;

    /// <summary>The kinds a pinned squad holds: the advance-type moves.</summary>
    private static bool IsHeldKind(OrderKind kind) => OrderKinds.IsMove(kind) && OrderKinds.ClassOf(kind) == OrderClass.Advance;

    private bool Pinned(Squad squad) => suppression?.IsPinned(squad.Id) == true;

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

    private Squad FindSquad(int unitId) =>
        org.Squads.FirstOrDefault(s => s.Id == unitId) ?? throw new InvalidOperationException($"No unit {unitId}.");

    private DelayReason Reasons(Squad squad)
    {
        var r = DelayReason.None;
        r |= squad.HasEffectiveLeader ? DelayReason.None : DelayReason.NoLeader;
        r |= Band(squad.Morale) switch
        {
            MoraleState.Shaken => DelayReason.Shaken,
            MoraleState.Broken => DelayReason.Broken,
            _ => DelayReason.None,
        };
        r |= InContact(squad) ? DelayReason.None : DelayReason.OutOfContact;
        r |= squad.MoraleLevel < Fixed.FromInt(100) ? DelayReason.LowMorale : DelayReason.None;
        return r;
    }

    /// <summary>
    /// The morale band the order rules use: Steady, Shaken (also Pinned and Rallying) or Broken (also Routing).
    /// </summary>
    internal static MoraleState Band(MoraleState state) => state switch
    {
        MoraleState.Shaken or MoraleState.Pinned or MoraleState.Rallying => MoraleState.Shaken,
        MoraleState.Broken or MoraleState.Routing => MoraleState.Broken,
        _ => MoraleState.Steady,
    };

    private void Deliver(World world, Squad squad, Ticket t)
    {
        var rng = world.Rng;
        var cls = OrderKinds.ClassOf(t.Order.Kind);
        if (squad.Morale == MoraleState.Routing && cls != OrderClass.FallBack)
        {
            t.Status = OrderStatus.Refused;
            t.Note = OrderNote.Routing;
            return;
        }

        var band = Band(squad.Morale);
        var refuse = cls != OrderClass.Advance
            ? Fixed.Zero
            : band switch
            {
                MoraleState.Broken => Rules.BrokenRefuseChance,
                MoraleState.Shaken => Rules.ShakenRefuseChance,
                _ => Fixed.Zero,
            };
        if (refuse > Fixed.Zero && Roll(rng, refuse))
        {
            t.Status = OrderStatus.Refused;
            t.Note = band == MoraleState.Broken ? OrderNote.MoraleBroken : OrderNote.MoraleShaken;
            return;
        }

        if (band == MoraleState.Shaken && cls != OrderClass.FallBack && Rules.HesitateChance > Fixed.Zero
            && Roll(rng, Rules.HesitateChance) && Rules.HesitateTicks > 0)
        {
            t.Status = OrderStatus.Hesitating;
            t.Note = OrderNote.MoraleShaken;
            t.Remaining = Rules.HesitateTicks;
            return;
        }

        Execute(world, squad, t);
    }

    private void Execute(World world, Squad squad, Ticket t)
    {
        var kind = t.Order.Kind;
        if (kind == OrderKind.SetRoe)
        {
            // Rules of engagement change nothing else: a Fire order or a move under way carries on.
            var valid = t.Order.TargetId is >= 0 and <= byte.MaxValue && Enum.IsDefined((RoeMode)t.Order.TargetId);
            if (valid)
            {
                _roe[squad.Id] = (RoeMode)t.Order.TargetId;
            }

            t.Status = valid ? OrderStatus.Done : OrderStatus.Rejected;
            t.Note = valid ? OrderNote.None : OrderNote.InvalidRoe;
            return;
        }

        if (kind == OrderKind.CallFire && Artillery != null)
        {
            // A call for fire changes nothing else either: the caller's move or fire carries on.
            var note = Artillery.Request(world, squad, t.Order);
            t.Status = note == OrderNote.None ? OrderStatus.Done : OrderStatus.Rejected;
            t.Note = note;
            return;
        }

        fire?.Stop(squad.Id);
        if (IsHeldKind(kind) && Pinned(squad))
        {
            move.Stop(squad.Id);
            t.Status = OrderStatus.Held;
            t.Note = OrderNote.Pinned;
            return;
        }

        if (kind == OrderKind.Fire && fire != null)
        {
            var note = fire.Start(world, squad, t.Order);
            t.Status = note == OrderNote.None ? OrderStatus.Executing : OrderStatus.Rejected;
            t.Note = note;
            return;
        }

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
            t.Note = OrderNote.None;
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
