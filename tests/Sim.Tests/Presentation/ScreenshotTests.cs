using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using PointBlank.Presentation;
using PointBlank.Sim.Map;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>Runs only with <c>PB_SCREENSHOT=1</c>: it opens Godot with a real renderer (a small window appears briefly).</summary>
public sealed class ScreenshotFactAttribute : FactAttribute
{
    public ScreenshotFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PB_SCREENSHOT") != "1")
        {
            Skip = "Screenshot test: set PB_SCREENSHOT=1 (needs Godot and a real renderer).";
        }
    }
}

/// <summary><see cref="ScreenshotFactAttribute"/>, for a theory.</summary>
public sealed class ScreenshotTheoryAttribute : TheoryAttribute
{
    public ScreenshotTheoryAttribute() => Skip = new ScreenshotFactAttribute().Skip;
}

/// <summary>
/// Item 2.5 screenshot-mode tests. The game saves frames; the same battle is run here (it is deterministic), where the
/// truth is visible, and the frames' pixels are checked against it. The frames stay in <c>TestResults/screens</c>.
/// </summary>
[Trait("Category", "Screenshot")]
public class ScreenshotTests
{
    private static string Root([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    private static float F(Fixed v) => v.Raw / 4294967296f;

    private static readonly Lazy<bool> Built = new(() =>
    {
        // Godot runs the project's Debug assemblies whatever configuration the tests use, so build exactly those first:
        // a frame from a stale game proves nothing.
        Start("dotnet", ["build", Path.Combine("game", "PointBlank.csproj"), "-c", "Debug", "--disable-build-servers", "-nologo"], null);
        return true;
    });

    private static void Capture(params string[] options)
    {
        Assert.True(Built.Value);
        var godot = Environment.GetEnvironmentVariable("PB_GODOT")
            ?? @"C:\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64_console.exe";
        string[] launch = ["--path", "game", "--audio-driver", "Dummy", "--windowed", "--resolution", "320x180", "--", "--screenshot"];
        Start(godot, [.. launch, .. options], "ERROR");
    }

    private static void Start(string program, IEnumerable<string> arguments, string? forbidden)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root() };
        foreach (var a in arguments)
        {
            start.ArgumentList.Add(a);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(180_000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"{program} did not finish within three minutes.");
        }

        var output = stdout.Result + stderr.Result;
        Assert.True(
            process.ExitCode == 0 && (forbidden == null || !output.Contains(forbidden, StringComparison.Ordinal)),
            $"{program}: exit {process.ExitCode}\n{output}");
    }

    private static GameController Twin(string scenario, bool script, params int[] select)
    {
        var c = new GameController(PlayerBattle.Create(scenario, 1, script));
        c.Select(select);
        return c;
    }

    private static void StepTo(GameController c, int tick)
    {
        while (c.Battle.View.Tick < tick)
        {
            c.Step();
        }
    }

    private static IReadOnlyList<Soldier> Men(GameController c, string side) =>
        c.Battle.World.GetSystem<OrgSystem>().Squads.Where(q => q.Side == side).SelectMany(q => q.Soldiers).ToArray();

    private static Pt ScreenOf(GameController c, Soldier s) => c.Camera.ToScreen(F(s.X), F(s.Y));

    private static float Gap(Pt a, Pt b) => MathF.Max(MathF.Abs(a.X - b.X), MathF.Abs(a.Y - b.Y));

    /// <summary>Where a squad's label and bars start: above its topmost man, centred on its able men.</summary>
    private static Pt LabelOf(GameController c, SquadView q)
    {
        var able = q.Soldiers.Where(s => s.Health is HealthState.Ok or HealthState.Wounded).ToArray();
        var centre = able.Average(s => c.Camera.ToScreen(F(s.X), F(s.Y)).X);
        return new Pt(centre - 30, q.Soldiers.Min(s => c.Camera.ToScreen(F(s.X), F(s.Y)).Y) - 30);
    }

