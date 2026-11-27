using System.Reflection;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.UnitAi;
using static PointBlank.Sim.Tests.UnitAi.AiRig;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>Item 2.3: unit AI pieces.</summary>
public class UnitAiTests
{
    [Fact]
    public void Bearings_snap_to_16_sectors_and_distances_to_bands()
    {
        Assert.Equal(0, Bearings.SectorOf(M(10), M(0)));
        Assert.Equal(4, Bearings.SectorOf(M(0), M(10))); // south: y grows southward
        Assert.Equal(8, Bearings.SectorOf(M(-10), M(1)));
        Assert.Equal(12, Bearings.SectorOf(M(0), M(-10)));
        Assert.Equal(2, Bearings.SectorOf(M(7), M(7)));
        Assert.Equal(M(50), Bearings.Band(M(73), M(50)));
        Assert.Equal(M(100), Bearings.Band(M(80), M(50)));
        Assert.Equal(M(50), Bearings.Band(M(3), M(50)));
        var (x, y) = Bearings.Dir(4);
        Assert.Equal(Fixed.Zero, x);
        Assert.Equal(Fixed.One, y);
    }

    [Fact]
    public void IncomingFire_gives_a_coarse_bearing_and_band_and_never_the_unseen_origin()
    {
        var rig = Create(1, new GameMap("open", 200, 60));
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(250), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(100), M(73));
        var german = de.Soldiers[0];
        for (var i = 0; i < 200 && rig.Fire.NearMisses.Count == 0; i++)
        {
            rig.Fire.Shoot(rig.World, german, us.Soldiers[0].X, us.Soldiers[0].Y, null);
        }

        var incoming = rig.World.ViewFor("us").IncomingFire;
        Assert.NotEmpty(incoming);
        foreach (var e in incoming)
        {
            Assert.Contains(us.Soldiers, s => s.Id == e.SoldierId);
            Assert.Equal(-1, e.SourceId); // not a contact the US knows
            Assert.Equal(Bearings.Dir(e.Sector), (e.DirX, e.DirY));
            Assert.Equal(Fixed.Zero, Fixed.FromRaw(e.Distance.Raw % M(50).Raw));
            Assert.InRange(e.Sector, 7, 9); // roughly west
        }

