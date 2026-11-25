using System.Globalization;

namespace PointBlank.Sim;

/// <summary>
/// The sim's number type: signed Q31.32 fixed point in a <see cref="long"/>, built only on
/// integer arithmetic so every machine gets the same bits.
/// Range is about ±2.1 billion with steps of 2^-32.
/// Every operation throws <see cref="OverflowException"/> instead of wrapping or saturating.
/// <c>*</c>, <c>/</c>, <see cref="FromRatio"/> and <see cref="ToInt"/> truncate toward zero,
/// so <c>(-a) / b == -(a / b)</c>. <see cref="Sqrt"/> rounds down.
/// </summary>
public readonly record struct Fixed : IComparable<Fixed>
{
    private const int FractionBits = 32;
    private const long OneRaw = 1L << FractionBits;

    private Fixed(long raw) => Raw = raw;

    /// <summary>The value multiplied by 2^32.</summary>
    public long Raw { get; }

    public static Fixed Zero => default;
    public static Fixed One => new(OneRaw);
    public static Fixed MinValue => new(long.MinValue);
    public static Fixed MaxValue => new(long.MaxValue);

    public static Fixed FromRaw(long raw) => new(raw);

    // Every int fits, so this cannot overflow.
    public static Fixed FromInt(int value) => new((long)value << FractionBits);

    /// <summary>numerator / denominator, for constants such as one tenth.</summary>
    public static Fixed FromRatio(long numerator, long denominator) =>
        Divide((Int128)numerator * OneRaw, denominator);

    /// <summary>The whole part, truncated toward zero.</summary>
    public int ToInt() => (int)(Raw / OneRaw);

    public static Fixed operator +(Fixed a, Fixed b) => new(checked(a.Raw + b.Raw));

    public static Fixed operator -(Fixed a, Fixed b) => new(checked(a.Raw - b.Raw));

    public static Fixed operator -(Fixed a) => new(checked(-a.Raw));

    public static Fixed operator *(Fixed a, Fixed b) =>
        new(checked((long)((Int128)a.Raw * b.Raw / OneRaw)));

    public static Fixed operator /(Fixed a, Fixed b) => Divide((Int128)a.Raw * OneRaw, b.Raw);

    public static bool operator <(Fixed a, Fixed b) => a.Raw < b.Raw;
    public static bool operator >(Fixed a, Fixed b) => a.Raw > b.Raw;
    public static bool operator <=(Fixed a, Fixed b) => a.Raw <= b.Raw;
    public static bool operator >=(Fixed a, Fixed b) => a.Raw >= b.Raw;

    public int CompareTo(Fixed other) => Raw.CompareTo(other.Raw);

    /// <summary>The square root, rounded down.</summary>
    public static Fixed Sqrt(Fixed value)
    {
        if (value.Raw < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Square root of a negative number.");
        }

        // Bit-by-bit integer square root of raw * 2^32, which is the raw value of the root.
        UInt128 rest = (UInt128)(ulong)value.Raw << FractionBits;
        UInt128 root = 0;
        UInt128 bit = (UInt128)1 << 94; // highest even bit a 95-bit input can use

        while (bit > rest)
        {
            bit >>= 2;
        }

        while (bit != 0)
        {
            if (rest >= root + bit)
            {
                rest -= root + bit;
                root = (root >> 1) + bit;
            }
            else
            {
                root >>= 1;
            }

            bit >>= 2;
        }

        return new Fixed((long)root);
    }

    // Int128 division by zero does not reliably throw DivideByZeroException on .NET 8.
    private static Fixed Divide(Int128 numerator, long denominator) =>
        denominator == 0
            ? throw new DivideByZeroException()
            : new Fixed(checked((long)(numerator / denominator)));

    /// <summary>Decimal text for logs and debugging, the same in every culture.</summary>
    public override string ToString() =>
        ((decimal)Raw / OneRaw).ToString(CultureInfo.InvariantCulture);
}
