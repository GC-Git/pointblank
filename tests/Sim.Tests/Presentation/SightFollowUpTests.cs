using System.Diagnostics;
using PointBlank.Presentation;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>Item 2.5a: the sight overlay keeps its dots while the picture moves, and does little work per frame.</summary>
[Collection(TimingCollection.Name)]
public class SightFollowUpTests(ITestOutputHelper output)
{
    private static GameController Start()
    {
        var c = new GameController(PlayerBattle.Create("fix-assault", 1, true));
        c.Select([0, 2]);
        c.Toggle(Overlay.Sight);
        return c;
    }

    private static List<(float X, float Y)> Dots(GameController c) => c.Frame().OfType<RectPrim>()
        .Where(r => r.Colour == Palette.Sight && r.Y >= c.Layout.Map.Y)
        .Select(r => c.Camera.ToWorld(new Pt(r.X + (r.W / 2), r.Y + (r.H / 2)))).ToList();

    [Fact]
    public void The_dots_stay_while_the_picture_is_panned_and_zoomed()
    {
        var c = Start();
        c.FinishOverlays();
        var full = Dots(c).Count;
        Assert.True(full > 100, $"{full} dots");

        for (var frame = 0; frame < 20; frame++)
        {
            c.Camera.Pan(6, 0);
            var shown = Dots(c).Count;
            Assert.True(shown >= full / 2, $"pan frame {frame}: {shown} of {full} dots");
        }

        for (var frame = 0; frame < 6; frame++)
        {
            c.Camera.Zoom(c.Layout.Map.Middle, 1.2f);
            var shown = Dots(c).Count;
            Assert.True(shown >= 20, $"zoom frame {frame}: {shown} dots");
        }
    }

    [Fact]
    public void After_a_pan_the_finished_pass_shows_the_same_ground_as_a_fresh_one()
    {
        var c = Start();
        c.FinishOverlays();
        c.Camera.Pan(120, 40);
        c.Frame();
        c.FinishOverlays();
        var panned = Dots(c).Order().ToList();

        var fresh = Start();
        fresh.Camera.Set(c.Camera.CentreX, c.Camera.CentreY, c.Camera.Scale);
        fresh.FinishOverlays();
        Assert.Equal(Dots(fresh).Order(), panned);
    }

    [Fact]
    public void A_frame_does_a_bounded_slice_of_the_checks_and_none_twice()
    {
        var c = Start();
        c.Frame();
        Assert.InRange(c.SightChecksLastFrame, 1, GameController.SightChecksPerFrame);
        c.FinishOverlays();
        c.Frame();
        Assert.Equal(0, c.SightChecksLastFrame);

        // A small pan asks only about the ground that came into the picture.
        c.FinishOverlays();
        c.Camera.Pan(1, 0);
        c.Frame();
        Assert.True(c.SightChecksLastFrame <= GameController.SightChecksPerFrame);
    }

    /// <summary>Not a pass mark (machines differ): the figures are written to the test output for the record.</summary>
    [Fact]
    public void Measure_the_cost_of_a_frame()
    {
        foreach (var (name, pan) in new[] { ("still", 0f), ("panning", 6f) })
        {
            var c = Start();
            c.Frame();
            var times = new List<double>();
            var dots = 0;
            for (var frame = 0; frame < 120; frame++)
            {
                if (frame % 6 == 0)
                {
                    c.Step();
                }

                c.Camera.Pan(frame < 60 ? pan : -pan, 0);
                var watch = Stopwatch.StartNew();
                var shown = Dots(c).Count;
                times.Add(watch.Elapsed.TotalMilliseconds);
                dots += shown;
            }

            times.Sort();
            output.WriteLine($"{name}: median {times[60]:0.0} ms, p95 {times[114]:0.0} ms, max {times[^1]:0.0} ms, mean dots {dots / 120}");
        }
    }
}
