using System.Globalization;
using PointBlank.Sim;
using PointBlank.Sim.Artillery;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;

namespace PointBlank.Presentation;

/// <summary>What is drawn over the ground, built from the player's <see cref="SideView"/> and nothing else.</summary>
public static class FrameBuilder
{
    private const int TicksPerSecond = 10;
    private const int ImpactTicks = 20;
    private const float LineHeight = 16;

    public static IReadOnlyList<Prim> Build(GameController c)
    {
        var view = c.Battle.View;
        var camera = c.Camera;
        var squads = view.Squads;
        var list = new List<Prim>();

        // Seen ground lies under everything else.
        if (c.Overlays.HasFlag(Overlay.Sight))
        {
            c.Sight.Draw(list, c);
        }

        foreach (var q in squads)
        {
            OrderLine(list, c, q);
        }

        Incoming(list, c, squads);

        // Selection rings go under every man, so a ring never hides a neighbour.
        foreach (var man in squads.Where(q => c.Selected.Contains(q.Id)).SelectMany(q => q.Soldiers).Where(Able))
        {
            list.Add(new CirclePrim(camera.ToScreen(man.X.F(), man.Y.F()), (Size(camera) * 1.6f) + 2, Palette.Selected, false, 1.5f));
        }

        foreach (var man in squads.SelectMany(q => q.Soldiers))
        {
            list.AddRange(Man(man, camera));
        }

        foreach (var contact in view.KnownEnemies)
        {
            list.AddRange(Contact(contact, camera, view.Tick));
        }

        foreach (var m in view.FireMissions.Where(m => m.RoundsLeft > 0 && m.Status != MissionStatus.Complete))
        {
            var at = camera.ToScreen(m.X.F(), m.Y.F());
            list.Add(new LinePrim(new Pt(at.X - 8, at.Y - 8), new Pt(at.X + 8, at.Y + 8), Palette.Mission, 2));
            list.Add(new LinePrim(new Pt(at.X - 8, at.Y + 8), new Pt(at.X + 8, at.Y - 8), Palette.Mission, 2));
            Label(list, new Pt(at.X + 10, at.Y), m.EtaTicks > 0 ? $"fire in {Seconds(m.EtaTicks)}" : $"{m.RoundsLeft} rounds", Palette.Text);
        }

        foreach (var impact in view.ShellImpacts.Where(i => view.Tick - i.Tick <= ImpactTicks))
        {
            list.Add(new CirclePrim(camera.ToScreen(impact.X.F(), impact.Y.F()), MathF.Max(4, 3 * camera.Scale), Palette.Impact, false, 2));
        }

        // Labels last, so no man hides his own squad's state.
        foreach (var q in squads)
        {
            SquadLabel(list, c, q);
        }

        foreach (var q in squads)
        {
            RejectedOrder(list, c, q);
        }

        TruthOverlays.Draw(list, c);

        if (c.DragBox is { } box)
        {
            list.Add(new RectPrim(box.X, box.Y, box.W, box.H, Palette.Selected, false));
        }

        TopStrip(list, c);
        SidePanel(list, c, squads);
        OrderBar(list, c);
        return list;
    }

    /// <summary>
    /// One of the side's own men: a circle standing, a square kneeling, a long bar prone, a cross when down. A white dot
    /// marks a wounded man.
    /// </summary>
    public static IReadOnlyList<Prim> Man(SoldierView man, Camera camera)
    {
        var at = camera.ToScreen(man.X.F(), man.Y.F());
        var r = Size(camera);
        if (!Able(man))
        {
            return
            [
                new LinePrim(new Pt(at.X - r, at.Y - r), new Pt(at.X + r, at.Y + r), Palette.Down, 2),
                new LinePrim(new Pt(at.X - r, at.Y + r), new Pt(at.X + r, at.Y - r), Palette.Down, 2),
            ];
        }

        Prim shape = man.Posture switch
        {
            Posture.Standing => new CirclePrim(at, r, Palette.Own),
            Posture.Kneeling => new RectPrim(at.X - r, at.Y - r, 2 * r, 2 * r, Palette.Own),
            _ => new RectPrim(at.X - (1.6f * r), at.Y - (0.6f * r), 3.2f * r, 1.2f * r, Palette.Own),
        };
        return man.Health == HealthState.Wounded ? [shape, new CirclePrim(at, MathF.Max(1, r / 3), Palette.Wounded)] : [shape];
    }

