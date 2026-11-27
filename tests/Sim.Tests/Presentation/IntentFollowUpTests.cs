using PointBlank.Presentation;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Tests.UnitAi;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Org;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>
/// Item 2.5a: the F4 intent labels tell what each man is doing now. Every expectation is worked out from the sim's own
/// state (rounds, reload, fire task, movement, morale, posture, orders), not from the labelling code.
/// </summary>
public class IntentFollowUpTests
{
    private static readonly IntentKind[] MustAppear =
    [
        IntentKind.Reloading, IntentKind.Aiming, IntentKind.NoAmmo,
        IntentKind.DownBetweenRushes, IntentKind.FiringLine, IntentKind.Flanking, IntentKind.Ambushing, IntentKind.Fleeing,
        IntentKind.Cowering, IntentKind.Firing,
    ];

    /// <summary>Whether the man is one of those his squad's fire task asks to shoot.</summary>
    internal static bool TakesPart(FireSystem fire, Squad q, Soldier s) =>
        fire.FireTaskOf(q.Id) is { } task && (task.TeamMask & (1 << s.TeamIndex)) != 0 && !(task.Ai && fire.IsAiSilent(s.Id));

    /// <summary>On a task, with rounds, not reloading, and the aim point within his weapon's range.</summary>
    internal static bool Free(FireSystem fire, Squad q, Soldier s) =>
        TakesPart(fire, q, s) && fire.RoundsOf(s.Id) > 0 && fire.ReloadTicksOf(s.Id) == 0 && Within(fire, q, s);

    private static bool Within(FireSystem fire, Squad q, Soldier s)
    {
        var task = fire.FireTaskOf(q.Id)!.Value;
        var (dx, dy) = (task.X - s.X, task.Y - s.Y);
        return Fixed.Sqrt((dx * dx) + (dy * dy)) <= fire.Rules.Weapon(s.WeaponId).MaxRange;
    }

    [Fact]
    public void Every_intent_label_matches_the_sims_state_and_every_state_appears()
    {
        var seen = new SortedSet<IntentKind>();
        int notYetCowering = 0, silentOnTheLine = 0;
        foreach (var (scenario, seed, orders) in new[]
        {
            ("fix-assault", 1UL, false), ("fix-assault", 2UL, false), ("bombard-creep", 1UL, false), ("bombard-creep", 2UL, false),
            ("view-sandbox", 1UL, true), ("fix-assault", 3UL, true),
        })
        {
            var c = new GameController(PlayerBattle.Create(scenario, seed, !orders));
            var world = c.Battle.World;
            var fire = world.GetSystem<FireSystem>();
            var move = world.GetSystem<MovementSystem>();
            var command = world.GetSystem<CommandSystem>();
            var squads = world.GetSystem<OrgSystem>().Squads;
            if (orders)
            {
                // The first squad lies in ambush; the others fire at far ground until their rounds are gone.
                var own = c.Battle.View.Squads;
                var map = c.Battle.View.Map!;
                var far = Fixed.FromInt(map.Width) * map.TileSize - Fixed.One;
                c.Battle.View.Issue(own[0].Id, new Order(OrderKind.Ambush, own[0].Soldiers[0].X, own[0].Soldiers[0].Y));
                foreach (var q in own.Skip(1))
                {
                    var man = q.Soldiers[0];
                    var x = man.X + Fixed.FromInt(150) < far ? man.X + Fixed.FromInt(150) : far;
                    c.Battle.View.Issue(q.Id, new Order(OrderKind.Fire, x, man.Y));
                }
            }

            for (var tick = 0; tick < (orders ? 6000 : 1800); tick++)
            {
                c.Step();
                if (orders && tick % 300 == 299)
                {
                    // Fire orders lapse (a pinned or shaken squad, a new order); give them again.
                    foreach (var q in c.Battle.View.Squads.Skip(1).Where(q => command.StatusOf(q.Id).Status != OrderStatus.Executing))
                    {
                        if (command.StatusOf(q.Id).Order is { Kind: OrderKind.Fire } again)
                        {
                            c.Battle.View.Issue(q.Id, again);
                        }
                    }
                }

                foreach (var man in c.Battle.DebugTruth()!.Soldiers)
                {
                    var q = squads.First(x => x.Id == man.SquadId);
                    var s = q.Soldiers.First(x => x.Id == man.Id);
                    if (!s.CanLead)
                    {
                        continue;
                    }

                    var at = $"{scenario} seed {seed} tick {tick} man {s.Id}: fire {man.Fire}, task {man.Task}";
                    var rounds = fire.RoundsOf(s.Id);
                    var task = fire.FireTaskOf(q.Id);
                    var part = TakesPart(fire, q, s);

                    // Fire: the present state first, "firing" only when nothing stops him now.
                    if (rounds < 0)
                    {
                        Assert.True(man.Fire == IntentKind.None, at);
                    }
                    else if (rounds == 0)
                    {
                        Assert.True(man.Fire == IntentKind.NoAmmo && !man.Aims, at);
                    }
                    else if (fire.ReloadTicksOf(s.Id) > 0)
                    {
                        Assert.True(man.Fire == IntentKind.Reloading, at);
                    }
                    else if (!part)
                    {
                        Assert.True(man.Fire == IntentKind.None, at);
                    }
                    else if (!Within(fire, q, s))
                    {
                        Assert.True(man.Fire == IntentKind.OutOfRange, at);
                    }
                    else if (man.Fire != IntentKind.Blocked)
                    {
                        var recent = world.Tick - fire.LastFiredTick(s.Id) <= FireSystem.FiredRecentlyTicks;
                        Assert.True(man.Fire == (recent ? IntentKind.Firing : IntentKind.Aiming), at);
                    }

                    Assert.True(man.Aims == (part && rounds > 0), at);
                    if (man.Aims)
                    {
                        Assert.True((man.FireX, man.FireY) == (task!.Value.X, task.Value.Y), at);
                    }

                    // Drill: only what the man is actually doing.
                    var marching = move.ModeOf(q.Id) != null;
                    var cowering = (world.GetSystem<MoraleSystem>().ReasonsOf(q.Id) & MoraleReason.Cowering) != 0;
                    var order = command.StatusOf(q.Id);
                    var ok = man.Task switch
                    {
                        IntentKind.Cowering => cowering && s.Posture == Posture.Prone,
                        IntentKind.Fleeing => q.Morale == MoraleState.Routing && marching,
                        IntentKind.Rushing => marching && move.IsSoldierMoving(s.Id),
                        IntentKind.DownBetweenRushes => marching,
                        IntentKind.Bounding => marching && (move.HeldTeamsOf(q.Id) & (1 << s.TeamIndex)) == 0,
                        IntentKind.Covering => marching && (move.HeldTeamsOf(q.Id) & (1 << s.TeamIndex)) != 0,
                        IntentKind.FiringLine => !marching && part && Free(fire, q, s),
                        IntentKind.Flanking => !marching && (move.IsSoldierMoving(s.Id) || (part && Free(fire, q, s))),
                        IntentKind.Defending => !marching && order.Status == OrderStatus.Done && order.Order!.Kind == OrderKind.Defend,
                        IntentKind.Ambushing => !marching && order.Status == OrderStatus.Done && order.Order!.Kind == OrderKind.Ambush,
                        _ => man.Task == IntentKind.None,
                    };
                    Assert.True(ok, at);
                    notYetCowering += cowering && man.Task != IntentKind.Cowering ? 1 : 0;
                    silentOnTheLine += !marching && task is { Ai: true } && !part && man.Task == IntentKind.None ? 1 : 0;
                    seen.Add(man.Fire);
                    seen.Add(man.Task);
                }
            }
        }

        Assert.Empty(MustAppear.Except(seen));
        Assert.True(notYetCowering > 0, "a whole squad always cowers at once");
        Assert.True(silentOnTheLine > 0, "no man without a part in the fire was ever seen");
    }

