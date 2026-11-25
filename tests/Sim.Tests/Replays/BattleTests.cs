using PointBlank.Sim.Replays;

namespace PointBlank.Sim.Tests.Replays;

/// <summary>Item 0.3: a saved replay re-runs to the same final hash.</summary>
public class BattleTests
{
    private static readonly TallyScenario Scenario = new();

    private static Replay Recorded(ulong seed = 7) => Battle.Run(Scenario, seed).Replay;

    [Fact]
    public void Run_records_the_seed_content_orders_and_final_hash()
    {
        var result = Battle.Run(Scenario, 7);

        Assert.Equal("tally", result.Replay.Scenario);
        Assert.Equal(7UL, result.Replay.Seed);
        Assert.Equal(Scenario.Map, result.Replay.Map);
        Assert.Equal(Scenario.Forces, result.Replay.Forces);
        Assert.Equal(50, result.Replay.Ticks);
        Assert.Equal(4, result.Replay.Orders.Count);
        Assert.Equal(50, result.World.Tick);
        Assert.Equal(result.World.ComputeStateHash(), result.Replay.FinalHash);
    }

    [Fact]
    public void Run_applies_the_scripted_orders()
    {
        var quiet = Scenario.CreateWorld(7);
        quiet.Run(50);

        Assert.NotEqual(quiet.ComputeStateHash(), Recorded().FinalHash);
    }

    [Fact]
    public void A_saved_replay_re_runs_to_the_same_final_hash_for_seeds_1_to_100()
    {
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var recorded = Recorded(seed);
            var loaded = ReplayJson.Read(ReplayJson.Write(recorded));

            Assert.Equal(recorded.FinalHash, Battle.Play(Scenario, loaded).ComputeStateHash());
        }
    }

    [Fact]
    public void Play_uses_the_replays_seed()
    {
        var replay = Recorded();

        Assert.NotEqual(
            replay.FinalHash,
            Battle.Play(Scenario, replay with { Seed = 8 }).ComputeStateHash());
    }

    [Fact]
    public void Play_uses_the_replays_orders_not_the_scenarios()
    {
        var replay = Recorded();
        var edited = replay with { Orders = [new(0, "add", [6]), .. replay.Orders.Skip(1)] };
        var empty = replay with { Orders = [] };

        Assert.NotEqual(replay.FinalHash, Battle.Play(Scenario, edited).ComputeStateHash());
        Assert.NotEqual(replay.FinalHash, Battle.Play(Scenario, empty).ComputeStateHash());
    }

    [Fact]
    public void Orders_on_the_same_tick_keep_their_sequence()
    {
        var replay = Recorded();
        var o = replay.Orders;
        var swapped = replay with { Orders = [o[0], o[2], o[1], o[3]] };

        Assert.NotEqual(replay.FinalHash, Battle.Play(Scenario, swapped).ComputeStateHash());
    }

    [Fact]
    public void Play_runs_for_the_replays_length()
    {
        var shorter = Recorded() with { Ticks = 20, Orders = [] };

        Assert.Equal(20, Battle.Play(Scenario, shorter).Tick);
    }

    [Fact]
    public void Play_rejects_a_replay_of_another_scenario_map_or_forces()
    {
        var replay = Recorded();
        Replay[] wrong =
        [
            replay with { Scenario = "other" },
            replay with { Map = new("flat", 2) },
            replay with { Map = new("hills", 1) },
            replay with { Map = new("flat", 1, 99) },
            replay with { Forces = [] },
            replay with { Forces = [new("counters", 1)] },
        ];

        Assert.All(wrong, r => Assert.Throws<FormatException>(() => Battle.Play(Scenario, r)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50)]
    public void Play_rejects_an_order_outside_the_battle(long tick)
    {
        var replay = Recorded() with { Orders = [new(tick, "add", [1])] };

        Assert.Throws<FormatException>(() => Battle.Play(Scenario, replay));
    }

    [Fact]
    public void Play_rejects_a_negative_length_and_an_unknown_order()
    {
        var replay = Recorded();

        Assert.Throws<FormatException>(
            () => Battle.Play(Scenario, replay with { Ticks = -1, Orders = [] }));
        Assert.Throws<FormatException>(
            () => Battle.Play(Scenario, replay with { Orders = [new(0, "dance", [])] }));
    }
}
