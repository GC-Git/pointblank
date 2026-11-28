using PointBlank.Runner;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Item 2.4c measurement: how long men with a fire task are masked by friendlies, by whom, and what each squad fires,
/// over seeds 1-50. A report, not a test: it runs only with <c>PB_MASK_REPORT=1</c> (use detailed console logging).
/// </summary>
public class MaskedFireMetrics(ITestOutputHelper output)
{
    [Theory]
    [InlineData("fix-assault")]
    [InlineData("fix-assault-nobase")]
    [InlineData("bombard-creep")]
    [InlineData("pin-open")]
    public void Report_masking(string id)
    {
        if (Environment.GetEnvironmentVariable("PB_MASK_REPORT") != "1")
        {
            return;
        }

        var scenario = ScenarioCatalog.Find(id)!;
        var tally = new SortedDictionary<int, Tally>();
        for (ulong seed = 1; seed <= 50; seed++)
        {
            Measure(scenario, seed, tally);
        }

        foreach (var (q, t) in tally)
        {
            output.WriteLine($"{id} squad {q} ({t.Side}): {t}");
        }
    }

    private static void Measure(IScenario scenario, ulong seed, SortedDictionary<int, Tally> tally)
    {
        var world = scenario.CreateWorld(seed);
        var org = world.GetSystem<OrgSystem>();
        var fire = world.GetSystem<FireSystem>();
        var run = new Dictionary<int, int>();
        var shots = new Dictionary<int, int>();
        var taskTicks = new Dictionary<int, int>();
        var blockedTicks = new Dictionary<int, int>();
        foreach (var order in scenario.ScriptedOrders)
        {
            world.Schedule(order.Tick, scenario.CreateInput(order));
        }

        var move = world.GetSystem<MovementSystem>();
        var suppression = world.GetSystem<SuppressionSystem>();
        for (var t = 0; t < scenario.Ticks; t++)
        {
            world.Step();
            foreach (var q in org.Squads)
            {
                if (!tally.TryGetValue(q.Id, out var a))
                {
                    tally[q.Id] = a = new Tally { Side = q.Side };
                }

                var task = fire.FireTaskOf(q.Id);
                foreach (var s in q.Soldiers)
                {
                    if (fire.LastFiredTick(s.Id) == world.Tick - 1 || fire.LastFiredTick(s.Id) == world.Tick)
                    {
                        if (!shots.TryGetValue(s.Id, out var last) || last != (int)fire.LastFiredTick(s.Id))
                        {
                            a.ShotTicks++;
                            shots[s.Id] = (int)fire.LastFiredTick(s.Id);
                        }
                    }

                    if (task is not { } f || !s.CanLead)
                    {
                        End(s.Id, a);
                        continue;
                    }

                    var kind = fire.ActivityOf(q, s, world.Tick).Activity;
                    if (kind is not (IntentKind.Blocked or IntentKind.Firing or IntentKind.Aiming))
                    {
                        End(s.Id, a);
                        continue;
                    }

                    a.TaskTicks++;
                    taskTicks[s.Id] = taskTicks.GetValueOrDefault(s.Id) + 1;
                    if (kind != IntentKind.Blocked)
                    {
                        End(s.Id, a);
                        continue;
                    }

                    a.Blocked++;
                    a.SquadMoving += move.IsMoving(q.Id) ? 1 : 0;
                    blockedTicks[s.Id] = blockedTicks.GetValueOrDefault(s.Id) + 1;
                    run[s.Id] = run.GetValueOrDefault(s.Id) + 1;
                    if (!f.Ai)
                    {
                        a.PlayerOrder++;
                    }

                    if (move.IsSoldierMoving(s.Id))
                    {
                        a.FirerMoving++;
                    }

                    // Masked time of a stationary squad, and the share spent pinned, broken or suppressed.
                    if (!move.IsMoving(q.Id))
                    {
                        a.Still++;
                        var squad = suppression.IsPinned(q.Id) || q.Morale is MoraleState.Pinned or MoraleState.Broken or MoraleState.Routing;
                        a.SquadDown += squad ? 1 : 0;
                        a.ManDown += !squad && suppression.StateOf(s.Id) != SuppressionState.None ? 1 : 0;
                    }

                    Blockers(org, q, s, f.X, f.Y, f.Ai ? fire.Rules.NearMissRadius : fire.Rules.MaskClearance, a, move);
                }
            }
        }

        foreach (var q in org.Squads)
        {
            var a = tally[q.Id];
            foreach (var s in q.Soldiers)
            {
                End(s.Id, a);
                if (taskTicks.GetValueOrDefault(s.Id) >= 100)
                {
                    a.MenWithTask++;
                    if (blockedTicks.GetValueOrDefault(s.Id) * 2 > taskTicks[s.Id])
                    {
                        a.MenMostlyBlocked++;
                    }
                }
            }
        }

        void End(int man, Tally a)
        {
            if (run.TryGetValue(man, out var n) && n > 0)
            {
                a.Runs.Add(n);
                run[man] = 0;
            }
        }
    }

