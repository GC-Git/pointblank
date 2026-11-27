using PointBlank.Sim;
using PointBlank.Sim.Sight;

namespace PointBlank.Presentation;

/// <summary>
/// The line-of-sight overlay (item 2.6): a dot on each sampled tile where a standing target could be seen by an able man of
/// a selected squad, asked of the sim (<see cref="SideView.CanSee"/>). The checks are costly, so each frame does a slice of
/// them, and every answer is kept by tile (item 2.5a): panning and zooming show what is known at once and ask only about the
/// ground not yet checked. When the observers change, a new pass rechecks the picture while the old dots stay; a change of
/// selection drops them.
/// </summary>
internal sealed class SightOverlay
{
    private const float DotSpacing = 12;
    private const int DotSize = 3;
    private readonly Dictionary<(int X, int Y), bool> _known = [];
    private readonly HashSet<(int X, int Y)> _fresh = []; // answered for the current observers
    private string _selection = "";
    private string _view = "";
    private string _observers = "";
    private int[] _men = [];
    private (int X, int Y)[] _samples = []; // the sampled tiles in the picture
    private (int X, int Y)[] _queue = []; // those still to ask about, never-seen ground first
    private int _step = 1;
    private int _next;
    private int _man;

    public bool Complete => _next >= _queue.Length;

    /// <summary>Line-of-sight checks asked in the last <see cref="Update"/>.</summary>
    public int ChecksLastUpdate { get; private set; }

    public void Draw(List<Prim> list, GameController c)
    {
        Update(c, GameController.SightChecksPerFrame);
        var tile = c.Map.TileSize.F();
        var covered = new HashSet<(int, int)>();
        void Dot((int X, int Y) t)
        {
            var at = c.Camera.ToScreen((t.X + 0.5f) * tile, (t.Y + 0.5f) * tile);
            if (c.Layout.Map.Contains(at))
            {
                list.Add(new RectPrim(at.X - (DotSize / 2f), at.Y - (DotSize / 2f), DotSize, DotSize, Palette.Sight));
            }
        }

        foreach (var t in _samples)
        {
            if (_known.TryGetValue(t, out var seen))
            {
                covered.Add((t.X / _step, t.Y / _step));
                if (seen)
                {
                    Dot(t);
                }
            }
        }

        if (Complete)
        {
            return;
        }

        // Until the pass is done, ground it has not reached shows what was seen there at another zoom, one dot a cell.
        foreach (var (t, seen) in _known)
        {
            if (seen && covered.Add((t.X / _step, t.Y / _step)))
            {
                Dot(t);
            }
        }
    }

    /// <summary>Runs up to <paramref name="budget"/> checks, starting a new pass if something changed.</summary>
    public void Update(GameController c, int budget)
    {
        ChecksLastUpdate = 0;
        var tile = c.Map.TileSize.F();
        var chosen = c.Selected.Select(id => c.Battle.View.Squad(id)).OfType<SquadView>().ToArray();

        // One observer per tile and posture: men sharing both see all but the same ground.
        var men = chosen.SelectMany(q => q.Soldiers)
            .Where(s => s.Health is Sim.Org.HealthState.Ok or Sim.Org.HealthState.Wounded)
            .GroupBy(s => ((int)(s.X.F() / tile), (int)(s.Y.F() / tile), s.Posture))
            .Select(g => (Key: g.Key, Man: g.First()))
            .ToArray();
        var selection = string.Join(",", c.Selected);
        var view = $"{c.Camera.CentreX};{c.Camera.CentreY};{c.Camera.Scale}";
        var observers = string.Join(";", men.Select(m => m.Key.ToString()));
        if (selection != _selection || men.Length == 0)
        {
            _known.Clear();
            _fresh.Clear();
            (_selection, _observers, _view) = (selection, "", "");
        }

        var moved = view != _view;
        var changed = observers != _observers;
        if (moved || (changed && Complete))
        {
            if (moved)
            {
                _samples = men.Length == 0 ? [] : Samples(c, tile);
            }

            if (changed)
            {
                _fresh.Clear();
                _observers = observers;
                _men = men.Select(m => m.Man.Id).ToArray();
            }

            _view = view;
            _queue = _samples.Where(t => !_fresh.Contains(t)).OrderBy(_known.ContainsKey).ToArray();
            (_next, _man) = (0, 0);
        }

        while (!Complete && budget > 0)
        {
            var t = _queue[_next];
            budget--;
            ChecksLastUpdate++;
            var seen = c.Battle.View.CanSee(_men[_man], Num.ToFixed((t.X + 0.5f) * tile), Num.ToFixed((t.Y + 0.5f) * tile), Posture.Standing);
            if (seen || ++_man == _men.Length)
            {
                _known[t] = seen;
                _fresh.Add(t);
                (_next, _man) = (_next + 1, 0);
            }
        }
    }

    /// <summary>Every n-th tile in the picture, n chosen so the dots stand well apart.</summary>
    private (int X, int Y)[] Samples(GameController c, float tile)
    {
        var step = _step = Math.Max(1, (int)MathF.Ceiling(DotSpacing / (tile * c.Camera.Scale)));
        var area = c.Layout.Map;
        var (left, top) = c.Camera.ToWorld(new Pt(area.X, area.Y));
        var (right, bottom) = c.Camera.ToWorld(new Pt(area.Right, area.Bottom));
        int First(float edge) => Math.Max(0, (int)(edge / tile) / step * step);
        var list = new List<(int, int)>();
        for (var y = First(top); y < c.Map.Height && y * tile < bottom; y += step)
        {
            for (var x = First(left); x < c.Map.Width && x * tile < right; x += step)
            {
                list.Add((x, y));
            }
        }

        return list.ToArray();
    }
}
