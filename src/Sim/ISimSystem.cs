namespace PointBlank.Sim;

/// <summary>A part of the world: owns some state and advances it once per tick.</summary>
public interface ISimSystem
{
    void Tick(World world);

    /// <summary>
    /// Adds all of this system's state to the hash, always in the same order. Write a count
    /// before anything of variable length, and never rely on the iteration order of an
    /// unordered collection such as a dictionary or hash set.
    /// </summary>
    void AppendState(StateHasher hasher);
}
