using PointBlank.Sim.Org;
using PointBlank.Sim.Tests.Movement;

namespace PointBlank.Sim.Tests.Org;

/// <summary>Item 1.3 follow-up: command is not handed back to a soldier who is restored.</summary>
public class RestoredSoldierTests
{
    private static (World World, Squad Squad) NewSquad()
    {
        var org = new OrgSystem(OrgTables.FromData(PathFinderTests.Data));
        var world = new World(1, org);
        return (world, org.SpawnSquad("us_rifle_squad", Fixed.Zero, Fixed.Zero));
    }

    [Theory]
    [InlineData(HealthState.Dead, HealthState.Ok)]
    [InlineData(HealthState.Incapacitated, HealthState.Wounded)]
    [InlineData(HealthState.Dead, HealthState.Wounded)]
    public void A_leader_restored_before_succession_does_not_regain_command(HealthState down, HealthState restored)
    {
        var (world, squad) = NewSquad();
        var leader = squad.Leader!;

        squad.SetHealth(leader.Id, down);
        world.Run(5);
        squad.SetHealth(leader.Id, restored);

        Assert.Null(squad.Leader);
        Assert.False(squad.HasEffectiveLeader);
        world.Run(squad.SuccessionDelayTicks);
        Assert.Equal("asl", squad.Leader!.Key);
    }

    [Fact]
    public void A_leader_restored_after_succession_stays_a_follower()
    {
        var (world, squad) = NewSquad();
        var leader = squad.Leader!;
        squad.SetHealth(leader.Id, HealthState.Incapacitated);
        world.Run(squad.SuccessionDelayTicks);

        squad.SetHealth(leader.Id, HealthState.Ok);
        world.Run(200);

        Assert.Equal("asl", squad.Leader!.Key);
        Assert.NotSame(leader, squad.Leader);
    }

    [Fact]
    public void A_restored_deputy_may_lead_again_only_when_the_post_falls_vacant_again()
    {
        var (world, squad) = NewSquad();
        squad.SetHealth(squad.Leader!.Id, HealthState.Dead);
        world.Run(squad.SuccessionDelayTicks);
        var deputy = squad.Leader!;
        squad.SetHealth(deputy.Id, HealthState.Dead);
        world.Run(squad.SuccessionDelayTicks);
        Assert.Equal("ra1", squad.Leader!.Key);

        squad.SetHealth(deputy.Id, HealthState.Ok);
        world.Run(100);
        Assert.Equal("ra1", squad.Leader!.Key); // not handed back

        squad.SetHealth(squad.Leader!.Id, HealthState.Dead);
        world.Run(squad.SuccessionDelayTicks);
        Assert.Same(deputy, squad.Leader); // the restored deputy is next in line again
    }
}
