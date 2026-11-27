using PointBlank.Presentation;
using PointBlank.Sim.Map;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Tests.Movement;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>Item 2.6: the debug overlays, headless. The pixels are checked by the screenshot tests.</summary>
public class OverlayTests
{
    private static readonly Overlay[] Each = [Overlay.Sight, Overlay.Values, Overlay.Truth, Overlay.Intent];

    private static GameController Start(params int[] select)
    {
        var c = new GameController(PlayerBattle.Create("fix-assault", 1, true));
        c.Select(select);
        return c;
    }

    private static float F(Fixed v) => v.Raw / 4294967296f;

    private static IReadOnlyList<Squad> Squads(GameController c) => c.Battle.World.GetSystem<OrgSystem>().Squads.ToArray();

    private static void StepTo(GameController c, int tick)
    {
        while (c.Battle.View.Tick < tick)
        {
            c.Step();
        }
    }

    private static Rgb ColourOf(Overlay o) => o switch
    {
        Overlay.Sight => Palette.Sight,
        Overlay.Values => Palette.Values,
        Overlay.Truth => Palette.Truth,
        _ => Palette.Intent,
    };

    /// <summary>The top strip's key help takes an overlay's colour while it is on; only the map counts here.</summary>
    private static bool InMap(GameController c, Prim p) => p switch
    {
        RectPrim r => r.Y >= c.Layout.Map.Y,
        CirclePrim k => k.Centre.Y >= c.Layout.Map.Y,
        LinePrim l => l.A.Y >= c.Layout.Map.Y,
        TextPrim t => t.At.Y >= c.Layout.Map.Y,
        _ => false,
    };

    private static int Drawn(GameController c, Overlay o)
    {
        c.FinishOverlays();
        return c.Frame().Count(p => p.Colour == ColourOf(o) && InMap(c, p));
    }

    [Fact]
    public void Every_overlay_is_off_at_the_start_and_toggles_by_itself()
    {
        var c = Start(2);
        Assert.Equal(Overlay.None, c.Overlays);
        Assert.All(Each, o => Assert.Equal(0, Drawn(c, o)));

        foreach (var o in Each)
        {
            c.Toggle(o);
            Assert.Equal(o, c.Overlays);
            Assert.True(Drawn(c, o) > 0, $"{o} draws nothing");
            Assert.All(Each.Where(other => other != o), other => Assert.Equal(0, Drawn(c, other)));
            c.Toggle(o);
            Assert.Equal(Overlay.None, c.Overlays);
            Assert.Equal(0, Drawn(c, o));
        }

        foreach (var o in Each)
        {
            c.Toggle(o);
        }

        Assert.All(Each, o => Assert.True(Drawn(c, o) > 0, $"{o} with all on"));
    }

    [Fact]
    public void The_key_help_names_every_overlay_key()
    {
        var texts = Start().Frame().OfType<TextPrim>().Select(t => t.Text).ToArray();
        Assert.All(new[] { "F1 sight", "F2 values", "F3 truth", "F4 intent" }, k => Assert.Contains(k, texts));
    }

    [Fact]
    public void With_overlays_off_an_unspotted_enemy_is_never_drawn()
    {
        var c = Start(2);
        for (var tick = 0; tick <= 600; tick += 50)
        {
            StepTo(c, tick);

            // Turned on and off again: nothing of it may linger.
            foreach (var o in Each.Concat(Each))
            {
                c.Toggle(o);
                c.Frame();
            }

            var known = c.Battle.View.KnownEnemies.Select(k => c.Camera.ToScreen(F(k.X), F(k.Y))).ToArray();
            var frame = c.Frame().Where(p => InMap(c, p)).ToArray();
            Assert.DoesNotContain(frame, p => Each.Any(o => p.Colour == ColourOf(o)));
            Assert.All(
                frame.Where(p => Palette.IsEnemy(p.Colour)),
                p => Assert.Contains(known, k => p.Touches(k, 40)));
        }
    }

    [Fact]
    public void Overlays_do_not_change_the_battle()
    {
        var plain = Start(1, 2);
        var shown = Start(1, 2);
        foreach (var o in Each)
        {
            shown.Toggle(o);
        }

        for (var tick = 0; tick < 600; tick++)
        {
            plain.Step();
            shown.Step();
            shown.Frame();
        }

        Assert.Equal(plain.Battle.World.ComputeStateHash(), shown.Battle.World.ComputeStateHash());
    }

