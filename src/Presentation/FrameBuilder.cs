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

        // Craters are part of the ground.
        foreach (var (cx, cy) in c.Effects.Craters)
        {
            var at = camera.ToScreen(cx.F(), cy.F());
            if (c.Layout.Map.Contains(at))
            {
                list.Add(new CirclePrim(at, MathF.Max(5, 0.9f * camera.Scale), Palette.Crater));
            }
        }

        foreach (var q in squads)
        {
            OrderLine(list, c, q);
        }

        Incoming(list, c, squads);

        var places = Places.Of(squads, camera);

        // Selection rings go under every man, so a ring never hides a neighbour.
        foreach (var man in squads.Where(q => c.Selected.Contains(q.Id)).SelectMany(q => q.Soldiers).Where(Able))
        {
            list.Add(new CirclePrim(places[man.Id], (Size(camera) * 1.6f) + 2, Palette.Selected, false, 1.5f));
        }

        var fallen = c.Effects.Fallen;
        foreach (var body in fallen.Values)
        {
            list.AddRange(Figures.Fallen(camera.ToScreen(body.X.F(), body.Y.F()), Size(camera), Palette.EnemyDown, null));
        }

        // The down lie under the living.
        foreach (var q in squads)
        {
            var order = view.StatusOf(q.Id);
            (float X, float Y)? target = order is { Status: OrderStatus.Executing, Order: { Kind: OrderKind.Fire } o }
                ? (o.X.F(), o.Y.F()) : null;
            foreach (var man in q.Soldiers.OrderBy(Able))
            {
                var facing = c.Effects.Facing(man, view.Tick, target);
                list.AddRange(Figures.Man(man, c.Drawn(places, man), Size(camera), facing is var (fx, fy) ? new Pt(fx, fy) : null));
            }
        }

        foreach (var contact in view.KnownEnemies.Where(k => !fallen.ContainsKey(k.EnemyId)))
        {
            list.AddRange(Contact(contact, camera, view.Tick));
        }

        Bursts(list, c);
        Tracers(list, c);

        foreach (var m in view.FireMissions.Where(m => m.RoundsLeft > 0 && m.Status != MissionStatus.Complete))
        {
            var at = camera.ToScreen(m.X.F(), m.Y.F());
            list.Add(new LinePrim(new Pt(at.X - 8, at.Y - 8), new Pt(at.X + 8, at.Y + 8), Palette.Mission, 2));
            list.Add(new LinePrim(new Pt(at.X - 8, at.Y + 8), new Pt(at.X + 8, at.Y - 8), Palette.Mission, 2));
            Label(list, new Pt(at.X + 10, at.Y), m.EtaTicks > 0 ? $"fire in {Seconds(m.EtaTicks)}" : $"{m.RoundsLeft} rounds", Palette.Text);
        }

        // Labels last, so no man hides his own squad's state.
        var labels = new List<Area>();
        foreach (var q in squads)
        {
            SquadLabel(list, c, q, labels);
        }

        foreach (var q in squads)
        {
            RejectedOrder(list, c, q);
        }

        OffScreen(list, c, squads);
        TruthOverlays.Draw(list, c);
        if (c.ShowLegend)
        {
            Legend.Draw(list, c);
        }

        if (c.DragBox is { } box)
        {
            list.Add(new RectPrim(box.X, box.Y, box.W, box.H, Palette.Selected, false));
        }

        TopStrip(list, c);
        SidePanel(list, c, squads);
        OrderBar(list, c);
        return list;
    }

    /// <summary>One of the side's own men (see <see cref="Figures"/>); <paramref name="facing"/> is the way he works, if known.</summary>
    public static IReadOnlyList<Prim> Man(SoldierView man, Camera camera, Pt? facing = null) =>
        Figures.Man(man, camera.ToScreen(man.X.F(), man.Y.F()), Size(camera), facing);

    /// <summary>A spotted enemy at his last known place (see <see cref="Figures.Contact"/>).</summary>
    public static IReadOnlyList<Prim> Contact(ContactView contact, Camera camera, long tick) =>
        Figures.Contact(contact, camera.ToScreen(contact.X.F(), contact.Y.F()), Size(camera), tick);

    /// <summary>Half the least size of a man's figure or a marker, in pixels, however far out the view is.</summary>
    public const float LeastSize = 8;

    /// <summary>How far a tracer flies on the screen at most, and how long its streak is, in pixels (display only).</summary>
    public const float TracerReach = 170;

    public const float TracerLength = 30;

    /// <summary>Half the size of a man's figure, in pixels.</summary>
    public static float Size(Camera camera) => Math.Clamp(0.8f * camera.Scale, LeastSize, 14);

    /// <summary>
    /// A shot seen: a flash at the muzzle and a short streak flying along the round's way as it ages. It goes at most
    /// <see cref="TracerReach"/> pixels, and stops sooner only where the round was seen to stop. A round whose stop was
    /// not seen looks like any long shot, so the picture tells nothing the view does not.
    /// </summary>
    private static void Tracers(List<Prim> list, GameController c)
    {
        var camera = c.Camera;
        var tick = c.Battle.View.Tick;
        var life = Effects.TracerTicks(c.Clock.Speed);
        var places = Places.Of(c.Battle.View.Squads, camera);
        foreach (var shot in c.Effects.Shots(tick, c.Clock.Speed))
        {
            var (dx, dy) = (shot.DirX.F(), shot.DirY.F());
            var r = Size(camera);
            var origin = camera.ToScreen(shot.X.F(), shot.Y.F());
            var man = shot.Own && places.TryGetValue(shot.FirerId, out var drawn) ? drawn : origin;
            var muzzle = new Pt(man.X + (dx * 1.6f * r), man.Y + (dy * 1.6f * r));
            var reach = TracerReach;
            if (shot.HasEnd)
            {
                var end = camera.ToScreen(shot.EndX.F(), shot.EndY.F());
                reach = MathF.Min(reach, ((end.X - muzzle.X) * dx) + ((end.Y - muzzle.Y) * dy));
            }

            var age = tick - shot.Tick;
            var head = MathF.Max(0, reach) * MathF.Min(1, (age + 1) / (float)(life + 1));
            var tail = MathF.Max(0, head - TracerLength);
            Pt At(float along) => new(muzzle.X + (dx * along), muzzle.Y + (dy * along));
            var colour = shot.Own ? Palette.OwnTracer : Palette.EnemyTracer;
            if (head > tail && Clip(c.Layout.Map, At(tail), At(head)) is var (a, b))
            {
                list.Add(new LinePrim(a, b, colour, 2.5f));
            }

            if (c.Layout.Map.Contains(muzzle) && age <= 2)
            {
                var s = 0.8f * r;
                list.Add(new LinePrim(new Pt(muzzle.X - s, muzzle.Y), new Pt(muzzle.X + s, muzzle.Y), Palette.Flash, 2));
                list.Add(new LinePrim(new Pt(muzzle.X, muzzle.Y - s), new Pt(muzzle.X, muzzle.Y + s), Palette.Flash, 2));
                list.Add(new CirclePrim(muzzle, 0.35f * r, Palette.Flash));
            }
        }
    }

    /// <summary>A burst: a fireball with thrown earth, then a spreading ring of smoke. Sizes are for the eye only.</summary>
    private static void Bursts(List<Prim> list, GameController c)
    {
        var camera = c.Camera;
        var tick = c.Battle.View.Tick;
        foreach (var (born, x, y) in c.Effects.Bursts)
        {
            var at = camera.ToScreen(x.F(), y.F());
            var age = tick - born;
            if (age > Effects.BurstTicks || !c.Layout.Map.Contains(at))
            {
                continue;
            }

            var size = MathF.Max(8, 2.5f * camera.Scale);
            if (age <= Effects.FireballTicks)
            {
                for (var i = 0; i < 8; i++)
                {
                    var (sin, cos) = MathF.SinCos(i * MathF.Tau / 8);
                    list.Add(new LinePrim(at, new Pt(at.X + (cos * size * 1.7f), at.Y + (sin * size * 1.7f)), Palette.Fireball, 2));
                }

                list.Add(new CirclePrim(at, size, Palette.Fireball));
                list.Add(new CirclePrim(at, size * 0.5f, Palette.Impact));
            }
            else
            {
                list.Add(new CirclePrim(at, size * (1 + (age / (float)Effects.BurstTicks)), Palette.Smoke, false, 4));
            }
        }
    }

    /// <summary>
    /// Markers at the picture's edge toward what is off it and the side knows of: its own squads (saying if they are
    /// under fire), spotted enemies, shell bursts it saw or felt and its own fire missions' targets. Markers of a kind
    /// that would fall on one another are shown once.
    /// </summary>
    private static void OffScreen(List<Prim> list, GameController c, IReadOnlyList<SquadView> squads)
    {
        var view = c.Battle.View;
        var map = c.Layout.Map;
        var placed = new List<(string Text, Pt At)>();
        void Mark(float x, float y, string what, Rgb colour)
        {
            var to = c.Camera.ToScreen(x, y);
            if (map.Contains(to))
            {
                return;
            }

            var edge = new Pt(Math.Clamp(to.X, map.X + 8, map.Right - 8), Math.Clamp(to.Y, map.Y + 8, map.Bottom - 8));
            if (placed.Any(p => p.Text == what && MathF.Abs(p.At.X - edge.X) < 90 && MathF.Abs(p.At.Y - edge.Y) < 40))
            {
                return;
            }

            // A marker on another's place steps aside along the edge.
            while (placed.Any(p => MathF.Abs(p.At.X - edge.X) < 90 && MathF.Abs(p.At.Y - edge.Y) < 16) && edge.Y < map.Bottom - 26)
            {
                edge = edge with { Y = edge.Y + 18 };
            }

            placed.Add((what, edge));
            list.Add(new CirclePrim(edge, 7, colour));
            list.Add(new CirclePrim(edge, 7, Palette.Text, false, 1.5f));
            var text = $"{what} (off screen)";
            var width = TextBox.Of(new TextPrim(edge, text, Palette.Text, 11)).W;
            Label(list, new Pt(
                Math.Clamp(edge.X + 12, map.X + 2, MathF.Max(map.X + 2, map.Right - width - 2)),
                Math.Clamp(edge.Y + (edge.Y > map.Y + 30 ? -10 : 20), map.Y + 12, map.Bottom - 4)), text, Palette.Text);
        }

        var underFire = c.Incoming.Select(i => i.Squad).ToHashSet();
        foreach (var q in squads.Where(q => q.Soldiers.Count > 0 && !q.Soldiers.Any(s => map.Contains(c.Camera.ToScreen(s.X.F(), s.Y.F())))))
        {
            var (x, y) = GameController.Centre(q);
            Mark(x, y, $"#{q.Id} {Words.Unit(q.OrgId)}{(underFire.Contains(q.Id) ? " under fire" : "")}", Palette.Own);
        }

        foreach (var k in view.KnownEnemies.Where(k => !c.Effects.Fallen.ContainsKey(k.EnemyId)).OrderBy(k => k.Quality))
        {
            Mark(k.X.F(), k.Y.F(), "enemy", Palette.Contact(k.Quality));
        }

        foreach (var (_, x, y) in c.Effects.Bursts.Where(b => view.Tick - b.Tick <= Effects.BurstTicks))
        {
            Mark(x.F(), y.F(), "shells", Palette.Fireball);
        }

        foreach (var m in view.FireMissions.Where(m => m.RoundsLeft > 0 && m.Status != MissionStatus.Complete))
        {
            Mark(m.X.F(), m.Y.F(), "fire mission", Palette.Mission);
        }
    }

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

    /// <summary>
    /// A refused or impossible order, marked for a while: a cross at its point and why. A point outside the picture gets a
    /// line toward it that ends at the picture's edge, with no cross; an order with no point on the map (a rules-of-engagement
    /// change, or a target off the map) gets its cross beside the squad.
    /// </summary>
    private static void RejectedOrder(List<Prim> list, GameController c, SquadView q)
    {
        foreach (var (order, roe) in new[] { (c.Battle.View.StatusOf(q.Id), false), (c.Battle.View.RoeStatusOf(q.Id), true) })
        {
            if (order is not { Status: OrderStatus.Refused or OrderStatus.Rejected, Order: { } o } || c.Queued(q.Id) != null
                || c.RefusalExpired(q.Id, roe, order.IssuedTick))
            {
                continue;
            }

            var (x, y) = GameController.Centre(q);
            var from = c.Camera.ToScreen(x, y);
            var at = c.Camera.ToScreen(o.X.F(), o.Y.F());
            var tile = c.Map.TileSize.F();
            var pointed = o.Kind != OrderKind.SetRoe && o.X.F() >= 0 && o.Y.F() >= 0
                && o.X.F() < c.Map.Width * tile && o.Y.F() < c.Map.Height * tile;
            at = pointed ? at : new Pt(from.X + 40, from.Y);
            var cross = c.Layout.Map.Contains(at);
            if (Clip(c.Layout.Map, from, at) is not var (a, b))
            {
                continue;
            }

            list.Add(new LinePrim(a, b, Palette.Rejected, 1));
            if (cross)
            {
                list.Add(new LinePrim(new Pt(at.X - 7, at.Y - 7), new Pt(at.X + 7, at.Y + 7), Palette.Rejected, 3));
                list.Add(new LinePrim(new Pt(at.X - 7, at.Y + 7), new Pt(at.X + 7, at.Y - 7), Palette.Rejected, 3));
            }

            var text = $"#{q.Id} {OrderText(order)}";
            var label = new Pt(b.X + 10, b.Y + 4);
            if (!cross)
            {
                // Keep the words inside the picture, by the end of the line.
                var width = TextBox.Of(new TextPrim(label, text, Palette.Rejected, 11)).W;
                label = new Pt(
                    Math.Clamp(label.X, c.Layout.Map.X + 2, c.Layout.Map.Right - width - 2),
                    Math.Clamp(label.Y, c.Layout.Map.Y + 14, c.Layout.Map.Bottom - 6));
            }

            Label(list, label, text, Palette.Rejected);
        }
    }

    /// <summary>The part of the segment inside the area, or null if none of it is.</summary>
    private static (Pt A, Pt B)? Clip(Area area, Pt a, Pt b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var (enter, leave) = (0f, 1f);
        foreach (var (p, q) in new[] { (-dx, a.X - area.X), (dx, area.Right - a.X), (-dy, a.Y - area.Y), (dy, area.Bottom - a.Y) })
        {
            if (p == 0)
            {
                if (q < 0)
                {
                    return null;
                }
            }
            else if (p < 0)
            {
                enter = MathF.Max(enter, q / p);
            }
            else
            {
                leave = MathF.Min(leave, q / p);
            }
        }

        return enter <= leave
            ? (new Pt(a.X + (enter * dx), a.Y + (enter * dy)), new Pt(a.X + (leave * dx), a.Y + (leave * dy)))
            : null;
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
    private static void SquadLabel(List<Prim> list, GameController c, SquadView q, List<Area> taken)
    {
        var (x, y) = GameController.Centre(q);
        var centre = c.Camera.ToScreen(x, y);
        var top = q.Soldiers.Min(s => c.Camera.ToScreen(s.X.F(), s.Y.F()).Y);
        var at = new Pt(centre.X - 30, top - 30);

        // Far out, neighbouring squads' labels would run together: a later one moves up clear of those placed.
        Area Box() => new(at.X, at.Y - 24, 170, 38);
        for (var i = 0; i < taken.Count; i++)
        {
            var (box, other) = (Box(), taken[i]);
            if (box.X < other.Right && other.X < box.Right && box.Y < other.Bottom && other.Y < box.Bottom)
            {
                at = at with { Y = other.Y - 15 };
                i = -1;
            }
        }

        taken.Add(Box());
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

        list.Add(new TextPrim(new Pt(465, 20), "L: legend", c.ShowLegend ? Palette.Selected : Palette.Text, 13));
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
            Line("L: what the symbols mean", Palette.Selected);
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
            // A delivered Defend order carries the sim's "no behaviour of its own" note, but the men do defend.
                if (order.Note != OrderNote.None && !(o.Kind == OrderKind.Defend && order.Note == OrderNote.NotImplemented))
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
