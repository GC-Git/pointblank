using PointBlank.Presentation;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;

namespace PointBlank.Sim.Tests.Presentation;

public class PresentationTests
{
    private static GameController Start(string scenario = "fix-assault", bool script = false) =>
        new(PlayerBattle.Create(scenario, 1, script));

    private static Squad Truth(GameController c, int id) => c.Battle.World.GetSystem<OrgSystem>().Squads[id];

    private static float F(Fixed v) => v.Raw / 4294967296f;

    private static Pt ScreenOf(GameController c, Soldier s) => c.Camera.ToScreen(F(s.X), F(s.Y));

    private static Pt Centre(GameController c, int squad)
    {
        var men = Truth(c, squad).Soldiers;
        return c.Camera.ToScreen(men.Average(s => F(s.X)), men.Average(s => F(s.Y)));
    }

    private static IEnumerable<string> Texts(IReadOnlyList<Prim> frame) => frame.OfType<TextPrim>().Select(t => t.Text);

    private static void Run(GameController c, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            c.Step();
        }
    }

    [Fact]
    public void The_player_battle_leaves_the_players_scripted_orders_to_the_player()
    {
        var own = Start();
        var scripted = Start(script: true);
        Run(own, 1);
        Run(scripted, 1);

        Assert.Equal(OrderStatus.None, own.Battle.View.StatusOf(2)!.Status);
        Assert.NotEqual(OrderStatus.None, scripted.Battle.View.StatusOf(2)!.Status);
    }

    [Fact]
    public void The_other_sides_scripted_orders_are_still_given()
    {
        var c = Start("bombard-creep");
        Run(c, 1);

        Assert.NotEqual(OrderStatus.None, c.Battle.World.ViewFor("de").StatusOf(3)!.Status);
        Assert.Equal(OrderStatus.None, c.Battle.View.StatusOf(1)!.Status);
    }

    [Theory]
    [InlineData("fix-assault")]
    [InlineData("bombard-creep")]
    [InlineData("view-sandbox")]
    public void Every_setup_loads_and_steps_for_the_us(string id)
    {
        var c = Start(id);
        Run(c, 20);

        Assert.Equal("us", c.Battle.View.Side);
        Assert.NotEmpty(c.Battle.View.Squads);
        Assert.NotNull(c.Battle.View.Map);
    }

    [Fact]
    public void The_map_view_is_a_faithful_copy()
    {
        var c = Start("view-sandbox");
        var view = c.Battle.View.Map!;
        var map = c.Battle.World.GetSystem<global::PointBlank.Sim.Map.MapSystem>().Map;

        Assert.Equal((map.Width, map.Height), (view.Width, view.Height));
        Assert.Equal(map.Buildings.Count, view.Buildings.Count);
        for (var y = 0; y < map.Height; y++)
        {
            for (var x = 0; x < map.Width; x++)
            {
                Assert.Equal(map.GetTerrain(x, y), view.Tile(x, y).Terrain);
                Assert.Equal(map.GetElevationDm(x, y), view.Tile(x, y).ElevationDm);
                Assert.Equal(map.HasHedge(x, y, global::PointBlank.Sim.Map.EdgeSide.East), view.Tile(x, y).HedgeEast);
                Assert.Equal(map.HasHedge(x, y, global::PointBlank.Sim.Map.EdgeSide.South), view.Tile(x, y).HedgeSouth);
            }
        }
    }

    [Fact]
    public void The_terrain_layer_draws_woods_roads_hedges_and_buildings()
    {
        var c = Start("view-sandbox");
        var colours = c.Terrain().Select(p => p.Colour).ToHashSet();

        var map = c.Battle.View.Map!;
        foreach (var type in new[] { global::PointBlank.Sim.Map.TerrainType.LightWoods, global::PointBlank.Sim.Map.TerrainType.Road })
        {
            var tile = map.Tiles.First(t => t.Terrain == type);
            Assert.Contains(Palette.Terrain(type, tile.ElevationDm), colours);
        }

        Assert.Contains(Palette.Hedge, colours);
        Assert.Contains(Palette.Building, colours);
    }

    [Fact]
    public void An_unspotted_enemy_is_not_drawn_and_cannot_change_the_frame()
    {
        var c = Start();
        var before = c.Frame();
        Assert.Empty(c.Battle.View.KnownEnemies);
        Assert.DoesNotContain(before, p => Palette.IsEnemy(p.Colour));

        // Move every (unspotted) German somewhere else and make them stand: nothing the player sees may change.
        foreach (var s in Truth(c, 1).Soldiers)
        {
            s.X += Fixed.FromInt(40);
            s.Y -= Fixed.FromInt(25);
            s.Posture = global::PointBlank.Sim.Sight.Posture.Standing;
        }

        Assert.Equal(before, c.Frame());
    }

    [Fact]
    public void A_spotted_enemy_is_drawn_where_the_side_last_knew_him_and_stale_differs()
    {
        var c = Start(script: true);
        for (var i = 0; i < 1000 && c.Battle.View.KnownEnemies.Count == 0; i++)
        {
            c.Step();
        }

        var contact = c.Battle.View.KnownEnemies[0];
        var at = c.Camera.ToScreen(F(contact.X), F(contact.Y));
        Assert.Contains(c.Frame(), p => Palette.IsEnemy(p.Colour) && p.Touches(at, 1.5f));

        var tracked = FrameBuilder.Contact(contact with { Quality = global::PointBlank.Sim.Spotting.ContactQuality.Tracked }, c.Camera, 100);
        var stale = FrameBuilder.Contact(contact with { Quality = global::PointBlank.Sim.Spotting.ContactQuality.Stale }, c.Camera, 100);
        var lost = FrameBuilder.Contact(contact with { Quality = global::PointBlank.Sim.Spotting.ContactQuality.Lost }, c.Camera, 100);
        Assert.Contains(tracked, p => p.Filled);
        Assert.DoesNotContain(stale, p => p.Filled);
        Assert.NotEqual(tracked[0].Colour, stale[0].Colour);
        Assert.NotEqual(stale[0].Colour, lost[0].Colour);
    }

    [Fact]
    public void Clicking_selects_a_squad_and_a_drag_box_selects_several()
    {
        var c = Start();
        var man = ScreenOf(c, Truth(c, 0).Soldiers[0]);
        c.Click(man, add: false);
        Assert.Equal([0], c.Selected);

        c.Click(ScreenOf(c, Truth(c, 2).Soldiers[0]), add: true);
        Assert.Equal([0, 2], c.Selected);

        c.Click(new Pt(5, c.Layout.Map.Y + 5), add: false);
        Assert.Empty(c.Selected);

        c.BoxSelect(new Pt(c.Layout.Map.X, c.Layout.Map.Y), new Pt(c.Layout.Map.Right, c.Layout.Map.Bottom), add: false);
        Assert.Equal([0, 2], c.Selected);
    }

    [Fact]
    public void An_enemy_cannot_be_selected()
    {
        var c = Start();
        c.Click(ScreenOf(c, Truth(c, 1).Soldiers[0]), add: false);

        Assert.Empty(c.Selected);
    }

    [Fact]
    public void A_right_click_orders_through_the_chain_of_command_and_shows_the_delay()
    {
        var c = Start();
        c.Click(Centre(c, 0), add: false);
        c.Click(ScreenOf(c, Truth(c, 0).Soldiers[0]), add: false);
        c.Mode = OrderMode.Sneak;
        c.RightClick(c.Camera.ToScreen(250, 60));

        Assert.Contains(Texts(c.Frame()), t => t.Contains("queued", StringComparison.OrdinalIgnoreCase));
        c.Step();

        var order = c.Battle.View.StatusOf(0)!;
        Assert.Equal(OrderKind.Sneak, order.Order!.Kind);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.True(order.TicksRemaining > 0);
        Assert.InRange(F(order.Order.X), 249.5f, 250.5f);
        var texts = Texts(c.Frame()).ToArray();
        Assert.Contains(texts, t => t.StartsWith("Sneak pending", StringComparison.Ordinal)); // on the unit
        Assert.Contains(texts, t => t.StartsWith("Order: Sneak", StringComparison.Ordinal)); // in the panel
        Assert.Contains(texts, t => t.StartsWith("Delay", StringComparison.Ordinal));
    }

    [Fact]
    public void A_group_order_keeps_the_squads_apart()
    {
        var c = Start();
        c.BoxSelect(new Pt(c.Layout.Map.X, c.Layout.Map.Y), new Pt(c.Layout.Map.Right, c.Layout.Map.Bottom), add: false);
        c.RightClick(c.Camera.ToScreen(200, 60));
        c.Step();

        Assert.NotEqual(c.Battle.View.StatusOf(0)!.Order!.Y, c.Battle.View.StatusOf(2)!.Order!.Y);
    }

    [Fact]
    public void Fire_on_a_contact_targets_him_and_empty_ground_is_area_fire()
    {
        var c = Start(script: true);
        for (var i = 0; i < 1000 && c.Battle.View.KnownEnemies.Count == 0; i++)
        {
            c.Step();
        }

        var contact = c.Battle.View.KnownEnemies[0];
        c.Click(ScreenOf(c, Truth(c, 0).Soldiers.First(s => s.CanLead)), add: false);
        c.Mode = OrderMode.Fire;
        c.RightClick(c.Camera.ToScreen(F(contact.X), F(contact.Y)));
        c.Step();
        Assert.Equal(OrderKind.Fire, c.Battle.View.StatusOf(c.Selected[0])!.Order!.Kind);
        Assert.NotEqual(-1, c.Battle.View.StatusOf(c.Selected[0])!.Order!.TargetId);

        c.RightClick(c.Camera.ToScreen(20, 10));
        c.Step();
        Assert.Equal(-1, c.Battle.View.StatusOf(c.Selected[0])!.Order!.TargetId);
    }

    [Fact]
    public void Hold_and_rules_of_engagement_are_issued_to_the_selection()
    {
        var c = Start();
        c.Click(ScreenOf(c, Truth(c, 0).Soldiers[0]), add: false);
        c.Press(Command.CycleRoe);
        Run(c, 400);
        Assert.Equal(RoeMode.ReturnFireOnly, c.Battle.View.RoeOf(0));

        c.Press(Command.Hold);
        c.Step();
        Assert.Equal(OrderKind.Hold, c.Battle.View.StatusOf(0)!.Order!.Kind);
    }

    [Fact]
    public void Call_fire_goes_to_one_observer_in_the_selection()
    {
        var c = Start("bombard-creep");
        c.BoxSelect(new Pt(c.Layout.Map.X, c.Layout.Map.Y), new Pt(c.Layout.Map.Right, c.Layout.Map.Bottom), add: false);
        c.Mode = OrderMode.CallFire;
        c.RightClick(c.Camera.ToScreen(112, 60));
        c.Step();

        var calls = c.Selected.Where(id => c.Battle.View.StatusOf(id)!.Order?.Kind == OrderKind.CallFire).ToArray();
        Assert.Equal([1], calls);
    }

    [Fact]
    public void The_panel_and_the_unit_show_morale_and_suppression()
    {
        var c = Start();
        var squad = Truth(c, 0);
        c.Click(ScreenOf(c, squad.Soldiers[0]), add: false);
        var frame = c.Frame();

        Assert.Contains(Texts(frame), t => t.StartsWith("Morale: Steady 100", StringComparison.Ordinal));
        Assert.Contains(Texts(frame), t => t.StartsWith("Suppression: None 0", StringComparison.Ordinal));
        var fullBar = frame.OfType<RectPrim>().Count(r => r.Colour == Palette.MoraleBar);
        Assert.True(fullBar >= c.Battle.View.Squads.Count, "every own squad shows a morale bar");

        squad.SetMorale(MoraleState.Broken);
        squad.SetMoraleLevel(Fixed.FromInt(20));
        Assert.Contains(Texts(c.Frame()), t => t.StartsWith("Morale: Broken 20", StringComparison.Ordinal));
        Assert.Contains(Texts(c.Frame()), t => t.Contains("Broken", StringComparison.Ordinal) && t.StartsWith("#0", StringComparison.Ordinal));
    }

    [Fact]
    public void Pause_and_speed_drive_the_fixed_tick_clock()
    {
        var clock = new SimClock();
        int Run(double seconds, int frames) => Enumerable.Range(0, frames).Sum(_ => clock.Advance(seconds / frames));

        Assert.InRange(Run(2, 120), 19, 20);
        clock.Speed = 2;
        Assert.InRange(Run(2, 120), 39, 41);
        clock.Speed = 4;
        Assert.InRange(Run(2, 120), 79, 81);
        clock.Paused = true;
        Assert.Equal(0, Run(2, 120));
        clock.Paused = false;
        Assert.True(clock.Advance(60) <= SimClock.MaxTicksPerFrame, "a stall must not be replayed all at once");
    }

    [Fact]
    public void Orders_given_while_paused_apply_when_the_battle_resumes()
    {
        var c = Start();
        c.Press(Command.TogglePause);
        c.Click(ScreenOf(c, Truth(c, 0).Soldiers[0]), add: false);
        c.RightClick(c.Camera.ToScreen(300, 60));
        c.Advance(1.0);
        Assert.Equal(0, c.Battle.View.Tick);
        Assert.Contains(Texts(c.Frame()), t => t.Contains("PAUSED", StringComparison.Ordinal));

        c.Press(Command.TogglePause);
        c.Advance(0.15);
        Assert.Equal(OrderKind.Move, c.Battle.View.StatusOf(0)!.Order!.Kind);
    }

    [Fact]
    public void The_order_bar_and_speed_buttons_are_clickable()
    {
        var c = Start();
        Pt Button(Command command) => c.Buttons().Single(b => b.Command == command).Area.Middle;

        c.Click(Button(Command.Sneak), add: false);
        Assert.Equal(OrderMode.Sneak, c.Mode);
        c.Click(Button(Command.Speed4), add: false);
        Assert.Equal(4, c.Clock.Speed);
        c.Click(Button(Command.TogglePause), add: false);
        Assert.True(c.Clock.Paused);
    }

    [Fact]
    public void The_camera_pans_zooms_about_a_point_and_round_trips()
    {
        var c = Start();
        var p = new Pt(300, 300);
        var (x, y) = c.Camera.ToWorld(p);
        c.Camera.Zoom(p, 2);
        var after = c.Camera.ToScreen(x, y);
        Assert.InRange(after.X, 299.5f, 300.5f);
        Assert.InRange(after.Y, 299.5f, 300.5f);

        c.Camera.Pan(10, -5);
        var panned = c.Camera.ToScreen(x, y);
        Assert.InRange(panned.X, 309.5f, 310.5f);
        Assert.InRange(panned.Y, 294.5f, 295.5f);
    }

    [Fact]
    public void Launch_options_are_parsed()
    {
        var o = LaunchOptions.Parse(["--screenshot", "--scenario", "bombard-creep", "--seed", "7", "--ticks", "600,0", "--out", "x", "--scripted", "--select", "1", "--zoom", "8", "--center", "300,60"]);

        Assert.True(o.Screenshot);
        Assert.True(o.Scripted);
        Assert.Equal(("bombard-creep", 7UL, "x", 8f), (o.Scenario, o.Seed, o.Out, o.Zoom!.Value));
        Assert.Equal([0, 600], o.Ticks);
        Assert.Equal([1], o.Select);
        Assert.Equal((300f, 60f), o.Center);
        Assert.Equal("fix-assault", LaunchOptions.Parse([]).Scenario);
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--speed"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--screenshot"]));
    }
}
