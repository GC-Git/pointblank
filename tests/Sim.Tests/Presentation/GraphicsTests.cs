using System.Diagnostics;
using PointBlank.Presentation;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>Item 2.13: the placeholder figures, effects, legend and opening view.</summary>
public class GraphicsTests(ITestOutputHelper output)
{
    private static GameController Start(string scenario = "fix-assault") => new(PlayerBattle.Create(scenario, 1, playOwnScript: true));

    /// <summary>The bounds of the shapes in the side's colour: the man's body, legs and shoulders.</summary>
    internal static Area Body(IEnumerable<Prim> prims)
    {
        var points = prims.Where(p => p.Colour == Palette.Own).SelectMany(p => p switch
        {
            CirclePrim c => [new Pt(c.Centre.X - c.Radius, c.Centre.Y - c.Radius), new Pt(c.Centre.X + c.Radius, c.Centre.Y + c.Radius)],
            LinePrim l => new[] { l.A, l.B },
            PolyPrim g => g.Points,
            _ => throw new InvalidOperationException(p.ToString()),
        }).ToArray();
        var (x, y) = (points.Min(p => p.X), points.Min(p => p.Y));
        return new Area(x, y, points.Max(p => p.X) - x, points.Max(p => p.Y) - y);
    }

    [Fact]
    public void Each_posture_has_its_own_silhouette_and_the_weapon_points_where_the_man_works()
    {
        var c = Start();
        var man = c.Battle.View.Squads[0].Soldiers.First(s => s.Role == Role.Rifleman) with { Health = HealthState.Ok };
        var at = c.Camera.ToScreen(man.X.Raw / 4294967296f, man.Y.Raw / 4294967296f);
        IReadOnlyList<Prim> Draw(Posture p, Pt? facing) => FrameBuilder.Man(man with { Posture = p }, c.Camera, facing);

        var shapes = Enum.GetValues<Posture>().Select(p => Draw(p, new Pt(1, 0))).ToArray();
        var bodies = shapes.Select(Body).ToArray();
        var standing = Body(Draw(Posture.Standing, new Pt(1, 0)));
        var prone = Body(Draw(Posture.Prone, new Pt(1, 0)));
        Assert.True(prone.W > prone.H * 1.5f, "prone: long along the way he faces");
        Assert.True(Body(Draw(Posture.Prone, new Pt(0, 1))).H > Body(Draw(Posture.Prone, new Pt(0, 1))).W * 1.5f, "and it turns with him");
        Assert.True(standing.W < standing.H * 1.2f, "standing: compact, shoulders across");
        Assert.Contains(Draw(Posture.Standing, null), p => p is CirclePrim { Fill: true } k && k.Colour == Palette.Own);
        Assert.DoesNotContain(Draw(Posture.Standing, null), p => p is PolyPrim);
        Assert.Equal(3, Assert.Single(Draw(Posture.Kneeling, null).OfType<PolyPrim>()).Points.Count);
        Assert.Equal(4, Assert.Single(Draw(Posture.Prone, null).OfType<PolyPrim>()).Points.Count);
        Assert.Equal(3, bodies.Distinct().Count());

        foreach (var posture in Enum.GetValues<Posture>())
        {
            foreach (var way in new[] { new Pt(1, 0), new Pt(-1, 0), new Pt(0, 1), new Pt(0.6f, -0.8f) })
            {
                var weapon = Assert.Single(Draw(posture, way).OfType<LinePrim>(), l => l.Colour == Palette.Weapon);
                var (dx, dy) = (weapon.B.X - at.X, weapon.B.Y - at.Y);
                Assert.True(((dx * way.X) + (dy * way.Y)) / MathF.Sqrt((dx * dx) + (dy * dy)) > 0.9f, $"{posture} toward {way}");
            }

            Assert.DoesNotContain(Draw(posture, null), p => p.Colour == Palette.Weapon);
        }
    }

    [Fact]
    public void Wounded_incapacitated_and_dead_look_different()
    {
        var c = Start();
        var man = c.Battle.View.Squads[0].Soldiers[0];
        IReadOnlyList<Prim> Draw(HealthState h) => FrameBuilder.Man(man with { Health = h }, c.Camera);

        Assert.DoesNotContain(Draw(HealthState.Ok), p => p.Colour == Palette.RedCross);
        Assert.Contains(Draw(HealthState.Wounded), p => p.Colour == Palette.RedCross);
        Assert.Contains(Draw(HealthState.Wounded), p => p.Colour == Palette.Own);
        Assert.Contains(Draw(HealthState.Incapacitated), p => p.Colour == Palette.RedCross);
        Assert.Contains(Draw(HealthState.Incapacitated), p => p.Colour == Palette.Down);
        Assert.Contains(Draw(HealthState.Dead), p => p.Colour == Palette.Down);
        Assert.DoesNotContain(Draw(HealthState.Dead), p => p.Colour == Palette.RedCross || p.Colour == Palette.Own);
    }

