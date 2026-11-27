using PointBlank.Sim.Scenarios;
using PointBlank.Sim.Replays;

namespace PointBlank.Runner;

/// <summary>Every scenario the runner can run or replay.</summary>
public static class ScenarioCatalog
{
    private static readonly IScenario[] All = [new WanderScenario(), new PinScenario(hedges: false), new PinScenario(hedges: true), new PinScenario(hedges: false, noCasualties: true),
        new FixAssaultScenario(FixAssaultMode.BaseOfFire), new FixAssaultScenario(FixAssaultMode.NoBase), new FixAssaultScenario(FixAssaultMode.Unopposed),
        new BombardCreepScenario(barrage: true), new BombardCreepScenario(barrage: false)];

    public static IReadOnlyList<string> Ids { get; } = All.Select(s => s.Id).ToArray();

    public static IScenario? Find(string id) => All.FirstOrDefault(s => s.Id == id);
}
