using PointBlank.Sim.Sight;

namespace PointBlank.Sim.Org;

/// <summary>Owns every squad and soldier: ticks leader succession and hashes all of it.</summary>
public sealed class OrgSystem(OrgTables tables) : ISimSystem
{
    private readonly List<Squad> _squads = [];
    private int _nextSoldierId;

    public OrgTables Tables { get; } = tables;

    /// <summary>In spawn order.</summary>
    internal IReadOnlyList<Squad> Squads => _squads;

    internal Soldier? FindSoldier(int id) =>
        _squads.SelectMany(s => s.Soldiers).FirstOrDefault(s => s.Id == id);

    internal Squad? FindSquadOf(int soldierId) =>
        _squads.FirstOrDefault(q => q.Soldiers.Any(s => s.Id == soldierId));

    /// <summary>
    /// Creates a squad from the organisation data with its men in a loose block (2 m apart,
    /// four to a row) centred on the point, the leader at the front.
    /// </summary>
    /// <exception cref="ArgumentException">There is no such squad in the data.</exception>
    internal Squad SpawnSquad(string orgId, Fixed x, Fixed y, Posture posture = Posture.Standing)
    {
        var template = Tables.Get(orgId);
        var spacing = Fixed.FromInt(2);
        const int PerRow = 4;
        var rows = (template.Slots.Count + PerRow - 1) / PerRow;
        var teamIds = template.Slots.Select(s => s.TeamId).Distinct().ToList();

        var soldiers = new List<Soldier>();
        for (int i = 0; i < template.Slots.Count; i++)
        {
            var slot = template.Slots[i];
            var inRow = Math.Min(PerRow, template.Slots.Count - (i / PerRow) * PerRow);
            var column = i % PerRow;
            soldiers.Add(new Soldier(
                _nextSoldierId++, slot.Key, slot.Role, slot.Rank, slot.SuccessionOrder, slot.WeaponId, teamIds.IndexOf(slot.TeamId))
            {
                X = x + spacing * (Fixed.FromInt(column) - Fixed.FromRatio(inRow - 1, 2)),
                Y = y + spacing * (Fixed.FromInt(i / PerRow) - Fixed.FromRatio(rows - 1, 2)),
                Posture = posture,
            });
        }

        var teams = teamIds.Select((id, t) => new Team(id, soldiers.Where(s => s.TeamIndex == t).ToList())).ToList();
        var leader = soldiers.First(s => s.Role == Role.SquadLeader);
        var squad = new Squad(_squads.Count, orgId, template.Side, soldiers, teams, template.DelayTicks, template.LeaderBonus, leader, template.IsHq, template.HasRadio);
        _squads.Add(squad);
        return squad;
    }

    public void Tick(World world)
    {
        foreach (var squad in _squads)
        {
            squad.Tick();
        }
    }

    public void AppendState(StateHasher hasher)
    {
        hasher.Add(_nextSoldierId);
        hasher.Add(_squads.Count);
        foreach (var q in _squads)
        {
            hasher.Add(q.Id);
            hasher.Add(q.OrgId);
            hasher.Add(q.Side);
            hasher.Add(q.SuccessionDelayTicks);
            hasher.Add(q.LeaderBonus);
            hasher.Add(q.Leader?.Id ?? -1);
            hasher.Add(q.TicksToSuccession);
            hasher.Add(q.IsHq);
            hasher.Add(q.HasRadio);
            hasher.Add((byte)q.Morale);
            hasher.Add(q.MoraleLevel);
            hasher.Add(q.Soldiers.Count);
            foreach (var s in q.Soldiers)
            {
                hasher.Add(s.Id);
                hasher.Add(s.Key);
                hasher.Add((byte)s.Role);
                hasher.Add(s.Rank);
                hasher.Add(s.SuccessionOrder);
                hasher.Add(s.WeaponId);
                hasher.Add(s.TeamIndex);
                hasher.Add(s.X);
                hasher.Add(s.Y);
                hasher.Add((byte)s.Posture);
                hasher.Add(s.PendingPosture is { } p ? (byte)(1 + (int)p) : (byte)0);
                hasher.Add(s.PostureDueAt);
                hasher.Add(s.AwaitingPosture);
                hasher.Add((byte)s.Health);
            }
        }
    }
}

/// <summary>Spawns a squad from the organisation data when it applies.</summary>
public sealed class SpawnSquadInput(string orgId, Fixed x, Fixed y, Posture posture) : ISimInput
{
    public void Apply(World world) => world.GetSystem<OrgSystem>().SpawnSquad(orgId, x, y, posture);
}

/// <summary>Sets a soldier's health state (a casualty, for example) when it applies.</summary>
public sealed class SetHealthInput(int soldierId, HealthState health) : ISimInput
{
    public void Apply(World world)
    {
        var org = world.GetSystem<OrgSystem>();
        var squad = org.FindSquadOf(soldierId)
            ?? throw new InvalidOperationException($"No soldier {soldierId}.");
        squad.SetHealth(soldierId, health);
    }
}

/// <summary>
/// Forces a squad's morale state when it applies (a test and scenario override). With a
/// <see cref="Morale.MoraleSystem"/> the level is also set inside the state's band, so the model
/// carries on from there; without one only the state is set.
/// </summary>
public sealed class SetMoraleInput(int squadId, MoraleState morale) : ISimInput
{
    public void Apply(World world)
    {
        var squad = world.GetSystem<OrgSystem>().Squads.FirstOrDefault(s => s.Id == squadId)
            ?? throw new InvalidOperationException($"No squad {squadId}.");
        if (world.FindSystem<Morale.MoraleSystem>() is { } model)
        {
            model.Force(squad.Id, morale);
        }
        else
        {
            squad.SetMorale(morale);
        }
    }
}