    /// <summary>A bar's pixels: its colour over the given fraction of the area, the dark background over the rest.</summary>
    private static void BarShows(Png frame, Area bar, Rgb colour, float fraction, string what)
    {
        var all = bar.W * bar.H;
        var filled = frame.Count(bar, p => p == colour);
        var dark = frame.Count(bar, p => p == Palette.BarBack);
        Assert.True(
            Math.Abs(filled - (fraction * all)) <= all / 5 && Math.Abs(dark - ((1 - fraction) * all)) <= all / 5,
            $"{what} at {fraction:0.00}: {filled} filled and {dark} dark of {all}");
    }

    /// <summary>
    /// Of the points around a man's ring that no man's shape covers (rings are drawn under the men), how many have about
    /// the selection colour within a pixel.
    /// </summary>
    private static (int Ringed, int Open) OnRing(Png frame, Pt centre, float radius, (Pt At, Posture Posture)[] men, GameController c)
    {
        var r = Math.Clamp(0.7f * c.Camera.Scale, 3, 12);
        bool Covers((Pt At, Posture Posture) man, Pt p)
        {
            // A prone man is a long low bar; the others fit in a square.
            var (w, h) = man.Posture == Posture.Prone ? (1.6f * r, 0.6f * r) : (r, r);
            return MathF.Abs(man.At.X - p.X) <= w + 1 && MathF.Abs(man.At.Y - p.Y) <= h + 1;
        }

        var open = Enumerable.Range(0, 64)
            .Select(i => MathF.SinCos(i * MathF.Tau / 64))
            .Select(a => new Pt(centre.X + (radius * a.Cos), centre.Y + (radius * a.Sin)))
            .Where(p => !men.Any(m => Covers(m, p)))
            .ToArray();
        return (open.Count(p => frame.Near(p, 1, q => q.R > 200 && q.G > 180 && q.B < 110)), open.Length);
    }

    /// <summary>The panel shows the squad's two bars, and a line per man in its suppression and rounds columns.</summary>
    private static void PanelShowsTheSquad(Png frame, GameController c, int squad)
    {
        var view = c.Battle.View.Squads.Single(q => q.Id == squad);

        // The morale and suppression bars (200 by 5, in a dark frame one pixel wider all round).
        var (morale, suppression) = (F(view.MoraleLevel) / 100, F(view.SuppressionLevel) / 100);
        var dark = frame.Count(c.Layout.Panel, p => p == Palette.BarBack);
        var expected = (2 * 202 * 7) - (1000 * (morale + suppression));
        Assert.True(Math.Abs(dark - expected) <= 150, $"panel bars: {dark} dark pixels, expected {expected:0}");
        var filled = frame.Count(c.Layout.Panel, p => p == Palette.SuppressionBar);
        Assert.True(Math.Abs(filled - (1000 * suppression)) <= 100, $"panel suppression bar: {filled} at {suppression:0.00}");

        var men = view.Soldiers.Count;
        foreach (var (name, offset) in new[] { ("suppression", 245), ("rounds", 278) })
        {
            var column = new Area(c.Layout.Panel.X + 10 + offset, c.Layout.Panel.Y, 22, c.Layout.Panel.H);
            var rows = frame.Find(column, p => p.R > 190 && p.G > 190 && p.B > 180).Select(p => (int)p.Y).Distinct().Order().ToArray();
            var lines = rows.Where((y, i) => i == 0 || y > rows[i - 1] + 2).Count();
            Assert.True(lines == men, $"panel {name} column: {lines} lines for {men} men");
        }
    }

