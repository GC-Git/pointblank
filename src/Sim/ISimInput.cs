namespace PointBlank.Sim;

/// <summary>
/// Something from outside the sim (an order, for example) that takes effect on a scheduled
/// tick through <see cref="World.Schedule"/>. This is the only way outside code should
/// change a running battle, so that a replay can repeat it.
/// </summary>
public interface ISimInput
{
    void Apply(World world);
}
