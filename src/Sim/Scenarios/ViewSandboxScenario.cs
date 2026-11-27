using PointBlank.Sim.Artillery;
using PointBlank.Sim.Data;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Map;
using PointBlank.Sim.Morale;
using PointBlank.Sim.Movement;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;
using PointBlank.Sim.Suppression;
using PointBlank.Sim.UnitAi;

namespace PointBlank.Sim.Scenarios;

/// <summary>
/// Not a game scenario: the test map (woods, roads, hedges, buildings) with a US rifle squad and platoon HQ near the
/// north-west corner and a German Gruppe far to the south-east, for looking at the view and trying the controls. No
/// scripted orders and no measurements.
/// </summary>
internal sealed class ViewSandboxScenario : IScenario
{
    public const string ScenarioId = "view-sandbox";

    private readonly DataSet _data;
    private readonly string _mapPath;

    public ViewSandboxScenario()
    {
        var directory = DataDirectory();
        _data = DataLoader.LoadDirectory(directory);
        _mapPath = Path.Combine(directory, "maps", "test_300.map.json");
        var map = MapLoader.Load(_mapPath, _data);
        Map = new ContentRef(map.Id, 1, map.ComputeContentHash());
    }

    public string Id => ScenarioId;

    public ContentRef Map { get; }

    public IReadOnlyList<ContentRef> Forces { get; } = [new("us_rifle_squad", 1), new("us_platoon_hq", 1), new("de_gruppe", 1)];

    public int Ticks => 600;

    public IReadOnlyList<ReplayOrder> ScriptedOrders { get; } = [];

    public World CreateWorld(ulong seed)
    {
        var map = MapLoader.Load(_mapPath, _data);
        var org = new OrgSystem(OrgTables.FromData(_data));
        var suppression = new SuppressionSystem(org, SuppressionRules.FromData(_data));
        var move = new MovementSystem(map, org, MovementRules.FromData(_data), suppression);
        var sight = SightRules.FromData(_data);
        var los = new LineOfSight(map, sight);
        var spot = new SpottingSystem(map, org, move, los, SpottingRules.FromData(_data), suppression);
        var fire = new FireSystem(map, org, sight, FireRules.FromData(_data), spot, suppression);
        var command = new CommandSystem(org, move, CommandRules.FromData(_data), fire, suppression);
        var ai = new UnitAiSystem(map, org, move, fire, command, UnitAiRules.FromData(_data));
        var morale = new MoraleSystem(org, MoraleRules.FromData(_data), suppression, command, move, spot);
        var arty = new ArtillerySystem(map, org, los, ArtilleryRules.FromData(_data), command, suppression, morale);
        var world = new World(seed, new MapSystem(map), org, command, move, spot, ai, fire, arty, suppression, morale);

        org.SpawnSquad("us_rifle_squad", Fixed.FromInt(30), Fixed.FromInt(40));
        org.SpawnSquad("us_platoon_hq", Fixed.FromInt(24), Fixed.FromInt(70), Posture.Kneeling);
        org.SpawnSquad("de_gruppe", Fixed.FromInt(270), Fixed.FromInt(260), Posture.Prone);
        return world;
    }

    public ISimInput CreateInput(ReplayOrder order) =>
        throw new FormatException($"Scenario '{Id}' has no scripted orders.");

    public IReadOnlyList<Metric> Measure(World world) => [];

    /// <summary>The nearest <c>data</c> folder holding <c>unit_ai.json</c>, above the binaries or the working directory.</summary>
    private static string DataDirectory()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "unit_ai.json")))
                {
                    return candidate;
                }
            }
        }

        return "data";
    }
}
