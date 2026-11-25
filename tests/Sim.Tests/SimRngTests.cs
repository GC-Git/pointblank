namespace PointBlank.Sim.Tests;

public class SimRngTests
{
    [Fact]
    public void Matches_the_PCG32_reference_output()
    {
        // First outputs of the PCG project's pcg32 demo, seeded with (42, 54).
        var rng = new SimRng(seed: 42, stream: 54);
        uint[] expected = { 0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e };

        Assert.Equal(expected, expected.Select(_ => rng.NextUInt()).ToArray());
    }

    [Fact]
    public void Restoring_a_captured_state_continues_the_same_sequence()
    {
        var original = new SimRng(7);
        for (int i = 0; i < 100; i++)
        {
            original.NextUInt();
        }

        var restored = SimRng.FromState(original.State);

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(original.NextUInt(), restored.NextUInt());
        }
    }

    [Fact]
    public void An_even_increment_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => SimRng.FromState(new RngState(1, 2)));
    }

    [Fact]
    public void NextInt_covers_exactly_the_half_open_range()
    {
        var rng = new SimRng(1);

        var seen = Enumerable.Range(0, 1000).Select(_ => rng.NextInt(-3, 4)).Distinct().Order();

        Assert.Equal(new[] { -3, -2, -1, 0, 1, 2, 3 }, seen.ToArray());
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(5, 4)]
    public void NextInt_rejects_an_empty_range(int min, int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimRng(1).NextInt(min, max));
    }

    [Fact]
    public void NextInt_handles_the_whole_int_range()
    {
        var rng = new SimRng(1);

        var values = Enumerable.Range(0, 1000)
            .Select(_ => rng.NextInt(int.MinValue, int.MaxValue))
            .ToArray();

        Assert.Contains(values, v => v < 0);
        Assert.Contains(values, v => v > 0);
        Assert.DoesNotContain(int.MaxValue, values);
    }

    [Fact]
    public void NextFixed_is_at_least_zero_and_below_one()
    {
        var rng = new SimRng(1);

        for (int i = 0; i < 1000; i++)
        {
            var value = rng.NextFixed();
            Assert.True(value >= Fixed.Zero && value < Fixed.One, value.ToString());
        }
    }
}
