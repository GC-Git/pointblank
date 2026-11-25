using System.Globalization;

namespace PointBlank.Sim.Tests;

public class FixedTests
{
    private static Fixed N(long numerator, long denominator = 1) => Fixed.FromRatio(numerator, denominator);

    [Fact]
    public void Arithmetic_is_exact_for_binary_fractions()
    {
        Assert.Equal(N(11, 2), N(7, 2) + N(2));
        Assert.Equal(N(-1, 2), N(3, 2) - N(2));
        Assert.Equal(N(-3), N(-3, 2) * N(2));
        Assert.Equal(N(7, 2), N(7) / N(2));
        Assert.Equal(N(-7, 2), -N(7, 2));
        Assert.Equal(1L << 32, Fixed.One.Raw);
        Assert.Equal(Fixed.One, Fixed.FromInt(1));
        Assert.Equal((long)int.MinValue << 32, Fixed.FromInt(int.MinValue).Raw);
    }

    [Fact]
    public void Division_truncates_toward_zero_for_both_signs()
    {
        long third = Fixed.One.Raw / 3;
        Assert.Equal(third, (N(1) / N(3)).Raw);
        Assert.Equal(-third, (N(-1) / N(3)).Raw);
        Assert.Equal(-third, (N(1) / N(-3)).Raw);
        Assert.Equal(third, (N(-1) / N(-3)).Raw);
        Assert.Equal(-third, N(-1, 3).Raw);
        Assert.Equal(-third, N(1, -3).Raw);
    }

    [Fact]
    public void Multiplication_truncates_toward_zero_for_both_signs()
    {
        var tiny = Fixed.FromRaw(1);
        var half = N(1, 2);
        Assert.Equal(Fixed.Zero, tiny * half);
        Assert.Equal(Fixed.Zero, -tiny * half);
        Assert.Equal(-1, (Fixed.FromRaw(-3) * half).Raw);
    }

    [Theory]
    [InlineData(7, 2, 3)]
    [InlineData(-7, 2, -3)]
    [InlineData(5, 1, 5)]
    [InlineData(-1, 2, 0)]
    public void ToInt_truncates_toward_zero(long numerator, long denominator, int expected)
    {
        Assert.Equal(expected, N(numerator, denominator).ToInt());
    }

    [Fact]
    public void Comparisons_order_values()
    {
        Assert.True(N(-1) < Fixed.Zero);
        Assert.True(N(1, 2) > Fixed.Zero);
        Assert.True(N(2) <= N(2));
        Assert.True(N(2) >= N(2));
        Assert.False(N(2) < N(2));
        Assert.True(N(1).CompareTo(N(2)) < 0);
        Assert.Equal(0, N(1).CompareTo(N(1)));
        Assert.True(Fixed.MinValue < Fixed.MaxValue);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(144, 12)]
    [InlineData(1_000_000, 1000)]
    public void Sqrt_of_perfect_squares_is_exact(int value, int root)
    {
        Assert.Equal(Fixed.FromInt(root), Fixed.Sqrt(Fixed.FromInt(value)));
    }

    [Fact]
    public void Sqrt_floors()
    {
        Assert.Equal(6074000999L, Fixed.Sqrt(N(2)).Raw);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    [InlineData(123456789012345L)]
    public void Sqrt_is_the_largest_root_not_above_the_input(long raw)
    {
        Int128 root = Fixed.Sqrt(Fixed.FromRaw(raw)).Raw;
        Int128 target = (Int128)raw << 32;
        Assert.True(root * root <= target);
        Assert.True((root + 1) * (root + 1) > target);
    }

    [Fact]
    public void Sqrt_of_a_negative_value_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed.Sqrt(Fixed.FromRaw(-1)));
    }

    [Fact]
    public void Division_by_zero_throws()
    {
        Assert.Throws<DivideByZeroException>(() => N(1) / Fixed.Zero);
        Assert.Throws<DivideByZeroException>(() => Fixed.FromRatio(1, 0));
    }

    [Fact]
    public void Overflow_throws_instead_of_wrapping()
    {
        var step = Fixed.FromRaw(1);
        var big = Fixed.FromInt(100_000);
        Assert.Throws<OverflowException>(() => Fixed.MaxValue + step);
        Assert.Throws<OverflowException>(() => Fixed.MinValue - step);
        Assert.Throws<OverflowException>(() => -Fixed.MinValue);
        Assert.Throws<OverflowException>(() => big * big);
        Assert.Throws<OverflowException>(() => big / step);
        Assert.Throws<OverflowException>(() => Fixed.MinValue / -Fixed.One);
        Assert.Throws<OverflowException>(() => Fixed.FromRatio(long.MaxValue, 1));
    }

    [Fact]
    public void ToString_ignores_the_machine_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("-3.5", N(-7, 2).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
