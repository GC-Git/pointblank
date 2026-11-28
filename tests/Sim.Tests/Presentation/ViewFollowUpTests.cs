using PointBlank.Presentation;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>Item 2.5a: readability, the multi-squad panel, incoming-fire bearings and rejected orders.</summary>
public class ViewFollowUpTests
{
    private static GameController Start(string scenario = "fix-assault", bool script = false) =>
        new(PlayerBattle.Create(scenario, 1, script));

    private static float F(Fixed v) => v.Raw / 4294967296f;

    private static IEnumerable<TextPrim> Texts(GameController c) => c.Frame().OfType<TextPrim>();

    private static void Run(GameController c, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            c.Step();
        }
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("bombard-creep")]
    [InlineData("view-sandbox")]
    public void The_opening_view_shows_every_own_man_apart_from_his_neighbours(string scenario)
    {
        var c = Start(scenario);
        c.FrameOwnForces();

        var men = c.Battle.View.Squads.SelectMany(q => q.Soldiers).Select(s => c.Camera.ToScreen(F(s.X), F(s.Y))).ToArray();
        Assert.True(c.Camera.Scale >= GameController.ReadableScale, $"{c.Camera.Scale} px/m");
        var shown = men.Count(c.Layout.Map.Contains);
        var first = c.Battle.View.Squads[0].Soldiers.Select(s => c.Camera.ToScreen(F(s.X), F(s.Y)));
        Assert.All(first, p => Assert.True(c.Layout.Map.Contains(p)));
        Assert.True(shown >= men.Length / 2, $"{shown} of {men.Length} on screen");
        var size = Math.Clamp(0.7f * c.Camera.Scale, 3, 12);
        Assert.True(size >= 4, "a circle and a square this small look alike");
        for (var i = 0; i < men.Length; i++)
        {
            for (var j = i + 1; j < men.Length; j++)
            {
                var gap = MathF.Max(MathF.Abs(men[i].X - men[j].X), MathF.Abs(men[i].Y - men[j].Y));
                Assert.True(gap >= size, $"men {i} and {j} are {gap} px apart");
            }
        }
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("view-sandbox")]
    public void In_the_opening_view_no_two_mens_symbols_touch_in_any_posture(string scenario)
    {
        var c = Start(scenario);
        c.FrameOwnForces();
        var men = c.Battle.View.Squads.SelectMany(q => q.Soldiers).ToArray();
        foreach (var posture in Enum.GetValues<global::PointBlank.Sim.Sight.Posture>())
        {
            var boxes = men.Select(m => FrameBuilder.Man(m with { Posture = posture }, c.Camera)[0])
                .Select(p => p is RectPrim r ? new Area(r.X, r.Y, r.W, r.H)
                    : p is CirclePrim k ? new Area(k.Centre.X - k.Radius, k.Centre.Y - k.Radius, 2 * k.Radius, 2 * k.Radius)
                    : throw new InvalidOperationException()).ToArray();
            for (var i = 0; i < boxes.Length; i++)
            {
                for (var j = i + 1; j < boxes.Length; j++)
                {
                    var (a, b) = (boxes[i], boxes[j]);
                    Assert.False(a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom, $"{posture}: men {i} and {j} touch");
                }
            }
        }
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("view-sandbox")]
    public void Units_are_named_in_words_not_by_data_ids(string scenario)
    {
        var c = Start(scenario);
        var q = c.Battle.View.Squads[0];
        c.Select([q.Id]);

        var texts = Texts(c).Select(t => t.Text).Where(t => !t.StartsWith(scenario, StringComparison.Ordinal)).ToArray();
        Assert.DoesNotContain(texts, t => t.Contains('_'));
        Assert.Contains(texts, t => t.StartsWith($"#{q.Id} US rifle squad", StringComparison.Ordinal));
        Assert.Equal("US platoon HQ", Words.Unit("us_platoon_hq"));
        Assert.Equal("German Gruppe", Words.Unit("de_gruppe"));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(14)]
    [InlineData(30)]
    public void Overlay_text_does_not_overlap_when_zoomed_in(float zoom)
    {
        var c = Start(script: true);
        c.Toggle(Overlay.Values);
        c.Toggle(Overlay.Intent);
        foreach (var tick in new[] { 1, 300, 900 })
        {
            Run(c, tick - (int)c.Battle.View.Tick);
            var q = c.Battle.View.Squads[0];
            c.Camera.Set(q.Soldiers.Average(s => F(s.X)), q.Soldiers.Average(s => F(s.Y)), zoom);
            var all = Texts(c).Where(t => t.Colour != Palette.Shadow && c.Layout.Map.Contains(t.At)).ToArray();
            var overlay = all.Where(t => t.Colour == Palette.Values || t.Colour == Palette.Intent).ToArray();
            Assert.NotEmpty(overlay);
            foreach (var a in overlay)
            {
                foreach (var b in all.Where(b => !ReferenceEquals(a, b)))
                {
                    var (p, r) = (TextBox.Of(a), TextBox.Of(b));
                    Assert.False(
                        p.X < r.Right && r.X < p.Right && p.Y < r.Bottom && r.Y < p.Bottom,
                        $"tick {tick}: '{a.Text}' at {a.At} overlaps '{b.Text}' at {b.At}");
                }
            }
        }
    }

    [Fact]
    public void Overlay_and_marker_colours_stand_apart_from_the_contact_colours()
    {
        var contacts = new[] { Palette.EnemyTracked, Palette.EnemyStale, Palette.EnemyLost };
        foreach (var colour in new[] { Palette.Sight, Palette.Values, Palette.Truth, Palette.Intent, Palette.Incoming, Palette.Rejected })
        {
            foreach (var enemy in contacts)
            {
                var (r, g, b) = (colour.R - enemy.R, colour.G - enemy.G, colour.B - enemy.B);
                Assert.True((r * r) + (g * g) + (b * b) >= 90 * 90, $"{colour} is too like {enemy}");
            }
        }
    }

    [Fact]
    public void With_several_squads_selected_the_panel_shows_every_squads_order()
    {
        var c = Start();
        var ids = c.Battle.View.Squads.Select(q => q.Id).ToArray();
        Assert.True(ids.Length > 1);
        var second = c.Battle.View.Squad(ids[1])!.Value.Soldiers[0];
        c.Battle.View.Issue(ids[1], new Order(OrderKind.Sneak, second.X, second.Y - Fixed.FromInt(10)));
        c.Step();
        c.Select(ids);

        var panel = Texts(c).Where(t => t.At.X >= c.Layout.Panel.X && t.At.Y > c.Layout.Panel.Y).OrderBy(t => t.At.Y).Select(t => t.Text).ToList();
        var heads = ids.Select(id => panel.FindIndex(t => t.StartsWith($"#{id} ", StringComparison.Ordinal))).ToArray();
        Assert.All(heads, h => Assert.True(h >= 0));
        Assert.Contains("Order: none", panel[heads[0]..heads[1]]);
        var secondBlock = panel[heads[1]..];
        var status = c.Battle.View.StatusOf(ids[1])!;
        Assert.Contains("Order: Sneak: Pending", secondBlock);
        Assert.Contains(secondBlock, t => t.StartsWith($"Delay: {status.TicksRemaining / 10f:0.0}".Replace(',', '.'), StringComparison.Ordinal));
    }

    [Fact]
    public void Incoming_fire_is_drawn_as_a_bearing_from_the_squad_toward_the_shooters_sector()
    {
        var c = Start("fix-assault", true);
        var drawn = 0;
        for (var tick = 0; tick < 1800 && drawn < 20; tick++)
        {
            c.Step();
            var incoming = c.Battle.View.IncomingFire.Where(f => !f.Friendly).ToArray();
            if (incoming.Length == 0)
            {
                continue;
            }

            var lines = c.Frame().OfType<LinePrim>().Where(l => l.Colour == Palette.Incoming).ToArray();
            foreach (var f in incoming)
            {
                var q = c.Battle.View.Squads.First(s => s.Soldiers.Any(m => m.Id == f.SoldierId));
                var centre = c.Camera.ToScreen(q.Soldiers.Average(s => F(s.X)), q.Soldiers.Average(s => F(s.Y)));

                // A shaft pointing inward from the side the fire came from.
                Assert.Contains(lines, l =>
                {
                    var (ox, oy) = (l.A.X - centre.X, l.A.Y - centre.Y);
                    var length = MathF.Sqrt((ox * ox) + (oy * oy));
                    var inward = MathF.Sqrt(((l.B.X - centre.X) * (l.B.X - centre.X)) + ((l.B.Y - centre.Y) * (l.B.Y - centre.Y)));
                    return inward < length && ((ox * F(f.DirX)) + (oy * F(f.DirY))) / length > 0.9f;
                });
                drawn++;
            }
        }

        Assert.True(drawn >= 20, $"only {drawn} rounds came in");
    }

    [Fact]
    public void An_incoming_fire_mark_goes_when_its_bearing_has_been_quiet()
    {
        var c = Start("fix-assault", true);
        var last = new Dictionary<(int Squad, int Sector), long>(); // from the side's own view, tick by tick
        var expired = 0;
        for (var tick = 0; tick < 1800; tick++)
        {
            c.Step();
            var now = c.Battle.View.Tick;
            foreach (var f in c.Battle.View.IncomingFire.Where(f => !f.Friendly))
            {
                var q = c.Battle.View.Squads.First(s => s.Soldiers.Any(m => m.Id == f.SoldierId));
                last[(q.Id, f.Sector)] = now;
            }

            foreach (var key in last.Where(e => now - e.Value > GameController.IncomingTicks).Select(e => e.Key).ToArray())
            {
                last.Remove(key);
                expired++;
            }

            // One shaft (the widest line) for each bearing still fresh, and no other.
            Assert.Equal(last.Count, c.Frame().OfType<LinePrim>().Count(l => l.Colour == Palette.Incoming && l.Width == 3));
        }

        Assert.True(expired > 0, "no bearing ever went quiet");
    }

    private static (GameController C, SquadView Squad, Fixed X, Fixed Y) Rejected()
    {
        var c = Start("bombard-creep");
        var q = c.Battle.View.Squads.First(s => !s.IsHq && !s.HasRadio);
        var man = q.Soldiers[0];
        c.Select([q.Id]);
        var (x, y) = (man.X - Fixed.FromInt(20), man.Y);
        c.Battle.View.Issue(q.Id, new Order(OrderKind.CallFire, x, y));
        for (var i = 0; i < 600 && c.Battle.View.StatusOf(q.Id)!.Status != OrderStatus.Rejected; i++)
        {
            c.Step();
        }

        Assert.Equal(OrderStatus.Rejected, c.Battle.View.StatusOf(q.Id)!.Status);
        return (c, c.Battle.View.Squad(q.Id)!.Value, x, y);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(2f)]
    [InlineData(-1.5f)]
    public void A_refused_order_whose_point_is_off_screen_is_marked_toward_it_at_the_edge(float pan)
    {
        var (c, q, x, y) = Rejected();
        var man = q.Soldiers[0];
        c.Camera.Set(F(man.X) + pan, F(man.Y) + pan, 90); // the point, 20 m away, is far outside the picture
        var at = c.Camera.ToScreen(F(x), F(y));
        Assert.False(c.Layout.Map.Contains(at));
        var able = q.Soldiers.Where(s => s.Health is HealthState.Ok or HealthState.Wounded).ToArray();
        var from = c.Camera.ToScreen(able.Average(s => F(s.X)), able.Average(s => F(s.Y)));

        var lines = c.Frame().OfType<LinePrim>().Where(l => l.Colour == Palette.Rejected).ToArray();

        // One line, along the true bearing to the point, ending at the map's edge: no cross where the point is not.
        var line = Assert.Single(lines);
        var (dx, dy) = (at.X - from.X, at.Y - from.Y);
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        foreach (var p in new[] { line.A, line.B })
        {
            Assert.True(MathF.Abs(((p.X - from.X) * dy) - ((p.Y - from.Y) * dx)) / length < 0.5f, $"{p} is off the bearing");
        }

        var map = c.Layout.Map;
        var edge = new[] { line.B.X - map.X, map.Right - line.B.X, line.B.Y - map.Y, map.Bottom - line.B.Y };
        Assert.InRange(edge.Min(), -0.5f, 0.5f);
        Assert.Contains(c.Frame().OfType<TextPrim>(), t => t.Colour == Palette.Rejected && map.Contains(t.At)
            && t.Text.Contains("Call fire not possible", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refused_orders_mark_expires()
    {
        var (c, _, _, _) = Rejected();
        bool Marked() => c.Frame().Any(p => (p is LinePrim l && l.Colour == Palette.Rejected)
            || (p is TextPrim t && t.Colour == Palette.Rejected));

        // Rejected() stops on the tick the refusal is first seen.
        Run(c, GameController.RejectedTicks);
        Assert.True(Marked());
        Run(c, 1);
        Assert.False(Marked());
    }

    [Fact]
    public void A_delivered_defend_order_is_not_called_unavailable()
    {
        var c = Start("fix-assault");
        var q = c.Battle.View.Squads[0];
        c.Select([q.Id]);
        c.Battle.View.Issue(q.Id, new Order(OrderKind.Defend, q.Soldiers[0].X, q.Soldiers[0].Y));
        for (var i = 0; i < 600 && c.Battle.View.StatusOf(q.Id)!.Status != OrderStatus.Done; i++)
        {
            c.Step();
        }

        var status = c.Battle.View.StatusOf(q.Id)!;
        Assert.Equal((OrderKind.Defend, OrderStatus.Done), (status.Order!.Kind, status.Status));
        var texts = Texts(c).Select(t => t.Text).ToArray();
        Assert.Contains("Order: Defend: Done", texts);
        Assert.DoesNotContain(texts, t => t.Contains("not available", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_defend_order_to_a_marching_squad_reads_done_only_once_it_has_halted()
    {
        var c = Start("fix-assault");
        var q = c.Battle.View.Squads[0];
        c.Select([q.Id]);
        c.Battle.View.Issue(q.Id, new Order(OrderKind.Move, q.Soldiers[0].X + Fixed.FromInt(60), q.Soldiers[0].Y));
        for (var i = 0; i < 600 && !c.Battle.View.Squads[0].IsMoving; i++)
        {
            c.Step();
        }

        Run(c, 50);
        c.Battle.View.Issue(q.Id, new Order(OrderKind.Defend, q.Soldiers[0].X + Fixed.FromInt(100), q.Soldiers[0].Y));
        var inProgress = false;
        for (var i = 0; i < 600; i++)
        {
            c.Step();
            var status = c.Battle.View.StatusOf(q.Id)!;
            if (status.Order!.Kind != OrderKind.Defend)
            {
                continue;
            }

            var texts = Texts(c).Select(t => t.Text).ToArray();
            if (c.Battle.View.Squads[0].IsMoving)
            {
                Assert.DoesNotContain("Order: Defend: Done", texts);
                inProgress |= texts.Contains("Order: Defend: Executing");
            }
            else if (status.Status == OrderStatus.Done)
            {
                Assert.Contains("Order: Defend: Done", texts);
                Assert.True(inProgress, "the halt was never shown as under way");
                return;
            }
        }

        Assert.Fail("the Defend was never done");
    }

    [Fact]
    public void A_rejected_order_is_marked_at_its_point_with_the_reason()
    {
        var c = Start("bombard-creep");
        var q = c.Battle.View.Squads.First(s => !s.IsHq && !s.HasRadio);
        var man = q.Soldiers[0];
        c.Select([q.Id]);
        var (x, y) = (man.X - Fixed.FromInt(20), man.Y);
        c.Battle.View.Issue(q.Id, new Order(OrderKind.CallFire, x, y));
        for (var i = 0; i < 600 && c.Battle.View.StatusOf(q.Id)!.Status != OrderStatus.Rejected; i++)
        {
            c.Step();
        }

        Assert.Equal(OrderStatus.Rejected, c.Battle.View.StatusOf(q.Id)!.Status);
        var at = c.Camera.ToScreen(F(x), F(y));
        var frame = c.Frame();
        Assert.Equal(2, frame.OfType<LinePrim>().Count(l => l.Colour == Palette.Rejected && l.Touches(at, 0.5f) && l.A.X != l.B.X && l.Width > 1));
        Assert.Contains(frame.OfType<TextPrim>(), t => t.Colour == Palette.Rejected && t.Text.Contains("Call fire not possible (Not an observer)", StringComparison.Ordinal));

        // An enemy's refused order is not the player's to see.
        var de = c.Battle.World.ViewFor("de").Squads[0];
        c.Battle.World.ViewFor("de").Issue(de.Id, new Order(OrderKind.Move, Fixed.FromInt(-50), Fixed.Zero));
        var before = c.Frame().Count(p => p.Colour == Palette.Rejected);
        c.Step();
        Assert.Equal(before, c.Frame().Count(p => p.Colour == Palette.Rejected));
    }
}