    /// <summary>
    /// A spotted enemy at his last known place: filled while tracked; an outline with its age when stale; a fainter outline
    /// with a question mark when lost. A machine-gun crewman is a square, other infantry a circle.
    /// </summary>
    public static IReadOnlyList<Prim> Contact(ContactView contact, Camera camera, long tick)
    {
        var at = camera.ToScreen(contact.X.F(), contact.Y.F());
        var r = Size(camera);
        var colour = Palette.Contact(contact.Quality);
        var filled = contact.Quality == ContactQuality.Tracked;
        var list = new List<Prim>
        {
            contact.Kind == ContactKind.MachineGun
                ? new RectPrim(at.X - r, at.Y - r, 2 * r, 2 * r, colour, filled, 1.5f)
                : new CirclePrim(at, r, colour, filled, 1.5f),
        };
        if (contact.Quality == ContactQuality.Stale)
        {
            list.Add(new TextPrim(new Pt(at.X + r + 1, at.Y + r), Seconds(tick - contact.LastSeenTick), colour, 10));
        }
        else if (contact.Quality == ContactQuality.Lost)
        {
            list.Add(new TextPrim(new Pt(at.X + r + 1, at.Y + r), "?", colour, 12));
        }

        return list;
    }

    private static float Size(Camera camera) => Math.Clamp(0.7f * camera.Scale, 3, 12);

    private static bool Able(SoldierView s) => s.Health is HealthState.Ok or HealthState.Wounded;

    private static string Seconds(long ticks) => string.Create(CultureInfo.InvariantCulture, $"{ticks / (float)TicksPerSecond:0.0}s");

    private static string Number(Fixed value) => value.F().ToString("0", CultureInfo.InvariantCulture);

    private static void Label(List<Prim> list, Pt at, string text, Rgb colour, int size = 11)
    {
        list.Add(new TextPrim(new Pt(at.X + 1, at.Y + 1), text, Palette.Shadow, size));
        list.Add(new TextPrim(at, text, colour, size));
    }

    private static void Bar(List<Prim> list, float x, float y, float w, float h, float fraction, Rgb colour)
    {
        list.Add(new RectPrim(x - 1, y - 1, w + 2, h + 2, Palette.BarBack));
        var filled = w * Math.Clamp(fraction, 0, 1);
        if (filled > 0)
        {
            list.Add(new RectPrim(x, y, filled, h, colour));
        }
    }

    /// <summary>The latest order in a few words, or null when there is nothing to say.</summary>
    private static string? OrderText(OrderView? order) => order?.Order is not { Kind: var kind } ? null : order.Status switch
    {
        OrderStatus.Pending => $"{Words.Of(kind)} pending {Seconds(order.TicksRemaining)}",
        OrderStatus.Hesitating => $"{Words.Of(kind)} hesitating {Seconds(order.TicksRemaining)}",
        OrderStatus.Refused => $"{Words.Of(kind)} refused ({Words.Of(order.Note)})",
        OrderStatus.Rejected => $"{Words.Of(kind)} not possible ({Words.Of(order.Note)})",
        OrderStatus.Held => $"{Words.Of(kind)} held ({Words.Of(order.Note)})",
        OrderStatus.Executing => $"{Words.Of(kind)} under way",
        _ => null,
    };

    /// <summary>
    /// For each recent bearing of enemy fire on a squad, an arrow outside the squad pointing in from that side; shell bursts
    /// have a ring on the tail. The bearing is the side's own fuzzed knowledge, never the shooter's place.
    /// </summary>
    private static void Incoming(List<Prim> list, GameController c, IReadOnlyList<SquadView> squads)
    {
        foreach (var (id, dx, dy, explosion) in c.Incoming)
        {
            if (squads.FirstOrDefault(q => q.Id == id) is not { Soldiers.Count: > 0 } q)
            {
                continue;
            }

            var (x, y) = GameController.Centre(q);
            var centre = c.Camera.ToScreen(x, y);
            var reach = q.Soldiers.Where(Able).Select(s => c.Camera.ToScreen(s.X.F(), s.Y.F()))
                .Select(p => ((p.X - centre.X) * dx) + ((p.Y - centre.Y) * dy)).DefaultIfEmpty(0).Max();
            var near = MathF.Max(reach, 0) + Size(c.Camera) + 8;
            Pt At(float along, float across) => new(centre.X + (dx * along) - (dy * across), centre.Y + (dy * along) + (dx * across));
            var (tail, head) = (At(near + 26, 0), At(near, 0));
            list.Add(new LinePrim(tail, head, Palette.Incoming, 3));
            list.Add(new LinePrim(At(near + 9, 7), head, Palette.Incoming, 2));
            list.Add(new LinePrim(At(near + 9, -7), head, Palette.Incoming, 2));
            if (explosion)
            {
                list.Add(new CirclePrim(tail, 5, Palette.Incoming, false, 2));
            }
        }
    }

