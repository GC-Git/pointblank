using System.Globalization;

namespace PointBlank.Runner;

/// <summary>The <c>--seeds</c> argument: <c>N</c> or <c>A..B</c>, both ends included.</summary>
public static class SeedRange
{
    public const int MaxSeeds = 100_000;

    public static bool TryParse(string text, out ulong first, out ulong last)
    {
        var parts = text.Split("..");
        first = last = 0;

        return parts.Length <= 2
            && TryParseSeed(parts[0], out first)
            && TryParseSeed(parts[^1], out last)
            && first <= last
            && last - first < MaxSeeds;
    }

    private static bool TryParseSeed(string text, out ulong seed) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out seed);
}
