using System.Reflection;
using System.Runtime.CompilerServices;

namespace PointBlank.Sim.Tests;

/// <summary>
/// Item 2.12. Slow tests carry <c>[Trait(Speed.Name, Speed.Slow)]</c>; the quick command in CLAUDE.md leaves exactly
/// those out. The full command (<c>dotnet test PointBlank.sln</c>) must run everything: these tests guard that.
/// </summary>
public class TestSpeedTests
{
    private const string Full = "dotnet test PointBlank.sln";
    private const string Quick = "dotnet test PointBlank.sln --filter \"Speed!=Slow\"";

    private static string Root([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));

    private static IEnumerable<string> Files(params string[] patterns)
    {
        var root = Root();
        var top = patterns.SelectMany(p => Directory.GetFiles(root, p));
        var below = new[] { "src", "tests", "tools", "game" }
            .SelectMany(d => patterns.SelectMany(p => Directory.GetFiles(Path.Combine(root, d), p, SearchOption.AllDirectories)));
        var sep = Path.DirectorySeparatorChar;
        return top.Concat(below).Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}.godot{sep}"));
    }

    private static List<MethodInfo> Tests() => typeof(TestSpeedTests).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        .Where(m => m.GetCustomAttributes<FactAttribute>(inherit: true).Any())
        .ToList();

    private static IEnumerable<(string Name, string Value)> Traits(MemberInfo member) => member.GetCustomAttributesData()
        .Where(a => a.AttributeType == typeof(TraitAttribute))
        .Select(a => ((string)a.ConstructorArguments[0].Value!, (string)a.ConstructorArguments[1].Value!));

    private static bool IsSlow(MethodInfo m) => Traits(m).Concat(Traits(m.DeclaringType!)).Any(t => t == (Speed.Name, Speed.Slow));

    [Fact]
    public void No_project_or_settings_file_filters_the_full_run()
    {
        var files = Files("*.csproj", "*.props", "*.targets", "*.rsp", "*.runsettings", "*.sln", "xunit.runner.json", "global.json").ToList();
        Assert.Contains(files, f => f.EndsWith("Sim.Tests.csproj", StringComparison.Ordinal));
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var word in new[] { "TestCaseFilter", "RunSettings", "--filter", "Speed!=", "methodDisplayFilter", "IsTestProject>false" })
            {
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{file} mentions {word}: the full run must exclude nothing");
            }
        }

        Assert.DoesNotContain(files, f => f.EndsWith(".runsettings", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Tests_are_skipped_only_in_the_two_documented_places()
    {
        var skipping = Files("*.cs")
            .Where(f => Path.GetFileName(f) != $"{nameof(TestSpeedTests)}.cs")
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), @"\bSkip\s*="))
            .Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["NoPlaceholdersTests.cs", "ScreenshotTests.cs"], skipping);
    }

    [Fact]
    public void The_only_speed_trait_is_Slow_and_determinism_and_the_fog_boundary_stay_in_the_quick_set()
    {
        var tests = Tests();
        var speed = tests.SelectMany(m => Traits(m).Concat(Traits(m.DeclaringType!))).Where(t => t.Name.Equals(Speed.Name, StringComparison.OrdinalIgnoreCase));
        Assert.All(speed, t => Assert.Equal((Speed.Name, Speed.Slow), t));
        Assert.Contains(tests, IsSlow);
        foreach (var m in tests.Where(IsSlow))
        {
            var type = m.DeclaringType!;
            Assert.False(
                type.Namespace!.EndsWith(".Boundary", StringComparison.Ordinal) || type.Namespace.EndsWith(".Determinism", StringComparison.Ordinal)
                    || type == typeof(DeterminismTests),
                $"{type.Name}.{m.Name} must stay in the quick set");
        }
    }

    [Fact]
    public void Every_area_keeps_a_check_in_the_quick_set()
    {
        // CreepTraceTests only prints a diagnostic of the battles BombardCreepTests checks.
        var empty = Tests().GroupBy(m => m.DeclaringType!).Where(g => g.All(IsSlow)).Select(g => g.Key.Name).ToList();
        Assert.Equal(["CreepTraceTests"], empty);
    }

    [Theory]
    [InlineData("CLAUDE.md")]
    [InlineData("docs/process/session-brief.md")]
    [InlineData("docs/process/evaluator.md")]
    public void The_documents_give_the_quick_command_and_require_the_full_run(string document)
    {
        var text = File.ReadAllText(Path.Combine(Root(), document));
        Assert.Contains(Quick, text);
        Assert.Matches(System.Text.RegularExpressions.Regex.Escape(Full) + @"(\r?\n|`)", text);
    }
}

/// <summary>The one speed trait.</summary>
internal static class Speed
{
    public const string Name = "Speed";
    public const string Slow = "Slow";
}