    /// <summary>A refused or impossible order: a cross at its point (the squad itself if that is off the map) and why.</summary>
    private static void RejectedOrder(List<Prim> list, GameController c, SquadView q)
    {
        foreach (var order in new[] { c.Battle.View.StatusOf(q.Id), c.Battle.View.RoeStatusOf(q.Id) })
        {
            if (order is not { Status: OrderStatus.Refused or OrderStatus.Rejected, Order: { } o } || c.Queued(q.Id) != null)
            {
                continue;
            }

            var (x, y) = GameController.Centre(q);
            var from = c.Camera.ToScreen(x, y);
            var at = c.Camera.ToScreen(o.X.F(), o.Y.F());
            var pointed = o.Kind != OrderKind.SetRoe && c.Layout.Map.Contains(at);
            at = pointed ? at : new Pt(from.X + 40, from.Y);
            list.Add(new LinePrim(from, at, Palette.Rejected, 1));
            list.Add(new LinePrim(new Pt(at.X - 7, at.Y - 7), new Pt(at.X + 7, at.Y + 7), Palette.Rejected, 3));
            list.Add(new LinePrim(new Pt(at.X - 7, at.Y + 7), new Pt(at.X + 7, at.Y - 7), Palette.Rejected, 3));
            Label(list, new Pt(at.X + 10, at.Y + 4), $"#{q.Id} {OrderText(order)}", Palette.Rejected);
        }
    }

    /// <summary>A rules-of-engagement change on its way, or refused; null when there is none to tell of.</summary>
    private static string? RoeText(GameController c, int squad)
    {
        if (c.QueuedRoe(squad) is { } queued)
        {
            return $"RoE change: {Words.Of(queued)} queued";
        }

        if (c.Battle.View.RoeStatusOf(squad) is not { Order: { } o } change)
        {
            return null;
        }

        var to = $"RoE change: {Words.Of((RoeMode)o.TargetId)}";
        return change.Status switch
        {
            OrderStatus.Pending => $"{to}, pending {Seconds(change.TicksRemaining)}",
            OrderStatus.Hesitating => $"{to}, hesitating {Seconds(change.TicksRemaining)}",
            OrderStatus.Refused or OrderStatus.Rejected => $"{to}, refused ({Words.Of(change.Note)})",
            _ => null,
        };
    }

    private static void OrderLine(List<Prim> list, GameController c, SquadView q)
    {
        var order = c.Battle.View.StatusOf(q.Id);
        if (order?.Order is not { } o || o.Kind is OrderKind.Hold
            || order.Status is not (OrderStatus.Pending or OrderStatus.Hesitating or OrderStatus.Executing or OrderStatus.Held))
        {
            return;
        }

        var (x, y) = GameController.Centre(q);
        var to = c.Camera.ToScreen(o.X.F(), o.Y.F());
        var colour = order.Status == OrderStatus.Executing ? Palette.OrderLine : Palette.DelayBar;
        list.Add(new LinePrim(c.Camera.ToScreen(x, y), to, colour, 1));
        list.Add(new CirclePrim(to, 4, colour, false, 1.5f));
    }

