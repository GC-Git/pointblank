using PointBlank.Sim.Data;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.Tests.Movement;

namespace PointBlank.Sim.Tests.Morale;

/// <summary>A world with org, command, movement, (optional) spotting, suppression and morale on an open 600 × 400 m map.</summary>
internal sealed record MoraleRig(
    World World, OrgSystem Org, CommandSystem Command, MovementSystem Move, SuppressionSystem Suppression, MoraleSystem Morale)
{
    public static readonly DataSet Data = PathFinderTests.Data;

    public static Fixed M(int metres) => Fixed.FromInt(metres);

    public static MoraleRig Create(ulong seed = 1, bool spotting = false, MoraleRules? rules = null)
    {
        var map = new GameMap("open", 300, 200);
        var org = new OrgSystem(OrgTables.FromData(Data));
        var suppression = new SuppressionSystem(org, SuppressionRules.FromData(Data));
        var move = new MovementSystem(map, org, PathFinderTests.Rules, suppression);
        var command = new CommandSystem(org, move, CommandRules.FromData(Data), null, suppression);
        SpottingSystem? spot = spotting
            ? new SpottingSystem(map, org, move, new LineOfSight(map, SightRules.FromData(Data)), SpottingRules.FromData(Data), suppression)
            : null;
        var morale = new MoraleSystem(org, rules ?? MoraleRules.FromData(Data), suppression, command, move, spot);
        ISimSystem[] systems = spot != null
            ? [new MapSystem(map), org, command, move, spot, suppression, morale]
            : [new MapSystem(map), org, command, move, suppression, morale];
        return new MoraleRig(new World(seed, systems), org, command, move, suppression, morale);
    }

    /// <summary>Steps the world <paramref name="ticks"/> times.</summary>
    public void Run(int ticks) => World.Run(ticks);
}