    // Neither happens in the player scenarios: their maps are smaller than every weapon's range, and their moving squads
    // have two teams (bounding and covering).
    [Fact]
    public void A_man_beyond_his_weapons_range_reads_out_of_range()
    {
        var rig = AiRig.Create(1, new GameMap("open", 600, 40), noCasualties: true);
        var de = rig.Org.SpawnSquad("de_gruppe", AiRig.M(20), AiRig.M(40));
        var rifle = rig.Fire.Rules.Weapon(de.Soldiers.First(s => s.WeaponId.Contains("rifle", StringComparison.Ordinal)).WeaponId).MaxRange;
        var farX = AiRig.M(20) + rifle + AiRig.M(100); // beyond the rifles, within the machine gun
        rig.World.ViewFor("de").Issue(de.Id, new Order(OrderKind.Fire, farX, AiRig.M(40)));
        var found = 0;
        for (var tick = 0; tick < 400; tick++)
        {
            rig.World.Step();
            foreach (var man in rig.Spot.DebugTruth(rig.World).Soldiers.Where(m => m.Fire == IntentKind.OutOfRange))
            {
                var s = de.Soldiers.First(x => x.Id == man.Id);
                Assert.True(TakesPart(rig.Fire, de, s) && !Within(rig.Fire, de, s), $"tick {tick}, man {s.Id}");
                found++;
            }
        }

        Assert.True(found > 0);
    }

    [Fact]
    public void A_walking_man_of_a_one_team_squad_under_fire_reads_rushing_and_down_between_rushes()
    {
        var seen = new HashSet<IntentKind>();
        for (var seed = 1UL; seed <= 5; seed++)
        {
            var rig = AiRig.Create(seed, new GameMap("open", 200, 60), noCasualties: true);
            var hq = rig.Org.SpawnSquad("us_platoon_hq", AiRig.M(40), AiRig.M(60));
            var de = rig.Org.SpawnSquad("de_gruppe", AiRig.M(200), AiRig.M(60));
            rig.World.ViewFor("us").Issue(hq.Id, new Order(OrderKind.Move, AiRig.M(150), AiRig.M(60)));
            rig.World.ViewFor("de").Issue(de.Id, new Order(OrderKind.Fire, AiRig.M(80), AiRig.M(60)));
            for (var tick = 0; tick < 900; tick++)
            {
                rig.World.Step();
                foreach (var man in rig.Spot.DebugTruth(rig.World).Soldiers.Where(m => m.SquadId == hq.Id))
                {
                    if (man.Task is IntentKind.Rushing or IntentKind.DownBetweenRushes)
                    {
                        Assert.NotNull(rig.Move.ModeOf(hq.Id));
                        Assert.True(man.Task != IntentKind.Rushing || rig.Move.IsSoldierMoving(man.Id), $"seed {seed} tick {tick}, man {man.Id}");
                        seen.Add(man.Task);
                    }
                }
            }
        }

        Assert.Equal(2, seen.Count);
    }
}