    /// <summary>Above the squad: name and state, morale and suppression bars, and the order with its remaining delay.</summary>
    private static void SquadLabel(List<Prim> list, GameController c, SquadView q)
    {
        var (x, y) = GameController.Centre(q);
        var centre = c.Camera.ToScreen(x, y);
        var top = q.Soldiers.Min(s => c.Camera.ToScreen(s.X.F(), s.Y.F()).Y);
        var at = new Pt(centre.X - 30, top - 30);
        var order = c.Battle.View.StatusOf(q.Id);

        var name = $"#{q.Id} {Words.Unit(q.OrgId)}";
        if (q.Morale != MoraleState.Steady)
        {
            name += $" {Words.Of(q.Morale)}";
        }

        if (q.Suppression != SuppressionState.None)
        {
            name += $" {Words.Of(q.Suppression)}";
        }

        Label(list, at, name, c.Selected.Contains(q.Id) ? Palette.Selected : Palette.Text);
        Bar(list, at.X, at.Y + 4, 60, 3, q.MoraleLevel.F() / 100, Palette.MoraleBar);
        Bar(list, at.X, at.Y + 10, 60, 3, q.SuppressionLevel.F() / 100, Palette.SuppressionBar);

        var text = c.Queued(q.Id) is { } queued ? $"{Words.Of(queued)} queued" : OrderText(order);
        if (text == null)
        {
            return;
        }

        if (order is { Status: OrderStatus.Pending, TotalDelayTicks: > 0 })
        {
            Bar(list, at.X + 64, at.Y + 4, 30, 9, 1 - (order.TicksRemaining / (float)order.TotalDelayTicks), Palette.DelayBar);
        }

        Label(list, new Pt(at.X, at.Y - 13), text, Palette.DelayBar);
    }

    private static void TopStrip(List<Prim> list, GameController c)
    {
        var area = c.Layout.Top;
        list.Add(new RectPrim(area.X, area.Y, area.W, area.H, Palette.Panel));
        var seconds = c.Battle.View.Tick / TicksPerSecond;
        var clock = c.Clock.Paused ? "PAUSED" : $"{c.Clock.Speed}x";
        list.Add(new TextPrim(
            new Pt(10, 20),
            $"{c.Battle.ScenarioId}  seed {c.Battle.Seed}   time {seconds / 60:00}:{seconds % 60:00}   {clock}   right-click: {Words.Of(c.Mode)}",
            Palette.Text, 14));
        // The overlay keys; one that is on shows in its overlay's colour.
        var x = 560f;
        foreach (var (overlay, label, colour) in new[]
        {
            (Overlay.Sight, "F1 sight", Palette.Sight), (Overlay.Values, "F2 values", Palette.Values),
            (Overlay.Truth, "F3 truth", Palette.Truth), (Overlay.Intent, "F4 intent", Palette.Intent),
        })
        {
            var lit = c.Overlays.HasFlag(overlay) && (overlay == Overlay.Sight || !c.TruthOverlaysInert);
            list.Add(new TextPrim(new Pt(x, 20), label, lit ? colour : Palette.DimText, 13));
            x += 90;
        }

        list.Add(new TextPrim(new Pt(c.Layout.Panel.X + 10, 20), $"contacts {c.Battle.View.KnownEnemies.Count}", Palette.DimText, 13));
    }

