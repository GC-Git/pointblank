namespace PointBlank.Sim.Tests;

/// <summary>Item 0.2: same seed and same scripted inputs give the same battle.</summary>
public class DeterminismTests
{
    private const int LongRun = 10_000;

    /// <summary>
    /// Test-only stand-in for game rules: points wander at random, so the state depends on
    /// the RNG, on <see cref="Fixed"/> arithmetic and on the scripted inputs.
    /// </summary>
    private sealed class WanderSystem : ISimSystem
    {
        public Fixed[] X { get; } = new Fixed[8];
        public Fixed[] Y { get; } = new Fixed[8];
        public Fixed Travelled { get; private set; }

        public void Tick(World world)
        {
            for (int i = 0; i < X.Length; i++)
            {
                var dx = Step(world);
                var dy = Step(world);
                X[i] += dx;
                Y[i] += dy;
                Travelled += Fixed.Sqrt(dx * dx + dy * dy);
            }
        }

        private static Fixed Step(World world) =>
            Fixed.FromInt(world.Rng.NextInt(-5, 6)) * World.TickDuration/ Fixed.FromInt(3);

        public void AppendState(StateHasher hasher)
        {
            hasher.Add(X.Length);
            for (int i = 0; i < X.Length; i++)
            {
                hasher.Add(X[i]);
                hasher.Add(Y[i]);
            }

            hasher.Add(Travelled);
        }
    }

    private sealed class Nudge(int index, int distance) : ISimInput
    {
        public void Apply(World world) =>
            world.GetSystem<WanderSystem>().X[index] += Fixed.FromInt(distance);
    }

    private static World Run(ulong seed, int ticks, int nudge = 25)
    {
        var world = new World(seed, new WanderSystem());
        world.Schedule(0, new Nudge(0, 10));
        world.Schedule(50, new Nudge(3, nudge));
        world.Schedule(50, new Nudge(4, -7));
        world.Schedule(9_999, new Nudge(7, 1));
        world.Run(ticks);
        return world;
    }

    [Fact]
    public void Same_seed_and_inputs_give_the_same_hash_after_10000_ticks_in_3_runs()
    {
        var hashes = Enumerable.Range(0, 3)
            .Select(_ => Run(seed: 1, LongRun).ComputeStateHash())
            .ToArray();

        Assert.Single(hashes.Distinct());
    }

    [Fact]
    public void The_10000_tick_hash_matches_the_pinned_value()
    {
        // Pinned so that drift between processes or machines is caught, which three runs in
        // one process cannot do. Change it only for a deliberate change to the sim's
        // behaviour or hash layout, and say so in the PR: it invalidates saved replays.
        Assert.Equal(13893661199542830400UL,Run(seed: 1, LongRun).ComputeStateHash());
    }

    [Fact]
    public void Different_seeds_give_different_hashes_and_different_battles()
    {
        var first = Run(1, LongRun);
        var second = Run(2, LongRun);
        var third = Run(3, LongRun);

        var hashes = new[] { first, second, third }.Select(w => w.ComputeStateHash());
        Assert.Equal(3, hashes.Distinct().Count());

        // The hash includes the seed, so also check the simulated state itself differs.
        Assert.NotEqual(first.GetSystem<WanderSystem>().X, second.GetSystem<WanderSystem>().X);
    }

    [Fact]
    public void Seeds_1_to_100_all_give_different_hashes()
    {
        var hashes = Enumerable.Range(1, 100)
            .Select(seed => Run((ulong)seed, ticks: 100).ComputeStateHash());

        Assert.Equal(100, hashes.Distinct().Count());
    }

    [Fact]
    public void Changing_one_scripted_input_changes_the_hash()
    {
        Assert.NotEqual(
            Run(1, LongRun, nudge: 25).ComputeStateHash(),
            Run(1, LongRun, nudge: 26).ComputeStateHash());
    }
}
