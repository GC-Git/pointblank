using System.Reflection;
using System.Runtime.CompilerServices;
using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Tests.Movement;

namespace PointBlank.Sim.Tests.Boundary;

/// <summary>Item 1.6 review: the fog-of-war boundary is enforced by types, not by convention.</summary>
public class SideViewTests
{
    private static readonly DataSet Data = PathFinderTests.Data;
    private static readonly Assembly SimAssembly = typeof(World).Assembly;
    private static readonly Type[] LiveTypes = [typeof(Soldier), typeof(Squad), typeof(Team)];

    private static Fixed M(int metres) => Fixed.FromInt(metres);

    private static (World World, OrgSystem Org) Setup(SpottingRules? rules = null)
    {
        var map = new GameMap("open", 300, 60);
        var org = new OrgSystem(OrgTables.FromData(Data));
        var move = new MovementSystem(map, org, PathFinderTests.Rules);
        var command = new CommandSystem(org, move, CommandRules.FromData(Data));
        var spot = new SpottingSystem(map, org, move, new LineOfSight(map, SightRules.FromData(Data)), rules ?? SpottingRules.FromData(Data));
        return (new World(1, new MapSystem(map), org, command, move, spot), org);
    }

    /// <summary>Every type reachable from <paramref name="root"/>'s public surface, transitively.</summary>
    private static List<(Type Type, string Path)> Reachable(Type root)
    {
        var seen = new HashSet<Type>();
        var found = new List<(Type, string)>();
        var queue = new Queue<(Type, string)>();
        queue.Enqueue((root, root.Name));
        while (queue.Count > 0)
        {
            var (t, path) = queue.Dequeue();
            if (t.IsByRef || t.IsArray || t.IsPointer)
            {
                queue.Enqueue((t.GetElementType()!, path));
                continue;
            }

            if (!seen.Add(t))
            {
                continue;
            }

            found.Add((t, path));
            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    queue.Enqueue((arg, $"{path}<{arg.Name}>"));
                }
            }

