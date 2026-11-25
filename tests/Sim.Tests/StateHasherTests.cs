namespace PointBlank.Sim.Tests;

public class StateHasherTests
{
    private static ulong Hash(Action<StateHasher> write)
    {
        var hasher = new StateHasher();
        write(hasher);
        return hasher.Value;
    }

    private static ulong HashBytes(params byte[] bytes) => Hash(h =>
    {
        foreach (var b in bytes)
        {
            h.Add(b);
        }
    });

    [Theory]
    [InlineData("", 0xcbf29ce484222325UL)]
    [InlineData("a", 0xaf63dc4c8601ec8cUL)]
    [InlineData("foobar", 0x85944171f73967e8UL)]
    public void Matches_the_FNV1a_64_reference_vectors(string text, ulong expected)
    {
        Assert.Equal(expected, HashBytes(text.Select(c => (byte)c).ToArray()));
    }

    [Fact]
    public void Integers_are_written_least_significant_byte_first()
    {
        Assert.Equal(HashBytes(1, 2, 3, 4), Hash(h => h.Add(0x04030201)));
        Assert.Equal(HashBytes(1, 2, 3, 4, 5, 6, 7, 8), Hash(h => h.Add(0x0807060504030201L)));
        Assert.Equal(HashBytes(1, 2, 3, 4, 5, 6, 7, 0x88), Hash(h => h.Add(0x8807060504030201UL)));
        Assert.Equal(HashBytes(255, 255, 255, 255), Hash(h => h.Add(-1)));
    }

    [Fact]
    public void Bool_and_Fixed_have_one_canonical_form()
    {
        Assert.Equal(HashBytes(1), Hash(h => h.Add(true)));
        Assert.Equal(HashBytes(0), Hash(h => h.Add(false)));
        Assert.Equal(Hash(h => h.Add(-5L)), Hash(h => h.Add(Fixed.FromRaw(-5))));
    }

    [Fact]
    public void Order_changes_the_hash()
    {
        Assert.NotEqual(
            Hash(h => { h.Add(1); h.Add(2); }),
            Hash(h => { h.Add(2); h.Add(1); }));
    }
}