    [ScreenshotFact]
    public void Fix_assault_frames_show_own_units_orders_and_only_spotted_enemies()
    {
        var out_ = Path.Combine(Root(), "TestResults", "screens");
        Capture("--scenario", "fix-assault", "--seed", "1", "--ticks", "0,15,600", "--scripted", "--select", "2", "--out", out_);
        var c = Twin("fix-assault", true, 2);
        Png Frame(int tick) => Png.Load(Path.Combine(out_, $"fix-assault-seed1-tick{tick}.png"));
        var ground = Palette.Terrain(TerrainType.Open, 0);

        var first = Frame(0);
        Assert.Equal((1280, 720), (first.Width, first.Height));
        Assert.True(first.Is(c.Camera.ToScreen(390, 110), ground), "open ground");
        Assert.All(Men(c, "us"), s => Assert.True(first.Is(ScreenOf(c, s), Palette.Own), "own man drawn"));
        Assert.All(Men(c, "de"), s => Assert.True(first.Is(ScreenOf(c, s), ground), "unspotted enemy not drawn"));
        Assert.Equal(0, first.Count(new Area(0, 0, 1280, 720), Palette.IsEnemy));
        Assert.True(first.Count(c.Layout.Map, p => p == Palette.MoraleBar) >= 100, "morale bars on the units");
        Assert.True(first.Count(c.Layout.Panel, p => p == Palette.Panel) > 50_000, "panel");
        Assert.True(first.Count(c.Layout.Panel, p => p == Palette.Text) > 200, "panel text");

        PanelShowsTheSquad(first, c, 2);

        // 1.5 s in, the scripted orders are still travelling down the chain of command.
        StepTo(c, 15);
        Assert.Contains(c.Battle.View.Squads, q => c.Battle.View.StatusOf(q.Id)!.Status == global::PointBlank.Sim.Orders.OrderStatus.Pending);
        Assert.True(Frame(15).Count(c.Layout.Map, p => p == Palette.DelayBar) > 60, "order delay shown on the unit");
        Assert.True(Frame(15).Count(c.Layout.Panel, p => p == Palette.DelayBar) > 30, "order delay shown in the panel");
        var waiting = c.Battle.View.Squads
            .Where(q => c.Battle.View.StatusOf(q.Id)!.Status == global::PointBlank.Sim.Orders.OrderStatus.Pending).ToArray();
        Assert.All(waiting, q =>
        {
            // The delay progress bar, right of the morale bar: filled as far as the delay has run, dark beyond.
            var order = c.Battle.View.StatusOf(q.Id)!;
            var done = 1 - (order.TicksRemaining / (float)order.TotalDelayTicks);
            var at = LabelOf(c, q);
            BarShows(Frame(15), new Area(at.X + 64, at.Y + 4, 30, 9), Palette.DelayBar, done, $"delay bar of squad {q.Id}");
        });

        StepTo(c, 600);
        var late = Frame(600);
        PanelShowsTheSquad(late, c, 2);

        // Suppression bars on every squad, as long as its suppression; the battle must have suppressed someone.
        Assert.Contains(c.Battle.View.Squads, q => F(q.SuppressionLevel) > 10);
        Assert.All(c.Battle.View.Squads, q =>
        {
            var at = LabelOf(c, q);
            BarShows(late, new Area(at.X, at.Y + 10, 60, 3), Palette.SuppressionBar, F(q.SuppressionLevel) / 100, $"suppression bar of squad {q.Id}");
        });
        var contacts = c.Battle.View.KnownEnemies.Select(k => c.Camera.ToScreen(F(k.X), F(k.Y))).ToArray();
        Assert.NotEmpty(contacts);
        Assert.All(contacts, at => Assert.True(late.Near(at, 8, Palette.IsEnemy), "contact drawn"));
        Assert.All(
            late.Find(new Area(0, 0, 1280, 720), Palette.IsEnemy),
            p => Assert.True(contacts.Any(at => Gap(at, p) <= 40), $"enemy colour away from every contact at {p}"));
        Assert.All(
            Men(c, "de").Select(s => ScreenOf(c, s)).Where(at => contacts.All(k => Gap(at, k) > 40)),
            at => Assert.False(late.Near(at, 2, Palette.IsEnemy), "an unspotted enemy is drawn"));
    }

