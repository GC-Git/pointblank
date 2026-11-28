using PointBlank.Sim;
using PointBlank.Sim.Org;

namespace PointBlank.Presentation;

/// <summary>
/// Where each of the side's own able men is drawn (item 2.13). Far out, men a few metres apart would be one blob, so
/// crowded figures are eased apart on the screen. No figure moves farther than <see cref="MaxShiftMetres"/> of ground or
/// two figure half-sizes, whichever is less; men who are not crowded stay exactly on their places. Drawing and picking
/// both use these places; nothing else does, and none of it reaches the sim.
/// </summary>
public static class Places
{
    public const float MaxShiftMetres = 6;

    /// <summary>The spacing aimed for, in figure half-sizes.</summary>
    private const float Spacing = 1.7f;

    private const int Passes = 12;

    public static float Cap(Camera camera) => MathF.Min(2 * FrameBuilder.Size(camera), MaxShiftMetres * camera.Scale);

    public static IReadOnlyDictionary<int, Pt> Of(IReadOnlyList<SquadView> squads, Camera camera)
    {
        var men = squads.SelectMany(q => q.Soldiers).Where(s => s.Health is HealthState.Ok or HealthState.Wounded)
            .Select(s => (s.Id, At: camera.ToScreen(s.X.F(), s.Y.F())))
            .OrderBy(m => m.At.X).ThenBy(m => m.Id)
            .ToArray();
        var want = Spacing * FrameBuilder.Size(camera);
        var cap = Cap(camera);

        // Only pairs that could ever come within the spacing need looking at.
        var near = new List<(int A, int B)>();
        for (var i = 0; i < men.Length; i++)
        {
            for (var j = i + 1; j < men.Length && men[j].At.X - men[i].At.X < want + (2 * cap); j++)
            {
                if (MathF.Abs(men[j].At.Y - men[i].At.Y) < want + (2 * cap))
                {
                    near.Add((i, j));
                }
            }
        }

        var at = men.Select(m => m.At).ToArray();
        for (var pass = 0; pass < Passes; pass++)
        {
            foreach (var (a, b) in near)
            {
                var (dx, dy) = (at[b].X - at[a].X, at[b].Y - at[a].Y);
                var gap = MathF.Sqrt((dx * dx) + (dy * dy));
                if (gap >= want)
                {
                    continue;
                }

                if (gap < 0.01f)
                {
                    // Men on one spot part in a fixed direction of their own.
                    (dy, dx) = MathF.SinCos(men[b].Id * 2.399963f);
                }
                else
                {
                    (dx, dy) = (dx / gap, dy / gap);
                }

                var push = (want - gap) / 2;
                at[a] = Within(men[a].At, new Pt(at[a].X - (dx * push), at[a].Y - (dy * push)), cap);
                at[b] = Within(men[b].At, new Pt(at[b].X + (dx * push), at[b].Y + (dy * push)), cap);
            }
        }

        return men.Select((m, i) => (m.Id, At: at[i])).ToDictionary(p => p.Id, p => p.At);
    }

    private static Pt Within(Pt home, Pt p, float cap)
    {
        var (dx, dy) = (p.X - home.X, p.Y - home.Y);
        var far = MathF.Sqrt((dx * dx) + (dy * dy));
        return far <= cap ? p : new Pt(home.X + (dx * cap / far), home.Y + (dy * cap / far));
    }
}
