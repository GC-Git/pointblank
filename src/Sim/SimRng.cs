namespace PointBlank.Sim;

/// <summary>Everything needed to resume a <see cref="SimRng"/>.</summary>
public readonly record struct RngState(ulong State, ulong Increment);

/// <summary>
/// The sim's only source of randomness: PCG32 (XSH-RR variant) by M. E. O'Neill,
/// https://www.pcg-random.org. Integer arithmetic only, so a seed gives the same sequence
/// on every machine. Changing this algorithm breaks every saved replay.
/// </summary>
public sealed class SimRng
{
    public const ulong DefaultStream = 54;

    private const ulong Multiplier = 6364136223846793005UL;

    private ulong _state;
    private readonly ulong _increment; // always odd

    public SimRng(ulong seed, ulong stream = DefaultStream)
    {
        _increment = unchecked((stream << 1) | 1);
        NextUInt();
        _state = unchecked(_state + seed);
        NextUInt();
    }

    private SimRng(RngState state)
    {
        _state = state.State;
        _increment = state.Increment;
    }

    public RngState State => new(_state, _increment);

    public static SimRng FromState(RngState state) =>
        (state.Increment & 1) == 1
            ? new SimRng(state)
            : throw new ArgumentException("A PCG32 increment must be odd.", nameof(state));

    public uint NextUInt()
    {
        unchecked
        {
            ulong old = _state;
            _state = old * Multiplier + _increment;
            uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
            int rotation = (int)(old >> 59);
            return (xorShifted >> rotation) | (xorShifted << (-rotation & 31));
        }
    }

    /// <summary>A uniform integer from <paramref name="minInclusive"/> up to, not including,
    /// <paramref name="maxExclusive"/>.</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "The range is empty.");
        }

        uint range = (uint)((long)maxExclusive - minInclusive);

        // Reject the lowest (2^32 mod range) draws so every result is equally likely.
        uint threshold = unchecked(0u - range) % range;
        uint draw;
        do
        {
            draw = NextUInt();
        }
        while (draw < threshold);

        return (int)(minInclusive + (long)(draw % range));
    }

    /// <summary>A uniform value in [0, 1).</summary>
    public Fixed NextFixed() => Fixed.FromRaw(NextUInt());
}
