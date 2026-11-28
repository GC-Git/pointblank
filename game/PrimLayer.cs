using System.Collections.Generic;
using Godot;
using PointBlank.Presentation;

namespace PointBlank.Game;

/// <summary>Paints a draw list. All decisions about what to draw are made in <c>src/Presentation</c>.</summary>
public partial class PrimLayer : Node2D
{
    private IReadOnlyList<Prim> _prims = [];

    public void Show(IReadOnlyList<Prim> prims)
    {
        _prims = prims;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = ThemeDB.FallbackFont;
        foreach (var prim in _prims)
        {
            var colour = Color.Color8(prim.Colour.R, prim.Colour.G, prim.Colour.B);
            switch (prim)
            {
                case RectPrim { Fill: true } r:
                    DrawRect(new Rect2(r.X, r.Y, r.W, r.H), colour);
                    break;
                case RectPrim r:
                    DrawRect(new Rect2(r.X, r.Y, r.W, r.H), colour, false, r.Width);
                    break;
                case CirclePrim { Fill: true } c:
                    DrawCircle(ToVector(c.Centre), c.Radius, colour);
                    break;
                case CirclePrim c:
                    DrawArc(ToVector(c.Centre), c.Radius, 0, Mathf.Tau, 24, colour, c.Width);
                    break;
                case PolyPrim g:
                    var points = new Vector2[g.Points.Count];
                    for (var i = 0; i < points.Length; i++)
                    {
                        points[i] = ToVector(g.Points[i]);
                    }

                    DrawColoredPolygon(points, colour);
                    break;
                case LinePrim l:
                    DrawLine(ToVector(l.A), ToVector(l.B), colour, l.Width);
                    break;
                case TextPrim t:
                    DrawString(font, ToVector(t.At), t.Text, HorizontalAlignment.Left, -1, t.Size, colour);
                    break;
            }
        }
    }

    private static Vector2 ToVector(Pt p) => new(p.X, p.Y);
}
