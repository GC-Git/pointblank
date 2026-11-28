using PointBlank.Sim;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Presentation;

/// <summary>
/// The placeholder figures (item 2.13), seen from above and drawn from shapes. With a known direction: standing is wide
/// shoulders, a head and two arms on the weapon; kneeling a wedge with one leg out behind; prone a long thin body with
/// its legs apart; the weapon line points the way the man is working. When the view has nothing to go by, each posture
/// has a shape that points nowhere (a disc, a square, a cross-shaped sprawl) and no weapon, so no facing is invented.
/// Down: a lying body.
/// </summary>
public static class Figures
{
    /// <summary>One of the side's own men. <paramref name="facing"/> is a screen direction (any length), or null.</summary>
    public static IReadOnlyList<Prim> Man(SoldierView man, Pt at, float r, Pt? facing)
    {
        if (man.Health is not (HealthState.Ok or HealthState.Wounded))
        {
            return Fallen(at, r, Palette.Down, man.Health == HealthState.Dead);
        }

        if (facing is not { } way || (way.X == 0 && way.Y == 0))
        {
            return Unturned(man, at, r);
        }

        var f = Unit(way);
        Pt P(float along, float across) => new(at.X + (r * ((f.X * along) - (f.Y * across))), at.Y + (r * ((f.Y * along) + (f.X * across))));
        var list = new List<Prim>();
        var gun = man.Role == Role.MgGunner;
        void Weapon(Pt from, float reach) => list.Add(new LinePrim(from, P(reach, 0.3f), Palette.Weapon, gun ? 4 : 2.5f));

        switch (man.Posture)
        {
            case Posture.Standing:
                var shoulders = MathF.Max(3, 0.45f * r);
                list.Add(new LinePrim(P(-0.2f, -1.15f), P(-0.2f, 1.15f), Palette.Shadow, shoulders + 2));
                list.Add(new LinePrim(P(-0.2f, -1.05f), P(-0.2f, 1.05f), Palette.Own, shoulders));
                var arm = MathF.Max(2, 0.25f * r);
                list.Add(new LinePrim(P(-0.2f, -0.95f), P(0.85f, 0.25f), Palette.Own, arm));
                list.Add(new LinePrim(P(-0.2f, 0.95f), P(0.55f, 0.35f), Palette.Own, arm));
                list.Add(new CirclePrim(P(-0.05f, 0), (0.5f * r) + 1, Palette.Shadow));
                list.Add(new CirclePrim(P(-0.05f, 0), 0.5f * r, Palette.Own));
                Weapon(P(0.35f, 0.3f), 1.9f);
                list.Add(new CirclePrim(P(0.12f, 0), 0.22f * r, Palette.Helmet));
                break;
            case Posture.Kneeling:
                list.Add(new LinePrim(P(-0.5f, -0.4f), P(-1.2f, -0.65f), Palette.Own, MathF.Max(2, 0.4f * r)));
                Poly(list, Palette.Own, P(1.0f, 0), P(-0.75f, 1.0f), P(-0.75f, -1.0f));
                Weapon(P(0.2f, 0.3f), 1.8f);
                list.Add(new CirclePrim(P(0.38f, 0), 0.26f * r, Palette.Helmet));
                break;
            default:
                var leg = MathF.Max(1.5f, 0.28f * r);
                list.Add(new LinePrim(P(-0.4f, -0.15f), P(-1.05f, -0.5f), Palette.Own, leg));
                list.Add(new LinePrim(P(-0.4f, 0.15f), P(-1.05f, 0.5f), Palette.Own, leg));
                Poly(list, Palette.Own, P(0.6f, -0.35f), P(0.6f, 0.35f), P(-0.5f, 0.28f), P(-0.5f, -0.28f));
                Weapon(P(0.6f, 0.28f), 1.45f);
                list.Add(new CirclePrim(P(0.68f, 0), 0.34f * r, Palette.Own));
                list.Add(new CirclePrim(P(0.68f, 0), 0.18f * r, Palette.Helmet));
                break;
        }

        return Marked(list, man, at, r);
    }

    /// <summary>The figure of a man whose direction is not known: the same turned any quarter.</summary>
    private static IReadOnlyList<Prim> Unturned(SoldierView man, Pt at, float r)
    {
        Pt P(float x, float y) => new(at.X + (x * r), at.Y + (y * r));
        var list = new List<Prim>();
        switch (man.Posture)
        {
            case Posture.Standing:
                list.Add(new CirclePrim(at, (0.75f * r) + 1.5f, Palette.Shadow));
                list.Add(new CirclePrim(at, 0.75f * r, Palette.Own));
                break;
            case Posture.Kneeling:
                Poly(list, Palette.Own, P(-0.75f, -0.75f), P(0.75f, -0.75f), P(0.75f, 0.75f), P(-0.75f, 0.75f));
                break;
            default:
                var limb = MathF.Max(2.5f, 0.32f * r);
                list.Add(new LinePrim(P(-0.95f, -0.95f), P(0.95f, 0.95f), Palette.Own, limb));
                list.Add(new LinePrim(P(-0.95f, 0.95f), P(0.95f, -0.95f), Palette.Own, limb));
                list.Add(new CirclePrim(at, 0.35f * r, Palette.Own));
                break;
        }

        list.Add(new CirclePrim(at, 0.25f * r, Palette.Helmet));
        return Marked(list, man, at, r);
    }

