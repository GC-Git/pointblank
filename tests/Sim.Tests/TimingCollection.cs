namespace PointBlank.Sim.Tests;

/// <summary>
/// Test classes with wall-clock budget checks, and the heavy many-seed scenario runs that use every core, share this
/// collection so xUnit never runs them at the same time: a budget measured while other tests saturate the CPU is noise.
/// </summary>
[CollectionDefinition(Name)]
public sealed class TimingCollection
{
    public const string Name = "Timing and heavy runs";
}
