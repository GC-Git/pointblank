namespace PointBlank.Sim.Tests;

/// <summary>
/// Test classes with wall-clock budget checks share this collection. It is not run in parallel with anything
/// (item 2.12): xUnit runs it by itself after the other collections, because a budget measured while other tests
/// saturate the CPU is noise. Heavy many-seed runs don't need to be here; they spread their seeds over the cores with
/// <c>SeedRuns</c> (those that share a class with a budget check do so here, one test at a time).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing budgets";
}
