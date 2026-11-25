using PointBlank.Sim.Data;
using PointBlank.Sim.Org;
using PointBlank.Sim.Sight;
using PointBlank.Sim.Tests.Data;

namespace PointBlank.Sim.Tests.Org;

public class OrgTests
{
    private static readonly DataSet Data = DataLoader.LoadDirectory(DataPaths.DataDirectory());

    private static (World World, OrgSystem Org) NewWorld(ulong seed = 1)
    {
        var org = new OrgSystem(OrgTables.FromData(Data));
        return (new World(seed, org), org);
    }

    private static Soldier Find(Squad squad, Role role) => squad.Soldiers.First(s => s.Role == role);

    [Fact]
    public void UsSquad_HasTwelveMenInTeamsWithOneLeader()
    {
        var (_, org) = NewWorld();
        var squad = org.SpawnSquad("us_rifle_squad", Fixed.FromInt(10), Fixed.FromInt(20));

        Assert.Equal(12, squad.Soldiers.Count);
        Assert.Equal(["command", "bar_team", "rifle_team_a", "rifle_team_b"], squad.Teams.Select(t => t.Id));
        Assert.Equal(3, squad.Teams.First(t => t.Id == "bar_team").Members.Count);
        Assert.Equal(Role.SquadLeader, squad.Leader!.Role);
        Assert.Equal("us_bar_sample", Find(squad, Role.BarGunner).WeaponId);
        Assert.All(squad.Soldiers, s => Assert.Equal(HealthState.Ok, s.Health));
        Assert.True(squad.HasEffectiveLeader);
    }

    [Fact]
    public void GermanGruppe_HasMgTeamAndRifleTeamUnderTheDeputy()
    {
        var (_, org) = NewWorld();
        var squad = org.SpawnSquad("de_gruppe", Fixed.Zero, Fixed.Zero);

        Assert.Equal(10, squad.Soldiers.Count);
        Assert.Equal(3, squad.Teams.First(t => t.Id == "mg_team").Members.Count);
        Assert.Equal("de_mg_sample", Find(squad, Role.MgGunner).WeaponId);
        Assert.Equal(6, squad.Teams.First(t => t.Id == "rifle_team").Members.Count);
        Assert.Equal(Role.AssistantLeader, squad.Teams.First(t => t.Id == "rifle_team").Members[0].Role);
    }

    [Fact]
    public void Spawn_PlacesMenAroundThePointAndAssignsSequentialIds()
    {
        var (_, org) = NewWorld();
        var a = org.SpawnSquad("us_rifle_squad", Fixed.FromInt(50), Fixed.FromInt(60), Posture.Prone);
        var b = org.SpawnSquad("de_gruppe", Fixed.FromInt(100), Fixed.FromInt(60));

        Assert.Equal(0, a.Id);
        Assert.Equal(1, b.Id);
        Assert.Equal(Enumerable.Range(0, 22), a.Soldiers.Concat(b.Soldiers).Select(s => s.Id));
        Assert.All(a.Soldiers, s => Assert.Equal(Posture.Prone, s.Posture));
        Assert.Equal(12, a.Soldiers.Select(s => (s.X, s.Y)).Distinct().Count());
        Assert.All(a.Soldiers, s => Assert.True(Fixed.FromInt(40) <= s.X && s.X <= Fixed.FromInt(60)));
        Assert.Equal("us", a.Side);
        Assert.Equal("de", b.Side);
    }

    [Fact]
    public void LeaderDeath_NoEffectiveLeaderDuringDelay_ThenDeputyTakesOver()
    {
        var (world, org) = NewWorld();
        var squad = org.SpawnSquad("us_rifle_squad", Fixed.Zero, Fixed.Zero);
        var bonus = squad.LeaderBonus;
        Assert.True(bonus > Fixed.Zero);
        Assert.Equal(50, squad.SuccessionDelayTicks);

        squad.SetHealth(Find(squad, Role.SquadLeader).Id, HealthState.Dead);

        Assert.False(squad.HasEffectiveLeader);
        Assert.Equal(Fixed.Zero, squad.LeaderBonus);
        world.Run(squad.SuccessionDelayTicks - 1);
        Assert.False(squad.HasEffectiveLeader);
        Assert.Equal(Fixed.Zero, squad.LeaderBonus);

        world.Step();
        Assert.True(squad.HasEffectiveLeader);
        Assert.Equal(Role.AssistantLeader, squad.Leader!.Role);
        Assert.Equal(bonus, squad.LeaderBonus);
    }