    private static void Blockers(OrgSystem org, Squad q, Soldier s, Fixed x, Fixed y, Fixed clearance, Tally a, MovementSystem move)
    {
        double sx = D(s.X), sy = D(s.Y), dx = D(x) - sx, dy = D(y) - sy, d = Math.Sqrt((dx * dx) + (dy * dy)), c = D(clearance);
        bool own = false, other = false, abreast = true, near = true, onlyOutsideHalf = true, moving = true, low = true;
        foreach (var o in org.Squads.Where(o => o.Side == q.Side))
        {
            foreach (var f in o.Soldiers.Where(f => f != s && f.CanLead))
            {
                var along = (((D(f.X) - sx) * dx) + ((D(f.Y) - sy) * dy)) / d;
                var lat = Math.Abs((((D(f.X) - sx) * dy) - ((D(f.Y) - sy) * dx)) / d);
                if (along <= 0 || along >= d || lat > c)
                {
                    continue;
                }

                own |= o == q;
                other |= o != q;
                abreast &= along < c; // not further ahead than the clearance
                near &= along < 10;
                onlyOutsideHalf &= lat > 0.5;
                moving &= move.IsSoldierMoving(f.Id);
                low &= f.Posture == Posture.Prone && s.Posture != Posture.Prone;
            }
        }

        if (!own && !other)
        {
            return;
        }

        a.OwnOnly += own && !other ? 1 : 0;
        a.OtherSquad += other ? 1 : 0;
        a.Abreast += abreast ? 1 : 0;
        a.Near += near ? 1 : 0;
        a.Wide += onlyOutsideHalf ? 1 : 0;
        a.BlockersMoving += moving ? 1 : 0;
        a.ProneBelow += low ? 1 : 0;
    }

    private static double D(Fixed f) => f.Raw / 4294967296.0;

    private sealed class Tally
    {
        public string Side { get; init; } = "";
        public long Still, SquadDown, ManDown, SquadMoving, TaskTicks, Blocked, PlayerOrder, FirerMoving, OwnOnly, OtherSquad, Abreast, Near, Wide, BlockersMoving, ProneBelow, ShotTicks, MenWithTask, MenMostlyBlocked;
        public List<int> Runs { get; } = [];

        public override string ToString()
        {
            string P(long n) => Blocked == 0 ? "-" : $"{100.0 * n / Blocked:F0}%";
            Runs.Sort();
            var q = Runs.Count == 0 ? "-" : $"{Runs.Count} runs, median {Runs[Runs.Count / 2] / 10.0:F1} s, p90 {Runs[Runs.Count * 9 / 10] / 10.0:F1} s, max {Runs[^1] / 10.0:F0} s, over 10 s {Runs.Count(r => r > 100)} ({(Blocked == 0 ? 0 : 100.0 * Runs.Where(r => r > 100).Sum() / Blocked):F0}% of blocked time)";
            string S(long n) => Still == 0 ? "-" : $"{100.0 * n / Still:F0}%";
            return $"stationary blocked {Still}: squad pinned or broken {S(SquadDown)}, else man suppressed {S(ManDown)}; on-task man-ticks {TaskTicks}, blocked {Blocked} ({(TaskTicks == 0 ? 0 : 100.0 * Blocked / TaskTicks):F1}%), man-shot-ticks/seed {ShotTicks / 50.0:F0}; men with >=10 s task {MenWithTask}, blocked over half of it {MenMostlyBlocked}; "
                + $"of blocked: squad on a move {P(SquadMoving)}, player order {P(PlayerOrder)}, firer moving {P(FirerMoving)}, own squad only {P(OwnOnly)}, another squad {P(OtherSquad)}, all blockers abreast {P(Abreast)}, all within 10 m {P(Near)}, all over 0.5 m off line {P(Wide)}, all blockers moving {P(BlockersMoving)}, blockers all prone below upright firer {P(ProneBelow)}; {q}";
        }
    }
}
