using PointBlank.Sim.Replays;

namespace PointBlank.Sim.Tests.Replays;

/// <summary>
/// Test-only scenario: a total grows by a random amount each tick, and the <c>add</c> and
/// <c>double</c> orders change it. The two orders do not commute, so their order matters.
/// </summary>
internal sealed class TallyScenario : IScenario
{
    private sealed class TallySystem : ISimSystem
    {
        public long Total { get; set; }

        public void Tick(World world) => Total += world.Rng.NextInt(0, 10);

        public void AppendState(StateHasher hasher) => hasher.Add(Total);
    }

    private sealed class Order(string type, long amount) : ISimInput
    {
        public void Apply(World world)
        {
            var tally = world.GetSystem<TallySystem>();
            tally.Total = type == "add" ? tally.Total + amount : tally.Total * 2;
        }
    }

    public string Id => "tally";
    public ContentRef Map { get; init; } = new("flat", 1);
    public IReadOnlyList<ContentRef> Forces { get; init; } = [new("counters", 2)];
    public int Ticks => 50;

    public IReadOnlyList<ReplayOrder> ScriptedOrders =>
    [
        new(0, "add", [5]),
        new(10, "add", [3]),
        new(10, "double", []),
        new(49, "add", [1]),
    ];

    public World CreateWorld(ulong seed) => new(seed, new TallySystem());

    public ISimInput CreateInput(ReplayOrder order) => order.Type switch
    {
        "add" when order.Args.Count == 1 => new Order("add", order.Args[0]),
        "double" when order.Args.Count == 0 => new Order("double", 0),
        _ => throw new FormatException($"Bad order '{order.Type}'."),
    };

    public IReadOnlyList<Metric> Measure(World world) =>
        [new("total", Fixed.FromInt((int)world.GetSystem<TallySystem>().Total))];
}
