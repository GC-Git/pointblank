using System.Text.RegularExpressions;

namespace PointBlank.Sim.Tests.Determinism;

/// <summary>
/// Finds non-deterministic APIs in C# source. It reads text, not types: comments and string
/// literals are blanked (interpolation holes are kept) and the rest is matched against the
/// rules below. So it cannot see a floating-point value that is never named, such as
/// <c>var x = library.ReturnsADouble();</c>; add a rule when a new API like that appears.
/// </summary>
public static class BannedApiScanner
{
    private static readonly (Regex Pattern, string Reason)[] Rules =
    {
        Rule(@"\b(float|double|Single|Double|Half|MathF|ToSingle|ToDouble)\b",
            "floating point; use Fixed"),
        Rule(@"(?<![\w.])(\d[\d_]*\.\d[\d_]*([eE][+-]?\d+)?|\.\d[\d_]*([eE][+-]?\d+)?|\d[\d_]*[eE][+-]?\d+)(?![\d_mM])[fFdD]?",
            "floating-point literal; use Fixed.FromRatio"),
        Rule(@"(?<![\w.])\d[\d_]*[fFdD]\b",
            "floating-point literal; use Fixed.FromInt"),
        Rule(@"\bMath\s*\.\s*(?!(Abs|Min|Max|Clamp|Sign|DivRem|BigMul)\b)\w+",
            "System.Math member that works in floating point; use Fixed"),
        Rule(@"\busing\s+static\s+(System\s*\.\s*)?Math\b",
            "hides which System.Math members are used"),
        Rule(@"\bSystem\s*\.\s*Numerics\b|\b(Vector[234]|Quaternion|Matrix3x2|Matrix4x4)\b",
            "floating-point vector types; use Fixed"),
        Rule(@"\b(Random|RandomNumberGenerator|NewGuid)\b",
            "unseeded or framework randomness; use SimRng"),
        Rule(@"\b(DateTime|DateTimeOffset|DateOnly|TimeOnly|TimeSpan|TimeProvider|Stopwatch|TickCount|TickCount64)\b",
            "wall-clock time; use World.Tick"),
        Rule(@"\b(HashCode|GetHashCode)\b",
            "hash that can differ between runs; use StateHasher"),
    };

    /// <param name="sourcesByPath">Source text keyed by path relative to src/Sim.</param>
    /// <param name="approvedWrappers">Paths that are not checked.</param>
    /// <returns>One line per violation: <c>path(line): reason: 'text'</c>.</returns>
    public static IReadOnlyList<string> Scan(
        IReadOnlyDictionary<string, string> sourcesByPath,
        IReadOnlySet<string> approvedWrappers)
    {
        var violations = new List<string>();
        foreach (var (path, source) in sourcesByPath.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (approvedWrappers.Contains(path))
            {
                continue;
            }

            var lines = CSharpCodeOnly.Strip(source).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (var (pattern, reason) in Rules)
                {
                    foreach (Match match in pattern.Matches(lines[i]))
                    {
                        violations.Add($"{path}({i + 1}): {reason}: '{match.Value}'");
                    }
                }
            }
        }

        return violations;
    }

    private static (Regex, string) Rule(string pattern, string reason) =>
        (new Regex(pattern, RegexOptions.CultureInvariant), reason);
}