    [Fact]
    public void A_side_sees_the_ground_by_the_sims_own_line_of_sight_for_its_own_men_only()
    {
        var c = new GameController(PlayerBattle.Create("view-sandbox", 1));
        var map = c.Battle.World.GetSystem<MapSystem>().Map;
        var los = new LineOfSight(map, SightRules.FromData(PathFinderTests.Data));
        var man = Squads(c).First(q => q.Side == "us").Soldiers[0];
        var enemy = Squads(c).First(q => q.Side == "de").Soldiers[0];
        var range = SpottingRules.FromData(PathFinderTests.Data).MaxRange;
        int seen = 0, blocked = 0;
        for (var x = 1; x < map.Width * F(map.TileSize); x += 11)
        {
            for (var y = 1; y < map.Height * F(map.TileSize); y += 11)
            {
                var (fx, fy) = (Fixed.FromInt(x), Fixed.FromInt(y));
                var (dx, dy) = (fx - man.X, fy - man.Y);
                var expected = (dx * dx) + (dy * dy) <= range * range
                    && los.Check(new Viewpoint(man.X, man.Y, man.Posture), new Viewpoint(fx, fy, Posture.Standing)).Visible;
                Assert.Equal(expected, c.Battle.View.CanSee(man.Id, fx, fy, Posture.Standing));
                Assert.False(c.Battle.View.CanSee(enemy.Id, fx, fy, Posture.Standing));
                _ = expected ? seen++ : blocked++;
            }
        }

        Assert.True(seen > 0 && blocked > 0, $"seen {seen}, blocked {blocked}");
        Assert.False(c.Battle.View.CanSee(man.Id, Fixed.FromInt(-1), man.Y, Posture.Standing));
    }

    [Fact]
    public void The_sight_overlay_dots_only_ground_a_selected_man_sees_and_is_built_a_slice_at_a_time()
    {
        var c = Start();
        c.Toggle(Overlay.Sight);
        Assert.Equal(0, Drawn(c, Overlay.Sight));

        c.Select([2]);
        c.Frame();
        Assert.False(c.SightComplete, "one frame must not do the whole job");
        c.FinishOverlays();
        Assert.True(c.SightComplete);
        var dots = c.Frame().OfType<RectPrim>().Where(r => r.Colour == Palette.Sight).ToArray();
        Assert.NotEmpty(dots);
        var men = c.Battle.View.Squad(2)!.Value.Soldiers;
        Assert.All(dots, d =>
        {
            var (x, y) = c.Camera.ToWorld(new Pt(d.X + (d.W / 2), d.Y + (d.H / 2)));
            var half = Fixed.FromRatio(1, 2);
            var (fx, fy) = (Fixed.FromInt((int)MathF.Floor(x)) + half, Fixed.FromInt((int)MathF.Floor(y)) + half);
            Assert.Contains(men, m => c.Battle.View.CanSee(m.Id, fx, fy, Posture.Standing));
        });

        c.Select([]);
        Assert.Equal(0, Drawn(c, Overlay.Sight));
    }

    [Fact]
    public void The_truth_overlay_marks_every_enemy_where_he_really_is_and_ties_contacts_to_him()
    {
        var c = Start();
        StepTo(c, 600);
        c.Toggle(Overlay.Truth);
        var frame = c.Frame().Where(p => p.Colour == Palette.Truth && InMap(c, p)).ToArray();
        var enemies = Squads(c).Where(q => q.Side == "de").SelectMany(q => q.Soldiers).ToArray();
        Assert.Equal(enemies.Length, frame.OfType<RectPrim>().Count());
        Assert.All(enemies, e => Assert.Contains(frame.OfType<RectPrim>(), r => r.Touches(c.Camera.ToScreen(F(e.X), F(e.Y)), 0.5f)));
        Assert.NotEmpty(c.Battle.View.KnownEnemies);
        Assert.Equal(c.Battle.View.KnownEnemies.Count, frame.OfType<LinePrim>().Count());
    }

    [Fact]
    public void The_values_and_intent_overlays_cover_both_sides()
    {
        var c = Start();
        StepTo(c, 600);
        c.Toggle(Overlay.Values);
        var values = c.Frame().OfType<TextPrim>().Where(t => t.Colour == Palette.Values && InMap(c, t)).Select(t => t.Text).ToArray();
        Assert.All(Squads(c), q => Assert.Contains(values, v => v.StartsWith($"#{q.Id} sup ", StringComparison.Ordinal) && v.Contains(" mor ")));

        c.Toggle(Overlay.Values);
        c.Toggle(Overlay.Intent);
        var intent = c.Frame().Where(p => p.Colour == Palette.Intent && InMap(c, p)).ToArray();
        Assert.All(Squads(c), q => Assert.Contains(intent.OfType<TextPrim>(), t => t.Text.StartsWith($"#{q.Id}: ", StringComparison.Ordinal)));
        Assert.NotEmpty(intent.OfType<LinePrim>());
    }
}
