using PointBlank.Sim;
using PointBlank.Sim.Map;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Presentation;

/// <summary>The key to the symbols (item 2.13), drawn with the same figures as the battle, in the map's lower left.</summary>
internal static class Legend
{
    private const float Row = 24;
    private const float Figure = 8;

    public static void Draw(List<Prim> list, GameController c)
    {
        var right = new Pt(1, 0);
        var rows = new (string Text, Func<Pt, IEnumerable<Prim>> Symbol)[]
        {
            ("Your man, standing", at => Figures.Man(Sample(Posture.Standing), at, Figure, right)),
            ("Kneeling", at => Figures.Man(Sample(Posture.Kneeling), at, Figure, right)),
            ("Prone (lying down)", at => Figures.Man(Sample(Posture.Prone), at, Figure, right)),
            ("Facing unknown (three postures)", at => new[] { Posture.Standing, Posture.Kneeling, Posture.Prone }
                .SelectMany((p, i) => Figures.Man(Sample(p), new Pt(at.X + ((i - 1) * 17), at.Y), 7, null))),
            ("Wounded, still fighting", at => Figures.Man(Sample(Posture.Kneeling, HealthState.Wounded), at, Figure, right)),
            ("Badly hurt, out of action", at => Figures.Fallen(at, Figure, Palette.Down, false)),
            ("Killed", at => Figures.Fallen(at, Figure, Palette.Down, true)),
            ("Enemy in sight (square: MG)", at => Figures.Contact(Enemy(ContactQuality.Tracked), at, Figure, 0)),
            ("Enemy last seen here", at => Figures.Contact(Enemy(ContactQuality.Stale), at, Figure, 100).Where(p => p is not TextPrim)),
            ("Enemy lost track of", at => Figures.Contact(Enemy(ContactQuality.Lost), at, Figure, 0)),
            ("Enemy fallen (hurt or dead)", at => Figures.Fallen(at, Figure, Palette.EnemyDown, null)),
            ("Your fire", at => Streak(at, Palette.OwnTracer)),
            ("Enemy fire you can see", at => Streak(at, Palette.EnemyTracer)),
            ("Fire coming in from that side", at =>
            [
                new LinePrim(new Pt(at.X - 14, at.Y), new Pt(at.X + 12, at.Y), Palette.Incoming, 3),
                new LinePrim(new Pt(at.X + 3, at.Y - 6), new Pt(at.X + 12, at.Y), Palette.Incoming, 2),
                new LinePrim(new Pt(at.X + 3, at.Y + 6), new Pt(at.X + 12, at.Y), Palette.Incoming, 2),
            ]),
            ("Shell burst (a dark crater stays)", at => [new CirclePrim(at, 9, Palette.Fireball), new CirclePrim(at, 4, Palette.Impact)]),
            ("Hedge / woods", at =>
            [
                new RectPrim(at.X - 16, at.Y - 3, 16, 6, Palette.Hedge),
                new RectPrim(at.X + 4, at.Y - 9, 18, 18, Palette.Terrain(TerrainType.LightWoods, 0)),
                new CirclePrim(new Pt(at.X + 13, at.Y), 5, Palette.TreeCrown),
            ]),
            ("Road / building", at =>
            [
                new RectPrim(at.X - 20, at.Y - 5, 18, 10, Palette.Terrain(TerrainType.Road, 0)),
                new RectPrim(at.X + 4, at.Y - 9, 18, 18, Palette.Building),
                new RectPrim(at.X + 4, at.Y - 9, 18, 18, Palette.Wall, false, 2),
            ]),
            ("Where an order sends the squad", at =>
            [
                new LinePrim(new Pt(at.X - 18, at.Y), new Pt(at.X + 10, at.Y), Palette.OrderLine, 1),
                new CirclePrim(new Pt(at.X + 14, at.Y), 4, Palette.OrderLine, false, 1.5f),
            ]),
            ("Squad bars: morale / suppression", at =>
            [
                new RectPrim(at.X - 16, at.Y - 5, 32, 3, Palette.MoraleBar),
                new RectPrim(at.X - 16, at.Y + 2, 20, 3, Palette.SuppressionBar),
            ]),
        };

        var area = c.Layout.Map;
        var height = (rows.Length * Row) + 46;
        var (x, y) = (area.X + 8, area.Bottom - height - 8);
        list.Add(new RectPrim(x, y, 300, height, Palette.Panel));
        list.Add(new RectPrim(x, y, 300, height, Palette.DimText, false));
        list.Add(new TextPrim(new Pt(x + 10, y + 18), "Symbols (L closes)", Palette.Selected, 13));
        list.Add(new TextPrim(new Pt(x + 10, y + 34), "Dark line: aim or way. Crowded men are eased apart.", Palette.DimText, 11));
        for (var i = 0; i < rows.Length; i++)
        {
            var at = new Pt(x + 34, y + 52 + (i * Row));
            list.Add(new RectPrim(at.X - 26, at.Y - 12, 52, 24, Palette.Terrain(TerrainType.Open, 0)));
            list.AddRange(rows[i].Symbol(at));
            list.Add(new TextPrim(new Pt(x + 70, at.Y + 4), rows[i].Text, Palette.Text, 13));
        }
    }

    private static IEnumerable<Prim> Streak(Pt at, Rgb colour) =>
        [new CirclePrim(new Pt(at.X - 15, at.Y), 4, Palette.Flash), new LinePrim(new Pt(at.X - 12, at.Y), new Pt(at.X + 18, at.Y), colour, 2.5f)];

    private static SoldierView Sample(Posture posture, HealthState health = HealthState.Ok) =>
        new(0, "", Role.Rifleman, 0, "", 0, Fixed.Zero, Fixed.Zero, posture, health, 0, Fixed.Zero, default);

    private static ContactView Enemy(ContactQuality quality) =>
        new(0, ContactKind.Infantry, quality, Fixed.Zero, Fixed.Zero, 0, 0, 0);
}
