using PointBlank.Sim;
using PointBlank.Sim.Map;

namespace PointBlank.Presentation;

/// <summary>The ground: tiles (equal neighbours in a row merged), buildings with their openings, hedges.</summary>
public static class TerrainLayer
{
    public static IReadOnlyList<Prim> Build(MapView map, Camera camera)
    {
        var area = camera.Area;
        var list = new List<Prim> { new RectPrim(area.X, area.Y, area.W, area.H, Palette.Backdrop) };
        var tile = map.TileSize.F();
        var px = tile * camera.Scale;

        // Only the tiles in view.
        var (left, top) = camera.ToWorld(new Pt(area.X, area.Y));
        var (right, bottom) = camera.ToWorld(new Pt(area.Right, area.Bottom));
        var x0 = Math.Max(0, (int)MathF.Floor(left / tile));
        var y0 = Math.Max(0, (int)MathF.Floor(top / tile));
        var x1 = Math.Min(map.Width - 1, (int)MathF.Floor(right / tile));
        var y1 = Math.Min(map.Height - 1, (int)MathF.Floor(bottom / tile));

        Rgb ColourAt(int x, int y) => Palette.Terrain(map.Tile(x, y).Terrain, map.Tile(x, y).ElevationDm);

        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1;)
            {
                var colour = ColourAt(x, y);
                var end = x + 1;
                while (end <= x1 && ColourAt(end, y) == colour)
                {
                    end++;
                }

                var at = camera.ToScreen(x * tile, y * tile);
                list.Add(new RectPrim(at.X, at.Y, (end - x) * px, px, colour));
                x = end;
            }
        }

        foreach (var building in map.Buildings)
        {
            foreach (var t in building.Tiles)
            {
                var at = camera.ToScreen(t.X * tile, t.Y * tile);
                list.Add(new RectPrim(at.X, at.Y, px, px, Palette.Building));
            }

            foreach (var o in building.Openings.Where(o => o.Floor == 0))
            {
                var (ax, ay, bx, by) = o.Side switch
                {
                    WallSide.North => (0.25f, 0f, 0.75f, 0f),
                    WallSide.South => (0.25f, 1f, 0.75f, 1f),
                    WallSide.West => (0f, 0.25f, 0f, 0.75f),
                    _ => (1f, 0.25f, 1f, 0.75f),
                };
                list.Add(new LinePrim(
                    camera.ToScreen((o.Tile.X + ax) * tile, (o.Tile.Y + ay) * tile),
                    camera.ToScreen((o.Tile.X + bx) * tile, (o.Tile.Y + by) * tile),
                    o.Kind == OpeningKind.Door ? Palette.Door : Palette.Window,
                    MathF.Max(1, camera.Scale * 0.3f)));
            }
        }

        var hedge = MathF.Max(3, camera.Scale * 0.8f);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var t = map.Tile(x, y);
                if (t.HedgeEast)
                {
                    var at = camera.ToScreen((x + 1) * tile, y * tile);
                    list.Add(new RectPrim(at.X - (hedge / 2), at.Y, hedge, px, Palette.Hedge));
                }

                if (t.HedgeSouth)
                {
                    var at = camera.ToScreen(x * tile, (y + 1) * tile);
                    list.Add(new RectPrim(at.X, at.Y - (hedge / 2), px, hedge, Palette.Hedge));
                }
            }
        }

        return list;
    }
}
