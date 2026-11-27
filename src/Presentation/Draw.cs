using PointBlank.Sim;
using PointBlank.Sim.Map;
using PointBlank.Sim.Spotting;

namespace PointBlank.Presentation;

public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>A point on the screen, in pixels.</summary>
public readonly record struct Pt(float X, float Y);

/// <summary>A rectangle on the screen, in pixels.</summary>
public readonly record struct Area(float X, float Y, float W, float H)
{
    public float Right => X + W;

    public float Bottom => Y + H;

    public Pt Middle => new(X + (W / 2), Y + (H / 2));

    public bool Contains(Pt p) => p.X >= X && p.Y >= Y && p.X < Right && p.Y < Bottom;
}

/// <summary>One thing to paint. The painter knows only these four.</summary>
public abstract record Prim(Rgb Colour)
{
    public virtual bool Filled => false;

    /// <summary>Whether the shape's bounding box comes within <paramref name="slack"/> pixels of a point.</summary>
    public abstract bool Touches(Pt p, float slack);
}

public sealed record RectPrim(float X, float Y, float W, float H, Rgb Colour, bool Fill = true, float Width = 1) : Prim(Colour)
{
    public override bool Filled => Fill;

    public override bool Touches(Pt p, float slack) =>
        p.X >= X - slack && p.Y >= Y - slack && p.X <= X + W + slack && p.Y <= Y + H + slack;
}

public sealed record CirclePrim(Pt Centre, float Radius, Rgb Colour, bool Fill = true, float Width = 1) : Prim(Colour)
{
    public override bool Filled => Fill;

    public override bool Touches(Pt p, float slack) =>
        MathF.Abs(p.X - Centre.X) <= Radius + slack && MathF.Abs(p.Y - Centre.Y) <= Radius + slack;
}

public sealed record LinePrim(Pt A, Pt B, Rgb Colour, float Width = 1) : Prim(Colour)
{
    public override bool Touches(Pt p, float slack) =>
        p.X >= MathF.Min(A.X, B.X) - slack && p.X <= MathF.Max(A.X, B.X) + slack
        && p.Y >= MathF.Min(A.Y, B.Y) - slack && p.Y <= MathF.Max(A.Y, B.Y) + slack;
}

/// <summary>Text with its baseline starting at <paramref name="At"/>, in the engine's default font.</summary>
public sealed record TextPrim(Pt At, string Text, Rgb Colour, int Size = 13) : Prim(Colour)
{
    public override bool Touches(Pt p, float slack) => MathF.Abs(p.X - At.X) <= slack && MathF.Abs(p.Y - At.Y) <= slack;
}

/// <summary>Flat placeholder colours (the art style is a later milestone). The three enemy colours are used for nothing else.</summary>
public static class Palette
{
    public static readonly Rgb Backdrop = new(24, 26, 28);
    public static readonly Rgb Panel = new(36, 40, 44);
    public static readonly Rgb Button = new(62, 68, 76);
    public static readonly Rgb ButtonActive = new(150, 124, 40);
    public static readonly Rgb Text = new(232, 232, 224);
    public static readonly Rgb DimText = new(160, 164, 168);
    public static readonly Rgb Shadow = new(10, 10, 10);

    public static readonly Rgb Hedge = new(36, 74, 28);
    public static readonly Rgb Building = new(134, 112, 98);
    public static readonly Rgb Door = new(70, 46, 30);
    public static readonly Rgb Window = new(172, 208, 224);

    public static readonly Rgb Own = new(40, 92, 210);
    public static readonly Rgb Selected = new(255, 224, 64);
    public static readonly Rgb Down = new(72, 72, 72);
    public static readonly Rgb Wounded = new(255, 255, 255);

    public static readonly Rgb EnemyTracked = new(220, 28, 28);
    public static readonly Rgb EnemyStale = new(238, 124, 32);
    public static readonly Rgb EnemyLost = new(196, 120, 150);

    public static readonly Rgb BarBack = new(12, 12, 12);
    public static readonly Rgb MoraleBar = new(80, 210, 96);
    public static readonly Rgb SuppressionBar = new(236, 196, 24);
    public static readonly Rgb DelayBar = new(110, 200, 240);
    public static readonly Rgb OrderLine = new(224, 240, 255);
    public static readonly Rgb Impact = new(255, 250, 210);
    public static readonly Rgb Mission = new(196, 90, 220);

    // Debug overlays (item 2.6): each colour is used for its overlay only.
    public static readonly Rgb Sight = new(150, 250, 236);
    public static readonly Rgb Values = new(255, 160, 72);
    public static readonly Rgb Truth = new(255, 0, 255);
    public static readonly Rgb Intent = new(190, 255, 60);

    public static bool IsEnemy(Rgb c) => c == EnemyTracked || c == EnemyStale || c == EnemyLost;

    public static Rgb Contact(ContactQuality quality) => quality switch
    {
        ContactQuality.Tracked => EnemyTracked,
        ContactQuality.Stale => EnemyStale,
        _ => EnemyLost,
    };

    /// <summary>Ground colour by terrain type, lighter for higher ground.</summary>
    public static Rgb Terrain(TerrainType type, int elevationDm)
    {
        var c = type switch
        {
            TerrainType.Road => new Rgb(168, 152, 120),
            TerrainType.LightWoods => new Rgb(66, 104, 58),
            _ => new Rgb(118, 146, 82),
        };
        var lift = Math.Clamp(elevationDm, -50, 50);
        byte Shade(byte v) => (byte)Math.Clamp(v + lift, 0, 255);
        return new Rgb(Shade(c.R), Shade(c.G), Shade(c.B));
    }
}

public static class Num
{
    /// <summary>For drawing only: nothing computed here goes back into the sim except order points, rounded to millimetres.</summary>
    public static float F(this Fixed value) => (float)(value.Raw / 4294967296.0);

    public static Fixed ToFixed(float metres) => Fixed.FromRatio((long)MathF.Round(metres * 1000f), 1000);
}
