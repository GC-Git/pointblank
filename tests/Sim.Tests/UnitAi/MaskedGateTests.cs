using PointBlank.Sim.Artillery;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.UnitAi;

namespace PointBlank.Sim.Tests.UnitAi;

/// <summary>
/// Item 2.4c review: when a man may not step aside for a clear line, how a walk under way is called off, and the limits
/// on where, how and how often he goes.
/// </summary>
public class MaskedGateTests
{
    private const int AimX = 100;

    private static (AiRig Rig, Squad Squad) Block(ulong seed, OrderKind? order = OrderKind.Fire, GameMap? map = null)
    {
        var rig = AiRig.Create(seed, map ?? new GameMap("open", 200, 60), morale: false);
        var q = rig.Org.SpawnSquad("us_rifle_squad", AiRig.M(250), AiRig.M(60), Posture.Kneeling);
        if (order is { } kind)
        {
            rig.Order(q, kind, AimX, 60);
        }

        return (rig, q);
    }

    private static Soldier FirstWalker(AiRig rig, Squad q)
    {
        for (var t = 0; t < 600; t++)
        {
            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
            if (q.Soldiers.FirstOrDefault(s => rig.Move.IsSoldierMoving(s.Id)) is { } s)
            {
                return s;
            }
        }

        throw new InvalidOperationException("Nobody shifted.");
    }

    private static void RunCalm(AiRig rig, int ticks)
    {
        for (var t = 0; t < ticks; t++)
        {
            foreach (var q in rig.Org.Squads)
            {
                rig.Calm(q);
            }

            rig.World.Step();
        }
    }

    private static void NearMiss(AiRig rig, Soldier s) =>
        ((List<NearMissEvent>)rig.Fire.NearMisses).Add(
            new NearMissEvent(rig.World.Tick, s.Id, -999, s.X - AiRig.M(200), s.Y, CalibreClass.Rifle, Fixed.One));

    private static void Level(AiRig rig, Soldier s, int level) => rig.Suppression.SetLevel(s.Id, Fixed.FromInt(level));

    /// <summary>Puts the condition on the squad; called before every step.</summary>
    private static void Apply(string condition, AiRig rig, Squad q, Soldier? walker)
    {
        switch (condition)
        {
            case "man suppressed":
                foreach (var s in walker != null ? [walker] : q.Soldiers.ToArray())
                {
                    Level(rig, s, 30); // Suppressed, below the fire-hold level
                }

                break;
            case "squad pinned":
                foreach (var s in q.Soldiers)
                {
                    Level(rig, s, s == walker ? 0 : 100); // the walker himself is calm
                }

                break;
            case "broken":
            case "routing":
                if (q.Morale == MoraleState.Steady)
                {
                    rig.World.Schedule(rig.World.EarliestInputTick,
                        new SetMoraleInput(q.Id, condition == "broken" ? MoraleState.Broken : MoraleState.Routing));
                }

                break;
        }
    }