    [Fact]
    public void Tracers_show_for_seen_fire_age_out_and_stay_while_paused()
    {
        var c = Start();
        bool Own() => c.Frame().Any(p => p is LinePrim && p.Colour == Palette.OwnTracer && p.Touches(p is LinePrim l ? l.A : default, 0));
        var enemy = false;
        var own = false;
        var flash = false;
        for (var i = 0; i < 1200 && !(own && enemy && flash); i++)
        {
            c.Step();
            var shots = c.Battle.View.SeenShots;
            if (shots.Count == 0)
            {
                continue;
            }

            c.Camera.Fit(c.Battle.View.Map!);
            var frame = c.Frame();
            own |= shots.Any(s => s.Own) && Own();
            enemy |= shots.Any(s => !s.Own) && frame.Any(p => p.Colour == Palette.EnemyTracer);
            flash |= frame.Any(p => p.Colour == Palette.Flash);
        }

        Assert.True(own && enemy && flash, $"own {own}, enemy {enemy}, flash {flash}");

        // Paused: no ticks run, so what shows goes on showing.
        var before = c.Frame().Count(p => p.Colour == Palette.OwnTracer || p.Colour == Palette.EnemyTracer);
        c.Press(Command.TogglePause);
        c.Advance(5);
        Assert.Equal(before, c.Frame().Count(p => p.Colour == Palette.OwnTracer || p.Colour == Palette.EnemyTracer));

        // One streak a shooter at most, and each lives a short while at every speed.
        Assert.True(before <= 2 * c.Battle.View.Squads.Sum(q => q.Soldiers.Count) + 20);
        Assert.All([1, 2, 4], speed => Assert.InRange(Effects.TracerTicks(speed), 3, 15));
        Assert.True(Effects.TracerTicks(4) / 4f < Effects.TracerTicks(1), "shorter on the clock when fast");
    }

    [Fact]
    public void No_enemy_is_drawn_before_the_side_knows_of_him_and_bursts_show()
    {
        var c = Start("bombard-creep");
        var burst = false;
        for (var i = 0; i < 1200; i++)
        {
            c.Step();
            if (i % 5 != 0)
            {
                continue;
            }

            c.Camera.Fit(c.Battle.View.Map!);
            var frame = c.Frame();
            burst |= frame.Any(p => p.Colour == Palette.Fireball);
            if (c.Battle.View.KnownEnemies.Count == 0 && c.Effects.Fallen.Count == 0)
            {
                Assert.DoesNotContain(frame, p => Palette.IsEnemy(p.Colour) || p.Colour == Palette.EnemyDown || p.Colour == Palette.EnemyTracer);
            }
        }

        Assert.Equal(c.Effects.Craters.Count > 0, burst);
        Assert.InRange(c.Effects.Craters.Count, 0, Effects.MaxCraters);
    }

    [Fact]
    public void The_legend_key_puts_the_key_to_the_symbols_up_and_takes_it_down()
    {
        var c = Start();
        Assert.False(c.ShowLegend);
        var without = c.Frame().Count;
        Assert.Contains(c.Frame().OfType<TextPrim>(), t => t.Text == "L: legend");
        c.ToggleLegend();
        var texts = c.Frame().OfType<TextPrim>().Select(t => t.Text).ToArray();
        Assert.All(
            new[] { "Kneeling", "Prone (lying down)", "Killed", "Your fire", "Enemy last seen here" },
            t => Assert.Contains(t, texts));
        c.ToggleLegend();
        Assert.Equal(without, c.Frame().Count);
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("bombard-creep")]
    public void Every_own_squad_is_on_the_opening_view_or_pointed_to_from_its_edge(string scenario)
    {
        var c = Start(scenario);
        c.FrameOwnForces();
        var frame = c.Frame();
        Assert.All(c.Battle.View.Squads, q =>
        {
            var seen = q.Soldiers.Any(s => c.Layout.Map.Contains(c.Camera.ToScreen(s.X.Raw / 4294967296f, s.Y.Raw / 4294967296f)));
            Assert.True(seen || frame.OfType<TextPrim>().Any(t => t.Text.StartsWith($"#{q.Id} ", StringComparison.Ordinal)
                && t.Text.EndsWith("(off screen)", StringComparison.Ordinal)), $"squad {q.Id}");
        });
    }

    /// <summary>The cost of the events and the frame with forty squads. The bound is loose: it catches a runaway only.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Forty_squads_are_taken_in_and_drawn_in_good_time()
    {
        var map = new PointBlank.Sim.Map.GameMap("open", 300, 120);
        var data = Movement.PathFinderTests.Data;
        var org = new OrgSystem(PointBlank.Sim.Org.OrgTables.FromData(data));
        var move = new PointBlank.Sim.Movement.MovementSystem(map, org, Movement.PathFinderTests.Rules);
        var command = new PointBlank.Sim.Orders.CommandSystem(org, move, PointBlank.Sim.Orders.CommandRules.FromData(data));
        var spot = new PointBlank.Sim.Spotting.SpottingSystem(
            map, org, move, new LineOfSight(map, SightRules.FromData(data)), PointBlank.Sim.Spotting.SpottingRules.FromData(data));
        var world = new World(1, new PointBlank.Sim.Map.MapSystem(map), org, command, move, spot);
        for (var i = 0; i < 20; i++)
        {
            org.SpawnSquad("us_rifle_squad", Fixed.FromInt(60), Fixed.FromInt(10 + (i * 11)));
            org.SpawnSquad("de_gruppe", Fixed.FromInt(240), Fixed.FromInt(10 + (i * 11)));
        }

        world.Run(100);
        var view = world.ViewFor("us");
        var effects = new Effects();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            effects.Remember(view);
        }

        var remember = watch.Elapsed.TotalMilliseconds / 100;
        watch.Restart();
        var camera = new Camera(new Area(0, 30, 950, 648));
        camera.Set(150, 120, 7);
        var prims = 0;
        for (var i = 0; i < 100; i++)
        {
            prims = view.Squads.SelectMany(q => q.Soldiers).Sum(s => FrameBuilder.Man(s, camera, new Pt(1, 0)).Count);
        }

        var draw = watch.Elapsed.TotalMilliseconds / 100;
        output.WriteLine($"40 squads: remember {remember:0.00} ms a tick; figures {draw:0.00} ms a frame, {prims} shapes");
        Assert.True(remember < 20 && draw < 20, $"remember {remember} ms, figures {draw} ms");
    }
}
