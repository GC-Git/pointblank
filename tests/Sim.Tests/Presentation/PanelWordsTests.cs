using System.Text.RegularExpressions;
using PointBlank.Presentation;
using PointBlank.Sim.Fire;
using PointBlank.Sim.Orders;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>The panel shows a man's real status, and words a player can read instead of code names.</summary>
public class PanelWordsTests
{
    private static GameController Start() => new(PlayerBattle.Create("fix-assault", 1));

    private static Squad Truth(GameController c) => c.Battle.World.GetSystem<OrgSystem>().Squads[0];

    /// <summary>The texts of one column of the panel's man rows, top to bottom (the header is not part of it).</summary>
    private static string[] Column(GameController c, float offset)
    {
        var texts = c.Frame().OfType<TextPrim>().ToArray();
        var header = texts.Single(t => t.Text.StartsWith("Man ", StringComparison.Ordinal)).At.Y;
        return texts.Where(t => t.At.X == c.Layout.Panel.X + 10 + offset && t.At.Y > header)
            .OrderBy(t => t.At.Y).Take(Truth(c).Soldiers.Count).Select(t => t.Text).ToArray();
    }

    [Fact]
    public void Dead_and_incapacitated_men_show_their_real_status_not_a_posture()
    {
        var c = Start();
        c.Select([0]);
        var men = Truth(c).Soldiers;
        foreach (var s in men)
        {
            s.Posture = Posture.Standing;
        }

        men[0].Health = HealthState.Dead;
        men[1].Health = HealthState.Incapacitated;
        men[2].Health = HealthState.Wounded;

        Assert.Equal(["Dead", "Down", "Wounded", "OK"], Column(c, 100).Take(4));
        Assert.Equal(["-", "-", "Standing", "Standing"], Column(c, 175).Take(4));
    }

    [Fact]
    public void Every_state_has_plain_words()
    {
        var all = new[] { typeof(Role), typeof(HealthState), typeof(MoraleState), typeof(Posture) }
            .Concat(typeof(PlayerBattle).Assembly.GetExportedTypes().Where(t => t.IsEnum
                && t.Name is "MoraleReason" or "SuppressionState" or "AmmoState" or "OrderKind" or "OrderNote" or "OrderStatus"
                    or "DelayReason" or "RoeMode" or "MissionStatus"))
            .SelectMany(t => Enum.GetValues(t).Cast<Enum>())
            .Concat(Enum.GetValues<OrderMode>().Cast<Enum>())
            .ToArray();

        Assert.True(all.Length > 60);
        Assert.All(all, v => Assert.Matches("^(OK|MG|BAR|[A-Z][a-z]*)([ -]([a-z]+|MG|BAR|HQ))*$", Words.Of(v)));
        Assert.Equal("MG gunner", Words.Of(Role.MgGunner));
        Assert.Equal("Fire at will", Words.Of(RoeMode.FireAtWill));
        Assert.Equal("Out of ammo", Words.Of(AmmoState.OutOfAmmo));
        Assert.Equal("No leader, out of contact", Words.Of(DelayReason.NoLeader | DelayReason.OutOfContact));
    }

    [Fact]
    public void No_frame_shows_a_code_name()
    {
        var codeName = new Regex(@"\b(?:[A-Z][a-z]+){2,}\b");
        var c = Start();
        c.Select([0]);
        var frames = new List<IReadOnlyList<Prim>>();
        c.Press(Command.MoveFast);
        c.Press(Command.CycleRoe);
        c.Step();
        frames.Add(c.Frame());
        c.RightClick(c.Camera.ToScreen(200, 100));
        c.Step();
        frames.Add(c.Frame());
        Truth(c).SetMorale(MoraleState.Rallying);
        frames.Add(c.Frame());

        var texts = frames.SelectMany(f => f.OfType<TextPrim>()).Select(t => t.Text).ToArray();
        Assert.Contains(texts, t => t.Contains("right-click: Move fast", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("Squad leader", StringComparison.Ordinal));
        Assert.Empty(texts.Where(t => codeName.IsMatch(t)));
    }
}
