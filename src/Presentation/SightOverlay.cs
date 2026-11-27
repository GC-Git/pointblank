using PointBlank.Sim;
using PointBlank.Sim.Sight;

namespace PointBlank.Presentation;

/// <summary>
/// The line-of-sight overlay (item 2.6): a dot on each sampled tile where a standing target could be seen by an able man of
/// a selected squad, asked of the sim (<see cref="SideView.CanSee"/>). The checks are costly, so a pass runs a slice per
/// frame and the last finished pass stays on screen. A new pass starts when the observers or the picture change; a change
/// of squads or picture also drops the old dots.
/// </summary>
internal sealed class SightOverlay
{
    private const int ChecksPerFrame = 600;
    private const float DotSpacing = 12;
    private const int DotSize = 3;
    private string _view = "";
    private string _observers = "";
    private List<(float X, float Y)> _shown = [];
    private List<(float X, float Y)> _building = [];
    private (int Man, float X, float Y)[] _men = [];
    private (float X, float Y)[] _samples = [];
    private int _next;
    private int _man;

    public bool Complete => _next >= _samples.Length;

    public void Draw(List<Prim> list, GameController c)
    {
        Update(c, ChecksPerFrame);
        foreach (var (x, y) in _shown)
        {
            var at = c.Camera.ToScreen(x, y);
            if (c.Layout.Map.Contains(at))
            {
                list.Add(new RectPrim(at.X - (DotSize / 2f), at.Y - (DotSize / 2f), DotSize, DotSize, Palette.Sight));
            }
        }
    }

    /// <summary>Runs up to <paramref name="budget"/> checks of the current pass, starting a new one if something changed.</summary>
    public void Update(GameController c, int budget)
    {
        var tile = c.Map.TileSize.F();
        var chosen = c.Selected.Select(id => c.Battle.View.Squad(id)).OfType<SquadView>().ToArray();

        // One observer per tile and posture: men sharing both see all but the same ground.
        var men = chosen.SelectMany(q => q.Soldiers)
            .Where(s => s.Health is Sim.Org.HealthState.Ok or Sim.Org.HealthState.Wounded)
            .GroupBy(s => ((int)(s.X.F() / tile), (int)(s.Y.F() / tile), s.Posture))
            .Select(g => (Key: g.Key, Man: g.First()))
            .ToArray();
        var view = $"{string.Join(",", c.Selected)};{c.Camera.CentreX};{c.Camera.CentreY};{c.Camera.Scale}";
        var observers = string.Join(";", men.Select(m => m.Key.ToString()));
        var moved = view != _view;
        if (moved || (Complete && observers != _observers))
        {
            if (moved)
            {
                _shown = [];
            }

            (_view, _observers) = (view, observers);
            _men = men.Select(m => (m.Man.Id, m.Man.X.F(), m.Man.Y.F())).ToArray();
            _samples = men.Length == 0 ? [] : Samples(c, tile);
            (_building, _next, _man) = ([], 0, 0);
            if (men.Length == 0)
            {
                _shown = [];
            }
        }

        while (!Complete && budget > 0)
        {
            var (x, y) = _samples[_next];
            var (man, _, _) = _men[_man];
            budget--;
            var seen = c.Battle.View.CanSee(man, Num.ToFixed(x), Num.ToFixed(y), Posture.Standing);
            if (seen)
            {
                _building.Add((x, y));
            }

            if (seen || ++_man == _men.Length)
            {
                (_next, _man) = (_next + 1, 0);
                if (Complete)
                {
                    _shown = _building;
                }
            }
        }
    }

    /// <summary>The centres of every n-th tile in the picture, n chosen so the dots stand well apart.</summary>
    private static (float X, float Y)[] Samples(GameController c, float tile)
    {
        var step = Math.Max(1, (int)MathF.Ceiling(DotSpacing / (tile * c.Camera.Scale)));
        var area = c.Layout.Map;
        var (left, top) = c.Camera.ToWorld(new Pt(area.X, area.Y));
        var (right, bottom) = c.Camera.ToWorld(new Pt(area.Right, area.Bottom));
        int First(float edge) => Math.Max(0, (int)(edge / tile) / step * step);
        var list = new List<(float, float)>();
        for (var y = First(top); y < c.Map.Height && y * tile < bottom; y += step)
        {
            for (var x = First(left); x < c.Map.Width && x * tile < right; x += step)
            {
                list.Add(((x + 0.5f) * tile, (y + 0.5f) * tile));
            }
        }

        return list.ToArray();
    }
}
