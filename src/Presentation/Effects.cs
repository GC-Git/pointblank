using PointBlank.Sim;
using PointBlank.Sim.Org;

namespace PointBlank.Presentation;

/// <summary>
/// What the view goes on showing after the tick it happened (item 2.13): tracers and flashes, shell bursts and their
/// craters, enemies seen to fall, and the way each own man last worked. It is filled only from the player's
/// <see cref="SideView"/>, ages by battle ticks (so a paused picture stays), and is bounded: one tracer per shooter, a
/// capped number of craters, one entry per man. All its times are display choices, not battle rules.
/// </summary>
public sealed class Effects
{
    /// <summary>How long a burst's flash and smoke show, in ticks.</summary>
    public const int BurstTicks = 30;

    /// <summary>The part of <see cref="BurstTicks"/> with fire in it.</summary>
    public const int FireballTicks = 8;

    public const int MaxCraters = 150;

    /// <summary>How long a man goes on facing the way he last fired, in ticks.</summary>
    public const int AimTicks = 30;

    private readonly Dictionary<(int Firer, long X, long Y), SeenShotView> _shots = [];
    private readonly List<(long Tick, Fixed X, Fixed Y)> _bursts = [];
    private readonly Queue<(Fixed X, Fixed Y)> _craters = new();
    private readonly SortedDictionary<int, SeenCasualtyView> _fallen = [];
    private readonly Dictionary<int, (long Tick, float X, float Y)> _aim = [];
    private readonly Dictionary<int, (float X, float Y)> _heading = [];
    private readonly Dictionary<int, (Fixed X, Fixed Y)> _places = [];

    /// <summary>
    /// How long a tracer shows. Longer in ticks at faster speeds, so it stays long enough to notice, but less than the
    /// speed itself, so fast play does not fill the screen.
    /// </summary>
    public static int TracerTicks(int speed) => 3 + (2 * speed);

    /// <summary>The latest shot of each shooter still showing, in a fixed order.</summary>
    public IEnumerable<SeenShotView> Shots(long tick, int speed) =>
        _shots.OrderBy(s => s.Key).Select(s => s.Value).Where(s => tick - s.Tick <= TracerTicks(speed));

    public IReadOnlyList<(long Tick, Fixed X, Fixed Y)> Bursts => _bursts;

    public IReadOnlyCollection<(Fixed X, Fixed Y)> Craters => _craters;

    public IReadOnlyDictionary<int, SeenCasualtyView> Fallen => _fallen;

    /// <summary>
    /// Which way an own man is working, as a direction on the ground, or null if nothing says: the way he fired lately,
    /// else the way he is walking, else the point his squad was ordered to fire at, else the way he last faced.
    /// </summary>
    public (float X, float Y)? Facing(SoldierView man, long tick, (float X, float Y)? target)
    {
        if (_aim.TryGetValue(man.Id, out var a) && tick - a.Tick <= AimTicks)
        {
            return (a.X, a.Y);
        }

        if (!man.Moving && target is { } t)
        {
            return (t.X - man.X.F(), t.Y - man.Y.F());
        }

        return _heading.TryGetValue(man.Id, out var h) ? h : null;
    }

    /// <summary>Takes in the tick just run.</summary>
    public void Remember(SideView view)
    {
        var tick = view.Tick;
        foreach (var q in view.Squads)
        {
            foreach (var s in q.Soldiers)
            {
                if (_places.TryGetValue(s.Id, out var was) && (was.X != s.X || was.Y != s.Y))
                {
                    _heading[s.Id] = (s.X.F() - was.X.F(), s.Y.F() - was.Y.F());
                    // A man who walks off is no longer facing his last shot.
                    if (_aim.TryGetValue(s.Id, out var a) && tick - a.Tick > 5)
                    {
                        _aim.Remove(s.Id);
                    }
                }

                _places[s.Id] = (s.X, s.Y);
            }
        }

        foreach (var shot in view.SeenShots)
        {
            // An enemy has no id here: his rounds are told apart by where they come from.
            _shots[shot.Own ? (shot.FirerId, 0, 0) : (-1, shot.X.Raw, shot.Y.Raw)] = shot;
            if (shot.Own)
            {
                _aim[shot.FirerId] = (shot.Tick, shot.DirX.F(), shot.DirY.F());
                _heading[shot.FirerId] = (shot.DirX.F(), shot.DirY.F());
            }
        }

        foreach (var key in _shots.Where(s => tick - s.Value.Tick > TracerTicks(4)).Select(s => s.Key).ToArray())
        {
            _shots.Remove(key);
        }

        _bursts.RemoveAll(b => tick - b.Tick > BurstTicks);
        foreach (var b in view.SeenBursts)
        {
            _bursts.Add((b.Tick, b.X, b.Y));
            _craters.Enqueue((b.X, b.Y));
            if (_craters.Count > MaxCraters)
            {
                _craters.Dequeue();
            }
        }

        foreach (var f in view.SeenCasualties)
        {
            _fallen[f.EnemyId] = f;
        }
    }
}