    [Fact]
    public void IncapacitatedLeader_AlsoHandsOver_ButWoundedLeaderKeepsCommand()
    {
        var (world, org) = NewWorld();
        var squad = org.SpawnSquad("de_gruppe", Fixed.Zero, Fixed.Zero);
        var leader = squad.Leader!;

        squad.SetHealth(leader.Id, HealthState.Wounded);
        Assert.True(squad.HasEffectiveLeader);

        squad.SetHealth(leader.Id, HealthState.Incapacitated);
        Assert.False(squad.HasEffectiveLeader);
        world.Run(squad.SuccessionDelayTicks);
        Assert.Equal("dep", squad.Leader!.Key);
    }

    [Fact]
    public void CandidateWhoDiesDuringTheDelay_IsSkipped()
    {
        var (world, org) = NewWorld();
        var squad = org.SpawnSquad("us_rifle_squad", Fixed.Zero, Fixed.Zero);

        squad.SetHealth(squad.Leader!.Id, HealthState.Dead);
        world.Run(10);
        squad.SetHealth(Find(squad, Role.AssistantLeader).Id, HealthState.Dead);
        world.Run(squad.SuccessionDelayTicks);

        // Succession order 2 is the senior rifleman after the assistant leader (order 1).
        Assert.Equal("ra1", squad.Leader!.Key);
    }

    [Fact]
    public void NoEligibleSoldier_LeavesTheSquadLeaderless_AndRecoversNobody()
    {
        var (world, org) = NewWorld();
        var squad = org.SpawnSquad("de_gruppe", Fixed.Zero, Fixed.Zero);
        foreach (var s in squad.Soldiers)
        {
            squad.SetHealth(s.Id, HealthState.Dead);
        }

        world.Run(200);

        Assert.Null(squad.Leader);
        Assert.False(squad.HasEffectiveLeader);
        Assert.Equal(Fixed.Zero, squad.LeaderBonus);
    }

    [Fact]
    public void ChainOfSuccession_FollowsTheDataOrder()
    {
        var (world, org) = NewWorld();
        var squad = org.SpawnSquad("us_rifle_squad", Fixed.Zero, Fixed.Zero);
        var keys = new List<string> { squad.Leader!.Key };
        for (int i = 0; i < 3; i++)
        {
            squad.SetHealth(squad.Leader!.Id, HealthState.Dead);
            world.Run(squad.SuccessionDelayTicks);
            keys.Add(squad.Leader!.Key);
        }

        Assert.Equal(["sl", "asl", "ra1", "ra2"], keys);
    }

    [Fact]
    public void StateHash_IsDeterministic_AndSeesSoldierState()
    {
        ulong Hash(Action<Squad>? change = null)
        {
            var (world, org) = NewWorld(7);
            var squad = org.SpawnSquad("us_rifle_squad", Fixed.FromInt(5), Fixed.FromInt(5));
            change?.Invoke(squad);
            world.Run(5);
            return world.ComputeStateHash();
        }

        var baseline = Hash();
        Assert.Equal(baseline, Hash());
        Assert.NotEqual(baseline, Hash(s => s.Soldiers[3].Posture = Posture.Kneeling));
        Assert.NotEqual(baseline, Hash(s => s.Soldiers[3].X += Fixed.One));
        Assert.NotEqual(baseline, Hash(s => s.SetHealth(s.Soldiers[3].Id, HealthState.Wounded)));
        Assert.NotEqual(baseline, Hash(s => s.SetHealth(s.Leader!.Id, HealthState.Dead)));
    }

    [Fact]
    public void Inputs_SpawnAndWoundThroughTheWorldSchedule()
    {
        var (world, org) = NewWorld();
        world.Schedule(0, new SpawnSquadInput("us_rifle_squad", Fixed.FromInt(3), Fixed.FromInt(4), Posture.Standing));
        world.Schedule(1, new SetHealthInput(0, HealthState.Dead));
        world.Run(2);

        Assert.Single(org.Squads);
        Assert.False(org.Squads[0].HasEffectiveLeader);
        Assert.Equal(HealthState.Dead, org.FindSoldier(0)!.Health);
    }

    [Fact]
    public void BadOrgData_IsRejectedWithAClearMessage()
    {
        DataSet With(Func<DataEntry, bool> drop) =>
            new([.. Data.Tables.Select(t => t.Name != "organisation" ? t
                : new DataTable(t.Name, [.. t.Entries.Where(e => !drop(e))]))]);

        var noLeader = With(e => e.Id == "de_gruppe.gf");
        Assert.Contains("leader", Assert.Throws<DataException>(() => OrgTables.FromData(noLeader)).Message);

        var noWeapons = new DataSet([.. Data.Tables.Select(t => t.Name != "weapons" ? t
            : new DataTable(t.Name, [.. t.Entries.Where(e => e.Id != "de_mg_sample")]))]);
        Assert.Contains("de_mg_sample", Assert.Throws<DataException>(() => OrgTables.FromData(noWeapons)).Message);

        Assert.Throws<ArgumentException>(() => NewWorld().Org.SpawnSquad("nope", Fixed.Zero, Fixed.Zero));
    }
}
