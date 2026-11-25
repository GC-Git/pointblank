using PointBlank.Sim;
using PointBlank.Sim.Replays;

namespace PointBlank.Runner;

/// <summary>
/// A diagnostic scenario with no game rules: points wander at random and scripted
/// <c>nudge [index, distance]</c> orders push them along x. It exercises the RNG,
/// <see cref="Fixed"/> arithmetic and orders, so the runner and replays have something to
/// run before the game has battles.
/// </summary>
public sealed class WanderScenario : IScenario
{
    private const int Points = 8;
    private const long MaxNudge = 1000;

    private sealed class WanderSystem : ISimSystem
    {
        public Fixed[] X { get; } = new Fixed[Points];
        public Fixed[] Y { get; } = new Fixed[Points];
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
            Fixed.FromInt(world.Rng.NextInt(-5, 6)) * World.TickDuration / Fixed.FromInt(3);

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

    public string Id => "wander";

    public ContentRef Map { get; } = new("open-field", 1);

    public IReadOnlyList<ContentRef> Forces { get; } = [new("wanderers", 1)];

    public int Ticks => 1000;

    public IReadOnlyList<ReplayOrder> ScriptedOrders { get; } =
    [
        new(0, "nudge", [0, 10]),
        new(50, "nudge", [3, 25]),
        new(50, "nudge", [4, -7]),
        new(999, "nudge", [7, 1]),
    ];

    public World CreateWorld(ulong seed) => new(seed, new WanderSystem());

    public ISimInput CreateInput(ReplayOrder order)
    {
        if (order.Type == "nudge"
            && order.Args is [>= 0 and < Points, >= -MaxNudge and <= MaxNudge])
        {
            return new Nudge((int)order.Args[0], (int)order.Args[1]);
        }

        throw new FormatException(
            $"Invalid order '{order.Type}' [{string.Join(", ", order.Args)}] for scenario '{Id}'.");
    }

    public IReadOnlyList<Metric> Measure(World world)
    {
        var system = world.GetSystem<WanderSystem>();
        return
        [
            new("travelled", system.Travelled),
            new("meanX", Mean(system.X)),
            new("meanY", Mean(system.Y)),
        ];
    }

    private static Fixed Mean(Fixed[] values) =>
        values.Aggregate(Fixed.Zero, (sum, value) => sum + value) / Fixed.FromInt(values.Length);
}
