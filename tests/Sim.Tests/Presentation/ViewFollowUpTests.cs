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

        // The mark fades once the fire stops: nothing is remembered for ever.
        c.ForgetIncoming(c.Battle.View.Tick + GameController.IncomingTicks + 1);
        Assert.DoesNotContain(c.Frame().OfType<LinePrim>(), l => l.Colour == Palette.Incoming && l.Width > 1 && !c.Battle.View.IncomingFire.Any());
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
