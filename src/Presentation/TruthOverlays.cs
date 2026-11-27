using System.Globalization;
using PointBlank.Sim;
using PointBlank.Sim.Org;
using PointBlank.Sim.Spotting;

namespace PointBlank.Presentation;

/// <summary>
/// DEBUG ONLY (item 2.6): the overlays that show the truth behind the fog. This is the only UI code that may ask for it
/// (<see cref="PlayerBattle.DebugTruth"/>, held to this class by a boundary test); it turns it into shapes and keeps none.
/// </summary>
internal static class TruthOverlays
{

    // Men get their own text from 8 pixels per metre; further out only squads do.
    public static void Draw(List<Prim> list, GameController c)
    {
        if ((c.Overlays & (Overlay.Values | Overlay.Truth | Overlay.Intent)) == 0)
        {
            return;
        }

        var truth = c.Battle.DebugTruth();
        if (c.Overlays.HasFlag(Overlay.Truth))
        {
            Positions(list, c, truth.Soldiers);
        }

        if (c.Overlays.HasFlag(Overlay.Intent))
        {
            Intent(list, c, truth);
        }

        if (c.Overlays.HasFlag(Overlay.Values))
        {
            Values(list, c, truth);
        }
    }

    /// <summary>A hollow square on every enemy where he really is, and a line to it from the player's contact of him.</summary>
    private static void Positions(List<Prim> list, GameController c, IReadOnlyList<TruthSoldier> soldiers)
    {
        var contacts = c.Battle.View.KnownEnemies.ToDictionary(k => k.EnemyId);
        foreach (var s in soldiers.Where(s => s.Side != c.Battle.View.Side))
        {
            var at = c.Camera.ToScreen(s.X.F(), s.Y.F());
            if (!c.Layout.Map.Contains(at))
            {
                continue;
            }

            var r = Math.Clamp(0.7f * c.Camera.Scale, 3, 12) + (Able(s) ? 2 : 0);
            list.Add(new RectPrim(at.X - r, at.Y - r, 2 * r, 2 * r, Palette.Truth, false, Able(s) ? 2 : 1));
            if (contacts.TryGetValue(s.Id, out var k))
            {
                list.Add(new LinePrim(c.Camera.ToScreen(k.X.F(), k.Y.F()), at, Palette.Truth, 1));
            }
        }
    }

    /// <summary>Under each squad its mean suppression and morale; close in, each man's suppression beside him.</summary>
    private static void Values(List<Prim> list, GameController c, SpottingDebug truth)
    {
        foreach (var q in truth.Squads)
        {
            var men = truth.Soldiers.Where(s => s.SquadId == q.Id).ToArray();
            Under(list, c, men, 14, $"#{q.Id} sup {Number(q.Suppression)} mor {Number(q.MoraleLevel)}", Palette.Values);
            if (c.Camera.Scale < 8)
            {
                continue;
            }

            foreach (var s in men)
            {
                var at = c.Camera.ToScreen(s.X.F(), s.Y.F());
                Text(list, c, new Pt(at.X + 12, at.Y + 4), Number(s.Suppression), Palette.Values);
            }
        }
    }

    /// <summary>
    /// A line from each man to where he is going or what he fires at, and under each squad its order and what its men are
    /// doing; close in, each man's own task.
    /// </summary>
    private static void Intent(List<Prim> list, GameController c, SpottingDebug truth)
    {
        foreach (var q in truth.Squads)
        {
            var men = truth.Soldiers.Where(s => s.SquadId == q.Id).ToArray();
            foreach (var s in men.Where(s => s.Intent != IntentKind.None))
            {
                var from = c.Camera.ToScreen(s.X.F(), s.Y.F());
                var to = c.Camera.ToScreen(s.IntentX.F(), s.IntentY.F());
                if (c.Layout.Map.Contains(from))
                {
                    list.Add(new LinePrim(from, to, Palette.Intent, 1));
                    list.Add(new CirclePrim(to, 2, Palette.Intent));
                }
            }

            var order = q.Order is { } kind ? $"{Words.Of(kind)} ({Words.Of(q.OrderStatus)})" : "no order";
            var tasks = string.Join(", ", men.Where(Able).GroupBy(s => s.Intent).OrderBy(g => g.Key)
                .Select(g => $"{Task(g.Key)} {g.Count()}"));
            Under(list, c, men, 27, $"#{q.Id}: {order}; {(tasks.Length == 0 ? "none able" : tasks)}", Palette.Intent);
            if (c.Camera.Scale < 8)
            {
                continue;
            }

            foreach (var s in men.Where(Able))
            {
                var at = c.Camera.ToScreen(s.X.F(), s.Y.F());
                var next = s.PendingPosture is { } p ? $" > {Words.Of(p)}" : "";
                Text(list, c, new Pt(at.X + 12, at.Y + 15), Task(s.Intent) + next, Palette.Intent);
            }
        }
    }

    private static string Task(IntentKind kind) => kind switch
    {
        IntentKind.Move => "moving",
        IntentKind.LocalMove => "own move",
        IntentKind.Held => "held",
        IntentKind.Fire => "firing",
        _ => "idle",
    };

    private static bool Able(TruthSoldier s) => s.Health is HealthState.Ok or HealthState.Wounded;

    private static string Number(Fixed value) => value.F().ToString("0", CultureInfo.InvariantCulture);

    /// <summary>A line of text under a squad's lowest man.</summary>
    private static void Under(List<Prim> list, GameController c, TruthSoldier[] men, float drop, string text, Rgb colour)
    {
        if (men.Length == 0)
        {
            return;
        }

        var able = men.Where(Able).ToArray();
        var group = able.Length == 0 ? men : able;
        var x = group.Average(s => c.Camera.ToScreen(s.X.F(), s.Y.F()).X);
        var y = group.Max(s => c.Camera.ToScreen(s.X.F(), s.Y.F()).Y);
        Text(list, c, new Pt(x - 30, y + drop), text, colour);
    }

    private static void Text(List<Prim> list, GameController c, Pt at, string text, Rgb colour)
    {
        if (c.Layout.Map.Contains(at))
        {
            list.Add(new TextPrim(new Pt(at.X + 1, at.Y + 1), text, Palette.Shadow, 11));
            list.Add(new TextPrim(at, text, colour, 11));
        }
    }
}
