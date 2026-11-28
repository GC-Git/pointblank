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

        // Tree crowns mark woods, off the middle of each tile and the same every time.
        if (px >= 8)
        {
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (map.Tile(x, y).Terrain != TerrainType.LightWoods)
                    {
                        continue;
                    }

                    var shift = ((x * 7) + (y * 13)) % 5 / 25f;
                    list.Add(new CirclePrim(camera.ToScreen((x + 0.22f + shift) * tile, (y + 0.22f) * tile), 0.2f * px, Palette.TreeCrown));
                    list.Add(new CirclePrim(camera.ToScreen((x + 0.76f) * tile, (y + 0.78f - shift) * tile), 0.16f * px, Palette.TreeCrown));
                }
            }
        }

        foreach (var building in map.Buildings)
        {
            foreach (var t in building.Tiles)
            {
                var at = camera.ToScreen(t.X * tile, t.Y * tile);
                list.Add(new RectPrim(at.X, at.Y, px, px, Palette.Building));
            }

            // The outer walls.
            var inside = building.Tiles.ToHashSet();
            foreach (var t in building.Tiles)
            {
                foreach (var (nx, ny, ax, ay, bx, by) in new[] { (0, -1, 0, 0, 1, 0), (0, 1, 0, 1, 1, 1), (-1, 0, 0, 0, 0, 1), (1, 0, 1, 0, 1, 1) })
                {
                    if (!inside.Contains(new TilePos(t.X + nx, t.Y + ny)))
                    {
                        list.Add(new LinePrim(
                            camera.ToScreen((t.X + ax) * tile, (t.Y + ay) * tile),
                            camera.ToScreen((t.X + bx) * tile, (t.Y + by) * tile), Palette.Wall, 2));
                    }
                }
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
                    list.Add(new CirclePrim(new Pt(at.X, at.Y + (px * 0.25f)), hedge * 0.45f, Palette.HedgeLeaf));
                }

                if (t.HedgeSouth)
                {
                    var at = camera.ToScreen(x * tile, (y + 1) * tile);
                    list.Add(new RectPrim(at.X, at.Y - (hedge / 2), px, hedge, Palette.Hedge));
                    list.Add(new CirclePrim(new Pt(at.X + (px * 0.25f), at.Y), hedge * 0.45f, Palette.HedgeLeaf));
                }
            }
        }

        return list;
    }
}
