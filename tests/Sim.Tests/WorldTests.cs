namespace PointBlank.Sim.Tests;

public class WorldTests
{
    private sealed class LogSystem(string name, List<string> log) : ISimSystem
    {
        public void Tick(World world) => log.Add($"{world.Tick}:{name}");

        public void AppendState(StateHasher hasher) => hasher.Add(name.Length);
    }

    private sealed class Do(Action<World> action) : ISimInput
    {
        public void Apply(World world) => action(world);
    }

    private static Do Log(List<string> log, string text) => new(w => log.Add($"{w.Tick}:{text}"));

    [Fact]
    public void The_clock_runs_at_ten_ticks_per_simulated_second()
    {
        Assert.Equal(10, World.TicksPerSecond);
        Assert.Equal(Fixed.FromRatio(1, 10), World.TickDuration);
    }

    [Fact]
    public void Tick_counts_completed_steps()
    {
        var world = new World(1);
        Assert.Equal(0, world.Tick);

        world.Step();
        world.Run(24);

        Assert.Equal(25, world.Tick);
    }

    [Fact]
    public void Running_a_negative_number_of_ticks_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new World(1).Run(-1));
    }

    [Fact]
    public void Inputs_run_before_systems_and_both_keep_their_order()
    {
        var log = new List<string>();
        var world = new World(1, new LogSystem("a", log), new LogSystem("b", log));
        world.Schedule(1, Log(log, "later"));
        world.Schedule(0, Log(log, "first"));
        world.Schedule(0, Log(log, "second"));

        world.Run(3);

        Assert.Equal(
            new[] { "0:first", "0:second", "0:a", "0:b", "1:later", "1:a", "1:b", "2:a", "2:b" },
            log);
    }

    [Fact]
    public void Scheduling_a_tick_that_has_already_run_throws()
    {
        var world = new World(1);
        world.Run(5);

        Assert.Throws<ArgumentOutOfRangeException>(() => world.Schedule(4, new Do(_ => { })));
    }

    [Fact]
    public void An_input_may_schedule_a_later_tick_but_not_the_running_one()
    {
        var log = new List<string>();
        var world = new World(1);
        world.Schedule(0, new Do(w =>
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => w.Schedule(0, Log(log, "lost")));
            w.Schedule(1, Log(log, "next"));
        }));

        world.Run(2);

        Assert.Equal(new[] { "1:next" }, log);
    }

    [Fact]
    public void Step_cannot_be_called_from_inside_a_step()
    {
        var world = new World(1);
        world.Schedule(0, new Do(w => Assert.Throws<InvalidOperationException>(w.Step)));

        world.Step();

        Assert.Equal(1, world.Tick);
    }

    [Fact]
    public void GetSystem_returns_the_registered_system()
    {
        var system = new LogSystem("a", new List<string>());

        Assert.Same(system, new World(1, system).GetSystem<LogSystem>());
        Assert.Throws<InvalidOperationException>(() => new World(1).GetSystem<LogSystem>());
    }

    [Fact]
    public void State_hash_covers_seed_tick_rng_and_systems_in_a_fixed_layout()
    {
        var world = new World(42, new LogSystem("abc", new List<string>()));
        world.Run(3);

        var expected = new StateHasher();
        expected.Add(1);               // format version
        expected.Add(42UL);            // seed
        expected.Add(3L);              // tick
        expected.Add(world.Rng.State.State);
        expected.Add(world.Rng.State.Increment);
        expected.Add(1);               // system count
        expected.Add(3);               // LogSystem state

        Assert.Equal(expected.Value, world.ComputeStateHash());
    }

    [Fact]
    public void Drawing_from_the_rng_changes_the_state_hash()
    {
        var world = new World(1);
        var before = world.ComputeStateHash();

        world.Rng.NextUInt();

        Assert.NotEqual(before, world.ComputeStateHash());
    }
}
