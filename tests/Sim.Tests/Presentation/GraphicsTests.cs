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
        Assert.Equal(3, bodies.Distinct().Count());

        foreach (var posture in Enum.GetValues<Posture>())
        {
            foreach (var way in new[] { new Pt(1, 0), new Pt(-1, 0), new Pt(0, 1), new Pt(0.6f, -0.8f) })
            {
                var weapon = Assert.Single(Draw(posture, way).OfType<LinePrim>(), l => l.Colour == Palette.Weapon);
                var (dx, dy) = (weapon.B.X - at.X, weapon.B.Y - at.Y);
                Assert.True(((dx * way.X) + (dy * way.Y)) / MathF.Sqrt((dx * dx) + (dy * dy)) > 0.9f, $"{posture} toward {way}");
            }

            // No known direction: no weapon, and a shape that is the same turned any quarter, so it points nowhere.
            var unknown = Draw(posture, null);
            Assert.DoesNotContain(unknown, p => p.Colour == Palette.Weapon);
            var points = unknown.SelectMany(p => p switch
            {
                CirclePrim k => [k.Centre],
                LinePrim l => new[] { l.A, l.B },
                PolyPrim g => g.Points,
                _ => throw new InvalidOperationException(p.ToString()),
            }).ToArray();
            Assert.All(points, p => Assert.Contains(points, q =>
                MathF.Abs(q.X - (at.X - (p.Y - at.Y))) < 0.01f && MathF.Abs(q.Y - (at.Y + (p.X - at.X))) < 0.01f));
        }

        Assert.Equal(3, Enum.GetValues<Posture>().Select(p => string.Join(";", Draw(p, null).Select(s => s.GetType().Name))).Distinct().Count());
    }

    [Fact]
    public void An_enemy_seen_to_fall_is_not_said_to_be_dead()
    {
        var body = Figures.Fallen(new Pt(100, 100), 10, Palette.EnemyDown, null);
        Assert.All(body, p => Assert.Equal(Palette.EnemyDown, p.Colour));
        var c = Start();
        for (var i = 0; i < 1500 && c.Effects.Fallen.Count == 0; i++)
        {
            c.Step();
        }

        var fallen = c.Effects.Fallen.Values.First();
        var at = c.Camera.ToScreen(F(fallen.X), F(fallen.Y));
        var drawn = c.Frame();
        Assert.All(
            Figures.Fallen(at, FrameBuilder.Size(c.Camera), Palette.EnemyDown, null), p => Assert.Contains(p, drawn));
        // Neither the cross of the dead nor the red cross of the wounded lies on him.
        var marks = new bool?[] { true, false }
            .SelectMany(dead => Figures.Fallen(at, FrameBuilder.Size(c.Camera), Palette.EnemyDown, dead))
            .Where(p => p.Colour != Palette.EnemyDown).ToArray();
        Assert.NotEmpty(marks);
        Assert.All(marks, p => Assert.DoesNotContain(p, drawn));
        c.ToggleLegend();
        var texts = c.Frame().OfType<TextPrim>().Select(t => t.Text).ToArray();
        Assert.Contains("Enemy fallen (hurt or dead)", texts);
    }

    /// <summary>
    /// Tracers are short streaks that move. One whose stop was not seen starts out exactly as one that flies far, so
    /// nothing marks an own round that hit a man the side does not know.
    /// </summary>
    [Theory]
    [InlineData(2.4f)]
    [InlineData(14f)]
    public void Tracers_are_short_moving_streaks_and_an_unseen_stop_does_not_stand_out(float scale)
    {
        var c = Start();
        int unseen = 0, far = 0, moved = 0;
        for (var i = 0; i < 1200; i++)
        {
            c.Step();
            // Look where a round with no seen stop was fired, if there is one this tick.
            var watched = c.Battle.View.SeenShots.Where(s => s.Own).OrderBy(s => s.HasEnd).Select(s => ((float X, float Y)?)(F(s.X), F(s.Y))).FirstOrDefault()
                ?? (F(c.Battle.View.Squads[0].Soldiers[0].X), F(c.Battle.View.Squads[0].Soldiers[0].Y));
            c.Camera.Set(watched.X, watched.Y, scale);
            var streaks = c.Frame().OfType<LinePrim>().Where(l => l.Colour == Palette.OwnTracer || l.Colour == Palette.EnemyTracer).ToArray();
            Assert.All(streaks, l => Assert.True(Length(l.A, l.B) <= FrameBuilder.TracerLength + 0.1f, $"{Length(l.A, l.B)} px streak"));
            var places = Places.Of(c.Battle.View.Squads, c.Camera);
            foreach (var shot in c.Battle.View.SeenShots.Where(s => s.Own))
            {
                var r = FrameBuilder.Size(c.Camera);
                var from = places.TryGetValue(shot.FirerId, out var p) ? p : c.Camera.ToScreen(F(shot.X), F(shot.Y));
                var muzzle = new Pt(from.X + (F(shot.DirX) * 1.6f * r), from.Y + (F(shot.DirY) * 1.6f * r));
                var end = c.Camera.ToScreen(F(shot.EndX), F(shot.EndY));
                var isFar = shot.HasEnd && Length(muzzle, end) > FrameBuilder.TracerReach + (2 * r);
                if (shot.HasEnd && !isFar)
                {
                    continue;
                }

                var head = FrameBuilder.TracerReach * (c.Battle.View.Tick - shot.Tick + 1) / (Effects.TracerTicks(c.Clock.Speed) + 1);
                var tip = new Pt(muzzle.X + (F(shot.DirX) * head), muzzle.Y + (F(shot.DirY) * head));
                var inView = c.Layout.Map.Contains(muzzle) && c.Layout.Map.Contains(tip);
                // A later round of the same man on the same tick replaces this one.
                if (!inView || c.Battle.View.SeenShots.Count(s => s.FirerId == shot.FirerId) > 1)
                {
                    continue;
                }

                Assert.Contains(streaks, l => Length(l.B, tip) < 0.5f);
                unseen += shot.HasEnd ? 0 : 1;
                far += shot.HasEnd ? 1 : 0;
            }

            var before = streaks.Where(l => l.Colour == Palette.OwnTracer).Select(l => l.B).ToArray();
            if (before.Length > 0 && c.Battle.View.SeenShots.Count == 0)
            {
                moved++;
            }
        }

        Assert.True(unseen > 0 && (scale > 3 || far > 0) && moved > 0, $"{unseen} unseen, {far} far, {moved} later frames");
    }

    private static float Length(Pt a, Pt b) => MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static float F(Fixed v) => v.Raw / 4294967296f;

    [Fact]
    public void Off_screen_contacts_shells_missions_and_squads_under_fire_are_pointed_to_from_the_edge()
    {
        var c = Start();
        for (var i = 0; i < 150; i++)
        {
            c.Step();
        }

        Assert.NotEmpty(c.Battle.View.KnownEnemies);
        c.FrameOwnForces();
        Assert.DoesNotContain(c.Frame().OfType<TextPrim>(), t => t.Text.EndsWith("(off screen)", StringComparison.Ordinal));
        c.Camera.Set(390, 5, 20); // away from everything
        var texts = c.Frame().OfType<TextPrim>().Select(t => t.Text).ToArray();
        Assert.Contains("enemy (off screen)", texts);
        Assert.Contains(texts, t => t.Contains("under fire (off screen)", StringComparison.Ordinal));

        var shells = Start("bombard-creep");
        for (var i = 0; i < 900 && !shells.Battle.View.SeenBursts.Any(); i++)
        {
            shells.Step();
        }

        shells.Camera.Set(390, 5, 20);
        texts = shells.Frame().OfType<TextPrim>().Select(t => t.Text).ToArray();
        Assert.Contains("shells (off screen)", texts);
        Assert.Contains("fire mission (off screen)", texts);
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
    public void Every_own_man_is_on_the_opening_view(string scenario)
    {
        var c = Start(scenario);
        c.FrameOwnForces();
        var frame = c.Frame();
        Assert.All(c.Battle.View.Squads.SelectMany(q => q.Soldiers), s =>
            Assert.True(c.Layout.Map.Contains(c.Camera.ToScreen(F(s.X), F(s.Y))), $"man {s.Id} off the opening view"));
        Assert.DoesNotContain(frame.OfType<TextPrim>(), t => t.Text.EndsWith("(off screen)", StringComparison.Ordinal));
    }

    /// <summary>The cost of taking in the events and drawing the frame with forty squads firing. The bound is loose.</summary>
    [Fact]
    [Trait("Speed", "Slow")]
    public void Forty_squads_in_a_fire_fight_are_taken_in_and_drawn_in_good_time()
    {
        var map = new PointBlank.Sim.Map.GameMap("open", 300, 120);
        var data = Movement.PathFinderTests.Data;
        var org = new OrgSystem(PointBlank.Sim.Org.OrgTables.FromData(data));
        var suppression = new global::PointBlank.Sim.Suppression.SuppressionSystem(
            org, global::PointBlank.Sim.Suppression.SuppressionRules.FromData(data));
        var move = new PointBlank.Sim.Movement.MovementSystem(map, org, Movement.PathFinderTests.Rules, suppression);
        var sight = SightRules.FromData(data);
        var spot = new PointBlank.Sim.Spotting.SpottingSystem(
            map, org, move, new LineOfSight(map, sight), PointBlank.Sim.Spotting.SpottingRules.FromData(data), suppression);
        var fire = new global::PointBlank.Sim.Fire.FireSystem(
            map, org, sight, global::PointBlank.Sim.Fire.FireRules.FromData(data), spot, suppression);
        var command = new PointBlank.Sim.Orders.CommandSystem(
            org, move, PointBlank.Sim.Orders.CommandRules.FromData(data), fire, suppression);
        var world = new World(1, new PointBlank.Sim.Map.MapSystem(map), org, command, move, spot, fire, suppression);
        for (var i = 0; i < 20; i++)
        {
            org.SpawnSquad("us_rifle_squad", Fixed.FromInt(110), Fixed.FromInt(10 + (i * 11)));
            org.SpawnSquad("de_gruppe", Fixed.FromInt(160), Fixed.FromInt(10 + (i * 11)));
        }

        var c = new GameController(new PlayerBattle("perf", 1, world));
        var watch = new Stopwatch();
        var (shots, streaks, prims) = (0, 0, 0);
        const int ticks = 300;
        foreach (var q in c.Battle.View.Squads)
        {
            var man = q.Soldiers[0];
            c.Battle.View.Issue(q.Id, new global::PointBlank.Sim.Orders.Order(
                global::PointBlank.Sim.Orders.OrderKind.Fire, Fixed.FromInt(160), man.Y));
        }

        for (var i = 0; i < ticks; i++)
        {
            c.Step();
            shots += c.Battle.View.SeenShots.Count;
            watch.Start();
            var frame = c.Frame();
            watch.Stop();
            streaks += frame.Count(p => p.Colour == Palette.OwnTracer || p.Colour == Palette.EnemyTracer);
            prims = Math.Max(prims, frame.Count);
        }

        var draw = watch.Elapsed.TotalMilliseconds / ticks;
        watch.Restart();
        var effects = new Effects();
        for (var i = 0; i < 100; i++)
        {
            effects.Remember(c.Battle.View);
        }

        var remember = watch.Elapsed.TotalMilliseconds / 100;
        output.WriteLine($"40 squads: {shots} rounds seen, {streaks} streaks; remember {remember:0.00} ms a tick; frame {draw:0.00} ms, {prims} shapes at most");
        Assert.True(shots > 500 && streaks > 500, $"{shots} rounds, {streaks} streaks: no fire fight");
        Assert.True(remember < 40 && draw < 40, $"remember {remember} ms, frame {draw} ms");
    }
}