    private static void SidePanel(List<Prim> list, GameController c, IReadOnlyList<SquadView> squads)
    {
        var area = c.Layout.Panel;
        list.Add(new RectPrim(area.X, area.Y, area.W, area.H, Palette.Panel));
        var x = area.X + 10;
        var y = area.Y + 20;
        void Line(string text, Rgb? colour = null, int size = 13)
        {
            if (y < area.Bottom - 4)
            {
                list.Add(new TextPrim(new Pt(x, y), text, colour ?? Palette.Text, size));
            }

            y += LineHeight;
        }

        var chosen = squads.Where(q => c.Selected.Contains(q.Id)).ToArray();
        if (chosen.Length > 1)
        {
            // Several squads: every one with its state and orders, in a few lines each.
            foreach (var q in chosen)
            {
                Line($"#{q.Id} {Words.Unit(q.OrgId)}", Palette.Selected, 14);
                Line($"  Morale {Words.Of(q.Morale)} {Number(q.MoraleLevel)}, sup {Number(q.SuppressionLevel)}, ammo {Words.Of(q.Ammo)}", Palette.DimText, 12);
                OrderLines(Line, c, q.Id);
            }
        }
        else if (chosen.Length == 0)
        {
            Line("No squad selected", Palette.Selected, 15);
            Line("Left-click or drag: select (Shift adds)", Palette.DimText);
            Line("Right-click: give the order", Palette.DimText);
            Line("  Shift: fast   Ctrl: sneak", Palette.DimText);
            Line("Arrows or middle-drag: pan; wheel: zoom", Palette.DimText);
        }
        else
        {
            var q = chosen[0];
            Line($"#{q.Id} {Words.Unit(q.OrgId)}", Palette.Selected, 15);
            Line($"Morale: {Words.Of(q.Morale)} {Number(q.MoraleLevel)}");
            Bar(list, x, y - 11, 200, 5, q.MoraleLevel.F() / 100, Palette.MoraleBar);
            y += 8;
            foreach (var reason in q.MoraleTopReasons.Where(r => r != MoraleReason.None))
            {
                Line($"  {Words.Of(reason)}", Palette.DimText, 12);
            }

            Line($"Suppression: {Words.Of(q.Suppression)} {Number(q.SuppressionLevel)}");
            Bar(list, x, y - 11, 200, 5, q.SuppressionLevel.F() / 100, Palette.SuppressionBar);
            y += 8;
            var leader = q.Soldiers.FirstOrDefault(s => s.Id == q.LeaderId);
            Line(q.LeaderId < 0 ? $"No leader (successor in {Seconds(q.TicksToSuccession)})" : $"Leader: {Words.Of(leader.Role)}");
            Line($"Ammo: {Words.Of(q.Ammo)}   RoE: {(c.Battle.View.RoeOf(q.Id) is { } roe ? Words.Of(roe) : "-")}");

            OrderLines(Line, c, q.Id);

            var why = c.Battle.View.ReasonsFor(q.Id) ?? DelayReason.None;
            Line(why == DelayReason.None ? "Delay factors: none" : $"Delay factors: {Words.Of(why)}", Palette.DimText, 12);

            y += 6;
            Line("Man            health    posture    sup  rds", Palette.DimText, 12);
            foreach (var s in q.Soldiers)
            {
                var row = y;
                Line(Words.Of(s.Role), size: 12);
                list.Add(new TextPrim(new Pt(x + 100, row), Words.Of(s.Health), Palette.Text, 12));
                list.Add(new TextPrim(new Pt(x + 175, row), Able(s) ? Words.Of(s.Posture) : "-", Palette.Text, 12));
                list.Add(new TextPrim(new Pt(x + 245, row), Number(s.Suppression), Palette.Text, 12));
                list.Add(new TextPrim(new Pt(x + 278, row), s.Rounds < 0 ? "-" : s.Rounds.ToString(CultureInfo.InvariantCulture), Palette.Text, 12));
            }
        }

        foreach (var refused in c.Battle.View.Rejections.TakeLast(1))
        {
            Line($"Order to #{refused.UnitId} refused: {Words.Of(refused.Reason)}", Palette.Rejected, 12);
        }

        y += 6;
        Line($"Fire missions left: {c.Battle.View.MissionsLeft}", Palette.DimText, 12);
        foreach (var m in c.Battle.View.FireMissions.TakeLast(2))
        {
            Line($"  mission {m.Id}: {Words.Of(m.Status)}, eta {Seconds(m.EtaTicks)}, {m.RoundsLeft}/{m.RoundsTotal}", Palette.DimText, 12);
        }
    }

    /// <summary>A squad's orders: a rules change on its way, the order and its state, and the delay left.</summary>
    private static void OrderLines(Action<string, Rgb?, int> line, GameController c, int squad)
    {
        if (RoeText(c, squad) is { } change)
        {
            line(change, Palette.DelayBar, 12);
        }

        var order = c.Battle.View.StatusOf(squad);
        if (c.Queued(squad) is { } queued)
        {
            line($"Order: {Words.Of(queued)} queued", Palette.DelayBar, 13);
        }
        else if (order?.Order is { } o)
        {
            line($"Order: {Words.Of(o.Kind)}: {Words.Of(order.Status)}", Palette.DelayBar, 13);
            if (order.Note != OrderNote.None)
            {
                line($"  {Words.Of(order.Note)}", Palette.DimText, 12);
            }
        }
        else
        {
            line("Order: none", Palette.DimText, 13);
        }

        if (order is { Status: OrderStatus.Pending or OrderStatus.Hesitating })
        {
            line($"Delay: {Seconds(order.TicksRemaining)} left of {Seconds(order.TotalDelayTicks)}", Palette.DelayBar, 13);
        }
    }

    private static void OrderBar(List<Prim> list, GameController c)
    {
        var area = c.Layout.Bottom;
        list.Add(new RectPrim(area.X, area.Y, area.W, area.H, Palette.Panel));
        foreach (var b in c.Buttons())
        {
            list.Add(new RectPrim(b.Area.X, b.Area.Y, b.Area.W, b.Area.H, c.IsActive(b.Command) ? Palette.ButtonActive : Palette.Button));
            list.Add(new TextPrim(new Pt(b.Area.X + 8, b.Area.Y + 19), b.Label, Palette.Text, 13));
        }
    }
}