    private static IReadOnlyList<Prim> Marked(List<Prim> list, SoldierView man, Pt at, float r)
    {
        if (man.Health == HealthState.Wounded)
        {
            Cross(list, new Pt(at.X + (0.85f * r), at.Y - (0.85f * r)), r);
        }

        return list;
    }

    /// <summary>
    /// A body on the ground. For the side's own: grey, with a red cross while he may yet be saved, or a black cross over
    /// the dead (<paramref name="dead"/>). An enemy seen to fall has neither (null): the side cannot tell which he is.
    /// </summary>
    public static IReadOnlyList<Prim> Fallen(Pt at, float r, Rgb colour, bool? dead)
    {
        Pt P(float x, float y) => new(at.X + (x * r), at.Y + (y * r));
        var list = new List<Prim>
        {
            new LinePrim(P(-0.9f, 0.35f), P(0.5f, -0.2f), colour, MathF.Max(3, 0.65f * r)),
            new LinePrim(P(-0.2f, -0.55f), P(0.15f, 0.6f), colour, MathF.Max(1.5f, 0.25f * r)),
            new CirclePrim(P(0.75f, -0.3f), 0.38f * r, colour),
        };
        if (dead == true)
        {
            list.Add(new LinePrim(P(-0.7f, -0.7f), P(0.7f, 0.7f), Palette.Shadow, 2));
            list.Add(new LinePrim(P(-0.7f, 0.7f), P(0.7f, -0.7f), Palette.Shadow, 2));
        }
        else if (dead == false)
        {
            Cross(list, P(0.85f, -0.85f), r);
        }

        return list;
    }

    /// <summary>
    /// A spotted enemy at his last known place, a diamond (a square for a machine-gun crewman): solid in a ring while
    /// tracked; hollow with its age when stale; small and hollow with a question mark when lost.
    /// </summary>
    public static IReadOnlyList<Prim> Contact(ContactView contact, Pt at, float r, long tick)
    {
        var colour = Palette.Contact(contact.Quality);
        var s = contact.Quality == ContactQuality.Lost ? 0.85f * r : 1.1f * r;
        var machineGun = contact.Kind == ContactKind.MachineGun;
        Pt[] shape = machineGun
            ? [new(at.X - (0.8f * s), at.Y - (0.8f * s)), new(at.X + (0.8f * s), at.Y - (0.8f * s)),
                new(at.X + (0.8f * s), at.Y + (0.8f * s)), new(at.X - (0.8f * s), at.Y + (0.8f * s))]
            : [new(at.X, at.Y - s), new(at.X + s, at.Y), new(at.X, at.Y + s), new(at.X - s, at.Y)];
        var list = new List<Prim>();
        if (contact.Quality == ContactQuality.Tracked)
        {
            list.Add(new PolyPrim(shape, colour));
            Outline(list, shape, Palette.Shadow, 1.5f);
            list.Add(new CirclePrim(at, 1.5f * r, colour, false, 1.5f));
        }
        else
        {
            Outline(list, shape, colour, 2);
        }

        if (machineGun)
        {
            list.Add(new TextPrim(new Pt(at.X - r, at.Y - (1.6f * r)), "MG", colour, 11));
        }

        if (contact.Quality == ContactQuality.Stale)
        {
            var age = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(tick - contact.LastSeenTick) / 10f:0}s ago");
            list.Add(new TextPrim(new Pt(at.X + r + 3, at.Y + r + 3), age, colour, 10));
        }
        else if (contact.Quality == ContactQuality.Lost)
        {
            list.Add(new TextPrim(new Pt(at.X + r + 1, at.Y + r), "?", colour, 13));
        }

        return list;
    }

    private static void Poly(List<Prim> list, Rgb colour, params Pt[] points)
    {
        list.Add(new PolyPrim(points, colour));
        Outline(list, points, Palette.Shadow, 1);
    }

    private static void Outline(List<Prim> list, Pt[] points, Rgb colour, float width)
    {
        for (var i = 0; i < points.Length; i++)
        {
            list.Add(new LinePrim(points[i], points[(i + 1) % points.Length], colour, width));
        }
    }

    private static void Cross(List<Prim> list, Pt at, float r)
    {
        var s = MathF.Max(4, 0.55f * r);
        list.Add(new RectPrim(at.X - s, at.Y - s, 2 * s, 2 * s, Palette.Wounded));
        list.Add(new LinePrim(new Pt(at.X - s + 1, at.Y), new Pt(at.X + s - 1, at.Y), Palette.RedCross, 2));
        list.Add(new LinePrim(new Pt(at.X, at.Y - s + 1), new Pt(at.X, at.Y + s - 1), Palette.RedCross, 2));
    }

    private static Pt Unit(Pt v)
    {
        var length = MathF.Sqrt((v.X * v.X) + (v.Y * v.Y));
        return new Pt(v.X / length, v.Y / length);
    }
}
