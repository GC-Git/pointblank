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

        // Null outside development builds: the truth overlays are then inert.
        if (c.Battle.DebugTruth() is not { } truth)
        {
            c.TruthOverlaysInert = true;
            return;
        }

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
            // The men's own numbers take their places first; the squad's line then keeps clear of them.
            foreach (var s in c.Camera.Scale < 8 ? [] : men)
            {
                var at = c.Camera.ToScreen(s.X.F(), s.Y.F());
                Text(list, c, new Pt(at.X + 12, at.Y + 4), Number(s.Suppression), Palette.Values);
            }

            Under(list, c, men, 14, $"#{q.Id} sup {Number(q.Suppression)} mor {Number(q.MoraleLevel)}", Palette.Values);
        }
    }

    /// <summary>
    /// A line from each man to where he is going (ringed end) and to what his fire task aims at (crossed end; thick while
    /// he is firing). Under each squad its order and what its men are doing; close in, each man's own words.
    /// </summary>
    private static void Intent(List<Prim> list, GameController c, SpottingDebug truth)
    {
        foreach (var q in truth.Squads)
        {
            var men = truth.Soldiers.Where(s => s.SquadId == q.Id).ToArray();
            var able = men.Where(Able).ToArray();
            foreach (var s in able)
            {
                var from = c.Camera.ToScreen(s.X.F(), s.Y.F());
                if (!c.Layout.Map.Contains(from))
                {
                    continue;
                }

                if (s.Move != IntentKind.None)
                {
                    var to = c.Camera.ToScreen(s.MoveX.F(), s.MoveY.F());
                    list.Add(new LinePrim(from, to, Palette.Intent, 1));
                    list.Add(new CirclePrim(to, 2, Palette.Intent));
                }

                if (s.Aims)
                {
                    var at = c.Camera.ToScreen(s.FireX.F(), s.FireY.F());
                    list.Add(new LinePrim(from, at, Palette.Intent, s.Fire == IntentKind.Firing ? 2 : 1));
                    list.Add(new LinePrim(new Pt(at.X - 4, at.Y - 4), new Pt(at.X + 4, at.Y + 4), Palette.Intent, 1));
                    list.Add(new LinePrim(new Pt(at.X - 4, at.Y + 4), new Pt(at.X + 4, at.Y - 4), Palette.Intent, 1));
                }
            }

            var order = q.Order is { } kind ? $"{Words.Of(kind)} ({Words.Of(q.OrderStatus)})" : "no order";
            var tallies = able.SelectMany(Doing).GroupBy(k => k).OrderBy(g => g.Key)
                .Select(g => $"{Task(g.Key)} {g.Count()}").ToList();
            var idle = able.Count(s => !Doing(s).Any());
            if (idle > 0)
            {
                tallies.Add($"{Task(IntentKind.None)} {idle}");
            }

            foreach (var s in c.Camera.Scale < 8 ? [] : able)
            {
                var at = c.Camera.ToScreen(s.X.F(), s.Y.F());
                var words = Doing(s).Any() ? string.Join(", ", Doing(s).Select(Task)) : Task(IntentKind.None);
                var next = s.PendingPosture is { } p ? $" > {Words.Of(p)}" : "";
                Text(list, c, new Pt(at.X + 12, at.Y + 15), words + next, Palette.Intent);
            }

            Under(list, c, men, 27, $"#{q.Id}: {order}; {(tallies.Count == 0 ? "none able" : string.Join(", ", tallies))}", Palette.Intent);
        }
    }

    /// <summary>A man's drill, movement and fire, whichever he has.</summary>
    private static IEnumerable<IntentKind> Doing(TruthSoldier s) =>
        new[] { s.Task, s.Move, s.Fire }.Where(k => k != IntentKind.None);

    private static string Task(IntentKind kind) => kind switch
    {
        IntentKind.None => "idle",
        IntentKind.Move => "moving",
        IntentKind.LocalMove => "own move",
        IntentKind.BrokenLying => "lying broken",
        IntentKind.BrokenStill => "halted broken",
        _ => Words.Of(kind).ToLowerInvariant(),
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
        if (!c.Layout.Map.Contains(at))
        {
            return;
        }

        at = Clear(list, c, at, text);
        list.Add(new TextPrim(new Pt(at.X + 1, at.Y + 1), text, Palette.Shadow, 11));
        list.Add(new TextPrim(at, text, colour, 11));
    }

    /// <summary>
    /// The nearest place to <paramref name="at"/> where the text covers no text already drawn: line by line below and
    /// above it, then in columns to either side.
    /// </summary>
    private static Pt Clear(List<Prim> list, GameController c, Pt at, string text)
    {
        var taken = list.OfType<TextPrim>().Where(t => t.Colour != Palette.Shadow).Select(TextBox.Of).ToArray();
        var width = TextBox.Of(new TextPrim(at, text, Palette.Shadow, 11)).W;
        for (var column = 0; column < 12; column++)
        {
            var dx = (column % 2 == 0 ? 1 : -1) * ((column + 1) / 2) * (width + 6);
            for (var step = 0; step < 24; step++)
            {
                var dy = (step % 2 == 1 ? 1 : -1) * ((step + 1) / 2) * 13f;
                var place = new Pt(at.X + dx, at.Y + dy);
                var box = TextBox.Of(new TextPrim(place, text, Palette.Shadow, 11));
                if (c.Layout.Map.Contains(place)
                    && !taken.Any(t => box.X < t.Right && t.X < box.Right && box.Y < t.Bottom && t.Y < box.Bottom))
                {
                    return place;
                }
            }
        }

        return at;
    }
}
