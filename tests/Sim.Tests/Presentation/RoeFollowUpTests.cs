using PointBlank.Presentation;
using PointBlank.Sim.Orders;

namespace PointBlank.Sim.Tests.Presentation;

/// <summary>Item 2.5a, owner decision: changing the rules of engagement keeps a pending order, and quick presses cycle on.</summary>
public class RoeFollowUpTests
{
    private static GameController Start()
    {
        var c = new GameController(PlayerBattle.Create("fix-assault", 1, false));
        c.Select([0]);
        return c;
    }

    private static void Run(GameController c, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            c.Step();
        }
    }

    private static void MoveNorth(GameController c)
    {
        var men = c.Battle.View.Squad(0)!.Value.Soldiers;
        var (x, y) = (men.Average(s => s.X.Raw / 4294967296f), men.Average(s => s.Y.Raw / 4294967296f));
        c.Mode = OrderMode.Move;
        c.RightClick(c.Camera.ToScreen(x, y - 10));
    }

    [Theory]
    [InlineData(0)] // both in the same tick (as while paused)
    [InlineData(1)] // the move is already pending in the chain of command
    public void A_rules_of_engagement_change_keeps_the_pending_move_and_both_arrive(int between)
    {
        var c = Start();
        MoveNorth(c);
        Run(c, between);
        c.Press(Command.CycleRoe);
        c.Step();

        var move = c.Battle.View.StatusOf(0)!;
        var roe = c.Battle.View.RoeStatusOf(0)!;
        Assert.Equal((OrderKind.Move, OrderStatus.Pending), (move.Order!.Kind, move.Status));
        Assert.Equal((OrderKind.SetRoe, OrderStatus.Pending), (roe.Order!.Kind, roe.Status));
        Assert.True(roe.TicksRemaining > 0, "the change goes down the chain of command with a delay");
        Assert.Equal(RoeMode.FireAtWill, c.Battle.View.RoeOf(0));

        var moved = false;
        for (var i = 0; i < 400; i++)
        {
            c.Step();
            moved |= c.Battle.View.Squad(0)!.Value.IsMoving;
        }

        Assert.True(moved, "the squad never moved");
        Assert.Equal(RoeMode.ReturnFireOnly, c.Battle.View.RoeOf(0));
        Assert.Equal(OrderKind.Move, c.Battle.View.StatusOf(0)!.Order!.Kind);
        Assert.Equal(OrderStatus.Done, c.Battle.View.RoeStatusOf(0)!.Status);
    }

    [Fact]
    public void A_move_given_after_the_change_does_not_cancel_it()
    {
        var c = Start();
        c.Press(Command.CycleRoe);
        c.Step();
        MoveNorth(c);
        Run(c, 400);

        Assert.Equal(RoeMode.ReturnFireOnly, c.Battle.View.RoeOf(0));
        Assert.Equal(OrderKind.Move, c.Battle.View.StatusOf(0)!.Order!.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Quick_presses_cycle_onward_instead_of_repeating(int between)
    {
        var c = Start();
        c.Press(Command.CycleRoe);
        Run(c, between);
        c.Press(Command.CycleRoe);
        c.Step();
        Assert.Equal((int)RoeMode.HoldFire, c.Battle.View.RoeStatusOf(0)!.Order!.TargetId);

        Run(c, 400);
        Assert.Equal(RoeMode.HoldFire, c.Battle.View.RoeOf(0));

        c.Press(Command.CycleRoe);
        Run(c, 400);
        Assert.Equal(RoeMode.FireAtWill, c.Battle.View.RoeOf(0));
    }

    [Fact]
    public void The_pending_change_is_hashed_and_the_other_side_cannot_read_it()
    {
        var a = Start();
        var b = Start();
        a.Press(Command.CycleRoe);
        b.Press(Command.CycleRoe);
        b.Press(Command.CycleRoe);
        a.Step();
        b.Step();

        Assert.NotEqual(a.Battle.World.ComputeStateHash(), b.Battle.World.ComputeStateHash());
        Assert.Null(a.Battle.World.ViewFor("de").RoeStatusOf(0));
    }

    [Fact]
    public void The_panel_shows_the_pending_change_beside_the_pending_move()
    {
        var c = Start();
        MoveNorth(c);
        c.Press(Command.CycleRoe);
        var queued = c.Frame().OfType<TextPrim>().Select(t => t.Text).ToArray();
        Assert.Contains("Order: Move queued", queued);
        Assert.Contains(queued, t => t.StartsWith("RoE change: Return fire only queued", StringComparison.Ordinal));

        c.Step();
        var pending = c.Frame().OfType<TextPrim>().Select(t => t.Text).ToArray();
        Assert.Contains("Order: Move: Pending", pending);
        Assert.Contains(pending, t => t.StartsWith("RoE change: Return fire only, pending ", StringComparison.Ordinal));
    }
}
