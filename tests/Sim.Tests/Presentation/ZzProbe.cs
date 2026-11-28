using PointBlank.Presentation;
using Xunit.Abstractions;

namespace PointBlank.Sim.Tests.Presentation;

public class ZzProbe(ITestOutputHelper o)
{
    [Fact]
    public void Probe()
    {
        foreach (var s in new[] { "fix-assault", "bombard-creep" })
        {
            var c = new GameController(PlayerBattle.Create(s, 1, true));
            var m = c.Battle.View.Map!;
            o.WriteLine($"{s} map {m.Width * m.TileSize.F()} x {m.Height * m.TileSize.F()}");
            var bursts = 0;
            for (var t = 0; t <= 900; t++)
            {
                if (t > 0) c.Step();
                bursts += c.Battle.View.SeenBursts.Count;
                if (t is 0 or 150 or 300 or 600 or 900)
                {
                    o.WriteLine($" t{t} bursts so far {bursts}");
                    foreach (var q in c.Battle.View.Squads)
                    {
                        var x = q.Soldiers;
                        var k = c.Battle.View.StatusOf(q.Id)?.Order;
                        o.WriteLine($"  #{q.Id} {q.OrgId} {x.Count} x {x.Min(a => a.X.F()):0}-{x.Max(a => a.X.F()):0} y {x.Min(a => a.Y.F()):0}-{x.Max(a => a.Y.F()):0} {k?.Kind} {k?.X.F():0},{k?.Y.F():0} {string.Join("", x.Select(a => a.Posture.ToString()[0]))}");
                    }

                    foreach (var k in c.Battle.View.KnownEnemies) o.WriteLine($"  contact {k.Quality} {k.X.F():0},{k.Y.F():0}");
                    foreach (var f in c.Battle.View.FireMissions) o.WriteLine($"  mission {f.Status} {f.X.F():0},{f.Y.F():0} rounds {f.RoundsLeft} eta {f.EtaTicks}");
                    foreach (var i in c.Battle.View.IncomingFire) o.WriteLine($"  incoming {i.SoldierId} {i.DirX.F():0.0},{i.DirY.F():0.0}");
                }
            }
        }
    }
}
