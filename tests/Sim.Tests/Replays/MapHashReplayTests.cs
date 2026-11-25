using PointBlank.Sim.Map;
using PointBlank.Sim.Replays;
using PointBlank.Sim.Tests.Data;
using PointBlank.Sim.Tests.Movement;

namespace PointBlank.Sim.Tests.Replays;

/// <summary>Item 1.3 follow-up: a replay refuses a map file that was edited after recording.</summary>
public class MapHashReplayTests
{
    private static string MapPath() => Path.Combine(DataPaths.DataDirectory(), "maps", "test_300.map.json");

    [Fact]
    public void Replay_Play_fails_when_one_tile_of_the_map_file_is_changed()
    {
        var original = MapLoader.Load(MapPath(), PathFinderTests.Data);
        var recorded = Battle.Run(new CrossingScenario(original, 50), 3);
        Assert.Equal(original.ComputeContentHash(), recorded.Replay.Map.Hash);
        Assert.NotEqual(0UL, recorded.Replay.Map.Hash);

        // The untouched file replays to the same final hash.
        var same = MapLoader.Load(MapPath(), PathFinderTests.Data);
        Assert.Equal(recorded.Replay.FinalHash, Battle.Play(new CrossingScenario(same, 50), recorded.Replay).ComputeStateHash());

        // Change the first terrain character of the file (open ground becomes woods) and reload it.
        var text = File.ReadAllText(MapPath());
        var first = text.IndexOf('"', text.IndexOf('[', text.IndexOf("\"terrain\"", StringComparison.Ordinal))) + 1;
        Assert.Equal('.', text[first]);
        var tamperedPath = Path.Combine(Path.GetTempPath(), $"pb_tampered_{Guid.NewGuid():N}.map.json");
        try
        {
            File.WriteAllText(tamperedPath, text.Remove(first, 1).Insert(first, "w"));
            var tampered = MapLoader.Load(tamperedPath, PathFinderTests.Data);
            Assert.NotEqual(original.ComputeContentHash(), tampered.ComputeContentHash());

            var error = Assert.Throws<FormatException>(() => Battle.Play(new CrossingScenario(tampered, 50), recorded.Replay));
            Assert.Contains("map", error.Message);
        }
        finally
        {
            File.Delete(tamperedPath);
        }
    }
}
