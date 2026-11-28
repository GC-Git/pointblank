using PointBlank.Sim.Fire;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Spotting;

namespace PointBlank.Sim;

/// <summary>
/// A round fired on the last tick that the side saw (item 2.13). <see cref="Own"/>: one of its own men fired it, from
/// his true place, and <see cref="FirerId"/> is his id. An enemy round is listed only while the side is tracking the
/// shooter; it starts where the side knows him to be and <see cref="FirerId"/> is -1.
/// <see cref="DirX"/>, <see cref="DirY"/> is the unit direction of flight. <see cref="HasEnd"/>: where it stopped was
/// seen; otherwise the end is (0, 0) and nothing is told of how far it flew.
/// </summary>
public readonly record struct SeenShotView(
    long Tick, int FirerId, bool Own, Fixed X, Fixed Y, Fixed DirX, Fixed DirY, bool HasEnd, Fixed EndX, Fixed EndY);

/// <summary>An enemy the side saw down (item 2.13): where he lies. Whether he is dead or only hurt cannot be told.</summary>
public readonly record struct SeenCasualtyView(int EnemyId, Fixed X, Fixed Y);

/// <summary>
/// The events of the last tick as the side could observe them (item 2.13). All read only: nothing here changes the
/// battle, and nothing is stored in the sim. The caller keeps what it wants to go on showing.
/// "In sight" means by the line of sight and maximum range spotting uses, asked of the side's able men nearest the point
/// (for rounds and bodies at most <see cref="Witnesses"/>; for the rarer bursts the two nearest of every squad, so a far
/// observer counts). The limits bound the cost and err on the side of showing less.
/// </summary>
public sealed partial class SideView
{
    private const int Witnesses = 8;

    /// <summary>
    /// Own men's rounds, and the rounds of enemies the side is tracking. Other enemy fire is known only as
    /// <see cref="IncomingFire"/>. The stopping place is told only if in sight (own round: in the shooter's sight) and
    /// not on a man the side does not know; a round that hit one of the side's own men is always told to the side.
    /// </summary>
    public IReadOnlyList<SeenShotView> SeenShots
    {
        get
        {
            if (_fire == null || _spotting == null || _fire.Shots.Count == 0)
            {
                return [];
            }

            var men = new Dictionary<int, (bool Own, Soldier Man)>();
            foreach (var q in _org.Squads)
            {
                foreach (var s in q.Soldiers)
                {
                    men[s.Id] = (q.Side == Side, s);
                }
            }

            var own = OwnAble();
            var list = new List<SeenShotView>();
            foreach (var shot in _fire.Shots)
            {
                if (!men.TryGetValue(shot.FirerId, out var firer))
                {
                    continue;
                }

                var (x, y) = (shot.OriginX, shot.OriginY);
                if (!firer.Own)
                {
                    if (_spotting.ContactOf(Side, shot.FirerId) is not { Quality: ContactQuality.Tracked } c)
                    {
                        continue;
                    }

                    (x, y) = (c.X, c.Y);
                }

                var dx = shot.EndX - shot.OriginX;
                var dy = shot.EndY - shot.OriginY;
                var length = Fixed.Sqrt((dx * dx) + (dy * dy));
                if (length > Fixed.Zero)
                {
                    (dx, dy) = (dx / length, dy / length);
                }

                var hitOwn = shot.HitSoldierId >= 0 && men.TryGetValue(shot.HitSoldierId, out var h) && h.Own;
                var hitUnknown = shot.HitSoldierId >= 0 && !hitOwn
                    && _spotting.ContactOf(Side, shot.HitSoldierId) is not { Quality: ContactQuality.Tracked };
                var seen = !hitUnknown && (hitOwn
                    || (firer.Own
                        ? _spotting.Sees(firer.Man, shot.EndX, shot.EndY, Posture.Prone)
                        : InSight(own, shot.EndX, shot.EndY, Posture.Prone)));
                list.Add(new SeenShotView(
                    shot.Tick, firer.Own ? shot.FirerId : -1, firer.Own, x, y, dx, dy,
                    seen, seen ? shot.EndX : Fixed.Zero, seen ? shot.EndY : Fixed.Zero));
            }

            return list;
        }
    }

    /// <summary>
    /// The shell bursts of the last tick the side saw or felt: one of its able men is within the burst's suppression
    /// radius, or has its smoke and earth in sight (the battery's <c>plume_height</c> above the place, within spotting
    /// range). A burst heard from out of sight is not placed on the map.
    /// </summary>
    public IReadOnlyList<Artillery.ShellImpactView> SeenBursts
    {
        get
        {
            if (_artillery == null || _artillery.LastImpacts.Count == 0)
            {
                return [];
            }

            var own = OwnAble();
            return _artillery.LastImpacts
                .Where(b => own.Any(s => ((s.X - b.X) * (s.X - b.X)) + ((s.Y - b.Y) * (s.Y - b.Y)) <= b.Radius * b.Radius)
                    || _org.Squads.Where(q => q.Side == Side).Any(q => Nearest(q.Soldiers.Where(s => s.CanLead), b.X, b.Y).Take(2)
                        .Any(s => _spotting?.SeesRaised(s, b.X, b.Y, b.Plume) == true)))
                .Select(b => new Artillery.ShellImpactView(b.Tick, b.X, b.Y))
                .ToArray();
        }
    }

    /// <summary>
    /// Enemies who are down, whom the side is still tracking (so it saw him up a moment ago) and whose body is in sight
    /// now. A man who falls out of sight is never reported: the side keeps only its last sighting of him.
    /// </summary>
    public IReadOnlyList<SeenCasualtyView> SeenCasualties
    {
        get
        {
            if (_spotting == null)
            {
                return [];
            }

            Soldier[]? own = null;
            var list = new List<SeenCasualtyView>();
            foreach (var q in _org.Squads)
            {
                if (q.Side == Side)
                {
                    continue;
                }

                foreach (var s in q.Soldiers)
                {
                    if (!s.CanLead && _spotting.ContactOf(Side, s.Id) is { Quality: ContactQuality.Tracked }
                        && InSight(own ??= OwnAble(), s.X, s.Y, Posture.Prone))
                    {
                        list.Add(new SeenCasualtyView(s.Id, s.X, s.Y));
                    }
                }
            }

            return list;
        }
    }

    private Soldier[] OwnAble() => _org.Squads.Where(q => q.Side == Side).SelectMany(q => q.Soldiers).Where(s => s.CanLead).ToArray();

    private static IEnumerable<Soldier> Nearest(IEnumerable<Soldier> own, Fixed x, Fixed y) =>
        own.OrderBy(s => ((s.X - x) * (s.X - x)) + ((s.Y - y) * (s.Y - y))).ThenBy(s => s.Id);

    private bool InSight(Soldier[] own, Fixed x, Fixed y, Posture posture) =>
        Nearest(own, x, y).Take(Witnesses).Any(s => _spotting!.Sees(s, x, y, posture));
}
