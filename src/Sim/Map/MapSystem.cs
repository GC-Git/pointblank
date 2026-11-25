namespace PointBlank.Sim.Map;

/// <summary>
/// Puts a map into the world: its content hash joins the state hash, so two worlds on
/// different maps (or a map changed mid-battle) never hash alike. The map is read each time
/// the hash is computed, so later items can change it (for example a destroyed wall).
/// </summary>
public sealed class MapSystem(GameMap map) : ISimSystem
{
    public GameMap Map { get; } = map;

    public void Tick(World world)
    {
    }

    public void AppendState(StateHasher hasher) => hasher.Add(Map.ComputeContentHash());
}
