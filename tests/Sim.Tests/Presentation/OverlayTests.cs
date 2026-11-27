using System.Globalization;
using PointBlank.Presentation;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Suppression;
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

        shown.Frame(); // before the first tick too: asking must not set anything up in the sim
        Assert.Equal(plain.Battle.World.ComputeStateHash(), shown.Battle.World.ComputeStateHash());
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
    public void The_sight_overlay_is_built_a_slice_at_a_time_and_goes_with_the_selection()
    {
        var c = Start();
        c.Toggle(Overlay.Sight);
        Assert.Equal(0, Drawn(c, Overlay.Sight));

        c.Select([2]);
        c.Frame();
        Assert.False(c.SightComplete, "one frame must not do the whole job");
        Assert.True(Drawn(c, Overlay.Sight) > 0);
        Assert.True(c.SightComplete);

        c.Select([]);
        Assert.Equal(0, Drawn(c, Overlay.Sight));
    }

    /// <summary>
    /// Every US squad of every scenario, hedges, woods and buildings included: a sampled tile is dotted exactly when it is
    /// seen.
    /// </summary>
    [Fact]
    public void The_sight_overlay_dots_exactly_the_ground_the_selected_men_see_among_real_blockers()
    {
        int seen = 0, hidden = 0;
        foreach (var scenario in PlayerBattle.ScenarioIds)
        {
            var squads = Squads(new GameController(PlayerBattle.Create(scenario, 1))).Where(q => q.Side == "us").Select(q => q.Id);
            foreach (var id in squads.ToArray())
            {
                CheckSight(scenario, id, ref seen, ref hidden);
            }
        }

        Assert.True(seen > 200 && hidden > 200, $"in range: seen {seen}, hidden by blockers {hidden}");
    }

    private static void CheckSight(string scenario, int id, ref int seen, ref int hidden)
    {
        var c = new GameController(PlayerBattle.Create(scenario, 1));
        var map = c.Battle.World.GetSystem<MapSystem>().Map;
        var los = new LineOfSight(map, SightRules.FromData(PathFinderTests.Data));
        var range = SpottingRules.FromData(PathFinderTests.Data).MaxRange;
        var squad = Squads(c).First(q => q.Id == id);
        var tile = F(map.TileSize);
        const float zoom = 5;
        c.Camera.Set(F(squad.Soldiers[0].X), F(squad.Soldiers[0].Y), zoom);
        c.Select([squad.Id]);
        c.Toggle(Overlay.Sight);
        c.FinishOverlays();
        var dots = c.Frame().OfType<RectPrim>().Where(r => r.Colour == Palette.Sight)
            .Select(r => c.Camera.ToWorld(new Pt(r.X + (r.W / 2), r.Y + (r.H / 2))))
            .Select(p => ((int)(p.X / tile), (int)(p.Y / tile))).ToHashSet();

        // The overlay looks from one man per tile and posture.
        var observers = squad.Soldiers.Where(s => s.CanLead)
            .GroupBy(s => ((int)(F(s.X) / tile), (int)(F(s.Y) / tile), s.Posture)).Select(g => g.First()).ToArray();
        var step = Math.Max(1, (int)MathF.Ceiling(12 / (tile * zoom)));
        for (var y = 0; y < map.Height; y += step)
        {
            for (var x = 0; x < map.Width; x += step)
            {
                if (!c.Layout.Map.Contains(c.Camera.ToScreen((x + 0.5f) * tile, (y + 0.5f) * tile)))
                {
                    continue;
                }

                var half = Fixed.FromRatio(1, 2);
                var (fx, fy) = ((Fixed.FromInt(x) + half) * map.TileSize, (Fixed.FromInt(y) + half) * map.TileSize);
                var inRange = observers.Where(m => ((fx - m.X) * (fx - m.X)) + ((fy - m.Y) * (fy - m.Y)) <= range * range).ToArray();
                var expected = inRange.Any(m => los.Check(new Viewpoint(m.X, m.Y, m.Posture), new Viewpoint(fx, fy, Posture.Standing)).Visible);
                Assert.True(expected == dots.Contains((x, y)), $"{scenario}, squad {id}, tile {x},{y}: seen {expected}, dotted {!expected}");
                if (inRange.Length > 0)
                {
                    _ = expected ? seen++ : hidden++;
                }
            }
        }
    }

    private static bool HasAContactApartFromItsMan(string scenario)
    {
        var c = new GameController(PlayerBattle.Create(scenario, 1, true));
        var enemies = Squads(c).Where(q => q.Side == "de").SelectMany(q => q.Soldiers).ToDictionary(s => s.Id);
        while (c.Battle.View.Tick < 6000)
        {
            c.Step();
            if (c.Battle.View.KnownEnemies.Any(k => Math.Abs(F(k.X - enemies[k.EnemyId].X)) + Math.Abs(F(k.Y - enemies[k.EnemyId].Y)) > 1))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void The_truth_overlay_marks_every_enemy_where_he_really_is_and_ties_each_contact_to_him()
    {
        var c = new GameController(PlayerBattle.Create(
            new[] { "fix-assault", "bombard-creep" }.First(HasAContactApartFromItsMan), 1, true));
        var enemies = Squads(c).Where(q => q.Side == "de").SelectMany(q => q.Soldiers).ToDictionary(s => s.Id);
        bool Apart() => c.Battle.View.KnownEnemies.Any(k => Math.Abs(F(k.X - enemies[k.EnemyId].X)) + Math.Abs(F(k.Y - enemies[k.EnemyId].Y)) > 1);
        while (!Apart() && c.Battle.View.Tick < 6000)
        {
            c.Step();
        }

        Assert.True(Apart(), "no contact ever stood apart from the man it is of");
        c.Toggle(Overlay.Truth);
        var frame = c.Frame().Where(p => p.Colour == Palette.Truth && InMap(c, p)).ToArray();
        Assert.Equal(enemies.Count, frame.OfType<RectPrim>().Count());
        Assert.All(enemies.Values, e => Assert.Contains(frame.OfType<RectPrim>(), r => r.Touches(c.Camera.ToScreen(F(e.X), F(e.Y)), 0.5f)));
        var expected = c.Battle.View.KnownEnemies
            .Select(k => (A: c.Camera.ToScreen(F(k.X), F(k.Y)), B: c.Camera.ToScreen(F(enemies[k.EnemyId].X), F(enemies[k.EnemyId].Y))))
            .OrderBy(l => l.A.X).ThenBy(l => l.A.Y).ThenBy(l => l.B.X).ThenBy(l => l.B.Y);
        var lines = frame.OfType<LinePrim>().Select(l => (l.A, l.B))
            .OrderBy(l => l.A.X).ThenBy(l => l.A.Y).ThenBy(l => l.B.X).ThenBy(l => l.B.Y);
        Assert.Equal(expected, lines);
    }

    [Fact]
    public void The_values_overlay_shows_the_sims_suppression_and_morale_for_both_sides()
    {
        var c = Start();
        StepTo(c, 600);
        var suppression = c.Battle.World.GetSystem<SuppressionSystem>();
        static string N(Fixed v) => (v.Raw / 4294967296f).ToString("0", CultureInfo.InvariantCulture);
        c.Toggle(Overlay.Values);
        var shown = c.Frame().OfType<TextPrim>().Where(t => t.Colour == Palette.Values && InMap(c, t)).Select(t => t.Text);
        var expected = Squads(c).Select(q => $"#{q.Id} sup {N(suppression.SquadLevelOf(q.Id))} mor {N(q.MoraleLevel)}");
        Assert.Equal(expected.Order(), shown.Order());
        Assert.Contains(Squads(c), q => N(suppression.SquadLevelOf(q.Id)) != "0");
        Assert.Contains(Squads(c), q => N(q.MoraleLevel) != "0");

        // Close in on the most suppressed squad: each man's own number stands beside him.
        var worst = Squads(c).MaxBy(q => suppression.SquadLevelOf(q.Id))!;
        c.Camera.Set(worst.Soldiers.Average(s => F(s.X)), worst.Soldiers.Average(s => F(s.Y)), 14);
        var texts = c.Frame().OfType<TextPrim>().Where(t => t.Colour == Palette.Values).ToArray();
        var close = Squads(c).SelectMany(q => q.Soldiers)
            .Select(s => (Man: s, At: c.Camera.ToScreen(F(s.X), F(s.Y))))
            .Select(m => (m.Man, At: new Pt(m.At.X + 12, m.At.Y + 4)))
            .Where(m => c.Layout.Map.Contains(m.At)).ToArray();
        Assert.All(close, m => Assert.Contains(texts, t => t.At == m.At && t.Text == N(suppression.LevelOf(m.Man.Id))));
        Assert.Contains(close, m => N(suppression.LevelOf(m.Man.Id)) != "0");
    }

    /// <summary>Steps once, recording from the sim's shots the tick each man last fired.</summary>
    private static void StepRecording(GameController c, Dictionary<int, long> lastShot)
    {
        c.Step();
        foreach (var shot in c.Battle.World.GetSystem<FireSystem>().Shots)
        {
            lastShot[shot.FirerId] = shot.Tick;
        }
    }

    private static bool FiredLately(Dictionary<int, long> lastShot, int man, long now) =>
        lastShot.TryGetValue(man, out var at) && now - at <= FireSystem.FiredRecentlyTicks;

    /// <summary>Whether an able friendly stands within <paramref name="clearance"/> of the man's line to the point, short of it.</summary>
    private static bool FriendInTheWay(Squad[] squads, Squad own, Soldier s, float x, float y, float clearance)
    {
        var (dx, dy) = (x - F(s.X), y - F(s.Y));
        var d = MathF.Sqrt((dx * dx) + (dy * dy));
        return squads.Where(q => q.Side == own.Side).SelectMany(q => q.Soldiers).Where(f => f != s && f.CanLead).Any(f =>
        {
            var (rx, ry) = (F(f.X) - F(s.X), F(f.Y) - F(s.Y));
            var along = ((rx * dx) + (ry * dy)) / d;
            return along > 0 && along < d && MathF.Abs((rx * dy) - (ry * dx)) / d <= clearance;
        });
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("bombard-creep")]
    public void A_man_reads_as_firing_only_after_a_shot_and_as_blocked_only_with_a_friend_in_the_way(string scenario)
    {
        var c = new GameController(PlayerBattle.Create(scenario, 1, true));
        var fire = c.Battle.World.GetSystem<FireSystem>();
        var squads = Squads(c).ToArray();
        var mask = F(fire.Rules.MaskClearance);
        var widest = MathF.Max(mask, F(fire.Rules.NearMissRadius));
        var lastShot = new Dictionary<int, long>();
        int firing = 0, blocked = 0, shootingOnTheMove = 0, secondSquadBlocked = 0;
        for (var tick = 0; tick < 1800; tick++)
        {
            StepRecording(c, lastShot);
            var now = c.Battle.World.Tick;
            foreach (var man in c.Battle.DebugTruth()!.Soldiers)
            {
                var own = squads.First(q => q.Id == man.SquadId);
                var s = own.Soldiers.First(m => m.Id == man.Id);
                if (!s.CanLead)
                {
                    Assert.Equal((IntentKind.None, IntentKind.None, IntentKind.None), (man.Task, man.Move, man.Fire));
                    continue;
                }

                // Item 2.5a: present state comes before "recently fired". A recent shooter reads firing unless something
                // stops him now.
                var recent = FiredLately(lastShot, s.Id, now);
                Assert.True(man.Fire != IntentKind.Firing || (recent && IntentFollowUpTests.Free(fire, own, s)), $"tick {tick}, man {s.Id}: {man.Fire}");
                Assert.True(
                    !recent || !IntentFollowUpTests.Free(fire, own, s) || man.Fire is IntentKind.Firing or IntentKind.Blocked,
                    $"tick {tick}, man {s.Id} fired, nothing stops him, yet reads {man.Fire}");
                if (man.Fire == IntentKind.Blocked)
                {
                    Assert.True(
                        man.Aims && FriendInTheWay(squads, own, s, F(man.FireX), F(man.FireY), widest + 0.01f),
                        $"tick {tick}, man {s.Id} reads blocked with nobody in the way");
                    blocked++;
                    secondSquadBlocked += man.SquadId == 2 ? 1 : 0;
                }
                else if (man.Fire == IntentKind.Aiming)
                {
                    Assert.False(FriendInTheWay(squads, own, s, F(man.FireX), F(man.FireY), mask - 0.01f), $"tick {tick}, man {s.Id} aims through a friend");
                }

                firing += man.Fire == IntentKind.Firing ? 1 : 0;
                shootingOnTheMove += man.Fire == IntentKind.Firing && man.Move != IntentKind.None ? 1 : 0;
            }
        }

        Assert.True(firing > 0 && shootingOnTheMove > 0, $"firing {firing}, of them with a move {shootingOnTheMove}, blocked {blocked}");
        if (scenario == "fix-assault")
        {
            Assert.True(secondSquadBlocked > 0, "squad 2, masked by friendlies in this battle, never reads as blocked");
        }
    }

    [Fact]
    public void The_intent_overlay_shows_each_mans_move_fire_and_drill_as_the_sim_has_them()
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var both = 0;
        foreach (var scenario in new[] { "fix-assault", "bombard-creep" })
        {
            var c = new GameController(PlayerBattle.Create(scenario, 1, true));
            c.Toggle(Overlay.Intent);
            var move = c.Battle.World.GetSystem<MovementSystem>();
            var fire = c.Battle.World.GetSystem<FireSystem>();
            var lastShot = new Dictionary<int, long>();
            for (var tick = 1; tick <= 1800; tick++)
            {
                StepRecording(c, lastShot);
                if (tick % 10 != 0)
                {
                    continue;
                }

                var frame = c.Frame().Where(p => p.Colour == Palette.Intent && InMap(c, p)).ToArray();
                var lines = frame.OfType<LinePrim>().Select(l => (l.A, l.B)).ToHashSet();
                var rings = frame.OfType<CirclePrim>().Select(r => r.Centre).ToHashSet();
                var now = c.Battle.World.Tick;
                foreach (var q in Squads(c))
                {
                    var able = q.Soldiers.Where(s => s.CanLead).ToArray();
                    var label = frame.OfType<TextPrim>().FirstOrDefault(t => t.Text.StartsWith($"#{q.Id}: ", StringComparison.Ordinal));
                    if (label != null)
                    {
                        var tally = label.Text[(label.Text.IndexOf("; ", StringComparison.Ordinal) + 2)..].Split(", ")
                            .Where(w => w != "none able")
                            .ToDictionary(w => w[..w.LastIndexOf(' ')], w => int.Parse(w[(w.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture));
                        seen.UnionWith(tally.Keys);
                        Assert.Equal(able.Count(s => move.GoalOf(s.Id) is { Local: false, Held: false }), tally.GetValueOrDefault("moving"));
                        Assert.Equal(able.Count(s => move.GoalOf(s.Id) is { Local: true }), tally.GetValueOrDefault("own move"));
                        Assert.Equal(able.Count(s => move.GoalOf(s.Id) is { Held: true }), tally.GetValueOrDefault("held"));
                        Assert.Equal(
                            able.Count(s => FiredLately(lastShot, s.Id, now) && IntentFollowUpTests.Free(fire, q, s)
                                && fire.ActivityOf(q, s, now).Activity != IntentKind.Blocked),
                            tally.GetValueOrDefault("firing"));

                        // A bounding team is free to walk; a covering one is held.
                        var held = move.HeldTeamsOf(q.Id);
                        if (tally.ContainsKey("bounding") || tally.ContainsKey("covering"))
                        {
                            Assert.Equal(able.Count(s => (held & (1 << s.TeamIndex)) == 0), tally.GetValueOrDefault("bounding"));
                            Assert.Equal(able.Count(s => (held & (1 << s.TeamIndex)) != 0), tally.GetValueOrDefault("covering"));
                        }

                        if (tally.ContainsKey("defending"))
                        {
                            Assert.Equal(OrderKind.Defend, c.Battle.World.GetSystem<CommandSystem>().StatusOf(q.Id)!.Order!.Kind);
                        }
                    }

                    foreach (var s in able)
                    {
                        var from = c.Camera.ToScreen(F(s.X), F(s.Y));
                        if (!c.Layout.Map.Contains(from))
                        {
                            continue;
                        }

                        var goal = move.GoalOf(s.Id);
                        if (goal is { } g)
                        {
                            var to = c.Camera.ToScreen(F(g.X), F(g.Y));
                            Assert.Contains((from, to), lines);
                            Assert.Contains(to, rings);
                        }

                        // The aim point from the sim's fire task itself, not from the labelling code.
                        if (fire.FireTaskOf(q.Id) is { } aim && IntentFollowUpTests.TakesPart(fire, q, s) && fire.RoundsOf(s.Id) > 0)
                        {
                            Assert.Contains((from, c.Camera.ToScreen(F(aim.X), F(aim.Y))), lines);
                            both += goal != null && FiredLately(lastShot, s.Id, now) ? 1 : 0;
                        }
                    }

                    // The fire line points where the rounds go.
                    foreach (var shot in fire.Shots.Where(x => able.Any(s => s.Id == x.FirerId)))
                    {
                        var s = able.First(m => m.Id == shot.FirerId);
                        if (fire.ActivityOf(q, s, now).Target is not { } aim)
                        {
                            continue;
                        }

                        var (ax, ay) = (F(aim.X - shot.OriginX), F(aim.Y - shot.OriginY));
                        var (bx, by) = (F(shot.EndX - shot.OriginX), F(shot.EndY - shot.OriginY));
                        var cos = ((ax * bx) + (ay * by)) / MathF.Sqrt(((ax * ax) + (ay * ay)) * ((bx * bx) + (by * by)));
                        Assert.True(cos > 0.8f, $"tick {tick}: man {s.Id} fires away from his line ({cos})");
                    }
                }
            }
        }

        Assert.True(both > 0, "nobody was shown both moving and firing");
        Assert.All(
            new[] { "moving", "held", "own move", "firing", "blocked", "bounding", "covering", "defending" },
            w => Assert.Contains(w, seen));
    }

    [Fact]
    public void Close_in_a_man_who_moves_and_fires_is_labelled_with_both()
    {
        var c = Start();
        c.Toggle(Overlay.Intent);
        TruthSoldier? found = null;
        while (found == null && c.Battle.View.Tick < 1800)
        {
            c.Step();
            found = c.Battle.DebugTruth()!.Soldiers.Cast<TruthSoldier?>()
                .FirstOrDefault(s => s!.Value.Move == IntentKind.Move && s.Value.Fire == IntentKind.Firing);
        }

        Assert.NotNull(found);
        var man = found.Value;
        c.Camera.Set(F(man.X), F(man.Y), 14);
        var at = c.Camera.ToScreen(F(man.X), F(man.Y));
        var label = Assert.Single(c.Frame().OfType<TextPrim>(), t => t.Colour == Palette.Intent && t.At == new Pt(at.X + 12, at.Y + 15)).Text;
        Assert.Contains("moving", label);
        Assert.Contains("firing", label);
    }
}
