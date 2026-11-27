using System.Globalization;
using PointBlank.Sim;

namespace PointBlank.Presentation;

/// <summary>Where the parts of the screen are: a status strip on top, the map, a panel on the right, an order bar below.</summary>
public sealed record Layout(int Width = 1280, int Height = 720)
{
    private const int TopHeight = 30;
    private const int BottomHeight = 42;
    private const int PanelWidth = 330;

    public Area Top => new(0, 0, Width, TopHeight);

    public Area Map => new(0, TopHeight, Width - PanelWidth, Height - TopHeight - BottomHeight);

    public Area Panel => new(Width - PanelWidth, TopHeight, PanelWidth, Height - TopHeight - BottomHeight);

    public Area Bottom => new(0, Height - BottomHeight, Width, BottomHeight);
}

/// <summary>Maps battle metres to screen pixels: the point (CentreX, CentreY) sits in the middle of the map area.</summary>
public sealed class Camera(Area area)
{
    public const float MinScale = 0.5f;
    public const float MaxScale = 40f;

    public Area Area { get; } = area;

    /// <summary>Pixels per metre.</summary>
    public float Scale { get; private set; } = 1;

    public float CentreX { get; private set; }

    public float CentreY { get; private set; }

    /// <summary>Shows the whole map.</summary>
    public void Fit(MapView map)
    {
        var w = map.Width * map.TileSize.F();
        var h = map.Height * map.TileSize.F();
        Set(w / 2, h / 2, MathF.Min(Area.W / w, Area.H / h) * 0.98f);
    }

    public void Set(float centreX, float centreY, float scale)
    {
        CentreX = centreX;
        CentreY = centreY;
        Scale = Math.Clamp(scale, MinScale, MaxScale);
    }

    public Pt ToScreen(float x, float y) =>
        new(Area.Middle.X + ((x - CentreX) * Scale), Area.Middle.Y + ((y - CentreY) * Scale));

    public (float X, float Y) ToWorld(Pt p) =>
        (CentreX + ((p.X - Area.Middle.X) / Scale), CentreY + ((p.Y - Area.Middle.Y) / Scale));

    /// <summary>Moves the picture by a number of pixels (as a drag does).</summary>
    public void Pan(float dx, float dy) => Set(CentreX - (dx / Scale), CentreY - (dy / Scale), Scale);

    /// <summary>Zooms so that the ground under <paramref name="at"/> stays there.</summary>
    public void Zoom(Pt at, float factor)
    {
        var (x, y) = ToWorld(at);
        var scale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        Set(x - ((at.X - Area.Middle.X) / scale), y - ((at.Y - Area.Middle.Y) / scale), scale);
    }
}

/// <summary>
/// Turns frame time into whole sim ticks (ten per battle second at 1x). Only the number of ticks reaches the sim, so the
/// battle is the same at any frame rate or speed. A stall is dropped, not replayed all at once.
/// </summary>
public sealed class SimClock
{
    public const int MaxTicksPerFrame = 10;
    private const double MaxFrameSeconds = 0.25;
    private double _owed;

    public bool Paused { get; set; }

    /// <summary>1, 2 or 4.</summary>
    public int Speed { get; set; } = 1;

    public int Advance(double seconds)
    {
        if (Paused)
        {
            return 0;
        }

        // 10 is World.TicksPerSecond (a constant; naming World here is not allowed).
        _owed += Math.Min(seconds, MaxFrameSeconds) * Speed * 10;
        var ticks = (int)_owed;
        _owed -= ticks;
        return Math.Min(ticks, MaxTicksPerFrame);
    }
}

/// <summary>The command line after Godot's <c>--</c>.</summary>
public sealed record LaunchOptions(
    string Scenario, ulong Seed, bool Screenshot, IReadOnlyList<int> Ticks, string Out, bool Scripted, IReadOnlyList<int> Select,
    float? Zoom, (float X, float Y)? Center)
{
    /// <summary>Screenshot mode: keys pressed before the first frame, by Godot's key names (e.g. F1).</summary>
    public IReadOnlyList<string> Press { get; init; } = [];

    /// <summary>Start on the whole map instead of the player's forces.</summary>
    public bool WholeMap { get; init; }

    /// <exception cref="ArgumentException">An unknown or incomplete option.</exception>
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var o = new LaunchOptions("fix-assault", 1, false, [0], "", false, [], null, null);
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            string Value() => ++i < args.Count ? args[i] : throw new ArgumentException($"{name} needs a value.");
            int[] Numbers() => Value().Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).Order().ToArray();
            float Real(string v) => float.Parse(v, CultureInfo.InvariantCulture);
            o = name switch
            {
                "--scenario" => o with { Scenario = Value() },
                "--seed" => o with { Seed = ulong.Parse(Value(), CultureInfo.InvariantCulture) },
                "--screenshot" => o with { Screenshot = true },
                "--ticks" => o with { Ticks = Numbers() },
                "--out" => o with { Out = Value() },
                "--press" => o with { Press = Value().Split(',') },
                "--scripted" => o with { Scripted = true },
                "--whole-map" => o with { WholeMap = true },
                "--select" => o with { Select = Numbers() },
                "--zoom" => o with { Zoom = Real(Value()) },
                "--center" => o with { Center = Value().Split(',') is [var x, var y] ? (Real(x), Real(y)) : throw new ArgumentException("--center needs x,y.") },
                _ => throw new ArgumentException($"Unknown option '{name}'."),
            };
        }

        return o.Screenshot && o.Out.Length == 0 ? throw new ArgumentException("--screenshot needs --out <directory>.") : o;
    }
}
