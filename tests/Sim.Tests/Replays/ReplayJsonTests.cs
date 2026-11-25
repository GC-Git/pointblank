using PointBlank.Sim.Replays;

namespace PointBlank.Sim.Tests.Replays;

public class ReplayJsonTests
{
    private static readonly Replay Sample = new(
        "tally",
        Seed: ulong.MaxValue,
        Map: new("flat", 1, 0xfeedUL),
        Forces: [new("counters", 2)],
        Ticks: 50,
        Orders: [new(0, "add", [5, -3]), new(10, "double", [])],
        FinalHash: 0x00ab);

    private const string SampleText =
        """
        {
          "version": 2,
          "scenario": "tally",
          "seed": 18446744073709551615,
          "map": {
            "id": "flat",
            "version": 1,
            "hash": "000000000000feed"
          },
          "forces": [
            {
              "id": "counters",
              "version": 2,
              "hash": "0000000000000000"
            }
          ],
          "ticks": 50,
          "orders": [
            {
              "tick": 0,
              "type": "add",
              "args": [
                5,
                -3
              ]
            },
            {
              "tick": 10,
              "type": "double",
              "args": []
            }
          ],
          "finalHash": "00000000000000ab"
        }

        """;

    [Fact]
    public void Write_gives_a_fixed_layout_with_line_feeds_only()
    {
        // The literal's own line endings depend on how the file was checked out.
        Assert.Equal(SampleText.ReplaceLineEndings("\n"), ReplayJson.Write(Sample));
    }

    [Fact]
    public void Read_returns_what_was_written()
    {
        var read = ReplayJson.Read(ReplayJson.Write(Sample));

        Assert.Equal(Sample.Scenario, read.Scenario);
        Assert.Equal(Sample.Seed, read.Seed);
        Assert.Equal(Sample.Map, read.Map);
        Assert.Equal(Sample.Forces, read.Forces);
        Assert.Equal(Sample.Ticks, read.Ticks);
        Assert.Equal(Sample.FinalHash, read.FinalHash);
        Assert.Equal(2, read.Orders.Count);
        Assert.Equal(10, read.Orders[1].Tick);
        Assert.Equal("add", read.Orders[0].Type);
        Assert.Equal([5L, -3L], read.Orders[0].Args);
        Assert.Empty(read.Orders[1].Args);
    }

    [Theory]
    [InlineData("\"version\": 2,\n  \"scenario\"", "\"version\": 3,\n  \"scenario\"")]
    [InlineData("\"scenario\": \"tally\",", "")]
    [InlineData("\"scenario\": \"tally\"", "\"scenario\": null")]
    [InlineData("\"scenario\": \"tally\"", "\"scenario\": 3")]
    [InlineData("18446744073709551615", "-1")]
    [InlineData("\"ticks\": 50", "\"ticks\": \"50\"")]
    [InlineData("\"ticks\": 50", "\"ticks\": 1.5")]
    [InlineData("\"ticks\": 50", "\"ticks\": 3000000000")]
    [InlineData("\"forces\": [", "\"armies\": [")]
    [InlineData("\"args\": []", "\"args\": {}")]
    [InlineData("\"args\": []", "\"args\": [\"x\"]")]
    [InlineData("00000000000000ab", "xyz")]
    [InlineData("00000000000000ab", "ab")]
    public void Read_rejects_an_invalid_field(string find, string replace)
    {
        var text = ReplayJson.Write(Sample);
        Assert.Contains(find, text);

        Assert.Throws<FormatException>(() => ReplayJson.Read(text.Replace(find, replace)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{ \"version\": 1")]
    public void Read_rejects_text_that_is_not_a_replay(string text)
    {
        Assert.Throws<FormatException>(() => ReplayJson.Read(text));
    }
}
