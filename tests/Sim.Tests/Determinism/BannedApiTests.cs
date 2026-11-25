namespace PointBlank.Sim.Tests.Determinism;

/// <summary>Item 0.2: non-deterministic APIs stay out of Sim.</summary>
public class BannedApiTests
{
    /// <summary>
    /// Files under src/Sim (paths relative to it, with forward slashes) that may use banned
    /// APIs because they wrap them deterministically. None needs to today: Fixed and SimRng
    /// are pure integer code. Adding an entry weakens the check, so justify it in the PR.
    /// </summary>
    private static readonly IReadOnlySet<string> ApprovedWrappers = new HashSet<string>();

    private static IReadOnlyList<string> Scan(string path, string source, params string[] approved) =>
        BannedApiScanner.Scan(
            new Dictionary<string, string> { [path] = source },
            new HashSet<string>(approved));

    [Fact]
    public void Sim_uses_no_banned_api_outside_approved_wrappers()
    {
        var sources = ReadSimSources();
        Assert.Contains("Fixed.cs", sources.Keys); // guards against scanning the wrong folder
        Assert.Contains("World.cs", sources.Keys);

        var violations = BannedApiScanner.Scan(sources, ApprovedWrappers);

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Every_approved_wrapper_exists()
    {
        Assert.Subset(ReadSimSources().Keys.ToHashSet(), ApprovedWrappers.ToHashSet());
    }

    [Theory]
    // The three the checklist names.
    [InlineData("var r = new System.Random();")]
    [InlineData("var r = Random.Shared;")]
    [InlineData("using R = System.Random;")]
    [InlineData("var t = DateTime.UtcNow;")]
    [InlineData("System.DateTime t;")]
    [InlineData("float x;")]
    [InlineData("List<float> xs;")]
    // Other floating point.
    [InlineData("double x;")]
    [InlineData("System.Single x;")]
    [InlineData("Double.Parse(s);")]
    [InlineData("Half h;")]
    [InlineData("var x = MathF.PI;")]
    [InlineData("var x = Math.Sqrt(2);")]
    [InlineData("var x = Math . Floor(y);")]
    [InlineData("using static System.Math;")]
    [InlineData("var x = Convert.ToDouble(y);")]
    [InlineData("var x = 1.5;")]
    [InlineData("var x = .5;")]
    [InlineData("var x = 2f;")]
    [InlineData("var x = 2D;")]
    [InlineData("var x = 1e6;")]
    [InlineData("var x = 1_000.5f;")]
    [InlineData("using System.Numerics;")]
    // Code inside interpolation holes still counts.
    [InlineData("var s = $\"{(double)y}\";")]
    [InlineData("var s = $\"a {{b}} {Random.Shared.Next()}\";")]
    [InlineData("var s = $@\"c:\\{DateTime.Now}\";")]
    [InlineData("var s = $\"\"\"x {DateTime.Now} y\"\"\";")]
    [InlineData("var s = $$\"\"\"{x} {{DateTime.Now}}\"\"\";")]
    // A literal must not hide the code after it.
    [InlineData("var c = '\"'; float x;")]
    [InlineData("var s = \"a\\\"\"; float x;")]
    [InlineData("var s = @\"a\\\"; float x;")]
    [InlineData("var s = \"//\"; float x;")]
    // Clocks, machine state and unstable hashes.
    [InlineData("DateTimeOffset t;")]
    [InlineData("var w = new System.Diagnostics.Stopwatch();")]
    [InlineData("var t = Environment.TickCount64;")]
    [InlineData("var t = TimeSpan.Zero;")]
    [InlineData("TimeProvider p;")]
    [InlineData("var g = Guid.NewGuid();")]
    [InlineData("var h = name.GetHashCode();")]
    [InlineData("var h = HashCode.Combine(a, b);")]
    [InlineData("System.Security.Cryptography.RandomNumberGenerator.Fill(b);")]
    // Disabled code is still scanned.
    [InlineData("#if NEVER\nfloat x;\n#endif")]
    public void Banned_code_is_reported(string source)
    {
        Assert.NotEmpty(Scan("Bad.cs", source));
    }

    [Theory]
    [InlineData("// float, double, DateTime and System.Random are banned")]
    [InlineData("/* float x = 1.5;\n DateTime.Now */")]
    [InlineData("/// <summary>Never use <c>System.Random</c>.</summary>")]
    [InlineData("var s = \"float DateTime 1.5\";")]
    [InlineData("var s = @\"say \"\"float\"\"\";")]
    [InlineData("var s = $\"float {x} {{double}}\";")]
    [InlineData("var s = \"\"\"\n a \"float\" 1.5 \n\"\"\";")]
    [InlineData("var c = 'f';")]
    [InlineData("var x = Math.Max(Math.Abs(a), 3);")]
    [InlineData("var x = 0x1F + 0b1 + 10_000 + 5L + 7UL;")]
    [InlineData("var x = items[1..2];")]
    [InlineData("var m = 1.5m;")]
    [InlineData("SimRng rng; var x = rng.NextFixed();")]
    public void Allowed_code_passes(string source)
    {
        Assert.Empty(Scan("Good.cs", source));
    }

    [Fact]
    public void A_report_names_the_file_line_and_text()
    {
        var violation = Assert.Single(Scan("Units/Bad.cs", "int a;\n/* x\n y */\nfloat b;\n"));

        Assert.StartsWith("Units/Bad.cs(4): ", violation);
        Assert.Contains("float", violation);
    }

    [Fact]
    public void Only_the_exact_approved_path_is_exempt()
    {
        Assert.Empty(Scan("Maths/Wrapper.cs", "double x;", "Maths/Wrapper.cs"));
        Assert.NotEmpty(Scan("Other/Wrapper.cs", "double x;", "Maths/Wrapper.cs"));
    }

    private static Dictionary<string, string> ReadSimSources()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "PointBlank.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var sim = Path.Combine(directory.FullName, "src", "Sim");

        return Directory.EnumerateFiles(sim, "*.cs", SearchOption.AllDirectories)
            .Select(file => (File: file, Path: Path.GetRelativePath(sim, file).Replace('\\', '/')))
            .Where(f => !f.Path.StartsWith("bin/") && !f.Path.StartsWith("obj/"))
            .ToDictionary(f => f.Path, f => File.ReadAllText(f.File));
    }
}