            if (t.Assembly != SimAssembly || t.IsEnum)
            {
                continue; // BCL types are leaves (their generic arguments are walked above)
            }

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var p in t.GetProperties(Flags))
            {
                queue.Enqueue((p.PropertyType, $"{path}.{p.Name}"));
            }

            foreach (var f in t.GetFields(Flags))
            {
                queue.Enqueue((f.FieldType, $"{path}.{f.Name}"));
            }

            foreach (var m in t.GetMethods(Flags).Where(m => !m.IsSpecialName))
            {
                queue.Enqueue((m.ReturnType, $"{path}.{m.Name}()"));
                foreach (var a in m.GetParameters())
                {
                    queue.Enqueue((a.ParameterType, $"{path}.{m.Name}({a.Name})"));
                }
            }
        }

        return found;
    }

    private static bool IsInitOnly(PropertyInfo p) =>
        p.SetMethod is not { IsPublic: true } || p.SetMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));

    [Fact]
    public void Nothing_reachable_from_SideView_is_a_live_or_mutable_sim_object()
    {
        var reachable = Reachable(typeof(SideView));
        Assert.Contains(reachable, r => r.Type == typeof(SoldierView));
        Assert.Contains(reachable, r => r.Type == typeof(ContactView));
        foreach (var (t, path) in reachable)
        {
            Assert.False(LiveTypes.Contains(t), $"{path} exposes {t.Name}");
            Assert.False(typeof(ISimSystem).IsAssignableFrom(t), $"{path} exposes the system {t.Name}");
            Assert.False(t == typeof(World) || t == typeof(SimRng) || t == typeof(GameMap) || t == typeof(LineOfSight), $"{path} exposes {t.Name}");
            if (t.Assembly != SimAssembly || t.IsEnum || t == typeof(SideView) || t == typeof(Fixed))
            {
                continue;
            }

            // Any other sim type must be an immutable value: a record (struct or class) with init-only properties.
            var isRecord = t.GetMethod("<Clone>$") != null || (t.IsValueType && t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Instance) != null);
            Assert.True(isRecord, $"{path}: {t.Name} is not a record");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.True(IsInitOnly(p), $"{path}: {t.Name}.{p.Name} has a public setter");
            }

            Assert.All(t.GetFields(BindingFlags.Public | BindingFlags.Instance), f => Assert.True(f.IsInitOnly, $"{t.Name}.{f.Name} is a mutable field"));
        }
    }

    [Fact]
    public void Outside_the_sim_no_public_member_hands_out_live_soldiers_squads_or_systems()
    {
        bool Mentions(Type t, Type[] bad) =>
            bad.Any(b => b.IsAssignableFrom(t))
            || ((t.IsArray || t.IsByRef) && Mentions(t.GetElementType()!, bad))
            || (t.IsGenericType && t.GetGenericArguments().Any(a => Mentions(a, bad)));

        var offenders = new List<string>();
        foreach (var type in SimAssembly.GetExportedTypes().Where(t => !LiveTypes.Contains(t)))
        {
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var m in type.GetMethods(Flags))
            {
                var sig = m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType);
                if (sig.Any(t => Mentions(t, LiveTypes)))
                {
                    offenders.Add($"{type.Name}.{m.Name}");
                }

                // Only World's constructor takes systems; nothing public returns one.
                if (Mentions(m.ReturnType, [typeof(ISimSystem)]))
                {
                    offenders.Add($"{type.Name}.{m.Name} returns a system");
                }
            }
        }

        Assert.Empty(offenders);
        Assert.Null(typeof(World).GetMethod("GetSystem"));
        Assert.Null(typeof(CommandSystem).GetMethod("StatusOf"));
        Assert.Null(typeof(CommandSystem).GetMethod("ReasonsFor"));
        Assert.Null(typeof(CommandSystem).GetMethod("Issue"));
        Assert.Null(typeof(SpottingSystem).GetMethod("KnownEnemies"));
        Assert.Null(typeof(SpottingSystem).GetProperty("Sides"));
        Assert.Null(typeof(MovementSystem).GetMethod("Execute"));
        Assert.True(typeof(Soldier).GetProperty("X")!.SetMethod is not { IsPublic: true });
    }

    [Fact]
    public void An_unspotted_enemy_is_invisible_through_every_SideView_member()
    {
        var (world, org) = Setup(SpottingRules.FromData(Data) with { MaxRange = M(100) });
        var us = org.SpawnSquad("us_rifle_squad", M(20), M(60));
        var de = org.SpawnSquad("de_gruppe", M(320), M(60));
        world.Run(200);
        var view = world.ViewFor("us");

        Assert.Equal("us", view.Side);
        Assert.Equal(world.Tick, view.Tick);
        Assert.Equal([us.Id], view.Squads.Select(q => q.Id));
        Assert.DoesNotContain(view.Squads.SelectMany(q => q.Soldiers), s => de.Soldiers.Any(d => d.Id == s.Id));
        Assert.Empty(view.KnownEnemies);
        Assert.Null(view.StatusOf(de.Id));
        Assert.Null(view.ReasonsFor(de.Id));
        Assert.Null(view.Squad(de.Id));
        Assert.Equal(IssueResult.UnknownUnit, view.Issue(de.Id, new Order(OrderKind.Hold, M(0), M(0))));
        Assert.Equal(IssueResult.UnknownUnit, view.Issue(999, new Order(OrderKind.Hold, M(0), M(0)))); // same answer as a missing id
        world.Schedule(world.Tick, new IssueOrderInput("us", de.Id, new Order(OrderKind.Hold, M(0), M(0)))); // a raw input
        world.Step();
        Assert.Equal(IssueResult.UnknownUnit, Assert.Single(view.Rejections).Reason); // WrongSide would reveal the unit exists
        Assert.Empty(world.ViewFor("de").Rejections);
        Assert.Equal(OrderStatus.None, world.ViewFor("de").StatusOf(de.Id)!.Status);

    }

    [Fact]
    public void A_SideView_shows_own_units_in_full_and_spotted_enemies_as_contacts()
    {
        var (world, org) = Setup();
        var us = org.SpawnSquad("us_rifle_squad", M(20), M(60));
        org.SpawnSquad("de_gruppe", M(60), M(60));
        var view = world.ViewFor("us");
        Assert.Equal(IssueResult.Accepted, view.Issue(us.Id, new Order(OrderKind.Move, M(30), M(80))));
        world.Run(40);

        var squad = Assert.Single(view.Squads);
        Assert.Equal(us.Soldiers.Count, squad.Soldiers.Count);
        Assert.Equal(us.Soldiers.Select(s => (s.Id, s.X, s.Y, s.Posture, s.Health)), squad.Soldiers.Select(s => (s.Id, s.X, s.Y, s.Posture, s.Health)));
        Assert.Equal(us.Leader!.Id, squad.LeaderId);
        Assert.NotEqual(OrderStatus.None, view.StatusOf(us.Id)!.Status);
        Assert.NotNull(view.ReasonsFor(us.Id));
        Assert.NotEmpty(view.KnownEnemies);
        Assert.Equal(squad.Id, view.Squad(us.Id)!.Value.Id);

        var snapshot = view.Squads;
        world.Run(40);
        Assert.NotEqual(snapshot[0].Soldiers[0].Y, view.Squads[0].Soldiers[0].Y); // the old snapshot did not follow the move
        Assert.NotSame(snapshot, view.Squads);
    }
}