        Assert.All(rig.World.ViewFor("de").IncomingFire, e => Assert.Contains(de.Soldiers, s => s.Id == e.SoldierId)); // only its own men
    }

    [Fact]
    public void ROE_is_set_by_an_order_through_the_chain_of_command_and_shown_only_to_its_side()
    {
        var rig = Create(1, new GameMap("open", 100, 60), morale: false);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(50), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(150), M(60));
        rig.Command.SetRoe(de.Id, RoeMode.HoldFire);
        var view = rig.World.ViewFor("us");
        Assert.Equal(RoeMode.FireAtWill, view.RoeOf(us.Id));
        Assert.Null(view.RoeOf(de.Id));
        Assert.Equal(IssueResult.Accepted, view.Issue(us.Id, new Order(OrderKind.SetRoe, Fixed.Zero, Fixed.Zero, (int)RoeMode.HoldFire)));
        rig.World.Run(100);
        Assert.Equal(RoeMode.HoldFire, view.RoeOf(us.Id));
        Assert.Equal(OrderStatus.Done, view.RoeStatusOf(us.Id)!.Status);
        Assert.Equal(OrderStatus.None, view.StatusOf(us.Id)!.Status); // item 2.5a: the change has its own ticket
        view.Issue(us.Id, new Order(OrderKind.SetRoe, Fixed.Zero, Fixed.Zero, 99));
        rig.World.Run(300);
        Assert.Equal(OrderStatus.Rejected, view.RoeStatusOf(us.Id)!.Status);
        Assert.Equal(OrderNote.InvalidRoe, view.RoeStatusOf(us.Id)!.Note);
        Assert.Equal(RoeMode.HoldFire, view.RoeOf(us.Id));
    }

    private static (AiRig Rig, Squad Us, Squad De) Facing(ulong seed, RoeMode usRoe)
    {
        var rig = Create(seed, new GameMap("open", 200, 60), noCasualties: true);
        var us = rig.Org.SpawnSquad("us_rifle_squad", M(250), M(60));
        var de = rig.Org.SpawnSquad("de_gruppe", M(100), M(60), Posture.Kneeling);
        rig.Command.SetRoe(us.Id, usRoe);
        rig.Command.SetRoe(de.Id, RoeMode.HoldFire);
        return (rig, us, de);
    }

    [Fact]
    public void FireAtWill_engages_a_spotted_enemy_and_HoldFire_or_ReturnFireOnly_do_not()
    {
        var (atWill, _, _) = Facing(1, RoeMode.FireAtWill);
        atWill.World.Run(300);
        Assert.True(atWill.Watch.FirstShot.ContainsKey("us"));
        Assert.NotEmpty(atWill.World.ViewFor("us").KnownEnemies);
        foreach (var roe in new[] { RoeMode.HoldFire, RoeMode.ReturnFireOnly })
        {
            var (rig, _, _) = Facing(1, roe);
            rig.World.Run(300);
            Assert.NotEmpty(rig.World.ViewFor("us").KnownEnemies);
            Assert.False(rig.Watch.FirstShot.ContainsKey("us"), $"{roe} fired");
        }
    }

    [Fact]
    public void A_player_Fire_order_takes_priority_over_AI_fire()
    {
        var (rig, us, _) = Facing(2, RoeMode.FireAtWill);
        rig.World.Run(200);
        Assert.True(rig.Fire.IsAiFiring(us.Id));
        rig.Order(us, OrderKind.Fire, 200, 10); // area fire somewhere else
        for (var i = 0; i < 300; i++)
        {
            rig.World.Step();
            if (rig.Command.StatusOf(us.Id).Status == OrderStatus.Executing)
            {
                Assert.False(rig.Fire.IsAiFiring(us.Id));
                Assert.All(rig.Fire.Shots.Where(s => us.Soldiers.Any(u => u.Id == s.FirerId)), s => Assert.True(s.EndY < M(50)));
            }
        }
    }

    [Fact]
    public void A_squad_on_a_Move_order_returns_fire_while_moving()
    {
        var (rig, us, de) = Facing(3, RoeMode.FireAtWill);
        rig.Command.SetRoe(de.Id, RoeMode.FireAtWill);
        rig.Order(us, OrderKind.Move, 150, 60);
        var firedWhileMoving = false;
        for (var i = 0; i < 600 && !firedWhileMoving; i++)
        {
            rig.World.Step();
            firedWhileMoving = rig.Move.IsMoving(us.Id) && rig.Fire.Shots.Any(s => us.Soldiers.Any(u => u.Id == s.FirerId));
        }

        Assert.True(firedWhileMoving);
    }

    [Fact]
    public void Same_seed_gives_the_same_hash_and_the_AI_state_is_hashed()
    {
        ulong Run(ulong seed, bool ai)
        {
            var rig = Create(seed, new GameMap("open", 200, 60));
            rig.Org.SpawnSquad("us_rifle_squad", M(250), M(60));
            rig.Org.SpawnSquad("de_gruppe", M(100), M(60), Posture.Prone);
            rig.World.Run(200);
            return rig.World.ComputeStateHash();
        }

        Assert.Equal(Run(4, true), Run(4, true));
        var a = new StateHasher();
        var b = new StateHasher();
        var r1 = Create(4, new GameMap("open", 200, 60));
        r1.Org.SpawnSquad("us_rifle_squad", M(250), M(60));
        r1.Org.SpawnSquad("de_gruppe", M(100), M(60), Posture.Prone);
        r1.Ai!.AppendState(a);
        r1.World.Run(200);
        r1.Ai.AppendState(b);
        Assert.NotEqual(a.Value, b.Value);
    }

    [Fact]
    public void The_brain_reads_only_through_SideView_and_writes_only_through_effects()
    {
        Type[] banned = [typeof(World), typeof(OrgSystem), typeof(Squad), typeof(Soldier), typeof(Team), typeof(SimRng)];
        bool Bad(Type t) => banned.Contains(t) || typeof(ISimSystem).IsAssignableFrom(t)
            || (t.IsGenericType && t.GetGenericArguments().Any(Bad)) || (t.IsArray && Bad(t.GetElementType()!));
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var types = typeof(UnitAiBrain).Assembly.GetTypes().Where(t => t == typeof(UnitAiBrain) || t.DeclaringType == typeof(UnitAiBrain) || t == typeof(IAiEffects));
        foreach (var t in types)
        {
            Assert.All(t.GetFields(All), f => Assert.False(Bad(f.FieldType), $"{t.Name}.{f.Name}: {f.FieldType.Name}"));
            Assert.All(t.GetMethods(All).SelectMany(m => m.GetParameters().Select(p => (m, p))), x => Assert.False(Bad(x.p.ParameterType), $"{t.Name}.{x.m.Name}({x.p.Name})"));
            Assert.All(t.GetConstructors(All).SelectMany(c => c.GetParameters()), p => Assert.False(Bad(p.ParameterType), $"{t.Name} ctor {p.Name}"));
        }

        var think = typeof(UnitAiBrain).GetMethod("Think", All)!;
        Assert.Equal([typeof(SideView), typeof(IAiEffects)], think.GetParameters().Select(p => p.ParameterType));
    }

    [Fact]
    public void A_routing_squad_flees_to_a_reachable_point_when_the_straight_one_is_unreachable()
    {
        var map = new GameMap("flee", 300, 200);
        var tiles = new List<TilePos>();
        for (var x = 165; x <= 185; x++)
        {
            for (var y = 85; y <= 115; y++)
            {
                tiles.Add(new TilePos(x, y));
            }
        }

        map.AddBuilding(new Building("block", "brick_wall_sample", 1, tiles, [])); // no door: its inside is unreachable
        var rig = Create(5, map, ai: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(300), M(200));
        rig.Org.SpawnSquad("de_gruppe", M(200), M(200));
        rig.World.Run(World.TicksPerSecond * 10);
        Assert.NotEmpty(rig.World.ViewFor("us").KnownEnemies);
        var morale = rig.World.GetSystem<MoraleSystem>();
        var x0 = q.Soldiers.Average(s => s.X.ToInt());
        morale.SetLevel(q.Id, Fixed.FromInt(5));
        morale.SetState(q.Id, MoraleState.Broken);
        rig.World.Run(2);
        Assert.Equal(MoraleState.Routing, q.Morale);
        Assert.True(rig.Move.IsMoving(q.Id), "the flee move did not start");
        rig.World.Run(World.TicksPerSecond * 20);
        var x1 = q.Soldiers.Where(s => s.CanLead).Average(s => s.X.ToInt());
        Assert.True(x1 > x0 + 15, $"ran from {x0} to {x1}");
    }

    [Fact]
    public void A_routing_squad_runs_from_the_mean_bearing_of_the_known_enemies()
    {
        var rig = Create(6, new GameMap("flee", 300, 200), ai: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", M(300), M(200));
        rig.Org.SpawnSquad("de_gruppe", M(215), M(130));
        rig.Org.SpawnSquad("de_gruppe", M(205), M(280));
        rig.World.Run(World.TicksPerSecond * 15);
        Assert.True(rig.World.ViewFor("us").KnownEnemies.Select(c => c.Y < M(200)).Distinct().Count() == 2, "both groups spotted");
        var morale = rig.World.GetSystem<MoraleSystem>();
        var (x0, y0) = (q.Soldiers.Average(s => s.X.ToInt()), q.Soldiers.Average(s => s.Y.ToInt()));
        morale.SetLevel(q.Id, Fixed.FromInt(5));
        morale.SetState(q.Id, MoraleState.Broken);
        rig.World.Run(World.TicksPerSecond * 20);
        var able = q.Soldiers.Where(s => s.CanLead).ToList();
        var (x1, y1) = (able.Average(s => s.X.ToInt()), able.Average(s => s.Y.ToInt()));
        Assert.True(x1 - x0 > 20, $"dx {x1 - x0}");
        Assert.True(Math.Abs(y1 - y0) < 0.35 * (x1 - x0), $"dx {x1 - x0}, dy {y1 - y0}");
    }
}