    [ScreenshotFact]
    public void Selection_rings_are_drawn_round_the_selected_squads_men_only()
    {
        // Close in on the selected squad: at the default zoom its men stand on top of one another's rings.
        var c = Twin("fix-assault", true, 2);
        var us = c.Battle.World.GetSystem<OrgSystem>().Squads.Where(q => q.Side == "us").ToArray();
        var squad = us.Single(q => q.Id == 2).Soldiers;
        var (x, y) = (MathF.Round(squad.Average(s => F(s.X))), MathF.Round(squad.Average(s => F(s.Y))));
        const float zoom = 14;
        var out_ = Path.Combine(Root(), "TestResults", "screens", "zoomed");
        Capture(
            "--scenario", "fix-assault", "--seed", "1", "--ticks", "0", "--scripted", "--select", "2",
            "--zoom", $"{zoom}", "--center", $"{x},{y}", "--out", out_);
        c.Camera.Set(x, y, zoom);
        var frame = Png.Load(Path.Combine(out_, "fix-assault-seed1-tick0.png"));

        var radius = (Math.Clamp(0.7f * zoom, 3, 12) * 1.6f) + 2;
        var men = Men(c, "us").Select(m => (At: ScreenOf(c, m), m.Posture)).ToArray();
        var picked = squad.Select(m => ScreenOf(c, m)).Where(c.Layout.Map.Contains).ToArray();
        Assert.True(picked.Length >= squad.Count / 2, "the selected squad is in view");
        var rings = picked.Select(at => (At: at, Seen: OnRing(frame, at, radius, men, c))).ToArray();
        Assert.True(rings.Sum(r => r.Seen.Open) >= 32 * picked.Length, "most of each ring is in the open");
        Assert.All(rings, r => Assert.True(r.Seen.Ringed >= r.Seen.Open * 3 / 4, $"selection ring at {r.At}: {r.Seen}"));

        var apart = us.Where(q => q.Id != 2).SelectMany(q => q.Soldiers).Select(m => ScreenOf(c, m))
            .Where(at => c.Layout.Map.Contains(at) && picked.All(p => Gap(p, at) > 3 * radius)).ToArray();
        Assert.All(apart, at => Assert.True(OnRing(frame, at, radius, men, c).Ringed < 8, $"ring on an unselected man at {at}"));
    }

    [ScreenshotFact]
    public void The_test_map_frame_shows_terrain_hedges_and_buildings()
    {
        var out_ = Path.Combine(Root(), "TestResults", "screens");
        Capture("--scenario", "view-sandbox", "--seed", "1", "--ticks", "0", "--out", out_);
        var c = Twin("view-sandbox", false);
        var frame = Png.Load(Path.Combine(out_, "view-sandbox-seed1-tick0.png"));
        var map = c.Battle.View.Map!;
        var tile = F(map.TileSize);
        var men = Men(c, "us").Select(s => ScreenOf(c, s)).ToArray();
        var built = map.Buildings.SelectMany(b => b.Tiles).ToHashSet();
        Pt At(int x, int y, float dx = 0.5f, float dy = 0.5f) => c.Camera.ToScreen((x + dx) * tile, (y + dy) * tile);
        var tiles = (from y in Enumerable.Range(0, map.Height) from x in Enumerable.Range(0, map.Width) select new TilePos(x, y))
            .Where(t => men.All(m => Gap(m, At(t.X, t.Y)) > 90))
            .ToArray();

        foreach (var type in new[] { TerrainType.Open, TerrainType.Road, TerrainType.LightWoods })
        {
            var sample = tiles.Where(t => map.Tile(t.X, t.Y).Terrain == type && !built.Contains(t)).Take(25).ToArray();
            Assert.NotEmpty(sample);
            Assert.All(sample, t => Assert.True(
                frame.Is(At(t.X, t.Y), Palette.Terrain(type, map.Tile(t.X, t.Y).ElevationDm)), $"{type} at {t}"));
        }

        var buildings = tiles.Where(built.Contains).ToArray();
        Assert.NotEmpty(buildings);
        Assert.All(buildings, t => Assert.True(frame.Is(At(t.X, t.Y), Palette.Building), $"building at {t}"));

        var hedges = tiles.Where(t => map.Tile(t.X, t.Y).HedgeEast).Take(25).ToArray();
        Assert.NotEmpty(hedges);
        Assert.All(hedges, t => Assert.True(frame.Is(At(t.X, t.Y, 1), Palette.Hedge), $"hedge at {t}"));
        Assert.Equal(0, frame.Count(new Area(0, 0, 1280, 720), Palette.IsEnemy));
    }