    [Theory]
    [InlineData("man suppressed")]
    [InlineData("squad pinned")]
    [InlineData("broken")]
    [InlineData("routing")]
    public void A_shift_walk_stops_and_the_man_ends_low(string condition)
    {
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var (rig, q) = Block(seed);
            var walker = FirstWalker(rig, q);
            walker.Posture = Posture.Standing;
            var starts = 0;
            var was = false;
            for (var t = 0; t < 200; t++)
            {
                rig.Calm(q);
                Apply(condition, rig, q, walker);
                rig.World.Step();
                var moving = rig.Move.IsSoldierMoving(walker.Id);
                if (t >= 15)
                {
                    starts += moving && !was ? 1 : 0;
                    Assert.False(moving, $"seed {seed}: still walking {t} ticks after");
                }

                was = moving;
            }

            Assert.Equal(Posture.Prone, walker.Posture); // open ground: flat, not merely crouched
            Assert.Equal(0, starts);
        }
    }

    [Theory]
    [InlineData("man suppressed")]
    [InlineData("squad pinned")]
    [InlineData("broken")]
    [InlineData("routing")]
    public void No_shift_starts(string condition)
    {
        var (rig, q) = Block(1);
        for (var t = 0; t < 400; t++)
        {
            rig.Calm(q);
            Apply(condition, rig, q, null);
            if (condition == "squad pinned")
            {
                Level(rig, q.Soldiers[0], 0); // one calm man in a pinned squad
            }

            rig.World.Step();
            Assert.DoesNotContain(q.Soldiers, s => rig.Move.IsSoldierMoving(s.Id));
        }
    }

    [Fact]
    public void Shells_landing_near_stop_a_walk_and_every_start()
    {
        var shell = ArtilleryRules.FromData(AiRig.Data).For("us")! with { LethalChance = Fixed.Zero };
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var rig = AiRig.Create(seed, new GameMap("open", 200, 60), morale: false, artillery: true);
            var q = rig.Org.SpawnSquad("us_rifle_squad", AiRig.M(250), AiRig.M(60), Posture.Kneeling);
            rig.Order(q, OrderKind.Fire, AimX, 60);
            var walker = FirstWalker(rig, q);
            for (var t = 0; t < 300; t++)
            {
                if (t % 20 == 0)
                {
                    rig.Arty!.Burst(rig.World, shell, walker.X, walker.Y);
                }

                foreach (var s in q.Soldiers)
                {
                    Level(rig, s, 0); // only the shells themselves, not the suppression they cause
                }

                rig.World.Step();
                if (t >= 15)
                {
                    Assert.DoesNotContain(q.Soldiers, s => rig.Move.IsSoldierMoving(s.Id));
                    Assert.False(rig.Move.IsSoldierMoving(walker.Id), $"seed {seed}, tick {t}");
                }
            }

            Assert.Equal(Posture.Prone, walker.Posture);
        }
    }

    [Fact]
    public void A_man_just_shot_at_does_not_start_while_his_comrades_do()
    {
        var (rig, q) = Block(1);
        while (rig.Command.StatusOf(q.Id).Status != OrderStatus.Executing)
        {
            rig.World.Step(); // the fire order arrives first, so the AI forms no line of its own
        }

        var target = q.Soldiers.ToArray();
        var cool = rig.Ai!.Rules.UnmaskFireCoolOffTicks;
        var hot = target.Where((_, i) => i % 2 == 0).ToList();
        var calm = 0;
        var lastRound = 0L;
        var firstHot = long.MaxValue;
        for (var t = 0; t < 900; t++)
        {
            if (t < 300 && t % 10 == 0)
            {
                lastRound = rig.World.Tick;
                hot.ForEach(s => NearMiss(rig, s));
            }

            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
            if (t < 300)
            {
                Assert.DoesNotContain(hot, s => rig.Move.IsSoldierMoving(s.Id));
                calm += target.Except(hot).Count(s => rig.Move.IsSoldierMoving(s.Id));
            }
            else if (hot.Any(s => rig.Move.IsSoldierMoving(s.Id)))
            {
                firstHot = Math.Min(firstHot, rig.World.Tick);
            }
        }

        Assert.True(calm > 0, "the others shift");
        Assert.True(firstHot > lastRound + cool, $"shot-at man set off at {firstHot}, last round {lastRound}");
        Assert.True(firstHot < long.MaxValue, "after the cool-off they may go");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_squad_told_to_Hold_with_an_enemy_firing_on_it_does_not_shift(bool hold)
    {
        var (rig, q, a, c) = Pair(hold ? OrderKind.Hold : OrderKind.Defend, null);
        RunCalm(rig, 300); // the order is delivered
        for (var t = 0; t < 900; t++)
        {
            if (t % 10 == 0)
            {
                NearMiss(rig, c); // the squad fires back on its own
            }

            if (t == 200)
            {
                (c.X, c.Y) = (a.X - AiRig.M(6), a.Y); // after the firing line has formed, c stands in a's way
            }

            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
            if (t == 200)
            {
                break;
            }
        }

        var (x, y) = (a.X, a.Y);
        for (var t = 0; t < 600; t++)
        {
            if (t % 10 == 0)
            {
                NearMiss(rig, c);
            }

            (c.X, c.Y) = (a.X == x && a.Y == y) ? (x - AiRig.M(6), y) : (c.X, c.Y);
            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
        }

        Assert.NotNull(rig.Fire.FireTaskOf(q.Id));
        Assert.Equal(!hold, a.X != x || a.Y != y); // the control (Defend): without Hold he steps aside
    }

    // One masked man (a) behind one comrade (c); everyone else is out of it.
    private static (AiRig Rig, Squad Squad, Soldier A, Soldier C) Pair(GameMap? map = null, params (int Dx, int Dy)[] extra) =>
        Pair(OrderKind.Fire, map, extra);

    private static (AiRig Rig, Squad Squad, Soldier A, Soldier C) Pair(OrderKind order, GameMap? map, params (int Dx, int Dy)[] extra)
    {
        var (rig, q) = Block(1, order, map);
        var men = q.Soldiers.ToArray();
        var a = men[1];
        var c = men[2];
        Put(a, 125, 30);
        Put(c, 122, 30);
        for (var i = 3; i < men.Length; i++)
        {
            if (i - 3 < extra.Length)
            {
                Put(men[i], 125 + extra[i - 3].Dx, 30 + extra[i - 3].Dy);
            }
            else
            {
                men[i].Health = HealthState.Dead;
            }
        }

        men[0].Health = HealthState.Dead;
        return (rig, q, a, c);

        static void Put(Soldier s, int tx, int ty) => (s.X, s.Y) = (AiRig.M((tx * 2) + 1), AiRig.M((ty * 2) + 1));
    }

    private static List<(long Tick, Fixed X)> ShiftsOfA(AiRig rig, Soldier a, Soldier c, int ticks)
    {
        var starts = new List<(long, Fixed)>();
        var was = false;
        for (var t = 0; t < ticks; t++)
        {
            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
            var moving = rig.Move.IsSoldierMoving(a.Id);
            if (moving && !was)
            {
                starts.Add((rig.World.Tick, a.X));
            }
            else if (!moving && was)
            {
                (c.X, c.Y) = (a.X - AiRig.M(6), a.Y); // the comrade is in his way again
            }

            was = moving;
        }

        return starts;
    }

    [Fact]
    public void A_man_masked_again_and_again_gives_up_after_the_limit_and_waits_between_looks()
    {
        var (rig, _, a, c) = Pair();
        var starts = ShiftsOfA(rig, a, c, 1500);
        var rules = rig.Ai!.Rules;

        Assert.Equal(rules.UnmaskMaxShifts, starts.Count);
        Assert.All(starts.Zip(starts.Skip(1)), p => Assert.True(p.Second.Tick - p.First.Tick >= rules.UnmaskRetryTicks,
            $"looked again after {p.Second.Tick - p.First.Tick} ticks"));
    }

    [Fact]
    public void Repeated_shifts_do_not_creep_toward_the_target()
    {
        var (rig, _, a, c) = Pair();
        var from = a.X;
        var walked = false;
        for (var t = 0; t < 600 && !(walked && !rig.Move.IsSoldierMoving(a.Id)); t++)
        {
            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
            walked |= rig.Move.IsSoldierMoving(a.Id);
        }

        Assert.True(walked);
        a.X = from - AiRig.M(6); // as if earlier steps had carried him forward
        (c.X, c.Y) = (a.X - AiRig.M(6), a.Y);
        var (x, y) = (a.X, a.Y);
        RunCalm(rig, 600);

        Assert.True((a.X == x && a.Y == y) || a.X >= from - AiRig.M(2), $"he ends {from - a.X} m ahead of where he started");
    }

    private sealed class Recorder(World world) : IAiEffects
    {
        public List<(long Tick, int Soldier)> Moves { get; } = [];

        public bool MoveSoldier(int squadId, int soldierId, TilePos goal, bool fast)
        {
            Moves.Add((world.Tick, soldierId));
            return true; // nobody actually moves: the men stay masked
        }

        public void SetPosture(int soldierId, Posture posture, bool timed = false, bool wait = false)
        {
        }

        public void StopSoldier(int squadId, int soldierId)
        {
        }

        public void SetHeldTeams(int squadId, int teamMask, bool rush)
        {
        }

        public void SetHeldSoldier(int squadId, int soldierId, bool held)
        {
        }

        public void SetFire(int squadId, int targetId, Fixed x, Fixed y, int teamMask)
        {
        }

        public void StopFire(int squadId)
        {
        }

        public void SetSilent(int squadId, int soldierId, bool silent)
        {
        }
    }

    [Fact]
    public void A_shift_never_replaces_a_firing_line_move_made_the_same_tick()
    {
        var reforms = 0;
        // The line reforms when the enemy turns up on another bearing: try that at many moments of the men's masked wait.
        for (var turn = 20; turn < 160; turn++)
        {
            var rig = AiRig.Create(1, new GameMap("open", 200, 60), ai: false, morale: false);
            var us = rig.Org.SpawnSquad("us_rifle_squad", AiRig.M(250), AiRig.M(60), Posture.Kneeling);
            var de = rig.Org.SpawnSquad("de_gruppe", AiRig.M(170), AiRig.M(60), Posture.Standing);
            var brain = new UnitAiBrain(rig.Map, rig.Move.Rules, rig.Fire.Rules, UnitAiRules.FromData(AiRig.Data), rig.Move.Paths);
            var fx = new Recorder(rig.World);
            for (var t = 0; t < turn + 40; t++)
            {
                if (t == turn)
                {
                    foreach (var (s, i) in de.Soldiers.Select((s, i) => (s, i)))
                    {
                        (s.X, s.Y) = (AiRig.M(240 + i), AiRig.M(5));
                    }
                }

                rig.World.Step();
                brain.Think(rig.World.ViewFor(us.Side), fx);
            }

            var perTick = fx.Moves.GroupBy(m => m.Tick).ToList();
            reforms += perTick.Count(g => g.Key > turn && g.Count() >= 6);
            Assert.All(perTick, g => Assert.True(g.Count() == g.Select(m => m.Soldier).Distinct().Count(), $"turn {turn}: a man sent twice on tick {g.Key}"));
        }

        Assert.True(reforms >= 50, $"the line reformed {reforms} times");
    }

    [Fact]
    public void A_later_walk_of_another_kind_is_not_taken_for_the_shift()
    {
        var (rig, q, a, _) = Pair();
        for (var t = 0; t < 600 && !rig.Move.IsSoldierMoving(a.Id); t++)
        {
            RunCalm(rig, 1);
        }

        RunCalm(rig, 200); // he has arrived
        Assert.False(rig.Move.IsSoldierMoving(a.Id));
        Assert.True(rig.Move.MoveSoldier(q.Id, a.Id, new TilePos(140, 40), MoveMode.Move)); // another drill sends him on
        for (var t = 0; t < 20; t++)
        {
            Level(rig, a, 30);
            rig.World.Step();
        }

        Assert.True(rig.Move.IsSoldierMoving(a.Id));
    }

    [Fact]
    public void He_does_not_step_out_of_cover_to_get_clear()
    {
        // Woods exactly as wide as the blocked rows: every clear tile is the first open one beyond them.
        var map = new GameMap("woods", 200, 60);
        for (var ty = 28; ty <= 32; ty++)
        {
            for (var tx = 0; tx < 200; tx++)
            {
                map.SetTerrain(tx, ty, TerrainType.LightWoods);
            }
        }

        var (rig, _, a, _) = Pair(map, (-3, -1), (-3, 1));
        var (x, y) = (a.X, a.Y);
        RunCalm(rig, 600);

        Assert.True(a.X == x && a.Y == y, $"he walked to {a.X},{a.Y}");
    }

    [Fact]
    public void He_does_not_walk_through_a_comrades_line_of_fire_to_get_clear()
    {
        // Two comrades behind him on either side: their lines run past him three tiles out.
        var (open, _, a0, _) = Pair(null, (-3, -1), (-3, 1));
        var (rig, _, a, _) = Pair(null, (8, -3), (8, 3), (-3, -1), (-3, 1));
        var (x, y) = (a.X, a.Y);
        var control = false;
        for (var t = 0; t < 600; t++)
        {
            open.World.Step();
            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
            control |= open.Move.IsSoldierMoving(a0.Id);
        }

        Assert.True(control, "without the lines he goes");
        Assert.True(a.X == x && a.Y == y, $"he walked to {a.X},{a.Y}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void He_does_not_leave_cover_on_the_way_to_a_clear_line(bool gap)
    {
        var map = new GameMap("woods", 200, 60);
        for (var ty = 29; ty <= 40; ty++)
        {
            for (var tx = 0; tx < 200; tx++)
            {
                if (!gap || ty <= 31 || ty >= 35)
                {
                    map.SetTerrain(tx, ty, TerrainType.LightWoods);
                }
            }
        }

        var (rig, _, a, _) = Pair(map, (-3, -1), (-3, 1));
        var y = a.Y;
        var walked = false;
        for (var t = 0; t < 600; t++)
        {
            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
            walked |= rig.Move.IsSoldierMoving(a.Id);
        }

        Assert.Equal(!gap, walked);
        Assert.True(!gap || a.Y == y);
    }

    [Fact]
    public void A_man_who_has_left_the_tile_he_shifted_to_no_longer_counts_as_being_there()
    {
        var (rig, _, a, c) = Pair();
        var (ax, ay, cx, cy) = (a.X, a.Y, c.X, c.Y);
        var (twin, _, b, _) = Pair();
        for (var t = 0; t < 600 && !twin.Move.IsSoldierMoving(b.Id); t++)
        {
            twin.Calm(twin.Org.Squads[0]);
            twin.World.Step();
        }

        RunCalm(twin, 200);
        var (tx, ty) = (b.X, b.Y); // the tile a man in this spot picks

        for (var t = 0; t < 600 && !rig.Move.IsSoldierMoving(a.Id); t++)
        {
            rig.Calm(rig.Org.Squads[0]);
            rig.World.Step();
        }

        RunCalm(rig, 200);
        Assert.True(a.X == tx && a.Y == ty);

        // a leaves (and stops firing: no ammunition is simplest to express as being far out of range); another man takes his place.
        (a.X, a.Y) = (AiRig.M(399), AiRig.M(101));
        var d = rig.Org.SpawnSquad("us_rifle_squad", AiRig.M(250), AiRig.M(60), Posture.Kneeling);
        var men = d.Soldiers.ToArray();
        foreach (var s in men.Skip(1))
        {
            s.Health = HealthState.Dead;
        }

        (men[0].X, men[0].Y) = (ax, ay);
        (c.X, c.Y) = (cx, cy);
        rig.Order(d, OrderKind.Fire, AimX, 60);
        RunCalm(rig, 300);

        Assert.True(men[0].X == tx && men[0].Y == ty, $"he went to {men[0].X},{men[0].Y}, not {tx},{ty}");
    }

    [Fact]
    public void A_moving_squad_does_not_count_down_to_a_shift_before_it_has_arrived()
    {
        foreach (var seed in new ulong[] { 1, 2, 3 })
        {
            var (rig, q) = Block(seed, null);
            rig.Order(q, OrderKind.MoveFast, 240, 60);
            var arrived = -1L;
            var marched = false;
            var shifted = false;
            for (var t = 0; t < 900; t++)
            {
                if (t % 10 == 0)
                {
                    NearMiss(rig, q.Soldiers[0]);
                }

                rig.Calm(rig.Org.Squads[0]);
                rig.World.Step();
                marched |= rig.Move.IsMoving(q.Id);
                if (arrived < 0 && marched && !rig.Move.IsMoving(q.Id))
                {
                    arrived = rig.World.Tick;
                }
                else if (arrived >= 0 && q.Soldiers.Any(s => rig.Move.IsSoldierMoving(s.Id)))
                {
                    Assert.True(rig.World.Tick - arrived >= rig.Ai!.Rules.UnmaskDelayMinTicks, $"seed {seed}: shift {rig.World.Tick - arrived} ticks after arriving");
                    shifted = true;
                    break;
                }
            }

            Assert.True(shifted, $"seed {seed}: nobody shifted after arriving");
        }
    }
}