    [ScreenshotTheory]
    [InlineData("F1", "sight", 500)]
    [InlineData("F2", "values", 40)]
    [InlineData("F3", "truth", 100)]
    [InlineData("F4", "intent", 60)]
    public void Each_overlay_key_puts_its_overlay_on_the_screen_and_no_other(string key, string name, int least)
    {
        var colours = new Dictionary<string, Rgb>
        {
            ["sight"] = Palette.Sight, ["values"] = Palette.Values, ["truth"] = Palette.Truth, ["intent"] = Palette.Intent,
        };
        var map = Twin("fix-assault", true).Layout.Map;
        Png Frame(string folder, params string[] keys)
        {
            var out_ = Path.Combine(Root(), "TestResults", "screens", "overlays", folder);
            string[] options = ["--scenario", "fix-assault", "--seed", "1", "--ticks", "600", "--scripted", "--select", "2", "--out", out_];
            Capture(keys.Length == 0 ? options : [.. options, "--press", string.Join(",", keys)]);
            return Png.Load(Path.Combine(out_, "fix-assault-seed1-tick600.png"));
        }

        var on = Frame(name, key);
        var pixels = on.Count(map, p => p == colours[name]);
        Assert.True(pixels >= least, $"{name}: {pixels} pixels on, at least {least} expected");
        Assert.All(colours.Where(c => c.Key != name), c => Assert.Equal(0, on.Count(map, p => p == c.Value)));

        // Off, both never pressed and pressed twice.
        foreach (var off in new[] { Frame($"{name}-never"), Frame($"{name}-twice", key, key) })
        {
            Assert.All(colours.Values, c => Assert.Equal(0, off.Count(new Area(0, 0, 1280, 720), p => p == c)));
        }
    }

    /// <summary>A reader for the 8-bit RGB or RGBA, non-interlaced PNG files Godot writes.</summary>
    private sealed class Png(int width, int height, int channels, byte[] pixels)
    {
        public int Width => width;

        public int Height => height;

        private Rgb this[int x, int y]
        {
            get
            {
                var i = ((y * width) + x) * channels;
                return new Rgb(pixels[i], pixels[i + 1], pixels[i + 2]);
            }
        }

        public bool Is(Pt at, Rgb colour) => this[(int)at.X, (int)at.Y] == colour;

        public IEnumerable<Pt> Find(Area area, Func<Rgb, bool> wanted)
        {
            for (var y = Math.Max(0, (int)area.Y); y < Math.Min(height, (int)area.Bottom); y++)
            {
                for (var x = Math.Max(0, (int)area.X); x < Math.Min(width, (int)area.Right); x++)
                {
                    if (wanted(this[x, y]))
                    {
                        yield return new Pt(x, y);
                    }
                }
            }
        }

        public int Count(Area area, Func<Rgb, bool> wanted) => Find(area, wanted).Count();

        public bool Near(Pt at, float radius, Func<Rgb, bool> wanted) =>
            Find(new Area(at.X - radius, at.Y - radius, (2 * radius) + 1, (2 * radius) + 1), wanted).Any();

        public static Png Load(string path)
        {
            var file = File.ReadAllBytes(path).AsSpan();
            int width = 0, height = 0, channels = 0;
            var data = new MemoryStream();
            for (var at = 8; at < file.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(file[at..]);
                var type = System.Text.Encoding.ASCII.GetString(file.Slice(at + 4, 4));
                var body = file.Slice(at + 8, length);
                if (type == "IHDR")
                {
                    width = BinaryPrimitives.ReadInt32BigEndian(body);
                    height = BinaryPrimitives.ReadInt32BigEndian(body[4..]);
                    Assert.True(body[8] == 8 && body[9] is 2 or 6 && body[12] == 0, "unexpected PNG format");
                    channels = body[9] == 2 ? 3 : 4;
                }
                else if (type == "IDAT")
                {
                    data.Write(body);
                }

                at += length + 12;
            }

            data.Position = 0;
            using var zlib = new ZLibStream(data, CompressionMode.Decompress);
            var stride = width * channels;
            var pixels = new byte[stride * height];
            for (var y = 0; y < height; y++)
            {
                var filter = zlib.ReadByte();
                var row = pixels.AsSpan(y * stride, stride);
                zlib.ReadExactly(row);
                for (var i = 0; i < stride; i++)
                {
                    int a = i >= channels ? row[i - channels] : 0;
                    int b = y > 0 ? pixels[((y - 1) * stride) + i] : 0;
                    int c = y > 0 && i >= channels ? pixels[((y - 1) * stride) + i - channels] : 0;
                    int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - (2 * c));
                    row[i] += (byte)(filter switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
                        _ => 0,
                    });
                }
            }

            return new Png(width, height, channels, pixels);
        }
    }
}
